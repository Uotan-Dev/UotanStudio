using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// The FDL2 storage face of a connected Unisoc session: the native partition table, the validated
/// <c>user_partition</c> GPT, named-partition reads, writes and erases. Every operation re-checks the loader stage,
/// treats device replies as untrusted input and refuses destructive calls without the exact confirmation phrase.
/// <para>已连接紫光展锐会话的 FDL2 存储面：原生分区表、经过校验的 <c>user_partition</c> GPT、按名称的分区读写
/// 与擦除。每个操作都会重新检查加载阶段，设备应答按不可信输入处理，缺少精确确认短语时拒绝一切破坏性调用。</para>
/// </summary>
public sealed class UnisocStorageSession
{
    /// <summary>Default byte window read from <c>user_partition</c> for GPT validation.
    /// <para>为校验 GPT 而从 <c>user_partition</c> 读取的默认字节窗口。</para></summary>
    public const int DefaultGptWindowBytes = 32 * 1024;

    /// <summary>Upper bound of the GPT read window. <para>GPT 读取窗口的上限。</para></summary>
    public const int MaximumGptWindowBytes = 4 * 1024 * 1024;

    private readonly UnisocDeviceEngine _session;

    internal UnisocStorageSession(UnisocDeviceEngine session)
    {
        _session = session;
    }

    /// <summary>
    /// Applies a serialized vendor ExtTable and rebuilds the device partition table. This destroys every partition
    /// boundary, so callers must have obtained an explicit confirmation and a full backup first.
    /// <para>下发厂商 ExtTable 并重建设备分区表。该操作销毁所有分区边界，调用方必须已获得显式确认并完成完整备份。</para>
    /// </summary>
    /// <param name="extTable">The serialized ExtTable bytes from the PAC scheme. <para>来自 PAC scheme 的 ExtTable 字节。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <exception cref="UnisocProtocolException">The device refused; the session has faulted.
    /// <para>设备拒绝；会话已失效。</para></exception>
    public async Task RepartitionAsync(byte[] extTable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extTable);
        if (extTable.Length is < 4 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(extTable), extTable.Length,
                "An ExtTable must be between 4 bytes and 1 MiB.");
        }

        await _session.SendAndExpectAckAsync(UnisocCommand.SendExtTable, extTable,
            _session.EraseTimeoutMs, cancellationToken).ConfigureAwait(false);
        await _session.SendAndExpectAckAsync(UnisocCommand.Repartition, ReadOnlyMemory<byte>.Empty,
            _session.EraseTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the native partition table FDL2 reports. Size values carry the device's own unit, which is not
    /// confirmable from the table itself; the last record may mark all remaining capacity.
    /// <para>读取 FDL2 报告的原生分区表。大小字段携带设备自带的单位，表本身无法确认；最后一条记录可能表示
    /// 剩余全部容量。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The records in table order. <para>按表顺序排列的记录。</para></returns>
    /// <exception cref="UnisocProtocolException">The device refused, the table stride was wrong, or a name was
    /// invalid; the session has faulted. <para>设备拒绝、表步长错误或名称非法；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<IReadOnlyList<UnisocPartitionRecord>> ReadPartitionTableAsync(
        CancellationToken cancellationToken)
    {
        var reply = await _session.ExchangeAsync(UnisocCommand.ReadPartition, ReadOnlyMemory<byte>.Empty,
            _session.CommandTimeoutMs, cancellationToken).ConfigureAwait(false);
        if ((UnisocReply)reply.Type != UnisocReply.ReadPartition)
        {
            _session.Fault();
            throw new UnisocProtocolException(
                $"The device replied 0x{reply.Type:X4} to the partition-table query.");
        }

        if (reply.PayloadLength == 0 || reply.PayloadLength % UnisocConstants.PartitionRecordSize != 0)
        {
            _session.Fault();
            throw new UnisocProtocolException(
                $"The native partition table length {reply.PayloadLength} is not a record multiple.");
        }

        var records = new List<UnisocPartitionRecord>(reply.PayloadLength / UnisocConstants.PartitionRecordSize);
        try
        {
            for (var offset = 0; offset < reply.PayloadLength; offset += UnisocConstants.PartitionRecordSize)
            {
                var name = DecodeName(reply.Payload.AsSpan(offset, UnisocConstants.PartitionNameSize));
                if (name.Length == 0 || name.Any(char.IsControl) ||
                    records.Any(record => string.Equals(record.Name, name, StringComparison.Ordinal)))
                {
                    _session.Fault();
                    throw new UnisocProtocolException($"The native partition table carries an invalid name at record {records.Count}.");
                }

                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    reply.Payload.AsSpan(offset + UnisocConstants.PartitionNameSize));
                records.Add(new UnisocPartitionRecord(name, size, size == UnisocConstants.RemainingCapacityMarker));
            }
        }
        catch (UnisocProtocolException)
        {
            _session.Fault();
            throw;
        }

        return records;
    }

    /// <summary>
    /// Reads the <c>user_partition</c> prefix and validates the GPT inside it. Both 512- and 4096-byte layouts are
    /// accepted through the sector-aligned scan; exactly one validated copy must exist, and the snapshot carries the
    /// sector size the caller needs to translate LBAs.
    /// <para>读取 <c>user_partition</c> 前缀并校验其中的 GPT。经扇区对齐扫描同时接受 512 与 4096 字节布局；
    /// 必须恰好有一份通过校验的副本，快照携带调用方换算 LBA 所需的扇区大小。</para>
    /// </summary>
    /// <param name="windowBytes">Bytes to read from the partition start; the default window must hold the header
    /// and entry arrays of both layouts.
    /// <para>从分区起点读取的字节数；默认窗口必须容纳两种布局的头部与条目数组。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The validated snapshot, or <see langword="null"/> when no GPT validates in the window.
    /// <para>校验通过的快照；窗口内没有通过校验的 GPT 时为 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowBytes"/> is outside the bounds.
    /// <para><paramref name="windowBytes"/> 超出边界。</para></exception>
    /// <exception cref="UnisocProtocolException">The device refused, the read failed, or more than one GPT copy
    /// validated; the session has faulted. <para>设备拒绝、读取失败或有多份 GPT 副本通过校验；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<UnisocGptSnapshot?> ReadGptAsync(int windowBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowBytes, 12 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(windowBytes, MaximumGptWindowBytes);
        var window = new byte[windowBytes];
        var filled = 0;
        await ReadWindowAsync("user_partition", 0, windowBytes,
            (chunk, _) =>
            {
                chunk.CopyTo(window.AsMemory(filled));
                filled += chunk.Length;
                return ValueTask.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        var copies = GptImageEditor.DescribeCopies(window);
        if (copies.Count == 0)
        {
            return null;
        }

        if (copies.Count > 1)
        {
            _session.Fault();
            throw new UnisocProtocolException(
                $"The user_partition window validates {copies.Count} GPT copies; refusing the ambiguous table.");
        }

        var headerLba = copies[0].HeaderLba;
        var sectorSize = headerLba switch
        {
            1 => 512,
            8 => 4096,
            _ => 0
        };
        if (sectorSize == 0 || !GptImageEditor.TryRead(window, headerLba, out var table, out _))
        {
            return null;
        }

        return new UnisocGptSnapshot(table, sectorSize);
    }

    /// <summary>
    /// Reads a byte range of one partition into the destination stream by name.
    /// <para>按名称把一个分区的字节范围读入目标流。</para>
    /// </summary>
    /// <param name="name">Partition name as the device spells it. <para>设备拼写形式的分区名。</para></param>
    /// <param name="offset">Start offset inside the partition. <para>分区内的起始偏移。</para></param>
    /// <param name="length">Bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="destination">Destination stream; the session writes as chunks arrive.
    /// <para>目标流；会话随分块到达写入。</para></param>
    /// <param name="cancellationToken">Cancels the read; already-read chunks are kept by the caller's stream.
    /// <para>取消读取；已读分块由调用方的流自行保留。</para></param>
    /// <returns>Bytes actually read, which may be shorter than requested when the partition ends early.
    /// <para>实际读到的字节数；分区提前结束时可能小于请求量。</para></returns>
    /// <exception cref="UnisocProtocolException">The device refused or the link failed; the session has faulted.
    /// <para>设备拒绝或链路失败；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<long> ReadPartitionAsync(string name, long offset, long length, Stream destination,
        CancellationToken cancellationToken)
    {
        ValidateRange(name, offset, length);
        if (offset > 0x1_0000_0000L - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The read range exceeds the 32-bit address space.");
        }

        return await ReadWindowAsync(name, offset, length,
            (chunk, token) => destination.WriteAsync(chunk, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one partition from a seekable source. The confirmation phrase must match
    /// <see cref="UnisocFlashOptions.ConfirmationPhrase"/> exactly, and partitions whose names contain
    /// <c>nv</c> are refused outright.
    /// <para>从可定位源写入一个分区。确认短语必须与 <see cref="UnisocFlashOptions.ConfirmationPhrase"/> 完全一致，
    /// 名称含 <c>nv</c> 的分区一律拒绝。</para>
    /// </summary>
    /// <param name="name">Partition name as the device spells it. <para>设备拼写形式的分区名。</para></param>
    /// <param name="source">Seekable image source of exactly <paramref name="length"/> bytes.
    /// <para>恰好 <paramref name="length"/> 字节的可定位镜像源。</para></param>
    /// <param name="length">Bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="confirmation">The exact confirmation phrase. <para>精确的确认短语。</para></param>
    /// <param name="cancellationToken">Cancels the write; the device may have accepted part of the data already.
    /// <para>取消写入；设备可能已接受部分数据。</para></param>
    /// <param name="progress">Optional bytes-written sink for progress reporting.
    /// <para>可选的已写入字节接收器，用于进度上报。</para></param>
    /// <exception cref="ArgumentException">The name is invalid, names an NV partition, or the confirmation phrase
    /// does not match. <para>名称非法、指向 NV 分区，或确认短语不一致。</para></exception>
    /// <exception cref="UnisocProtocolException">The device refused or the link failed; the session has faulted.
    /// <para>设备拒绝或链路失败；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task WritePartitionAsync(string name, Stream source, long length, string confirmation,
        CancellationToken cancellationToken, IProgress<long>? progress = null)
    {
        ValidateName(name);
        if (name.Contains("nv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Partition '{name}' carries NV data and must not be written.", nameof(name));
        }

        if (!string.Equals(confirmation, UnisocFlashOptions.ConfirmationPhrase, StringComparison.Ordinal))
        {
            throw new ArgumentException("The flash confirmation phrase does not match.", nameof(confirmation));
        }

        if (!source.CanSeek)
        {
            throw new ArgumentException("The partition image source must be seekable.", nameof(source));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 0x1_0000_0000L);
        if (source.Length < length)
        {
            throw new ArgumentException("The image source is shorter than the declared length.", nameof(source));
        }

        var mode64 = length > uint.MaxValue;
        await _session.SendAndExpectAckAsync(UnisocCommand.StartData,
            SelectPacket(name, (ulong)length, mode64), _session.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[_session.StorageBlockSize];
        long transferred = 0;
        while (transferred < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(buffer.Length, length - transferred);
            await source.ReadExactlyAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            await _session.SendAndExpectAckAsync(UnisocCommand.MidstData, buffer.AsMemory(0, chunk),
                _session.StorageChunkTimeoutMs, cancellationToken).ConfigureAwait(false);
            transferred += chunk;
            progress?.Report(transferred);
        }

        await _session.SendAndExpectAckAsync(UnisocCommand.EndData, ReadOnlyMemory<byte>.Empty, _session.CommandTimeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Erases one partition by name. The confirmation phrase must match
    /// <see cref="UnisocFlashOptions.ConfirmationPhrase"/> exactly, and partitions whose names contain
    /// <c>nv</c> are refused outright.
    /// <para>按名称擦除一个分区。确认短语必须与 <see cref="UnisocFlashOptions.ConfirmationPhrase"/> 完全一致，
    /// 名称含 <c>nv</c> 的分区一律拒绝。</para>
    /// </summary>
    /// <param name="name">Partition name as the device spells it. <para>设备拼写形式的分区名。</para></param>
    /// <param name="confirmation">The exact confirmation phrase. <para>精确的确认短语。</para></param>
    /// <param name="cancellationToken">Cancels the erase. <para>取消擦除。</para></param>
    /// <exception cref="ArgumentException">The name is invalid, names an NV partition, or the confirmation phrase
    /// does not match. <para>名称非法、指向 NV 分区，或确认短语不一致。</para></exception>
    /// <exception cref="UnisocProtocolException">The device refused or the link failed; the session has faulted.
    /// <para>设备拒绝或链路失败；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task ErasePartitionAsync(string name, string confirmation, CancellationToken cancellationToken)
    {
        ValidateName(name);
        if (name.Contains("nv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Partition '{name}' carries NV data and must not be erased.", nameof(name));
        }

        if (!string.Equals(confirmation, UnisocFlashOptions.ConfirmationPhrase, StringComparison.Ordinal))
        {
            throw new ArgumentException("The flash confirmation phrase does not match.", nameof(confirmation));
        }

        await _session.SendAndExpectAckAsync(UnisocCommand.EraseFlash, SelectPacket(name, 0, mode64: false),
            _session.EraseTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ReadWindowAsync(string name, long offset, long length,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink, CancellationToken cancellationToken)
    {
        ValidateName(name);
        if (offset < 0 || length < 0 || offset > 0x1_0000_0000L - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The read range exceeds the 32-bit address space.");
        }

        var end = offset + length;
        var mode64 = end > uint.MaxValue;
        await _session.SendAndExpectAckAsync(UnisocCommand.ReadStart,
            SelectPacket(name, (ulong)end, mode64), _session.CommandTimeoutMs, cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[_session.StorageBlockSize];
        var midst = new byte[mode64 ? 12 : 8];
        long transferred = 0;
        while (transferred < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(buffer.Length, length - transferred);
            BinaryPrimitives.WriteUInt32LittleEndian(midst, (uint)chunk);
            if (mode64)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(midst[4..], (ulong)(offset + transferred));
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(midst[4..], (uint)(offset + transferred));
            }

            var reply = await _session.ExchangeAsync(UnisocCommand.ReadMidst, midst,
                _session.StorageChunkTimeoutMs, cancellationToken).ConfigureAwait(false);
            if ((UnisocReply)reply.Type != UnisocReply.ReadFlash)
            {
                _session.Fault();
                throw new UnisocProtocolException(
                    $"The device replied 0x{reply.Type:X4} to a partition read chunk.");
            }

            var received = reply.Payload.Length;
            if (received > chunk)
            {
                _session.Fault();
                throw new UnisocProtocolException(
                    $"The device returned {received} bytes for a {chunk}-byte read chunk.");
            }

            if (received > 0)
            {
                await sink(reply.Payload.AsMemory(0, received), cancellationToken).ConfigureAwait(false);
            }

            transferred += received;
            if (received < chunk)
            {
                break;
            }
        }

        await _session.SendAndExpectAckAsync(UnisocCommand.ReadEnd, ReadOnlyMemory<byte>.Empty,
            _session.CommandTimeoutMs, cancellationToken).ConfigureAwait(false);
        return transferred;
    }

    private static void ValidateRange(string name, long offset, long length)
    {
        ValidateName(name);
        if (offset < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The read range must be non-negative.");
        }
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > UnisocConstants.MaximumPartitionNameUnits)
        {
            throw new ArgumentException($"Partition name '{name}' exceeds {UnisocConstants.MaximumPartitionNameUnits} code units.", nameof(name));
        }

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsControl(ch))
            {
                throw new ArgumentException($"Partition name '{name}' carries control characters.", nameof(name));
            }

            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1]))
                {
                    throw new ArgumentException($"Partition name '{name}' carries unpaired surrogates.", nameof(name));
                }

                i++;
            }
            else if (char.IsLowSurrogate(ch))
            {
                throw new ArgumentException($"Partition name '{name}' carries unpaired surrogates.", nameof(name));
            }
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> field)
    {
        var units = new char[UnisocConstants.MaximumPartitionNameUnits];
        var count = 0;
        while (count < UnisocConstants.MaximumPartitionNameUnits && count * 2 + 1 < field.Length)
        {
            var unit = BinaryPrimitives.ReadUInt16LittleEndian(field[(count * 2)..]);
            if (unit == 0)
            {
                break;
            }

            units[count++] = (char)unit;
        }

        return new string(units, 0, count);
    }

    private static byte[] SelectPacket(string name, ulong size, bool mode64)
    {
        ValidateName(name);
        var packet = new byte[UnisocConstants.PartitionNameSize + (mode64 ? 16 : 4)];
        var units = name.ToCharArray();
        for (var i = 0; i < units.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(i * 2), units[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(UnisocConstants.PartitionNameSize), (uint)size);
        if (mode64)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(UnisocConstants.PartitionNameSize + 4),
                (uint)(size >> 32));
        }

        return packet;
    }
}

/// <summary>
/// A validated <c>user_partition</c> GPT with the sector size its header position confirms.
/// <para>校验通过的 <c>user_partition</c> GPT，扇区大小由其头部位置确认。</para>
/// </summary>
/// <param name="Table">The validated table. <para>校验通过的表。</para></param>
/// <param name="SectorSize">Confirmed sector size in bytes. <para>确认的扇区大小（字节）。</para></param>
public sealed record UnisocGptSnapshot(GptTable Table, int SectorSize)
{
    /// <summary>
    /// Translates one partition into absolute device byte offsets using the confirmed sector size.
    /// <para>用确认的扇区大小把一个分区换算成设备上的绝对字节偏移。</para>
    /// </summary>
    /// <param name="partition">A partition of <see cref="Table"/>. <para><see cref="Table"/> 中的分区。</para></param>
    /// <returns>The absolute byte offset and length. <para>绝对字节偏移与长度。</para></returns>
    public (long Offset, long Length) ExtentOf(GptPartition partition) =>
        ((long)partition.FirstLba * SectorSize,
            checked((long)(partition.LastLba - partition.FirstLba + 1) * SectorSize));
}
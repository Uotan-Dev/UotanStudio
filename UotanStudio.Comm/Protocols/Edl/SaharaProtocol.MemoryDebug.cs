using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Sahara memory-debug (RAM dump) host side: the target advertises a debug region table, the host
/// reads that table and then requests individual address ranges with MEMORY_READ.
/// <para>Sahara 内存调试（RAM dump）的主机侧：目标声明一张调试区域表，主机先读取该表，随后通过
/// MEMORY_READ 请求具体的地址区间。</para>
/// </summary>
/// <remarks>
/// The framing: a HELLO in memory-debug mode is answered with a
/// HELLO_RESPONSE naming that mode, the target then sends MEMORY_DEBUG or MEMORY_DEBUG64 carrying the
/// table location, and every MEMORY_READ reply is raw bytes with no packet header.
/// The HELLO_RESPONSE has no packet-size field, so the host keeps
/// advertising <see cref="EdlConstants.DefaultMaxPacketLength"/> and never negotiates a smaller value.
/// <para>成帧方式：内存调试模式下的 HELLO 会以声明该模式的 HELLO_RESPONSE 应答，目标随后
/// 发送携带表位置的 MEMORY_DEBUG 或 MEMORY_DEBUG64，而每次 MEMORY_READ 的回复都是不带包头的裸字节。
/// HELLO_RESPONSE 不含包长字段，
/// 因此主机继续对外声明 <see cref="EdlConstants.DefaultMaxPacketLength"/>，绝不协商更小的值。</para>
/// </remarks>
public sealed partial class SaharaProtocol
{
    private const int MemoryDumpStreamBufferSize = 64 * 1024;
    private bool? _memoryDebugUsesExtendedAddressing;

    /// <summary>
    /// Gets or sets the largest region this host is willing to dump in one call.
    /// <para>获取或设置主机单次愿意转储的最大区域长度。</para>
    /// </summary>
    /// <value>A local byte ceiling; the protocol bounds a region only by the target's
    /// own table, so this guard exists to keep a
    /// malformed table from driving an unbounded write. Must be positive.
    /// <para>本地字节上限；协议只用目标自报的表来约束区域
    /// ，因此该护栏是为了让畸形的表无法驱动无限写入。
    /// 必须为正数。</para></value>
    public long MaxMemoryDumpRegionBytes { get; set; } = EdlConstants.SaharaMemoryRegionLimitBytes;

    /// <summary>
    /// Reads the target's Sahara debug region table.
    /// <para>读取目标的 Sahara 调试区域表。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token checked between Sahara packets.
    /// <para>在 Sahara 数据包边界之间检查的取消令牌。</para></param>
    /// <returns>The regions in table order; empty when the target advertised a zero-length table.
    /// <para>按表顺序排列的区域；目标声明零长度表时为空。</para></returns>
    /// <exception cref="ObjectDisposedException">The protocol instance has been disposed.
    /// <para>协议实例已被释放。</para></exception>
    /// <exception cref="SaharaException">The target is not in memory-debug mode, its HELLO is
    /// malformed, the reported table exceeds the bounded buffer or is not a whole number of records,
    /// or the table payload arrived truncated.
    /// <para>目标不处于内存调试模式、HELLO 畸形、上报的表超出有界缓冲或不是整数条记录，又或者表载荷
    /// 被截断。</para></exception>
    public async Task<IReadOnlyList<SaharaMemoryRegion>> ReadMemoryDebugRegionsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var helloBytes = await ReadExactAsync(
            Marshal.SizeOf<SaharaHelloPacket>(), _timeoutMs, cancellationToken).ConfigureAwait(false);
        if (LooksLikeFirehoseXml(helloBytes))
        {
            throw new SaharaException("The device answered with Firehose XML; Sahara memory debug requires a target in crashdump mode.");
        }

        var hello = BytesToStruct<SaharaHelloPacket>(helloBytes);
        ValidateHello(hello);
        if (hello.Mode != SaharaMode.MemoryDebug)
        {
            throw new SaharaException($"Sahara memory debug requires a memory-debug HELLO, received {hello.Mode}.");
        }

        await WriteStructAsync(new SaharaHelloResponsePacket
        {
            Command = SaharaCommand.HelloResponse,
            Length = (uint)Marshal.SizeOf<SaharaHelloResponsePacket>(),
            Version = Math.Min(hello.Version, EdlConstants.SaharaVersion),
            MinVersion = EdlConstants.SaharaHostCompatibleVersion,
            Status = 0,
            Mode = SaharaMode.MemoryDebug
        }, cancellationToken).ConfigureAwait(false);

        var headerBytes = await ReadExactAsync(
            Marshal.SizeOf<SaharaPacketHeader>(), _timeoutMs, cancellationToken).ConfigureAwait(false);
        var header = BytesToStruct<SaharaPacketHeader>(headerBytes);
        var extended = header.Command switch
        {
            SaharaCommand.MemoryDebug => false,
            SaharaCommand.MemoryDebug64 => true,
            _ => throw new SaharaException($"Expected Sahara MEMORY_DEBUG, received {header.Command}.")
        };

        _memoryDebugUsesExtendedAddressing = extended;
        ulong tableAddress;
        ulong tableLength;
        if (extended)
        {
            var packet = BytesToStruct<SaharaMemoryDebug64Packet>(
                await ReadRemainingPacket<SaharaMemoryDebug64Packet>(headerBytes, cancellationToken).ConfigureAwait(false));
            tableAddress = packet.MemoryTableAddress;
            tableLength = packet.MemoryTableLength;
        }
        else
        {
            var packet = BytesToStruct<SaharaMemoryDebugPacket>(
                await ReadRemainingPacket<SaharaMemoryDebugPacket>(headerBytes, cancellationToken).ConfigureAwait(false));
            tableAddress = packet.MemoryTableAddress;
            tableLength = packet.MemoryTableLength;
        }

        var recordSize = (ulong)(extended ? EdlConstants.SaharaMemoryRecordBytes64 : EdlConstants.SaharaMemoryRecordBytesLegacy);
        if (tableLength > EdlConstants.SaharaMemoryTableLimitBytes || tableLength % recordSize != 0)
        {
            throw new SaharaException(
                $"Sahara debug table of {tableLength} bytes is neither within the {EdlConstants.SaharaMemoryTableLimitBytes} byte " +
                $"intermediate buffer nor a whole number of {recordSize}-byte records.");
        }

        if (tableAddress > ulong.MaxValue - tableLength)
        {
            throw new SaharaException("The Sahara debug table address range overflows 64-bit address space.");
        }

        if (tableLength == 0)
        {
            Log("Sahara memory debug reported an empty region table.");
            return [];
        }

        await SendMemoryReadAsync(tableAddress, tableLength, cancellationToken).ConfigureAwait(false);
        var table = await ReadExactAsync(checked((int)tableLength), _timeoutMs, cancellationToken).ConfigureAwait(false);
        Log($"Received a Sahara debug region table of {tableLength} bytes.");
        return ParseMemoryRegionTable(table, extended);
    }

    /// <summary>
    /// Streams one target memory region into <paramref name="destination"/>, requesting at most
    /// <see cref="EdlConstants.SaharaMemoryReadChunkBytes"/> bytes per MEMORY_READ and never buffering
    /// more than the chunk in flight.
    /// <para>把单个目标内存区域流式写入 <paramref name="destination"/>，每次 MEMORY_READ 最多请求
    /// <see cref="EdlConstants.SaharaMemoryReadChunkBytes"/> 字节，且绝不缓冲超过在途块的数据。</para>
    /// </summary>
    /// <param name="region">The region to dump; a zero-length region writes nothing.
    /// <para>要转储的区域；零长度区域不写入任何内容。</para></param>
    /// <param name="destination">The stream receiving the bytes in region order; the caller retains
    /// ownership and must be writable. <para>按区域顺序接收字节的流；调用方保留所有权且必须可写。</para></param>
    /// <param name="cancellationToken">Cancellation token checked before every MEMORY_READ.
    /// <para>在每次 MEMORY_READ 之前检查的取消令牌。</para></param>
    /// <returns>The number of bytes written. <para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="region"/> or <paramref name="destination"/>
    /// is <see langword="null"/>. <paramref name="region"/> 或 <paramref name="destination"/> 为
    /// <see langword="null"/>。</exception>
    /// <exception cref="SaharaException">The region exceeds <see cref="MaxMemoryDumpRegionBytes"/>, its
    /// address range overflows, or the target returned fewer bytes than requested.
    /// <para>区域超出 <see cref="MaxMemoryDumpRegionBytes"/>、地址区间溢出，或目标返回的字节少于请求。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled between chunks.
    /// <para>调用方在块之间取消。</para></exception>
    public async Task<ulong> DumpMemoryRegionAsync(SaharaMemoryRegion region, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMemoryDumpRegionBytes);

        if (region.Length == 0)
        {
            return 0;
        }

        if (region.Length > (ulong)MaxMemoryDumpRegionBytes)
        {
            throw new SaharaException($"Region length 0x{region.Length:X} exceeds the {MaxMemoryDumpRegionBytes} byte dump limit.");
        }

        if (region.Address > ulong.MaxValue - region.Length)
        {
            throw new SaharaException("The requested memory region address range overflows 64-bit address space.");
        }

        ulong completed = 0;
        while (completed < region.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = Math.Min((ulong)EdlConstants.SaharaMemoryReadChunkBytes, region.Length - completed);
            await SendMemoryReadAsync(region.Address + completed, chunk, cancellationToken).ConfigureAwait(false);
            await ReceiveMemoryBytesAsync(checked((long)chunk), destination, cancellationToken).ConfigureAwait(false);
            completed += chunk;
        }

        Log($"Sahara memory dump completed for 0x{region.Address:X16} ({completed} bytes).");
        return completed;
    }

    /// <summary>
    /// Streams one target memory region into a file, publishing it only after the whole region arrived.
    /// <para>把单个目标内存区域流式写入文件，只有整段区域到齐后才发布。</para>
    /// </summary>
    /// <remarks>
    /// <para>The bytes land in <c>filePath.partial</c> and are renamed over the target path once the dump
    /// finishes, so a failure leaves any previous file untouched.</para>
    /// <para>字节先落在 <c>filePath.partial</c>，转储完成后再重命名覆盖目标路径，因此失败时既有文件保持
    /// 原样。</para>
    /// </remarks>
    /// <param name="region">The region to dump. <para>要转储的区域。</para></param>
    /// <param name="filePath">The destination file path chosen by the caller; an existing file is
    /// replaced only after a complete dump. <para>由调用方选定的目标文件路径；既有文件仅在完整转储之后被替换。</para></param>
    /// <param name="cancellationToken">Cancellation token checked between chunks.
    /// <para>在块之间检查的取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="region"/> is <see langword="null"/>.
    /// <paramref name="region"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty or whitespace.
    /// <paramref name="filePath"/> 为空或仅空白。</exception>
    /// <exception cref="SaharaException">The dump failed; the partial file is removed.
    /// <para>转储失败；残留的部分文件会被删除。</para></exception>
    public async Task DumpMemoryRegionAsync(SaharaMemoryRegion region, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var targetPath = Path.GetFullPath(filePath);
        var partialPath = targetPath + ".partial";
        var parent = Path.GetDirectoryName(partialPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            await using (var output = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                MemoryDumpStreamBufferSize,
                FileOptions.SequentialScan))
            {
                _ = await DumpMemoryRegionAsync(region, output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partialPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeletePartialFile(partialPath);
            throw;
        }
    }

    /// <summary>
    /// Collects every non-empty region of the target's debug table into
    /// <paramref name="outputDirectory"/>.
    /// <para>把目标调试表中所有非空区域收集到 <paramref name="outputDirectory"/>。</para>
    /// Each file is named after the region's own proposed name, and the session ends with a Sahara RESET
    /// so the target leaves memory-debug mode.
    /// <para>每个文件以区域自报的名称命名，会话以一次 Sahara RESET 结束，使目标离开内存调试模式。</para>
    /// </summary>
    /// <param name="outputDirectory">Directory the region files are written into; created when
    /// missing. <para>写入区域文件的目录；不存在时会被创建。</para></param>
    /// <param name="cancellationToken">Cancellation token checked between packets and chunks.
    /// <para>在数据包与块之间检查的取消令牌。</para></param>
    /// <returns>The regions that were written, in table order. <para>实际写入的区域，按表顺序。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is empty or whitespace.
    /// <paramref name="outputDirectory"/> 为空或仅空白。</exception>
    /// <exception cref="SaharaException">A region proposed an unsafe or duplicated file name, or the
    /// table read failed. <para>某个区域给出了不安全或重复的文件名，又或者表读取失败。</para></exception>
    /// <exception cref="IOException">A region file could not be created inside
    /// <paramref name="outputDirectory"/>. <para>无法在 <paramref name="outputDirectory"/> 内创建区域文件。</para></exception>
    public async Task<IReadOnlyList<SaharaMemoryRegion>> CollectMemoryDumpAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ThrowIfDisposed();

        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var regions = await ReadMemoryDebugRegionsAsync(cancellationToken).ConfigureAwait(false);
        var written = new List<SaharaMemoryRegion>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var region in regions)
            {
                if (region.Length == 0)
                {
                    Log($"Skipping a zero sized Sahara region at 0x{region.Address:X}.");
                    continue;
                }

                if (!IsSafeMemoryDumpFileName(region.FileName))
                {
                    throw new SaharaException($"The target proposed an unsafe region file name: '{region.FileName}'.");
                }

                if (!usedNames.Add(region.FileName))
                {
                    throw new SaharaException($"Two Sahara regions propose the file name '{region.FileName}'.");
                }

                await DumpMemoryRegionAsync(region, Path.Combine(root, region.FileName), cancellationToken).ConfigureAwait(false);
                written.Add(region);
            }

            await SendResetOwnedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TrySendResetOwnedAsync().ConfigureAwait(false);
            throw;
        }

        Log($"Sahara memory dump finished: {written.Count} region(s) written to '{root}'.");
        return written;
    }

    /// <summary>
    /// Validates a file name proposed by the target, refusing anything that could escape the output
    /// directory or collide with a Windows device namespace.
    /// <para>校验目标提出的文件名，拒绝任何可能逃出输出目录或与 Windows 设备命名空间冲突的名称。</para>
    /// Leading separators are stripped, <c>..</c> is refused before opening a dump file, and a Windows
    /// reserved-name check applies.
    /// <para>打开转储文件前会剥掉前导分隔符并拒绝 <c>..</c>，并附带 Windows 保留名检查。</para>
    /// </summary>
    /// <param name="fileName">The candidate name from the target's debug table.
    /// <para>来自目标调试表的候选名称。</para></param>
    /// <returns><see langword="true"/> when the name may be combined with an output directory.
    /// <para>该名称可以与输出目录组合时为 <see langword="true"/>。</para></returns>
    public static bool IsSafeMemoryDumpFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName is "." or ".." || fileName.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(fileName) || fileName.Contains('/') || fileName.Contains('\\')
            || fileName.EndsWith(' ') || fileName.EndsWith('.'))
        {
            return false;
        }

        foreach (var character in fileName)
        {
            if (character is < ' ' or > '~')
            {
                return false;
            }
        }

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return false;
        }

        return !IsReservedWindowsStem(fileName);
    }

    /// <summary>
    /// Sends one MEMORY_READ for the addressing width established by the table read, falling back to
    /// the width that can represent the request when no table has been read yet.
    /// <para>按读取区域表时确定的地址宽度发送一次 MEMORY_READ；尚未读表时回退到能够表示该请求的宽度。</para>
    /// </summary>
    /// <param name="address">The target address. <para>目标地址。</para></param>
    /// <param name="length">The byte count to request. <para>要请求的字节数。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    private async Task SendMemoryReadAsync(ulong address, ulong length, CancellationToken cancellationToken)
    {
        var extended = _memoryDebugUsesExtendedAddressing
            ?? (address > uint.MaxValue || length > uint.MaxValue);

        if (extended)
        {
            await WriteStructAsync(new SaharaMemoryRead64Packet
            {
                Command = SaharaCommand.MemoryRead64,
                Length = (uint)Marshal.SizeOf<SaharaMemoryRead64Packet>(),
                MemoryAddress = address,
                MemoryLength = length
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteStructAsync(new SaharaMemoryReadPacket
        {
            Command = SaharaCommand.MemoryRead,
            Length = (uint)Marshal.SizeOf<SaharaMemoryReadPacket>(),
            MemoryAddress = (uint)address,
            MemoryLength = (uint)length
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> unframed target bytes into
    /// <paramref name="destination"/> through a pooled buffer.
    /// <para>通过池化缓冲区把恰好 <paramref name="length"/> 个无帧目标字节复制到
    /// <paramref name="destination"/>。</para>
    /// </summary>
    /// <param name="length">The number of bytes the target will send. <para>目标将要发送的字节数。</para></param>
    /// <param name="destination">The writable sink. <para>可写的接收端。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    private async Task ReceiveMemoryBytesAsync(long length, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MemoryDumpStreamBufferSize);
        try
        {
            var received = 0L;
            while (received < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = (int)Math.Min(buffer.Length, length - received);
                var read = await _transport.ReadAsync(buffer, 0, request, _timeoutMs, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new SaharaException($"The target returned only {received} of {length} memory-debug bytes.");
                }

                if (read > request)
                {
                    throw new SaharaException("The transport returned an invalid receive length for a memory dump.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Parses the debug region table into records at the field offsets the protocol header defines
    /// for the two record widths.
    /// <para>按协议头为两种记录宽度所定义的字段偏移，把调试区域表解析为记录。</para>
    /// </summary>
    /// <param name="table">The raw table bytes. <para>原始表字节。</para></param>
    /// <param name="extended">Whether the table uses the 64-byte record layout. <para>表是否采用 64 字节记录布局。</para></param>
    /// <returns>The parsed regions. <para>解析得到的区域列表。</para></returns>
    private static List<SaharaMemoryRegion> ParseMemoryRegionTable(byte[] table, bool extended)
    {
        var recordSize = extended ? EdlConstants.SaharaMemoryRecordBytes64 : EdlConstants.SaharaMemoryRecordBytesLegacy;
        var stringSize = EdlConstants.SaharaMemoryRecordStringBytes;
        var regions = new List<SaharaMemoryRegion>(table.Length / recordSize);

        for (var offset = 0; offset + recordSize <= table.Length; offset += recordSize)
        {
            var record = table.AsSpan(offset, recordSize);
            if (extended)
            {
                regions.Add(new SaharaMemoryRegion
                {
                    SavePreference = BinaryPrimitives.ReadUInt64LittleEndian(record),
                    Address = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
                    Length = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]),
                    Description = ReadFixedAnsiString(record.Slice(24, stringSize)),
                    FileName = ReadFixedAnsiString(record.Slice(24 + stringSize, stringSize))
                });
                continue;
            }

            regions.Add(new SaharaMemoryRegion
            {
                SavePreference = BinaryPrimitives.ReadUInt32LittleEndian(record),
                Address = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
                Length = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]),
                Description = ReadFixedAnsiString(record.Slice(12, stringSize)),
                FileName = ReadFixedAnsiString(record.Slice(12 + stringSize, stringSize))
            });
        }

        return regions;
    }

    /// <summary>
    /// Decodes a fixed-width NUL terminated ASCII field.
    /// <para>解码一个定宽、以 NUL 结尾的 ASCII 字段。</para>
    /// </summary>
    /// <param name="field">The field bytes. <para>字段字节。</para></param>
    /// <returns>The decoded text up to the first NUL. <para>截至首个 NUL 的解码文本。</para></returns>
    private static string ReadFixedAnsiString(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        return terminator >= 0
            ? Encoding.ASCII.GetString(field[..terminator])
            : Encoding.ASCII.GetString(field);
    }

    /// <summary>
    /// Leaves Sahara memory-debug mode by sending RESET and waiting for the RESET_RESPONSE, so a caller
    /// that collected the region bytes itself still ends the link cleanly.
    /// <para>通过发送 RESET 并等待 RESET_RESPONSE 离开 Sahara 内存调试模式，使自行收集区域字节的调用方
    /// 也能干净地结束链路。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token checked while writing and reading.
    /// <para>在写出与读取期间检查的取消令牌。</para></param>
    /// <exception cref="ObjectDisposedException">The protocol instance has been disposed.
    /// <para>协议实例已被释放。</para></exception>
    /// <exception cref="SaharaException">The target did not answer the RESET with a RESET_RESPONSE.
    /// <para>目标没有以 RESET_RESPONSE 应答这次 RESET。</para></exception>
    public async Task ResetMemoryDebugLinkAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await SendResetOwnedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort RESET for the failure path of a caller-owned collection: the write is attempted and
    /// every completion failure is logged and swallowed so the original cause survives.
    /// <para>为调用方自持的收集失败路径提供尽力而为的 RESET：仍会尝试写出，但任何完成失败都只记录并被吞掉，
    /// 使原始原因得以保留。</para>
    /// </summary>
    public async Task TryResetMemoryDebugLinkAsync()
    {
        if (_disposed)
        {
            return;
        }

        await TrySendResetOwnedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sends Sahara RESET and waits for the matching RESET_RESPONSE to leave memory-debug mode.
    /// <para>发送 Sahara RESET 并等待对应的 RESET_RESPONSE，以离开内存调试模式。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    private async Task SendResetOwnedAsync(CancellationToken cancellationToken)
    {
        await WriteStructAsync(new SaharaResetPacket
        {
            Command = SaharaCommand.Reset,
            Length = (uint)Marshal.SizeOf<SaharaResetPacket>()
        }, cancellationToken).ConfigureAwait(false);

        var headerBytes = await ReadExactAsync(
            Marshal.SizeOf<SaharaPacketHeader>(), _timeoutMs, cancellationToken).ConfigureAwait(false);
        var header = BytesToStruct<SaharaPacketHeader>(headerBytes);
        ValidateCommand(header.Command, SaharaCommand.ResetResponse, header.Length, (uint)Marshal.SizeOf<SaharaPacketHeader>());
        Log("Sahara RESET_RESPONSE received; the target left memory-debug mode.");
    }

    /// <summary>
    /// Best-effort RESET after a failed collection. Every cleanup failure is logged and swallowed so
    /// the caller still sees the original cause.
    /// <para>收集失败后的尽力 RESET。所有清理失败都只记录并被吞掉，使调用方仍能看到原始原因。</para>
    /// </summary>
    private async Task TrySendResetOwnedAsync()
    {
        try
        {
            await WriteStructAsync(new SaharaResetPacket
            {
                Command = SaharaCommand.Reset,
                Length = (uint)Marshal.SizeOf<SaharaResetPacket>()
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"The best-effort Sahara reset after a failed memory dump did not complete: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a partial dump file, ignoring failures because the original error is what the caller
    /// must see.
    /// <para>删除残留的部分转储文件，并忽略失败，因为调用方必须看到的是原始错误。</para>
    /// </summary>
    /// <param name="partialPath">The partial file path. <para>部分文件路径。</para></param>
    private void TryDeletePartialFile(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
        catch (Exception ex)
        {
            Log($"Removing the partial Sahara dump file '{partialPath}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks the Windows device-name stems a dump file may not use.
    /// <para>检查转储文件名不得使用的 Windows 设备名词干。</para>
    /// </summary>
    /// <param name="fileName">The candidate name. <para>候选名称。</para></param>
    /// <returns><see langword="true"/> when the stem is reserved. <para>词干属于保留名时为 <see langword="true"/>。</para></returns>
    private static bool IsReservedWindowsStem(string fileName)
    {
        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        var stem = dot >= 0 ? fileName[..dot] : fileName;
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && stem[3] is >= '1' and <= '9')
            || (stem.Length == 4 && stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) && stem[3] is >= '1' and <= '9');
    }
}
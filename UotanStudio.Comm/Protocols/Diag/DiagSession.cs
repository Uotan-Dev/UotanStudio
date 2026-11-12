using System.Text;
using UotanStudio.Comm.Protocols.Edl.Streaming;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Firmware.Radio.Qcn;

namespace UotanStudio.Comm.Protocols.Diag;

/// <summary>
/// One numbered NV item exchanged over a DIAG session: the item id and its data bytes.
/// <para>经 DIAG 会话交换的一条编号 NV 条目：条目号与数据字节。</para>
/// </summary>
/// <param name="Id">The NV item id. <para>NV 条目号。</para></param>
/// <param name="Data">The item data, at most 128 bytes. <para>条目数据，至多 128 字节。</para></param>
public sealed record DiagNvItem(ushort Id, byte[] Data)
{
    /// <summary>
    /// Validates the data length; used at every exchange boundary.
    /// <para>校验数据长度；每个交换边界都会调用。</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The data exceeds the NV payload limit.
    /// <para>数据超出 NV 负载上限。</para></exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Data.Length, DiagConstants.MaxNvDataBytes);
    }
}

/// <summary>
/// A read-only session over a DIAG transport: sends framed requests and awaits the matching reply.
/// The caller owns the transport; the session never disposes it.
/// <para>基于 DIAG 传输的只读会话：发送封帧请求并等待匹配应答。调用方持有传输；会话绝不释放它。</para>
/// </summary>
public sealed partial class DiagSession(ICommTransport transport, int? timeoutMs = null) : IDisposable
{
    private readonly ICommTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly int _timeoutMs = timeoutMs ?? DiagConstants.DefaultTimeoutMs;
    private readonly Lock _wireGate = new();
    private bool _disposed;

    /// <summary>Bound on bytes received without a single complete frame before the port is declared to not be
    /// speaking the framed DIAG protocol. <para>收到无完整帧字节的上限；超过即判定端口未按封帧 DIAG 协议通信。</para></summary>
    private const int MaximumUnframedReceptionBytes = 256 * 1024;

    /// <summary>
    /// Sends one DIAG payload and returns the reply payload starting at the command byte.
    /// <para>发送一个 DIAG 负载并返回从命令字节开始的应答负载。</para>
    /// </summary>
    /// <param name="request">The request payload starting at the command byte.
    /// <para>以命令字节开始的请求负载。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The reply payload starting at the command byte.
    /// <para>以命令字节开始的应答负载。</para></returns>
    public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var frame = DiagHdlcCodec.Encode(request, withCrc: true);
        var written = await _transport.WriteAsync(frame, 0, frame.Length, _timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (written != frame.Length)
        {
            throw new DiagException("The transport short-wrote a DIAG request.");
        }

        return await ReceiveReplyAsync(request[0], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives the reply frame whose command byte matches the request command or its response variant (request plus
    /// one, the convention other DIAG stacks use), scanning the raw wire for frames terminated by the trailing
    /// <c>0x7E</c> flag and discarding every other frame (for example the log frames a modem streams continuously on
    /// the DIAG port) until the matching reply or the deadline arrives.
    /// <para>接收命令字节与请求命令或其应答变体（请求加一，其他 DIAG 栈采用的惯例）匹配的应答帧：在原始线上按
    /// 尾部 <c>0x7E</c> 界符扫描完整帧并丢弃其余帧（例如调制解调器在 DIAG 端口上持续外发的日志帧），直到匹配的
    /// 应答或期限到达。</para>
    /// </summary>
    /// <param name="requestCommand">The request's command byte. <para>请求的命令字节。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The reply payload starting at the command byte.
    /// <para>以命令字节开始的应答负载。</para></returns>
    /// <exception cref="DiagException">The deadline expired before a matching frame arrived, or the wire flooded with
    /// unframed bytes. <para>期限内在匹配帧到达前耗尽，或线路被无帧字节灌满。</para></exception>
    private async Task<byte[]> ReceiveReplyAsync(byte requestCommand, CancellationToken cancellationToken)
    {
        var responseVariant = (byte)(requestCommand + 1);
        var buffer = new byte[4096];
        var received = new List<byte>();
        var discarded = 0;
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < _timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = _timeoutMs - (int)(Environment.TickCount64 - started);
            int n;
            try
            {
                n = await _transport.ReadAsync(buffer, 0, buffer.Length, Math.Max(1, remaining), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                break;
            }

            if (n > 0)
            {
                received.AddRange(buffer.Take(n));
            }

            var consumed = 0;
            while (true)
            {
                var close = received.IndexOf(DiagHdlcCodec.FrameFlag, consumed);
                if (close < 0)
                {
                    break;
                }

                var segment = received.GetRange(consumed, close - consumed + 1).ToArray();
                consumed = close + 1;
                if (segment.Length <= 1)
                {
                    continue;
                }

                if (DiagHdlcCodec.TryDecode(segment, withCrc: true, verifyCrc: false, out var payload)
                    && payload is not null && payload.Length > 0
                    && (payload[0] == requestCommand || payload[0] == responseVariant))
                {
                    return payload;
                }

                discarded++;
            }

            if (consumed > 0)
            {
                received.RemoveRange(0, consumed);
            }

            if (received.Count > MaximumUnframedReceptionBytes)
            {
                throw new DiagException(
                    $"The DIAG wire flooded with {received.Count} unframed byte(s); the port is not speaking the framed DIAG protocol.");
            }
        }

        throw new DiagException(
            $"The DIAG reply deadline expired before a frame with command byte 0x{requestCommand:X2} or 0x{responseVariant:X2} arrived; {discarded} other frame(s) were discarded.");
    }

    /// <summary>
    /// Reads one numbered NV item.
    /// <para>读取一条编号 NV 条目。</para>
    /// </summary>
    /// <param name="itemId">The item id. <para>条目号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The item with its data. <para>携带数据的条目。</para></returns>
    /// <exception cref="DiagException">The device rejected the read (non-zero status).
    /// <para>设备拒绝读取（状态码非零）。</para></exception>
    public async Task<DiagNvItem> ReadNvItemAsync(ushort itemId, CancellationToken cancellationToken = default)
    {
        byte[] request = [DiagConstants.NvReadRequest, .. DiagNvPacket.EncodeReadRequest(itemId)];
        var reply = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Length < 1 || reply[0] is not (DiagConstants.NvReadRequest or DiagConstants.NvReadResponse))
        {
            throw new DiagException("The reply to the NV read request carried an unexpected command byte.");
        }

        var (echo, data, status) = DiagNvPacket.DecodeReadResponse(reply.AsSpan(1));
        if (status != 0)
        {
            throw new DiagException($"The device rejected the NV read of item {itemId} with status {status}.");
        }

        if (echo != itemId)
        {
            throw new DiagException($"The NV read reply echoed item {echo} instead of {itemId}.");
        }

        return new DiagNvItem(itemId, data);
    }

    /// <summary>
    /// Writes one numbered NV item. This changes device radio configuration and must only be called
    /// after the caller has confirmed the write.
    /// <para>写入一条编号 NV 条目。这会改变设备射频配置，只能在调用方确认写入后调用。</para>
    /// </summary>
    /// <param name="item">The item to write. <para>待写入条目。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the write.
    /// <para>传 <see langword="true"/> 确认写入。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="ArgumentException">The confirmation was not given.
    /// <para>未给出确认。</para></exception>
    /// <exception cref="DiagException">The device rejected the write.
    /// <para>设备拒绝写入。</para></exception>
    public async Task WriteNvItemAsync(DiagNvItem item, bool confirm, CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            throw new ArgumentException("Writing an NV item requires explicit confirmation.", nameof(confirm));
        }

        item.Validate();
        byte[] request = [DiagConstants.NvWriteRequest, .. DiagNvPacket.EncodeWriteRequest(item.Id, item.Data)];
        var reply = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Length < 1 || reply[0] is not (DiagConstants.NvWriteRequest or DiagConstants.NvWriteResponse))
        {
            throw new DiagException("The reply to the NV write request carried an unexpected command byte.");
        }

        var (echo, status) = DiagNvPacket.DecodeWriteResponse(reply.AsSpan(1));
        if (status != 0)
        {
            throw new DiagException($"The device rejected the NV write of item {item.Id} with status {status}.");
        }

        if (echo != item.Id)
        {
            throw new DiagException($"The NV write reply echoed item {echo} instead of {item.Id}.");
        }
    }

    /// <summary>
    /// Unlocks the device for protected operations by writing the six-digit SPC to the SPC NV item.
    /// The caller is responsible for being authorized to change the radio configuration.
    /// <para>把六位 SPC 写入 SPC NV 条目以解锁受保护操作。调用方自行负责射频配置变更的授权。</para>
    /// </summary>
    /// <param name="spc">Exactly six ASCII digits. <para>恰好六位 ASCII 数字。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the unlock.
    /// <para>传 <see langword="true"/> 确认解锁。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="ArgumentException">The code is not six digits or the confirmation is missing.
    /// <para>码不是六位数字或缺少确认。</para></exception>
    public async Task SendSpcAsync(string spc, bool confirm, CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            throw new ArgumentException("Sending the SPC requires explicit confirmation.", nameof(confirm));
        }

        if (spc.Length != 6 || !spc.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("The SPC must be exactly six ASCII digits.", nameof(spc));
        }

        var data = Encoding.ASCII.GetBytes(spc);
        await WriteNvItemAsync(new DiagNvItem(DiagConstants.NvSpcItemId, data), confirm: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Backs up the numbered NV items with the given ids into a QCN file. Items the device
    /// refuses are skipped and reported.
    /// <para>把给定条目号的编号 NV 条目备份到 QCN 文件。设备拒绝的条目被跳过并上报。</para>
    /// </summary>
    /// <param name="path">Destination QCN file path. <para>目标 QCN 文件路径。</para></param>
    /// <param name="itemIds">The item ids to read, in order. <para>按顺序读取的条目号。</para></param>
    /// <param name="progress">Optional completed-count progress. <para>可选的完成数进度。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The item ids the device refused. <para>设备拒绝的条目号。</para></returns>
    public async Task<IReadOnlyList<ushort>> BackupQcnAsync(string path, IReadOnlyList<ushort> itemIds,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(itemIds);
        var items = new List<DiagNvItem>(itemIds.Count);
        var refused = new List<ushort>();
        var completed = 0;
        foreach (var id in itemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                items.Add(await ReadNvItemAsync(id, cancellationToken).ConfigureAwait(false));
            }
            catch (DiagException)
            {
                refused.Add(id);
            }
            finally
            {
                progress?.Report(++completed);
            }
        }

        if (items.Count == 0)
        {
            throw new DiagException("Every template item was refused; no QCN was built.");
        }

        var records = items
            .Select((item, ordinal) => CreateRecord(ordinal, item))
            .ToList();
        await QcnBuilder.BuildAsync(path, records, cancellationToken: cancellationToken).ConfigureAwait(false);
        return refused;
    }

    private static QcnNvItem CreateRecord(int ordinal, DiagNvItem item)
    {
        return QcnNvItem.Create(ordinal, item.Id, flags: 0, item.Data, "NV_NUMBERED_ITEMS/NV_ITEM_ARRAY", ordinal);
    }

    /// <summary>
    /// Restores the numbered NV records of a QCN file onto the device. Every restored item is written
    /// through the confirmed NV write path; refused items are reported.
    /// <para>把 QCN 文件的编号 NV 记录恢复到设备。每条恢复的条目都经确认的 NV 写入路径写入；
    /// 被拒绝的条目会被上报。</para>
    /// </summary>
    /// <param name="path">The QCN file to restore. <para>待恢复的 QCN 文件。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the restore.
    /// <para>传 <see langword="true"/> 确认恢复。</para></param>
    /// <param name="progress">Optional completed-count progress. <para>可选的完成数进度。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The item ids the device refused. <para>设备拒绝的条目号。</para></returns>
    public async Task<IReadOnlyList<ushort>> RestoreQcnAsync(string path, bool confirm,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            throw new ArgumentException("Restoring a QCN requires explicit confirmation.", nameof(confirm));
        }

        var document = await QcnReader.ParseAsync(path, cancellationToken).ConfigureAwait(false);
        var refused = new List<ushort>();
        var completed = 0;
        foreach (var record in document.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effective = record.EffectiveLength;
            if (effective == 0)
            {
                progress?.Report(++completed);
                continue;
            }

            try
            {
                await WriteNvItemAsync(new DiagNvItem(record.Id, record.Data[..effective]), confirm: true,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DiagException)
            {
                refused.Add(record.Id);
            }
            finally
            {
                progress?.Report(++completed);
            }
        }

        return refused;
    }

    /// <summary>
    /// Sends one EFS2 subsystem operation and returns its body after the result header.
    /// <para>发送一个 EFS2 子系统操作并返回结果头之后的操作体。</para>
    /// </summary>
    /// <param name="opcode">The EFS2 opcode. <para>EFS2 操作码。</para></param>
    /// <param name="body">The operation body after the dispatch header.
    /// <para>分发头之后的操作体。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The reply body after the 4-byte result header.
    /// <para>4 字节结果头之后的应答体。</para></returns>
    /// <exception cref="DiagException">The reply was not a dispatch response for the EFS2 subsystem
    /// or reported an error. <para>应答不是 EFS2 子系统的分发响应或报告了错误。</para></exception>
    public async Task<byte[]> SendEfs2Async(ushort opcode, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var request = DiagSubsysPacket.EncodeRequest(DiagConstants.Efs2SubsystemId, opcode, body.Span);
        var reply = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        var (subsystem, echo, replyBody) = DiagSubsysPacket.DecodeResponse(reply);
        if (subsystem != DiagConstants.Efs2SubsystemId || echo != opcode)
        {
            throw new DiagException($"The EFS2 reply echoed subsystem {subsystem} opcode {echo}.");
        }

        var header = Efs2ResultHeader.Decode(replyBody);
        if (!header.Succeeded)
        {
            throw new DiagException($"The EFS2 operation {opcode} failed with error {header.Error}.");
        }

        return replyBody[4..];
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DiagSession));
        }
    }

    /// <summary>
    /// Marks the session disposed without touching the transport.
    /// <para>标记会话已释放且不触碰传输。</para>
    /// </summary>
    public void Dispose() => _disposed = true;
}
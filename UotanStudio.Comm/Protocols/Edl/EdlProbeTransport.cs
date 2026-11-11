using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Retains inspected bytes for the subsequent protocol handshake.
/// <para>保留检测读取的字节，供后续协议握手继续消费。</para>
/// </summary>
/// <remarks>
/// The probe never writes a Sahara HELLO_RESPONSE, because the handshake that owns Sahara negotiation is the only
/// component that can consume the target state an unsolicited HELLO response would create. When the buffered
/// prefix holds no evidence it writes at most one Sahara RESET_STATE_MACHINE packet per session, which makes a
/// silent bootrom target re-announce HELLO and only produces a parser log on a target that already runs Firehose;
/// repeating the wake would only hand a target that is mid-transfer another command its state rejects. It also
/// writes at most one Firehose NOP: unconditionally on a transport that cannot silently lose the bootrom HELLO, and
/// on a serial transport only after the target has already produced a complete Firehose XML document, so a live
/// Sahara target is never handed XML it would parse as an unknown command. Emitting Firehose XML at all proves the
/// target is not a bootrom waiting for HELLO, because such a bootrom only sends binary HELLO or nothing.
/// <para>探测从不写入 Sahara HELLO_RESPONSE，因为只有负责 Sahara 协商的握手层才能消费主动 HELLO 应答造成的目标状态。
/// 当缓冲前缀不含任何证据时，它每个会话最多写入一条 Sahara RESET_STATE_MACHINE，使静默的 bootrom 目标重新宣告
/// HELLO，而在已运行 Firehose 的目标上只会留下一条解析日志；重复唤醒只会给正在传输的目标再送一条它当前状态会
/// 拒绝的命令。它还会写入至多一条 Firehose NOP：在不会静默丢掉 bootrom HELLO 的传输上无条件写入，在串口上则
/// 只在目标已经吐出一份完整的 Firehose XML 文档之后写入，因此处于 Sahara 的目标绝不会收到会被解析为未知命令的
/// XML。只要输出 Firehose XML 就证明目标不是等待 HELLO 的 bootrom（这种 bootrom 只会发二进制 HELLO 或什么都不发）。</para>
/// </remarks>
internal sealed class EdlProbeTransport(ICommTransport inner) : ICommTransport
{
    private const int MaximumProbeBytes = 64 * 1024;
    private const int MaximumProbeMilliseconds = EdlConstants.ProbeReadBudgetMs;
    private const int ProbeReadSliceMilliseconds = 500;

    /// <summary>
    /// Width of one probe read. It is deliberately wider than the 48-byte HELLO it usually receives: a bootrom can
    /// queue a second announcement of the same handshake state, and a request narrower than the queued burst makes the
    /// host controller abort the transfer as a babble, which libusb reports as <c>LIBUSB_ERROR_OVERFLOW</c> and the
    /// transport raises as a failed transfer. The probe buffer keeps every byte read, so a wider read only ever adds
    /// bytes to classify rather than losing them.
    /// <para>单次探测读取的宽度。它刻意宽于通常收到的 48 字节 HELLO：bootrom 可能排下第二份同一握手状态的宣告，而比这一批
    /// 更窄的请求会让主机以 babble 中止传输，libusb 将其报告为 <c>LIBUSB_ERROR_OVERFLOW</c>，传输层再把它抛成传输失败。
    /// 探测缓冲区保留读到的每个字节，因此更宽的读取只会增加待分类的字节，而不会丢失字节。</para>
    /// </summary>
    private const int ProbeReadChunkBytes = 1024;

    private const int MaximumIdleReads = 64;
    private const int FirehoseSilenceGraceMilliseconds = 1000;
    private const int SaharaResetStatePacketBytes = 8;

    /// <summary>
    /// How long a buffered probe HELLO stays worth replaying. A bootrom answers the host once and then waits for the
    /// reply, so an answer this old belongs to a target that has long stopped waiting for it; replaying it makes the
    /// host answer something the target no longer holds a slot for.
    /// <para>缓冲的探测 HELLO 保持可用于回放的时长。bootrom 只应答主机一次然后等待回复，因此这么旧的应答属于
    /// 早已不再等待它的目标；回放它会让主机去应答目标已不再预留位置的东西。</para>
    /// </summary>
    private const int DefaultBufferedEvidenceMaxAgeMs = 3000;

    private byte[]? _buffer;
    private int _length;
    private int _position;
    private bool _wakeAttempted;

    /// <summary>
    /// Timestamp of the last byte appended to the probe buffer, so buffered protocol evidence can be aged out.
    /// <para>探测缓冲区最后一次追加字节的时间戳，使缓冲的协议证据可以被判定为过期。</para>
    /// </summary>
    private long _bufferedAtMilliseconds;

    /// <summary>
    /// Gets or sets how long buffered protocol evidence stays replayable; defaults to
    /// <see cref="DefaultBufferedEvidenceMaxAgeMs"/> and exists so tests can shrink the window.
    /// <para>获取或设置缓冲的协议证据保持可回放的时长；默认为
    /// <see cref="DefaultBufferedEvidenceMaxAgeMs"/>，存在是为了让测试能缩短该窗口。</para>
    /// </summary>
    internal int BufferedEvidenceMaxAgeMs { get; set; } = DefaultBufferedEvidenceMaxAgeMs;

    /// <summary>
    /// Classifies the endpoint by reading what the target sends, waking a silent bootrom target with one Sahara
    /// RESET_STATE_MACHINE packet, then probing Firehose once: on a transport that cannot lose the bootrom HELLO
    /// as soon as the target stays silent, and on a serial transport only once the target has already produced a
    /// complete Firehose XML document.
    /// <para>通过读取目标发出的数据分类端点；对静默的 bootrom 目标先写入一条 Sahara RESET_STATE_MACHINE 唤醒，
    /// 再探测一次 Firehose：在不会丢失 bootrom HELLO 的传输上只要目标静默即可，在串口上则要等目标已经吐出一份
    /// 完整的 Firehose XML 文档之后。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels bounded protocol reads.<para>取消有界的协议读取。</para></param>
    /// <returns>The verified protocol, or Unknown when no bounded evidence arrived in the probe budget.
    /// <para>经校验的协议；探测预算内没有出现有界证据时为 Unknown。</para></returns>
    internal async Task<EdlProtocolState> ProbeAsync(CancellationToken cancellationToken)
    {
        _buffer ??= new byte[MaximumProbeBytes];
        Compact();
        var started = Environment.TickCount64;
        var idleReads = 0;
        var wakeSent = false;
        var readPendingAfterWake = false;
        var nopSent = false;
        var nopSentAtMilliseconds = 0L;
        var nopAllowed = Inner is not SerialTransport;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffered = _buffer.AsSpan(0, _length);
            var state = Classify(buffered);
            if (state == EdlProtocolState.Firehose)
            {
                if (wakeSent)
                {
                    CommTrace.Write(CommLogLevel.Debug, "edl",
                        "EDL probe read Firehose evidence after writing Sahara RESET_STATE_MACHINE.");
                }
                _length = 0;
                _position = 0;
                return state;
            }
            if (state != EdlProtocolState.Unknown)
            {
                if (wakeSent)
                {
                    CommTrace.Write(CommLogLevel.Debug, "edl",
                        $"EDL probe read {state} evidence after writing Sahara RESET_STATE_MACHINE.");
                }
                return state;
            }

            if (_length == MaximumProbeBytes || idleReads >= MaximumIdleReads)
            {
                break;
            }
            var elapsed = Environment.TickCount64 - started;
            if (elapsed >= MaximumProbeMilliseconds)
            {
                break;
            }
            var pendingDocument = HasPendingDocument(buffered);
            if (nopSent && !pendingDocument
                && Environment.TickCount64 - nopSentAtMilliseconds >= FirehoseSilenceGraceMilliseconds)
            {
                break;
            }
            var canWake = !wakeSent && !_wakeAttempted && !pendingDocument && HasNoProtocolEvidence(buffered);
            var canProbe = !nopSent && !readPendingAfterWake && !pendingDocument
                && AcceptsFirehoseProbe(buffered)
                && (nopAllowed || HasFirehoseDocumentEvidence(buffered));

            var remaining = (int)Math.Max(1, MaximumProbeMilliseconds - elapsed);
            var chunk = Math.Min(ProbeReadChunkBytes, MaximumProbeBytes - _length);
            int count;
            try
            {
                count = await inner.ReadAsync(_buffer, _length, chunk,
                    Math.Min(ProbeReadSliceMilliseconds, remaining), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                count = 0;
            }
            catch (EdlException ex) when (ex.ErrorCode is EdlErrorCode.ReadPortTimeout or EdlErrorCode.TransportTimeout)
            {
                count = 0;
            }
            if (count is < 0 || count > chunk)
            {
                throw new IOException("Invalid EDL probe receive length.");
            }
            if (count != 0)
            {
                _length += count;
                _bufferedAtMilliseconds = Environment.TickCount64;
                readPendingAfterWake = false;
                idleReads = 0;
                continue;
            }

            idleReads++;
            readPendingAfterWake = false;
            if (canWake)
            {
                _wakeAttempted = true;
                if (!await TryWriteSaharaWakeAsync(remaining, elapsed, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
                wakeSent = true;
                readPendingAfterWake = true;
                idleReads = 0;
                continue;
            }
            if (canProbe)
            {
                var nop = EdlConstants.NopProbeFrame;
                cancellationToken.ThrowIfCancellationRequested();
                if (await inner.WriteAsync(nop, 0, nop.Length, remaining, cancellationToken).ConfigureAwait(false) != nop.Length)
                {
                    throw new IOException("Incomplete EDL protocol probe write.");
                }
                nopSent = true;
                nopSentAtMilliseconds = Environment.TickCount64;
                idleReads = 0;
            }
        }

        if (wakeSent)
        {
            CommTrace.Write(CommLogLevel.Debug, "edl",
                $"EDL probe wrote Sahara RESET_STATE_MACHINE but observed no bounded Sahara or Firehose evidence within {MaximumProbeMilliseconds} ms.");
        }
        return EdlProtocolState.Unknown;
    }

    /// <summary>
    /// Writes one Sahara RESET_STATE_MACHINE packet so a silent bootrom target re-announces HELLO, and reports
    /// whether the write completed; every transport failure is logged and swallowed because the probe must
    /// still return Unknown instead of surfacing a write error.
    /// <para>写入一条 Sahara RESET_STATE_MACHINE 数据包，使静默的 bootrom 目标重新宣告 HELLO，并报告写入是否完成；
    /// 所有传输失败都只记录并被吞掉，因为探测仍须返回 Unknown 而不是抛出写入错误。</para>
    /// </summary>
    /// <param name="timeoutMs">Upper bound for the write in milliseconds.
    /// <para>写入的毫秒上限。</para></param>
    /// <param name="elapsedMilliseconds">Silence already observed before the write, used in diagnostics.
    /// <para>写入前已观测到的静默时长，用于诊断日志。</para></param>
    /// <param name="cancellationToken">Cancels the write before any byte is sent.
    /// <para>在发送任何字节前取消写入。</para></param>
    /// <returns><see langword="true"/> when the packet was written completely.
    /// <para>数据包完整写入时为 true。</para></returns>
    private async Task<bool> TryWriteSaharaWakeAsync(int timeoutMs, long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        var packet = new byte[SaharaResetStatePacketBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)SaharaCommand.ResetState);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var written = await inner.WriteAsync(packet, 0, packet.Length, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (written != packet.Length)
            {
                throw new IOException("Incomplete Sahara RESET_STATE_MACHINE write.");
            }
            CommTrace.Write(CommLogLevel.Debug, "edl",
                $"EDL probe wrote a Sahara RESET_STATE_MACHINE packet (cmd=0x13, {packet.Length} bytes) after {elapsedMilliseconds} ms of silence.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CommTrace.Write(CommLogLevel.Debug, "edl",
                $"EDL probe Sahara RESET_STATE_MACHINE wake failed after {elapsedMilliseconds} ms of silence: {ex.Message}");
            return false;
        }
    }

    private void Compact()
    {
        if (_position <= 0)
        {
            return;
        }

        var pending = _length - _position;
        if (pending > 0)
        {
            _buffer!.AsSpan(_position, pending).CopyTo(_buffer);
        }
        _length = pending;
        _position = 0;
    }

    /// <summary>
    /// Reports whether the buffered bytes are a Firehose document that has started but not finished, so the
    /// probe keeps waiting instead of discarding a target that is still writing its startup block.
    /// <para>报告缓冲字节是否为已开始但未结束的 Firehose 文档，使探测继续等待，而不是丢弃仍在输出启动块的
    /// 目标。</para>
    /// </summary>
    /// <param name="bytes">Buffered probe bytes.<para>已缓冲的探测字节。</para></param>
    /// <returns><see langword="true"/> when an opening tag arrived without its closing tag.
    /// <para>已出现开标签但缺少闭标签时为 true。</para></returns>
    private static bool HasPendingDocument(ReadOnlySpan<byte> bytes)
    {
        var document = bytes[EdlConstants.MeasureDocumentPrefix(bytes)..];
        return !document.IsEmpty && document[0] == (byte)'<' && document.IndexOf("</data>"u8) < 0;
    }

    private static bool AcceptsFirehoseProbe(ReadOnlySpan<byte> bytes)
        => bytes.IsEmpty
            || bytes[EdlConstants.MeasureDocumentPrefix(bytes)..].IsEmpty
            || ContainsOnlyFirehoseLogs(bytes)
            || HasFirehoseDocumentEvidence(bytes);

    /// <summary>
    /// Reports whether the buffered prefix has already yielded at least one complete Firehose XML document, which
    /// is the evidence a serial transport needs before a NOP is written. Concatenated documents are accepted
    /// because targets emit several documents back to back; only the first opening bracket and one closing
    /// <c>&lt;/data&gt;</c> are required, in that order.
    /// <para>报告缓冲前缀是否已经产出了至少一份完整的 Firehose XML 文档，这是串口在写入 NOP 之前所需的证据。
    /// 允许文档拼接，因为目标会连续输出多份文档；只要求第一个开尖括号与其后至少一个 <c>&lt;/data&gt;</c>。</para>
    /// </summary>
    /// <param name="bytes">Buffered probe bytes.<para>已缓冲的探测字节。</para></param>
    /// <returns><see langword="true"/> when the buffer starts with an opening tag and contains a closing tag.
    /// <para>缓冲以开标签开始并包含闭标签时为 true。</para></returns>
    private static bool HasFirehoseDocumentEvidence(ReadOnlySpan<byte> bytes)
    {
        var document = bytes[EdlConstants.MeasureDocumentPrefix(bytes)..];
        return !document.IsEmpty
            && document[0] == (byte)'<'
            && document.IndexOf("</data>"u8) >= 0;
    }

    /// <summary>
    /// Reports whether the buffered prefix carries no protocol evidence at all, counting packet padding and a
    /// UTF-8 BOM as absence, so the wake is issued only to a target that has said nothing.
    /// <para>报告缓冲前缀是否完全不含协议证据，把包填充和 UTF-8 BOM 视为无证据，从而只对尚未开口的目标
    /// 发出唤醒。</para>
    /// </summary>
    /// <param name="bytes">Buffered probe bytes.<para>已缓冲的探测字节。</para></param>
    /// <returns><see langword="true"/> when nothing but padding arrived.
    /// <para>只到达填充字节时为 true。</para></returns>
    private static bool HasNoProtocolEvidence(ReadOnlySpan<byte> bytes)
        => bytes[EdlConstants.MeasureDocumentPrefix(bytes)..].IsEmpty;

    private static bool ContainsOnlyFirehoseLogs(ReadOnlySpan<byte> bytes)
    {
        var foundLog = false;
        while (!bytes.IsEmpty)
        {
            var pointer = EdlConstants.MeasureDocumentPrefix(bytes);
            bytes = pointer < bytes.Length ? bytes[pointer..] : default;
            if (bytes.IsEmpty)
            {
                break;
            }
            var end = bytes.IndexOf("</data>"u8);
            if (end < 0)
            {
                return false;
            }
            try
            {
                var block = FirehoseResponseParser.ParseDataBlock(Encoding.UTF8.GetString(bytes[..(end + 7)]));
                if (block.Response is not null || block.Logs.Count == 0)
                {
                    return false;
                }
                foundLog = true;
            }
            catch (EdlException)
            {
                return false;
            }
            bytes = bytes[(end + 7)..];
        }
        return foundLog;
    }

    /// <summary>
    /// Passively reads the buffered Sahara HELLO after a successful Sahara classification; the bytes
    /// stay queued for the subsequent handshake.
    /// <para>Sahara 分类成功后被动读取缓冲的 Sahara HELLO；这些字节仍保留在队列中供后续握手使用。</para>
    /// </summary>
    /// <param name="hello">The decoded HELLO packet. <para>解码后的 HELLO 包。</para></param>
    /// <returns><see langword="true"/> when a complete HELLO is buffered.
    /// <para>缓冲中存在完整 HELLO 时为 true。</para></returns>
    internal bool TryReadSaharaHello(out SaharaHelloPacket hello)
    {
        hello = default;
        if (IsBufferedEvidenceStale())
        {
            return false;
        }

        var bytes = _buffer.AsSpan(0, _length);
        if (bytes.Length < Marshal.SizeOf<SaharaHelloPacket>())
        {
            return false;
        }
        var command = (SaharaCommand)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (command != SaharaCommand.Hello || length != 48)
        {
            return false;
        }
        hello = new SaharaHelloPacket
        {
            Command = command,
            Length = length,
            Version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            MinVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            MaxCommandLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            Mode = (SaharaMode)BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
        };
        return true;
    }

    /// <summary>
    /// Reports whether the buffered protocol evidence is too old to replay, discarding it so the next probe has to
    /// wake the target and read an answer the target is actually still waiting for.
    /// <para>报告缓冲的协议证据是否已旧到不可回放，并丢弃它，使下一次探测必须唤醒目标并读取目标确实仍在等待的
    /// 应答。</para>
    /// </summary>
    /// <remarks>
    /// The probe answers nothing, so a bootrom that announced HELLO here holds that slot open until a handshake
    /// replies. When the handshake reaches the buffered HELLO only after the user spent time picking files, the
    /// reply arrives far too late and the target answers the stale slot with a stall. Ageing the evidence out makes
    /// the handshake re-announce instead of answering a slot the target has abandoned.
    /// <para>探测不应答任何内容，因此在此宣告 HELLO 的 bootrom 会一直保留该位置直到握手回复。当用户在挑选文件上
    /// 花掉时间后握手才用到缓冲的 HELLO 时，回复来得太晚，目标会以一个 STALL 应答那个过期位置。把证据判定为过期
    /// 使握手改为重新唤醒，而不是去应答目标早已放弃的位置。</para>
    /// </remarks>
    /// <returns><see langword="true"/> when the evidence was discarded for age.
    /// <para>证据因过旧而被丢弃时为 <see langword="true"/>。</para></returns>
    private bool IsBufferedEvidenceStale()
    {
        if (_length == 0)
        {
            return false;
        }

        if (Environment.TickCount64 - _bufferedAtMilliseconds <= BufferedEvidenceMaxAgeMs)
        {
            return false;
        }

        CommTrace.Write(CommLogLevel.Debug, "edl",
            $"EDL probe dropped {_length} buffered byte(s) older than {BufferedEvidenceMaxAgeMs} ms; the target no " +
            "longer waits for the reply those bytes implied, so it has to be woken again.");
        _length = 0;
        _position = 0;
        _bufferedAtMilliseconds = 0;
        _wakeAttempted = false;
        return true;
    }

    internal static EdlProtocolState Classify(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8)
        {
            var command = (SaharaCommand)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            var expected = command switch
            {
                SaharaCommand.Hello => 48,
                SaharaCommand.ReadData => 20,
                SaharaCommand.EndOfImage => 16,
                SaharaCommand.DoneResponse => 12,
                SaharaCommand.ResetResponse or SaharaCommand.CommandReady => 8,
                SaharaCommand.MemoryDebug or SaharaCommand.ExecuteResponse => 16,
                SaharaCommand.MemoryDebug64 => 24,
                SaharaCommand.ReadData64 => 32,
                _ => 0
            };
            if (expected != 0 && length == expected && bytes.Length >= expected)
            {
                if (command != SaharaCommand.Hello)
                {
                    return EdlProtocolState.Sahara;
                }

                var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
                var minVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
                var mode = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
                if (version == 0 || minVersion > version || mode > (uint)SaharaMode.Command)
                {
                    return EdlProtocolState.Unknown;
                }
                return mode == (uint)SaharaMode.MemoryDebug
                    ? EdlProtocolState.SaharaMemoryDebug
                    : EdlProtocolState.Sahara;
            }
        }

        while (!bytes.IsEmpty)
        {
            var pointer = EdlConstants.MeasureDocumentPrefix(bytes);
            bytes = pointer < bytes.Length ? bytes[pointer..] : default;
            var end = bytes.IndexOf("</data>"u8);
            if (end < 0)
            {
                break;
            }
            try
            {
                var block = FirehoseResponseParser.ParseDataBlock(Encoding.UTF8.GetString(bytes[..(end + 7)]));
                if (block.Response is { } response && (response.IsAck || response.IsNak) && !response.IsRawMode)
                {
                    return EdlProtocolState.Firehose;
                }
            }
            catch (EdlException)
            {
                return EdlProtocolState.Unknown;
            }
            bytes = bytes[(end + 7)..];
        }
        return EdlProtocolState.Unknown;
    }

    /// <summary>
    /// Gets the underlying transport wrapped by this probe transport.
    /// <para>获取本探测传输包装的底层传输。</para>
    /// </summary>
    internal ICommTransport Inner => inner;

    public bool IsConnected => inner.IsConnected;
    public void Connect() => inner.Connect();
    public void Disconnect() => inner.Disconnect();
    public void Reset()
    {
        _length = 0;
        _position = 0;
        _bufferedAtMilliseconds = 0;
        _wakeAttempted = false;
        inner.Reset();
    }

    private int ReadBuffered(byte[] buffer, int offset, int length)
    {
        var count = Math.Min(length, _length - _position);
        if (count > 0)
        {
            _buffer!.AsSpan(_position, count).CopyTo(buffer.AsSpan(offset, count));
            _position += count;
            CommTrace.Write(CommLogLevel.Trace, "edl",
                $"EDL replay served {count} buffered byte(s) aged " +
                $"{Environment.TickCount64 - _bufferedAtMilliseconds} ms to the protocol layer.");
        }
        return count;
    }

    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
        => HasServableBufferedBytes()
            ? ReadBuffered(buffer, offset, length)
            : inner.Read(buffer, offset, length, timeoutMs);

    public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return HasServableBufferedBytes()
            ? Task.FromResult(ReadBuffered(buffer, offset, length))
            : inner.ReadAsync(buffer, offset, length, timeoutMs, cancellationToken);
    }

    /// <summary>
    /// Reports whether buffered bytes may still be replayed to the caller, ageing out evidence the target has
    /// already stopped waiting for.
    /// <para>报告缓冲字节是否仍可回放给调用方；已过期、目标不再等待的证据会被丢弃。</para>
    /// </summary>
    /// <remarks>
    /// This is the seam the handshake reads through, so the age check belongs here as well as in
    /// <see cref="TryReadSaharaHello"/>: serving a buffered HELLO straight from <see cref="ReadBuffered"/> would
    /// answer a slot the bootrom abandoned minutes ago.
    /// <para>这是握手读取所经的接缝，因此时效检查既要放在 <see cref="TryReadSaharaHello"/>，也要放在这里：
    /// 直接从 <see cref="ReadBuffered"/> 回放缓冲的 HELLO 会去应答 bootrom 几分钟前就已放弃的位置。</para>
    /// </remarks>
    /// <returns><see langword="true"/> when unread buffered bytes survive the age check.
    /// <para>未读缓冲字节通过时效检查时为 <see langword="true"/>。</para></returns>
    private bool HasServableBufferedBytes()
    {
        if (_position >= _length)
        {
            return false;
        }

        return !IsBufferedEvidenceStale() && _position < _length;
    }

    public long Write(byte[] data, int offset, int length, int timeoutMs) => inner.Write(data, offset, length, timeoutMs);

    public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => inner.WriteAsync(data, offset, length, timeoutMs, cancellationToken);

    public Stream GetStream() => new ProbeStream(this);
    public void Dispose() => inner.Dispose();

    private sealed class ProbeStream(EdlProbeTransport transport) : Stream
    {
        public override bool CanRead => transport.IsConnected;
        public override bool CanWrite => transport.IsConnected;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => transport.Read(buffer, offset, count, 10000);
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (transport.Write(buffer, offset, count, 10000) != count)
            {
                throw new IOException("Incomplete EDL stream write.");
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
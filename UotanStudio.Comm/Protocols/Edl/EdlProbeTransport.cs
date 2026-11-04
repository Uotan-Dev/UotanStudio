using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Retains inspected bytes for the subsequent protocol handshake.
/// <para>保留检测读取的字节，供后续协议握手继续消费。</para>
/// </summary>
/// <remarks>
/// The probe never writes a Sahara packet, because the handshake that owns Sahara negotiation is the only
/// component that can consume the target state a speculative HELLO response would create. It writes exactly
/// one Firehose NOP, and only on a transport that cannot silently lose the bootrom HELLO, so a live Sahara
/// target is never handed XML it would parse as an unknown command.
/// <para>探测从不写入 Sahara 数据包，因为只有负责 Sahara 协商的握手层才能消费主动 HELLO 应答造成的目标状态。
/// 它只写入一条 Firehose NOP，且仅在不会静默丢掉 bootrom HELLO 的传输上写入，因此处于 Sahara 的目标
/// 绝不会收到会被解析为未知命令的 XML。</para>
/// </remarks>
internal sealed class EdlProbeTransport(ICommTransport inner) : ICommTransport
{
    private const int MaximumProbeBytes = 64 * 1024;
    private const int MaximumProbeMilliseconds = EdlConstants.ProbeReadBudgetMs;
    private const int ProbeReadSliceMilliseconds = 500;
    private const int ProbeReadChunkBytes = 48;
    private const int MaximumIdleReads = 64;
    private const int FirehoseSilenceGraceMilliseconds = 1000;

    private byte[]? _buffer;
    private int _length;
    private int _position;

    /// <summary>
    /// Classifies the endpoint by reading what the target sends, then probing Firehose once when the target
    /// stays silent on a transport that cannot lose the bootrom HELLO.
    /// <para>通过读取目标发出的数据分类端点；当目标在不丢失 bootrom HELLO 的传输上保持静默时，再探测一次
    /// Firehose。</para>
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
        var nopSent = false;
        var nopAllowed = Inner is not SerialTransport;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffered = _buffer.AsSpan(0, _length);
            var state = Classify(buffered);
            if (state == EdlProtocolState.Firehose)
            {
                _length = 0;
                _position = 0;
                return state;
            }
            if (state != EdlProtocolState.Unknown)
            {
                return state;
            }

            if (_length == MaximumProbeBytes || idleReads >= MaximumIdleReads)
            {
                return EdlProtocolState.Unknown;
            }
            var elapsed = Environment.TickCount64 - started;
            if (elapsed >= MaximumProbeMilliseconds)
            {
                return EdlProtocolState.Unknown;
            }
            var pendingDocument = HasPendingDocument(buffered);
            if (nopSent && !pendingDocument && elapsed >= FirehoseSilenceGraceMilliseconds)
            {
                return EdlProtocolState.Unknown;
            }
            var canProbe = nopAllowed && !nopSent && !pendingDocument && AcceptsFirehoseProbe(buffered);

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
                idleReads = 0;
                continue;
            }

            idleReads++;
            if (canProbe)
            {
                var nop = Encoding.UTF8.GetBytes(EdlConstants.XmlHeader + EdlConstants.DataOpen +
                    "<nop value=\"ping\"/>\n" + EdlConstants.DataClose);
                cancellationToken.ThrowIfCancellationRequested();
                if (await inner.WriteAsync(nop, 0, nop.Length, remaining, cancellationToken).ConfigureAwait(false) != nop.Length)
                {
                    throw new IOException("Incomplete EDL protocol probe write.");
                }
                nopSent = true;
                idleReads = 0;
            }
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
            || ContainsOnlyFirehoseLogs(bytes);

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
        inner.Reset();
    }

    private int ReadBuffered(byte[] buffer, int offset, int length)
    {
        var count = Math.Min(length, _length - _position);
        if (count > 0)
        {
            _buffer!.AsSpan(_position, count).CopyTo(buffer.AsSpan(offset, count));
            _position += count;
        }
        return count;
    }

    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
        => _position < _length ? ReadBuffered(buffer, offset, length) : inner.Read(buffer, offset, length, timeoutMs);

    public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _position < _length ? Task.FromResult(ReadBuffered(buffer, offset, length))
            : inner.ReadAsync(buffer, offset, length, timeoutMs, cancellationToken);
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
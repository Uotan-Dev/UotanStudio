using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Retains inspected bytes for the subsequent protocol handshake.
/// <para>保留检测读取的字节，供后续协议握手继续消费。</para>
/// </summary>
internal sealed class EdlProbeTransport(ICommTransport inner) : ICommTransport
{
    private const int MaximumProbeBytes = 64 * 1024;
    private byte[]? _buffer;
    private int _length;
    private int _position;

    internal async Task<EdlProtocolState> ProbeAsync(CancellationToken cancellationToken)
    {
        _buffer ??= new byte[MaximumProbeBytes];
        if (_position > 0)
        {
            _buffer.AsSpan(_position, _length - _position).CopyTo(_buffer);
            _length -= _position;
            _position = 0;
        }
        var started = Environment.TickCount64;
        var sentNop = false;
        while (Environment.TickCount64 - started < 2000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = Classify(_buffer.AsSpan(0, _length));
            if (state != EdlProtocolState.Unknown)
            {
                if (state == EdlProtocolState.Firehose)
                {
                    _length = 0;
                    _position = 0;
                }
                return state;
            }
            if (_length == MaximumProbeBytes)
            {
                return EdlProtocolState.Unknown;
            }

            var remaining = (int)Math.Max(1, 2000 - (Environment.TickCount64 - started));
            int count;
            try
            {
                count = await inner.ReadAsync(_buffer, _length, Math.Min(48, MaximumProbeBytes - _length),
                    Math.Min(500, remaining), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                count = 0;
            }
            catch (EdlException ex) when (ex.ErrorCode == EdlErrorCode.ReadPortTimeout)
            {
                count = 0;
            }
            if (count is < 0 or > 48 || count > MaximumProbeBytes - _length)
            {
                throw new IOException("Invalid EDL probe receive length.");
            }
            if (count != 0)
            {
                _length += count;
                continue;
            }
            if (sentNop || (_length != 0 && !ContainsOnlyFirehoseLogs(_buffer.AsSpan(0, _length))))
            {
                return EdlProtocolState.Unknown;
            }

            var nop = Encoding.UTF8.GetBytes(EdlConstants.XmlHeader + EdlConstants.DataOpen +
                "<nop value=\"ping\"/>\n" + EdlConstants.DataClose);
            cancellationToken.ThrowIfCancellationRequested();
            if (await inner.WriteAsync(nop, 0, nop.Length, remaining, cancellationToken).ConfigureAwait(false) != nop.Length)
            {
                throw new IOException("Incomplete EDL protocol probe write.");
            }
            sentNop = true;
        }
        return EdlProtocolState.Unknown;
    }

    private static bool ContainsOnlyFirehoseLogs(ReadOnlySpan<byte> bytes)
    {
        var foundLog = false;
        while (!bytes.IsEmpty)
        {
            while (!bytes.IsEmpty && bytes[0] is 0 or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                bytes = bytes[1..];
            }
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
                if (command == SaharaCommand.Hello &&
                    (BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) == 0 ||
                     BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) > BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) ||
                     BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) > 3))
                {
                    return EdlProtocolState.Unknown;
                }
                return EdlProtocolState.Sahara;
            }
        }

        while (!bytes.IsEmpty)
        {
            while (!bytes.IsEmpty && bytes[0] is 0 or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                bytes = bytes[1..];
            }
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
            _buffer!.AsSpan(_position, count).CopyTo(buffer.AsSpan(offset, length));
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
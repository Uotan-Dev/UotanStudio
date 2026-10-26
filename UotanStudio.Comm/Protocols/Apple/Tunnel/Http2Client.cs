using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// One decoded HTTP/2 frame.
/// <para>一条解码后的 HTTP/2 帧。</para>
/// </summary>
/// <param name="Type">The frame type byte. <para>帧类型字节。</para></param>
/// <param name="Flags">The frame flags byte. <para>帧标志字节。</para></param>
/// <param name="StreamId">The stream id. <para>流 ID。</para></param>
/// <param name="Payload">The frame payload. <para>帧负载。</para></param>
public readonly record struct Http2Frame(byte Type, byte Flags, int StreamId, byte[] Payload)
{
    /// <summary>DATA frame type. <para>DATA 帧类型。</para></summary>
    public const byte TypeData = 0x0;

    /// <summary>HEADERS frame type. <para>HEADERS 帧类型。</para></summary>
    public const byte TypeHeaders = 0x1;

    /// <summary>RST_STREAM frame type. <para>RST_STREAM 帧类型。</para></summary>
    public const byte TypeRstStream = 0x3;

    /// <summary>SETTINGS frame type. <para>SETTINGS 帧类型。</para></summary>
    public const byte TypeSettings = 0x4;

    /// <summary>WINDOW_UPDATE frame type. <para>WINDOW_UPDATE 帧类型。</para></summary>
    public const byte TypeWindowUpdate = 0x8;

    /// <summary>GOAWAY frame type. <para>GOAWAY 帧类型。</para></summary>
    public const byte TypeGoAway = 0x7;

    /// <summary>END_STREAM flag. <para>END_STREAM 标志。</para></summary>
    public const byte FlagEndStream = 0x1;

    /// <summary>END_HEADERS flag. <para>END_HEADERS 标志。</para></summary>
    public const byte FlagEndHeaders = 0x4;
}

/// <summary>
/// Minimal blocking HTTP/2 client used by RemoteXPC: preface, SETTINGS, HEADERS (HPACK encoded with
/// literal-without-indexing plus the static table) and DATA with flow-control accounting. Only the
/// subset RemoteXPC needs is implemented; peer HEADERS are decoded with the static table and
/// literal forms.
/// <para>RemoteXPC 所需的最小阻塞式 HTTP/2 客户端：前导、SETTINGS、HEADERS（HPACK：不索引字面量与
/// 静态表）与带流控记账的 DATA。仅实现 RemoteXPC 所需子集；对端 HEADERS 按静态表与字面量形式解码。</para>
/// </summary>
public sealed class Http2Client : IDisposable
{
    private const byte PrefaceEnd = 0x0;
    private const int MaxFrameSize = 1 << 20;
    private const int InitialWindow = 65535;
    private const int DesiredWindow = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly Lock _writeLock = new();
    private readonly Http2HpackDecoder _decoder = new();
    private int _outboundWindow = InitialWindow;
    private int _nextStreamId = 1;

    /// <summary>
    /// Creates a client over a connected stream and performs the connection preface exchange.
    /// <para>在已连接流上创建客户端并完成连接前导交换。</para>
    /// </summary>
    /// <param name="stream">The connected byte stream. <para>已连接字节流。</para></param>
    public Http2Client(Stream stream)
        : this(stream, completeSettingsExchange: true)
    {
    }

    /// <summary>
    /// Creates a client over a connected stream, optionally deferring the peer SETTINGS exchange so
    /// protocol-specific frames can be emitted before it (the RemoteXPC handshake order).
    /// <para>在已连接流上创建客户端；可将对端 SETTINGS 交换延后，使协议专属帧先于其发出
    /// （RemoteXPC 握手顺序）。</para>
    /// </summary>
    /// <param name="stream">The connected byte stream. <para>已连接字节流。</para></param>
    /// <param name="completeSettingsExchange">Whether to wait for and acknowledge the peer SETTINGS
    /// immediately. <para>是否立即等待并应答对端 SETTINGS。</para></param>
    public Http2Client(Stream stream, bool completeSettingsExchange)
    {
        _stream = stream;
        WriteRaw("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray());

        var settings = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(settings.AsSpan(0), 0x3);
        BinaryPrimitives.WriteUInt32BigEndian(settings.AsSpan(2), 100);
        BinaryPrimitives.WriteUInt16BigEndian(settings.AsSpan(6), 0x4);
        BinaryPrimitives.WriteUInt32BigEndian(settings.AsSpan(8), (uint)DesiredWindow);
        WriteFrame(new Http2Frame(Http2Frame.TypeSettings, 0, 0, settings));
        WriteFrame(new Http2Frame(Http2Frame.TypeWindowUpdate, 0, 0,
            EncodeWindowUpdate(DesiredWindow - InitialWindow)));

        if (completeSettingsExchange)
        {
            CompleteSettingsExchange();
        }
    }

    /// <summary>
    /// Waits for the peer SETTINGS frame and acknowledges it, closing the preface exchange started
    /// by the constructor; connection-level frames observed meanwhile are answered automatically.
    /// <para>等待对端 SETTINGS 帧并应答，收束构造函数开启的前导交换；期间观察到的连接级帧会被
    /// 自动处理。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait. <para>取消等待。</para></param>
    /// <exception cref="IOException">The peer closed the connection. <para>对端关闭连接时抛出。</para></exception>
    public void CompleteSettingsExchange(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var frame = ReadFrame(cancellationToken);
            if (frame.Type == Http2Frame.TypeSettings)
            {
                if ((frame.Flags & 0x1) == 0)
                {
                    WriteFrame(new Http2Frame(Http2Frame.TypeSettings, 0x1, 0, []));
                }

                break;
            }
        }
    }

    /// <summary>
    /// Opens a new stream id for the next request.
    /// <para>为下一个请求打开新的流 ID。</para>
    /// </summary>
    /// <returns>The stream id. <para>流 ID。</para></returns>
    public int OpenStream() => _nextStreamId += 2;

    /// <summary>
    /// Sends an empty HEADERS frame ending the header block on a stream.
    /// <para>在流上发送结束头块的空 HEADERS 帧。</para>
    /// </summary>
    /// <param name="streamId">The stream id. <para>流 ID。</para></param>
    public void SendEndHeaders(int streamId) =>
        WriteFrame(new Http2Frame(Http2Frame.TypeHeaders, Http2Frame.FlagEndHeaders, streamId, []));

    /// <summary>
    /// Sends one DATA frame, blocking until the connection window has room.
    /// <para>发送一帧 DATA，连接窗口有空间时才写入。</para>
    /// </summary>
    /// <param name="streamId">The stream id. <para>流 ID。</para></param>
    /// <param name="data">The payload chunk. <para>负载块。</para></param>
    /// <param name="endStream">Whether this closes the stream. <para>是否就此关闭流。</para></param>
    public void SendData(int streamId, ReadOnlySpan<byte> data, bool endStream = false)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var budget = Math.Min(Math.Min(_outboundWindow, MaxFrameSize), data.Length - offset);
            if (budget <= 0)
            {
                Thread.Sleep(5);
                PumpWindowUpdates();
                continue;
            }

            var flags = offset + budget >= data.Length && endStream ? Http2Frame.FlagEndStream : (byte)0;
            WriteFrame(new Http2Frame(Http2Frame.TypeData, flags, streamId, data.Slice(offset, budget).ToArray()));
            _outboundWindow -= budget;
            offset += budget;
        }

        if (data.Length == 0 && endStream)
        {
            WriteFrame(new Http2Frame(Http2Frame.TypeData, Http2Frame.FlagEndStream, streamId, []));
        }
    }

    /// <summary>
    /// Sends a WINDOW_UPDATE increasing a stream or connection window.
    /// <para>发送 WINDOW_UPDATE 增加流或连接窗口。</para>
    /// </summary>
    /// <param name="streamId">The stream id, 0 for the connection. <para>流 ID；0 表示连接级。</para></param>
    /// <param name="increment">The window increment. <para>窗口增量。</para></param>
    public void SendWindowUpdate(int streamId, int increment) =>
        WriteFrame(new Http2Frame(Http2Frame.TypeWindowUpdate, 0, streamId, EncodeWindowUpdate(increment)));

    /// <summary>
    /// Reads the next frame, automatically answering connection-level housekeeping frames.
    /// <para>读取下一帧，自动应答连接级维护帧。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The next protocol-relevant frame. <para>下一条协议相关帧。</para></returns>
    /// <exception cref="IOException">The peer closed the connection or the frame is malformed. <para>对端关闭连接或帧畸形时抛出。</para></exception>
    public Http2Frame ReadFrame(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var header = new byte[9];
            ReadAll(header, cancellationToken);
            var length = (header[0] << 16) | (header[1] << 8) | header[2];
            var frame = new Http2Frame(
                header[3],
                header[4],
                (int)(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5)) & 0x7FFFFFFF),
                new byte[length]);
            if (length > MaxFrameSize)
            {
                throw new IOException($"The HTTP/2 frame of {length} bytes exceeds the limit.");
            }

            if (length > 0)
            {
                ReadAll(frame.Payload, cancellationToken);
            }

            switch (frame.Type)
            {
                case Http2Frame.TypeSettings when (frame.Flags & 0x1) == 0:
                    WriteFrame(new Http2Frame(Http2Frame.TypeSettings, 0x1, 0, []));
                    continue;
                case Http2Frame.TypeWindowUpdate:
                    if (frame.StreamId == 0)
                    {
                        _outboundWindow += (int)(BinaryPrimitives.ReadUInt32BigEndian(frame.Payload) & 0x7FFFFFFF);
                    }

                    continue;
                case Http2Frame.TypeGoAway:
                    throw new IOException("The HTTP/2 peer sent GOAWAY.");
                default:
                    return frame;
            }
        }
    }

    /// <summary>
    /// Collects DATA payloads of one stream until END_STREAM, decoding HEADERS when interleaved.
    /// <para>收集一条流的 DATA 负载直到 END_STREAM；交错的 HEADERS 会被解码。</para>
    /// </summary>
    /// <param name="streamId">The stream id. <para>流 ID。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The accumulated DATA bytes. <para>累计的 DATA 字节。</para></returns>
    public byte[] ReadStreamData(int streamId, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        while (true)
        {
            var frame = ReadFrame(cancellationToken);
            if (frame.StreamId != streamId)
            {
                continue;
            }

            if (frame.Type == Http2Frame.TypeData)
            {
                output.Write(frame.Payload);
                SendWindowUpdate(streamId, frame.Payload.Length);
                if ((frame.Flags & Http2Frame.FlagEndStream) != 0)
                {
                    return output.ToArray();
                }
            }
            else if (frame.Type == Http2Frame.TypeRstStream)
            {
                throw new IOException("The HTTP/2 stream was reset by the peer.");
            }
        }
    }

    private void PumpWindowUpdates()
    {
    }

    private static byte[] EncodeWindowUpdate(int increment)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)increment);
        return payload;
    }

    /// <summary>
    /// Encodes a header list as HPACK literal-without-indexing entries.
    /// <para>把头部列表编码为 HPACK 不索引字面量条目。</para>
    /// </summary>
    /// <param name="headers">The header name/value pairs. <para>头部名值对。</para></param>
    /// <returns>The encoded block. <para>编码块。</para></returns>
    public static byte[] EncodeHeaders(IEnumerable<(string Name, string Value)> headers)
    {
        using var output = new MemoryStream();
        foreach (var (name, value) in headers)
        {
            output.WriteByte(0x00);
            WriteHpackString(output, name);
            WriteHpackString(output, value);
        }

        return output.ToArray();
    }

    private static void WriteHpackString(MemoryStream output, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length < 127)
        {
            output.WriteByte((byte)bytes.Length);
        }
        else
        {
            output.WriteByte(0x7F);
            var remainder = bytes.Length - 127;
            while (remainder >= 128)
            {
                output.WriteByte((byte)((remainder & 0x7F) | 0x80));
                remainder >>= 7;
            }

            output.WriteByte((byte)remainder);
        }

        output.Write(bytes);
    }

    private void WriteRaw(byte[] bytes) => WriteAll(bytes);

    private void WriteFrame(Http2Frame frame)
    {
        lock (_writeLock)
        {
            var header = new byte[9];
            var length = frame.Payload.Length;
            header[0] = (byte)(length >> 16);
            header[1] = (byte)(length >> 8);
            header[2] = (byte)length;
            header[3] = frame.Type;
            header[4] = frame.Flags;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), (uint)frame.StreamId);
            WriteAll(header);
            if (length > 0)
            {
                WriteAll(frame.Payload);
            }
        }
    }

    private void WriteAll(byte[] bytes)
    {
        _stream.WriteAsync(bytes, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private void ReadAll(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = _stream.ReadAsync(buffer.AsMemory(read), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (count <= 0)
            {
                throw new IOException("The HTTP/2 peer closed the connection during a read.");
            }

            read += count;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// HPACK decoder restricted to the static table plus literal representations (new and indexed).
/// <para>HPACK 解码器：仅支持静态表加字面量表示（新增与索引形式）。</para>
/// </summary>
public sealed class Http2HpackDecoder
{
    private static readonly string[][] StaticTable =
    [
        [":authority", ""], [":method", "GET"], [":method", "POST"], [":path", "/"], [":path", "/index.html"],
        [":scheme", "http"], [":scheme", "https"], [":status", "200"], [":status", "204"], [":status", "206"],
        [":status", "304"], [":status", "400"], [":status", "404"], [":status", "500"], ["accept-charset", ""],
        ["accept-encoding", "gzip, deflate"], ["accept-language", ""], ["accept-ranges", ""], ["accept", ""],
        ["access-control-allow-origin", ""], ["age", ""], ["allow", ""], ["authorization", ""],
        ["cache-control", ""], ["content-disposition", ""], ["content-encoding", ""], ["content-language", ""],
        ["content-length", ""], ["content-location", ""], ["content-range", ""], ["content-type", ""], ["cookie", ""],
        ["date", ""], ["etag", ""], ["expect", ""], ["expires", ""], ["from", ""], ["host", ""],
        ["if-match", ""], ["if-modified-since", ""], ["if-none-match", ""], ["if-range", ""],
        ["if-unmodified-since", ""], ["last-modified", ""], ["link", ""], ["location", ""], ["max-forwards", ""],
        ["proxy-authenticate", ""], ["proxy-authorization", ""], ["range", ""], ["referer", ""], ["refresh", ""],
        ["retry-after", ""], ["server", ""], ["set-cookie", ""], ["strict-transport-security", ""],
        ["transfer-encoding", ""], ["user-agent", ""], ["vary", ""], ["via", ""], ["www-authenticate", ""],
    ];

    /// <summary>
    /// Decodes one HPACK header block into name/value pairs.
    /// <para>把一个 HPACK 头块解码为名值对。</para>
    /// </summary>
    /// <param name="block">The encoded block. <para>编码块。</para></param>
    /// <returns>The decoded headers. <para>解码后的头部。</para></returns>
    /// <exception cref="InvalidDataException">The block uses unsupported representations. <para>块使用了不受支持的表示时抛出。</para></exception>
    public IReadOnlyList<(string Name, string Value)> Decode(ReadOnlySpan<byte> block)
    {
        var headers = new List<(string, string)>();
        var position = 0;
        while (position < block.Length)
        {
            var first = block[position];
            if ((first & 0x80) != 0)
            {
                var index = (int)ReadInteger(block, ref position, 7);
                var entry = StaticTable[index - 1];
                headers.Add((entry[0], entry[1]));
                continue;
            }

            if ((first & 0xC0) == 0x40)
            {
                var index = (int)ReadInteger(block, ref position, 6);
                var name = index == 0 ? DecodeString(block, ref position) : StaticTable[index - 1][0];
                headers.Add((name, DecodeString(block, ref position)));
                continue;
            }

            if ((first & 0xE0) == 0x20)
            {
                ReadInteger(block, ref position, 5);
                continue;
            }

            if ((first & 0xF0) == 0x00)
            {
                var index = (int)ReadInteger(block, ref position, 4);
                var name = index == 0 ? DecodeString(block, ref position) : StaticTable[index - 1][0];
                headers.Add((name, DecodeString(block, ref position)));
                continue;
            }

            throw new InvalidDataException($"The HPACK representation 0x{first:X2} is unsupported.");
        }

        return headers;
    }

    private static ulong ReadInteger(ReadOnlySpan<byte> block, ref int position, int prefixBits)
    {
        var mask = (1 << prefixBits) - 1;
        ulong value = (uint)(block[position++] & mask);
        if (value < (uint)mask)
        {
            return value;
        }

        ulong shift = 0;
        while (true)
        {
            var octet = block[position++];
            value += (ulong)(octet & 0x7F) * (1UL << (int)shift);
            if ((octet & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    private static string DecodeString(ReadOnlySpan<byte> block, ref int position)
    {
        var first = block[position];
        var huffman = (first & 0x80) != 0;
        var length = (int)ReadInteger(block, ref position, 7);
        var bytes = block.Slice(position, length).ToArray();
        position += length;
        if (huffman)
        {
            throw new InvalidDataException("The HPACK Huffman coding is not supported by this client.");
        }

        return Encoding.ASCII.GetString(bytes);
    }
}
using System.IO;
using System.Text;

namespace UotanStudio.Mcp.Http;

/// <summary>
/// A request the server refused before MCP handling; the status line travels with the exception so
/// the connection can answer with the matching HTTP error.
/// <para>服务器在进入 MCP 处理前拒绝的请求；状态码随异常传递，便于连接按对应 HTTP 错误作答。</para>
/// </summary>
internal sealed class HttpProtocolException : Exception
{
    internal HttpProtocolException(int statusCode, string reason, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Reason = reason;
    }

    internal int StatusCode { get; }

    internal string Reason { get; }
}

/// <summary>
/// One parsed HTTP request: request line, headers and the fully read body (Content-Length or
/// chunked framing).
/// <para>一次解析完成的 HTTP 请求：请求行、请求头与已完整读取的请求体（Content-Length 或分块传输）。</para>
/// </summary>
internal sealed class HttpRequest
{
    private HttpRequest(string method, string target, bool keepAlive, string? origin, byte[] body)
    {
        Method = method;
        Target = target;
        KeepAlive = keepAlive;
        Origin = origin;
        Body = body;
    }

    internal string Method { get; }

    internal string Target { get; }

    internal bool KeepAlive { get; }

    /// <summary>Value of the Origin header, if the client sent one. <para>客户端发送的 Origin 头（若有）。</para></summary>
    internal string? Origin { get; }

    internal byte[] Body { get; }

    /// <summary>
    /// Reads one request from the connection, or returns null when the client closed it cleanly
    /// between requests.
    /// <para>从连接读取一个请求；客户端在请求之间干净关闭时返回 null。</para>
    /// </summary>
    internal static async Task<HttpRequest?> ReadAsync(Stream stream, int maxHeadBytes, int maxBodyBytes, CancellationToken cancellationToken)
    {
        var reader = new ConnectionReader(stream, maxHeadBytes + 1024);

        while (true)
        {
            if (TryFindHeadEnd(reader.Buffered, out var headEnd, out var bodyStart))
            {
                var head = reader.Buffered[..headEnd].ToArray();
                reader.Consume(bodyStart);

                var (method, target, keepAlive, headers) = ParseHead(head);
                var framing = ResolveBodyFraming(method, headers);
                if (!framing.Chunked && framing.Length > maxBodyBytes)
                {
                    throw new HttpProtocolException(413, "Payload Too Large", "The request body exceeds the accepted size.");
                }

                var body = framing.Chunked
                    ? await reader.ReadChunkedAsync(maxBodyBytes, cancellationToken).ConfigureAwait(false)
                    : await reader.ReadExactAsync(framing.Length, cancellationToken).ConfigureAwait(false);

                headers.TryGetValue("Origin", out var origin);
                return new HttpRequest(method, target, keepAlive, origin, body);
            }

            if (reader.Buffered.Length > maxHeadBytes)
            {
                throw new HttpProtocolException(431, "Request Header Fields Too Large", "The request head exceeds the accepted size.");
            }

            if (!await reader.FillAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.Buffered.Length == 0)
                {
                    return null;
                }

                throw new HttpProtocolException(400, "Bad Request", "The connection ended inside the request head.");
            }
        }
    }

    /// <summary>Locates the blank line separating head and body. <para>定位分隔请求头与请求体的空行。</para></summary>
    private static bool TryFindHeadEnd(ReadOnlySpan<byte> data, out int headEnd, out int bodyStart)
    {
        for (var i = 1; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n')
            {
                continue;
            }

            // CRLF CRLF: the preceding bytes must be CR LF CR.
            if (i >= 3 && data[i - 3] == (byte)'\r' && data[i - 2] == (byte)'\n' && data[i - 1] == (byte)'\r')
            {
                headEnd = i - 1;
                bodyStart = i + 1;
                return true;
            }

            // Bare LF LF.
            if (data[i - 1] == (byte)'\n')
            {
                headEnd = i - 1;
                bodyStart = i + 1;
                return true;
            }
        }

        headEnd = 0;
        bodyStart = 0;
        return false;
    }

    private static (string Method, string Target, bool KeepAlive, Dictionary<string, string> Headers) ParseHead(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split('\n');
        if (lines.Length == 0)
        {
            throw new HttpProtocolException(400, "Bad Request", "The request line is missing.");
        }

        var startLine = lines[0].TrimEnd('\r').Trim();
        var parts = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new HttpProtocolException(400, "Bad Request", $"Malformed request line: '{startLine}'.");
        }

        var method = parts[0];
        var target = parts[1];
        var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new HttpProtocolException(400, "Bad Request", $"Malformed header line: '{line}'.");
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing) ? $"{existing}, {value}" : value;
        }

        headers.TryGetValue("Connection", out var connection);
        var keepAlive = version.Equals("HTTP/1.0", StringComparison.OrdinalIgnoreCase)
            ? connection?.Contains("keep-alive", StringComparison.OrdinalIgnoreCase) == true
            : connection?.Contains("close", StringComparison.OrdinalIgnoreCase) != true;

        return (method, target, keepAlive, headers);
    }

    private static (bool Chunked, int Length) ResolveBodyFraming(string method, Dictionary<string, string> headers)
    {
        var chunked = headers.TryGetValue("Transfer-Encoding", out var transferEncoding)
            && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        var hasLength = headers.TryGetValue("Content-Length", out var rawLength);

        if (chunked && hasLength)
        {
            throw new HttpProtocolException(400, "Bad Request", "Content-Length and chunked transfer encoding are mutually exclusive.");
        }

        if (chunked)
        {
            return (true, 0);
        }

        if (!hasLength)
        {
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpProtocolException(411, "Length Required", "POST requests must declare a Content-Length.");
            }

            return (false, 0);
        }

        if (!int.TryParse(rawLength, out var length) || length < 0)
        {
            throw new HttpProtocolException(400, "Bad Request", $"Invalid Content-Length: '{rawLength}'.");
        }

        return (false, length);
    }

    /// <summary>
    /// Buffered reader over the connection: the head may arrive in several reads and any bytes after
    /// it already belong to the body, so both share one buffer.
    /// <para>连接的缓冲读取器：请求头可能分多次到达，且其后的字节已属于请求体，因此两者共用同一个缓冲。</para>
    /// </summary>
    private sealed class ConnectionReader(Stream stream, int maxBuffer)
    {
        private byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        internal ReadOnlySpan<byte> Buffered => _buffer.AsSpan(_start, _end - _start);

        internal void Consume(int count) => _start += count;

        /// <summary>Returns false when the peer closed the connection. <para>对端关闭连接时返回 false。</para></summary>
        internal async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
        {
            if (_start > 0)
            {
                var remaining = _end - _start;
                if (remaining > 0)
                {
                    _buffer.AsSpan(_start, remaining).CopyTo(_buffer);
                }

                _start = 0;
                _end = remaining;
            }

            if (_end == _buffer.Length)
            {
                if (_buffer.Length >= maxBuffer)
                {
                    throw new HttpProtocolException(431, "Request Header Fields Too Large", "The request head exceeds the accepted size.");
                }

                Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, maxBuffer));
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            _end += read;
            return true;
        }

        internal async ValueTask<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
        {
            var result = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                var copied = CopyBuffered(result, filled, count - filled);
                if (copied > 0)
                {
                    filled += copied;
                    continue;
                }

                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new HttpProtocolException(400, "Bad Request", "The connection ended inside the request body.");
                }
            }

            return result;
        }

        internal async ValueTask<byte[]> ReadChunkedAsync(int maxBytes, CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();

            while (true)
            {
                var sizeLine = await ReadLineAsync(64, cancellationToken).ConfigureAwait(false);
                var size = ParseChunkSize(sizeLine);
                if (size < 0)
                {
                    throw new HttpProtocolException(400, "Bad Request", $"Invalid chunk size: '{sizeLine}'.");
                }

                if (size == 0)
                {
                    break;
                }

                if (body.Length + size > maxBytes)
                {
                    throw new HttpProtocolException(413, "Payload Too Large", "The request body exceeds the accepted size.");
                }

                var chunk = await ReadExactAsync(size, cancellationToken).ConfigureAwait(false);
                body.Write(chunk);

                // The chunk data is followed by its own CRLF.
                await ReadLineAsync(8, cancellationToken).ConfigureAwait(false);
            }

            // Optional trailer section, terminated by an empty line.
            while ((await ReadLineAsync(1024, cancellationToken).ConfigureAwait(false)).Length > 0)
            {
            }

            return body.ToArray();
        }

        private async ValueTask<string> ReadLineAsync(int maxLength, CancellationToken cancellationToken)
        {
            var scanned = 0;
            while (true)
            {
                string? line = null;
                {
                    var buffered = Buffered;
                    var index = buffered.IndexOf((byte)'\n');
                    if (index >= 0)
                    {
                        var content = buffered[..index];
                        if (content.Length > 0 && content[^1] == (byte)'\r')
                        {
                            content = content[..^1];
                        }

                        line = Encoding.ASCII.GetString(content);
                        Consume(index + 1);
                    }
                    else
                    {
                        scanned += buffered.Length;
                    }
                }

                if (line is not null)
                {
                    return line;
                }

                if (scanned > maxLength)
                {
                    throw new HttpProtocolException(400, "Bad Request", "The chunked body framing line exceeds the accepted size.");
                }

                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new HttpProtocolException(400, "Bad Request", "The connection ended inside the chunked request body.");
                }
            }
        }

        private int CopyBuffered(byte[] destination, int offset, int count)
        {
            var buffered = Buffered;
            if (buffered.Length == 0)
            {
                return 0;
            }

            var take = Math.Min(buffered.Length, count);
            buffered[..take].CopyTo(destination.AsSpan(offset));
            Consume(take);
            return take;
        }

        private static int ParseChunkSize(string line)
        {
            var terminator = line.IndexOf(';');
            var value = (terminator >= 0 ? line[..terminator] : line).Trim();
            if (value.Length is 0 or > 8)
            {
                return -1;
            }

            var size = 0;
            foreach (var character in value)
            {
                var digit = character switch
                {
                    >= '0' and <= '9' => character - '0',
                    >= 'a' and <= 'f' => character - 'a' + 10,
                    >= 'A' and <= 'F' => character - 'A' + 10,
                    _ => -1
                };

                if (digit < 0)
                {
                    return -1;
                }

                size = (size << 4) | digit;
            }

            return size;
        }
    }
}
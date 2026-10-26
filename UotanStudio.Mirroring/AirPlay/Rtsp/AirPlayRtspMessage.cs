using System.Globalization;
using System.Text;

namespace UotanStudio.Mirroring.AirPlay.Rtsp;

/// <summary>One parsed RTSP request. <para>一条解析后的 RTSP 请求。</para></summary>
/// <param name="Method">Request method, for example <c>ANNOUNCE</c>. <para>请求方法，例如 <c>ANNOUNCE</c>。</para></param>
/// <param name="Uri">Request URI as sent by the client. <para>客户端发送的请求 URI。</para></param>
/// <param name="Version">Protocol version token. <para>协议版本标记。</para></param>
/// <param name="Headers">Header fields with case-insensitive names. <para>名称不区分大小写的头字段。</para></param>
/// <param name="Body">Request body, empty when the request carries none. <para>请求体；无请求体时为空。</para></param>
internal sealed record AirPlayRtspRequest(string Method, string Uri, string Version,
    IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    /// <summary>Returns one header value. <para>返回一个头字段值。</para></summary>
    /// <param name="name">Header name; comparison is case-insensitive. <para>头名称；比较不区分大小写。</para></param>
    /// <returns>The value, or <see langword="null"/> when the header is absent.
    /// <para>头字段值；不存在时为 <see langword="null"/>。</para></returns>
    public string? GetHeader(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Sequence number echoed in the response. <para>在应答中回显的序号。</para></summary>
    public int CSeq => int.TryParse(GetHeader("CSeq"), NumberStyles.Integer, CultureInfo.InvariantCulture,
        out var value) ? value : 0;
}

/// <summary>One RTSP response produced by the receiver. <para>接收端产生的一条 RTSP 应答。</para></summary>
/// <param name="StatusCode">Three-digit status code. <para>三位状态码。</para></param>
/// <param name="Reason">Reason phrase. <para>原因短语。</para></param>
/// <param name="Headers">Header fields to write before the body. <para>写在正文之前的头字段。</para></param>
/// <param name="Body">Response body, or <see langword="null"/> for none. <para>应答正文；无正文时为 <see langword="null"/>。</para></param>
internal sealed record AirPlayRtspResponse(int StatusCode, string Reason,
    IReadOnlyDictionary<string, string> Headers, byte[]? Body)
{
    /// <summary>Builds a <c>200 OK</c> response with an optional binary plist body.
    /// <para>构建带可选二进制 plist 正文的 <c>200 OK</c> 应答。</para>
    /// </summary>
    /// <param name="body">Binary plist body, or <see langword="null"/>. <para>二进制 plist 正文，或 <see langword="null"/>。</para></param>
    /// <param name="headers">Additional header fields. <para>额外的头字段。</para></param>
    /// <returns>The response to write. <para>要写入的应答。</para></returns>
    public static AirPlayRtspResponse Ok(byte[]? body = null, params (string Name, string Value)[] headers)
        => Create(200, "OK", body, headers);

    /// <summary>Builds an error response. <para>构建错误应答。</para></summary>
    /// <param name="statusCode">Status code to report. <para>要报告的状态码。</para></param>
    /// <param name="reason">Reason phrase to report. <para>要报告的原因短语。</para></param>
    /// <param name="headers">Additional header fields. <para>额外的头字段。</para></param>
    /// <returns>The response to write. <para>要写入的应答。</para></returns>
    public static AirPlayRtspResponse Error(int statusCode, string reason,
        params (string Name, string Value)[] headers) => Create(statusCode, reason, null, headers);

    private static AirPlayRtspResponse Create(int statusCode, string reason, byte[]? body,
        (string Name, string Value)[] headers)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            values[name] = value;
        }

        return new AirPlayRtspResponse(statusCode, reason, values, body);
    }
}

/// <summary>
/// Bounded RTSP reader: one request line, case-insensitive header fields and an optional binary plist body, with
/// every count and length checked against <see cref="AirPlayConstants"/> before allocation.
/// <para>有界的 RTSP 读取器：一条请求行、不区分大小写的头字段与可选的二进制 plist 正文；在分配之前对每个计数
/// 与长度按 <see cref="AirPlayConstants"/> 校验。</para>
/// </summary>
internal static class AirPlayRtspParser
{
    private static readonly Encoding HeaderEncoding = Encoding.Latin1;

    /// <summary>Locates the end of a header block, including the terminating empty line.
    /// <para>定位头部块的结束位置（包含结尾空行）。</para>
    /// </summary>
    /// <param name="buffer">Bytes received so far. <para>目前已接收的字节。</para></param>
    /// <returns>The index just past <c>\r\n\r\n</c>, or -1 when the block is incomplete.
    /// <para><c>\r\n\r\n</c> 之后的索引；块不完整时为 -1。</para></returns>
    public static int FindHeaderEnd(ReadOnlySpan<byte> buffer)
    {
        for (var index = 0; index + 3 < buffer.Length; index++)
        {
            if (buffer[index] == (byte)'\r' && buffer[index + 1] == (byte)'\n' &&
                buffer[index + 2] == (byte)'\r' && buffer[index + 3] == (byte)'\n')
            {
                return index + 4;
            }
        }

        return -1;
    }

    /// <summary>Parses one request from its header block and body. <para>由头部块与正文解析一条请求。</para></summary>
    /// <param name="header">Header block without the terminating empty line. <para>不含结尾空行的头部块。</para></param>
    /// <param name="body">Body bytes already read; length is checked against the header.
    /// <para>已读取的正文字节；长度会与头部核对。</para></param>
    /// <returns>The parsed request. <para>解析后的请求。</para></returns>
    /// <exception cref="InvalidDataException">The request line, a header line, the header count or the body
    /// length is malformed or exceeds a bound. <para>请求行、头行、头数量或正文长度格式非法或超出上限。</para></exception>
    public static AirPlayRtspRequest Parse(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        if (header.Length > AirPlayConstants.MaximumHeaderBytes)
        {
            throw new InvalidDataException("The RTSP header block exceeds the accepted size.");
        }

        var text = HeaderEncoding.GetString(header);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length is 0 or > AirPlayConstants.MaximumHeaderLines)
        {
            throw new InvalidDataException("The RTSP request carries too many header lines.");
        }

        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("RTSP/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The RTSP request line is malformed.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new InvalidDataException("An RTSP header line has no name separator.");
            }

            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (headers.TryGetValue("Content-Length", out var lengthText))
        {
            if (!int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            {
                throw new InvalidDataException("The RTSP content length is not an integer.");
            }

            if (length is < 0 || length > AirPlayConstants.MaximumBodyBytes)
            {
                throw new InvalidDataException("The RTSP body length is outside the accepted range.");
            }

            if (length != body.Length)
            {
                throw new InvalidDataException("The RTSP body length disagrees with the header.");
            }
        }
        else if (body.Length > 0)
        {
            throw new InvalidDataException("The RTSP request carries a body without a content length.");
        }

        return new AirPlayRtspRequest(requestLine[0], requestLine[1], requestLine[2], headers, [.. body]);
    }

    /// <summary>Reads the content length announced by a header block, or zero when absent.
    /// <para>读取头部块宣告的正文长度；缺失时为零。</para>
    /// </summary>
    /// <param name="header">Header block without the terminating empty line. <para>不含结尾空行的头部块。</para></param>
    /// <returns>The announced body length. <para>宣告的正文长度。</para></returns>
    /// <exception cref="InvalidDataException">The value is not an integer or exceeds
    /// <see cref="AirPlayConstants.MaximumBodyBytes"/>.
    /// <para>取值不是整数或超过 <see cref="AirPlayConstants.MaximumBodyBytes"/>。</para></exception>
    public static int ReadContentLength(ReadOnlySpan<byte> header)
    {
        var text = HeaderEncoding.GetString(header);
        foreach (var line in text.Split("\r\n", StringSplitOptions.None))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(line["Content-Length:".Length..].Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var length))
            {
                throw new InvalidDataException("The RTSP content length is not an integer.");
            }

            if (length is < 0 || length > AirPlayConstants.MaximumBodyBytes)
            {
                throw new InvalidDataException("The RTSP body length is outside the accepted range.");
            }

            return length;
        }

        return 0;
    }

    /// <summary>Serializes one response into the bytes written to the connection.
    /// <para>把一条应答序列化为写入连接的字节。</para>
    /// </summary>
    /// <param name="response">Response to serialize. <para>要序列化的应答。</para></param>
    /// <param name="cseq">Sequence number to echo. <para>要回显的序号。</para></param>
    /// <returns>The complete response bytes. <para>完整的应答字节。</para></returns>
    public static byte[] Serialize(AirPlayRtspResponse response, int cseq)
    {
        ArgumentNullException.ThrowIfNull(response);
        var builder = new StringBuilder(256);
        builder.Append("RTSP/1.0 ").Append(response.StatusCode.ToString(CultureInfo.InvariantCulture))
            .Append(' ').Append(response.Reason).Append("\r\n");
        builder.Append("Server: AirTunes/").Append(AirPlayConstants.SourceVersion).Append("\r\n");
        builder.Append("CSeq: ").Append(cseq.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var header in response.Headers)
        {
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        var body = response.Body ?? [];
        builder.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture))
            .Append("\r\n\r\n");
        var headerBytes = HeaderEncoding.GetBytes(builder.ToString());
        var packet = new byte[headerBytes.Length + body.Length];
        headerBytes.CopyTo(packet, 0);
        body.CopyTo(packet, headerBytes.Length);
        return packet;
    }
}
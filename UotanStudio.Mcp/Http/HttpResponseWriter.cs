using System.Text;

namespace UotanStudio.Mcp.Http;

/// <summary>
/// Writes HTTP/1.1 response heads and the simple bodies (empty, text, JSON).
/// <para>写 HTTP/1.1 响应头与简单响应体（空、文本、JSON）。</para>
/// Streamed bodies use <see cref="ChunkedBodyStream"/> after <see cref="WriteStreamHeadAsync"/>.
/// <para>流式响应体在 <see cref="WriteStreamHeadAsync"/> 之后使用 <see cref="ChunkedBodyStream"/> 输出。</para>
/// </summary>
internal static class HttpResponseWriter
{
    private static readonly byte[] EmptyBody = [];

    internal static Task WriteEmptyAsync(Stream stream, int status, string reason, bool keepAlive, CancellationToken cancellationToken)
        => WriteAsync(stream, BuildHead(status, reason, keepAlive, contentType: null, contentLength: 0, chunked: false, extraHeaders: null), EmptyBody, cancellationToken);

    internal static Task WriteTextAsync(Stream stream, int status, string reason, string text, bool keepAlive, CancellationToken cancellationToken, string? extraHeaders = null)
        => WriteAsync(stream, BuildHead(status, reason, keepAlive, "text/plain; charset=utf-8", Encoding.UTF8.GetByteCount(text), chunked: false, extraHeaders), Encoding.UTF8.GetBytes(text), cancellationToken);

    internal static Task WriteJsonAsync(Stream stream, int status, string reason, string json, bool keepAlive, CancellationToken cancellationToken)
        => WriteAsync(stream, BuildHead(status, reason, keepAlive, "application/json; charset=utf-8", Encoding.UTF8.GetByteCount(json), chunked: false, extraHeaders: null), Encoding.UTF8.GetBytes(json), cancellationToken);

    /// <summary>
    /// Writes a head for a streamed (chunked) response body.
    /// <para>为流式（分块）响应体写响应头。</para>
    /// </summary>
    internal static Task WriteStreamHeadAsync(Stream stream, int status, string reason, string contentType, bool keepAlive, CancellationToken cancellationToken)
        => WriteAsync(stream, BuildHead(status, reason, keepAlive, contentType, contentLength: null, chunked: true, extraHeaders: null), EmptyBody, cancellationToken);

    private static async Task WriteAsync(Stream stream, byte[] head, byte[] body, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] BuildHead(int status, string reason, bool keepAlive, string? contentType, long? contentLength, bool chunked, string? extraHeaders)
    {
        var builder = new StringBuilder(192);
        builder.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        if (contentType is not null)
        {
            builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
        }

        if (chunked)
        {
            builder.Append("Transfer-Encoding: chunked\r\n");
        }
        else if (contentLength is { } length)
        {
            builder.Append("Content-Length: ").Append(length).Append("\r\n");
        }

        builder.Append("Cache-Control: no-store\r\n");
        if (extraHeaders is not null)
        {
            builder.Append(extraHeaders);
        }

        builder.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        builder.Append("\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
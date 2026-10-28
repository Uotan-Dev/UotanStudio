namespace UotanStudio.Services.Network;

/// <summary>
/// Owns the process-wide <see cref="HttpClient"/> used by the services that download release assets and
/// creates dedicated clients for callers that need another timeout. The composition root registers
/// <see cref="Shared"/> in the container, so services take the client through their constructor instead of
/// allocating one at the call site.
/// <para>持有下载发布资产的进程级 <see cref="HttpClient"/>，并为需要其他超时的调用方创建专用客户端。
/// 组合根把 <see cref="Shared"/> 注册进容器，服务经构造函数取得客户端，而不是在调用点各自分配。</para>
/// </summary>
internal static class SharedHttpClient
{
    private static readonly Lazy<HttpClient> _shared =
        new(static () => Create(TimeSpan.FromMinutes(5)));

    /// <summary>
    /// Gets the shared client carrying the browser user agent the release hosts expect.
    /// <para>获取携带发布主机所期望浏览器 User-Agent 的共享客户端。</para>
    /// </summary>
    /// <value>A client identifying as a browser with a five-minute request timeout.
    /// <para>以浏览器身份请求、超时为五分钟的客户端。</para></value>
    internal static HttpClient Shared => _shared.Value;

    /// <summary>
    /// Creates a dedicated client with the same user agent and the supplied timeout.
    /// <para>创建使用相同 User-Agent、采用给定超时的专用客户端。</para>
    /// </summary>
    /// <param name="timeout">Request timeout; must be positive or infinite. <para>请求超时；必须为正或无限。</para></param>
    /// <returns>A new client owned by the caller. <para>调用方持有的新客户端。</para></returns>
    internal static HttpClient Create(TimeSpan timeout) => new()
    {
        Timeout = timeout,
        DefaultRequestHeaders = { { "User-Agent", UotanStudio.Net.Http.HttpDownload.DefaultBrowserUserAgent } },
    };
}
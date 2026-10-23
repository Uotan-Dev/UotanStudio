namespace UotanStudio.Services.Devices;

/// <summary>Session route an HDC operation opens for one target.
/// <para>针对单个目标的 HDC 操作所打开的会话路由。</para></summary>
internal enum HdcSessionRoute
{
    /// <summary>Open the target's exact USB or TCP endpoint without the local server.
    /// <para>不经本机 server，直接打开目标的精确 USB 或 TCP 端点。</para></summary>
    Direct,

    /// <summary>Run through a session created by the local HDC server.
    /// <para>经本机 HDC server 创建的会话运行。</para></summary>
    Server,
}

/// <summary>
/// Decides the HDC session route from already observed route state, so mirroring, shell and file work share
/// one rule without probing a server or opening a device.
/// <para>按已观测到的路由状态决定 HDC 会话路由，使投屏、shell 与文件操作共用同一规则，且不探测 server、不打开设备。</para>
/// </summary>
internal static class HdcSessionRouting
{
    /// <summary>
    /// Resolves the route: ServerOnly always uses the server and fails without a listening one; otherwise the
    /// server wins only when it is available and actually lists the target connect key.
    /// <para>解析路由：仅 server 模式始终使用 server，且没有 server 监听时失败；其它模式下只有 server 可用且
    /// 确实列出目标 connect key 时才算 server 拥有该目标。</para>
    /// </summary>
    /// <param name="serverOnly">Whether the fixed route mode is server-only.
    /// <para>固定路由模式是否为仅 server。</para></param>
    /// <param name="serverAvailable">Whether the HDC server endpoint is marked available.
    /// <para>HDC server 端点是否被标记为可用。</para></param>
    /// <param name="serverListsKey">Whether the live server lists the target connect key.
    /// <para>活动 server 是否列出该目标 connect key。</para></param>
    /// <returns><see cref="HdcSessionRoute.Server"/> when the server must serve the target, otherwise
    /// <see cref="HdcSessionRoute.Direct"/>.
    /// <para>目标必须由 server 服务时返回 <see cref="HdcSessionRoute.Server"/>，否则返回
    /// <see cref="HdcSessionRoute.Direct"/>。</para></returns>
    /// <exception cref="IOException">Server-only mode was selected but no server is available.
    /// <para>选择了仅 server 模式但没有可用的 server。</para></exception>
    internal static HdcSessionRoute Resolve(bool serverOnly, bool serverAvailable, bool serverListsKey)
    {
        if (serverOnly)
        {
            return serverAvailable
                ? HdcSessionRoute.Server
                : throw new IOException("The ServerOnly route mode requires a listening HDC server.");
        }

        return serverAvailable && serverListsKey ? HdcSessionRoute.Server : HdcSessionRoute.Direct;
    }
}
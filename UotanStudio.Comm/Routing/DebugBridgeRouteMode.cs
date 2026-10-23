namespace UotanStudio.Comm.Routing;

/// <summary>
/// Selects how ADB and HDC operations choose between a local debug-bridge server and a direct
/// transport. The mode is fixed for the process lifetime and is captured before the first probe,
/// so changing the persisted value requires an application restart.
/// <para>选择 ADB 与 HDC 操作在“本机调试桥 server”和“直连传输”之间的取路方式。模式在进程生命周期内固定，
/// 并在首次探测之前捕获，因此修改持久化值后必须重启应用。</para>
/// </summary>
public enum DebugBridgeRouteMode
{
    /// <summary>
    /// The server is used while it is listening and direct access otherwise; a conclusive server
    /// start or stop changes the route at the next operation boundary, never inside one.
    /// <para>server 监听期间经 server，否则直连；确定性的 server 启动或停止只会在下一次操作边界改变路由，
    /// 绝不在单次操作中途改变。</para>
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Only the local server route is allowed. A missing server is an error; direct debug access is
    /// always refused, so the application never switches to a raw USB write.
    /// <para>只允许本机 server 路由。server 不存在即报错；直连调试访问一律拒绝，因此应用绝不切换到裸 USB 写入。</para>
    /// </summary>
    ServerOnly = 1,

    /// <summary>
    /// Only direct debug access is allowed. While a local server is listening, direct access is
    /// refused instead of switching to the server, so the application never changes writers at runtime.
    /// <para>只允许直连调试访问。本机 server 正在监听时拒绝直连而不是改走 server，因此应用在运行期绝不更换写入方。</para>
    /// </summary>
    DirectOnly = 2,

    /// <summary>
    /// Both availability parameters are captured once at startup and are never refreshed by the
    /// periodic watch; a server that appears later is refused instead of switching routes.
    /// <para>两个可用性参数只在启动时采集一次，周期监视不再刷新；启动后出现的 server 会被拒绝而不是切换路由。</para>
    /// </summary>
    StartupFrozen = 3
}
namespace UotanStudio.Comm.Routing;

/// <summary>
/// Immutable pair of debug-bridge server availability flags plus the epoch that identifies the pair.
/// Every discovery and open decision reads one snapshot instead of separate mutable flags, and the
/// epoch is stamped on discovered endpoints so an open can detect that the route changed after
/// discovery.
/// <para>调试桥 server 可用性的不可变二元组，以及标识该二元组的 epoch。所有发现与打开决策都读取同一份快照，
/// 而不是各自读取可变标志；epoch 会盖在已发现的端点上，使打开操作能发现“发现之后路由已改变”。</para>
/// </summary>
/// <param name="AdbServerAvailable">Whether the last conclusive ADB probe found a listening server.
/// <para>最后一次确定性 ADB 探测是否发现正在监听的 server。</para></param>
/// <param name="HdcServerAvailable">Whether the last conclusive HDC probe found a listening server.
/// <para>最后一次确定性 HDC 探测是否发现正在监听的 server。</para></param>
/// <param name="Epoch">Monotonic snapshot identity; zero means no snapshot was captured yet.
/// <para>单调递增的快照标识；零表示尚未采集任何快照。</para></param>
public readonly record struct DebugBridgeRouteSnapshot(bool AdbServerAvailable, bool HdcServerAvailable, long Epoch)
{
    /// <summary>
    /// The pre-initialization snapshot: both servers absent and epoch zero, which no discovered
    /// endpoint is stamped with.
    /// <para>初始化之前的快照：两个 server 都不存在且 epoch 为零，已发现的端点不会盖该值。</para>
    /// </summary>
    public static DebugBridgeRouteSnapshot Empty { get; } = new(false, false, 0);
}
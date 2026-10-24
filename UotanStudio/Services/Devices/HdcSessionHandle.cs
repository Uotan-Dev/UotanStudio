using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Holds exactly one reference to a shared direct HDC session or to the lease adopted for a server route;
/// disposing it returns only that reference, so several operations can multiplex one authenticated
/// connection instead of opening the single WinUSB-bound interface again.
/// <para>持有共享直连 HDC 会话或 server 路由所纳入租约的恰好一个引用；释放时只归还该引用，
/// 使多个操作可以复用同一条已认证连接，而不必再次打开那个只能被占用一次的 WinUSB 接口。</para>
/// </summary>
public sealed class HdcSessionHandle : IDisposable, IAsyncDisposable
{
    private Action? _release;
    private int _disposed;

    private HdcSessionHandle(Action release, bool sharesConnection)
    {
        _release = release;
        SharesConnection = sharesConnection;
    }

    /// <summary>
    /// Gets whether the session behind this handle is shared with other operations, so a consumer must take a
    /// non-owning view before handing it to a component that disposes its session.
    /// <para>获取本句柄背后的会话是否与其他操作共享；共享时，消费方必须先取非拥有式视图，再交给会释放会话的组件。</para>
    /// </summary>
    internal bool SharesConnection { get; }

    /// <summary>
    /// Creates a handle that returns one reference to the shared session owned by the direct-session pool.
    /// <para>创建一个在释放时归还直连会话池中共享会话一个引用的句柄。</para>
    /// </summary>
    /// <param name="release">Reference-returning callback, invoked once. <para>归还引用的回调，只调用一次。</para></param>
    /// <returns>The owning handle. <para>持有引用的句柄。</para></returns>
    internal static HdcSessionHandle ForDirect(Action release) => new(release, sharesConnection: true);

    /// <summary>
    /// Creates a handle over a controller lease that is not shared, disposing the lease once.
    /// <para>创建一个包装非共享控制器租约的句柄，释放时释放该租约一次。</para>
    /// </summary>
    /// <param name="lease">The controller lease to own. <para>要持有的控制器租约。</para></param>
    /// <returns>The owning handle. <para>持有租约的句柄。</para></returns>
    internal static HdcSessionHandle ForLease(DeviceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new HdcSessionHandle(lease.Dispose, sharesConnection: false);
    }

    /// <summary>
    /// Returns this reference exactly once; later calls are no-ops.
    /// <para>恰好归还本引用一次；后续调用无操作。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    /// <summary>
    /// Returns this reference exactly once, matching <see cref="Dispose()"/>.
    /// <para>恰好归还本引用一次，与 <see cref="Dispose()"/> 等价。</para>
    /// </summary>
    /// <returns>A completed task once the reference was returned. <para>引用归还后的已完成任务。</para></returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
using System.Diagnostics.CodeAnalysis;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Owns the opened transport (and the protocol session built on it) until disposed.
/// <para>持有已打开的传输（及在其上构建的协议会话）直到被释放。</para>
/// Disposal releases the endpoint reservation.
/// <para>释放时同时归还端点占用。</para>
/// </summary>
public sealed class DeviceLease : IAsyncDisposable, IDisposable
{
    private readonly Action _release;
    private int _disposed;

    internal DeviceLease(DeviceEndpoint endpoint, IAsyncDisposable transport, Action release)
        : this(endpoint, transport, session: null, release)
    {
    }

    internal DeviceLease(DeviceEndpoint endpoint, IAsyncDisposable transport, object? session, Action release)
    {
        Endpoint = endpoint;
        Transport = transport;
        Session = session;
        Commands = new DeviceCommandDispatcher();
        _release = release;
    }

    /// <summary>
    /// The endpoint this lease reserves; its identity is returned to the controller on release.
    /// <para>本租约所占用的端点；释放时其身份归还给控制器。</para>
    /// </summary>
    /// <value>The exact leased endpoint. <para>被租用的精确端点。</para></value>
    public DeviceEndpoint Endpoint { get; }

    /// <summary>
    /// The opened transport owned by this lease and disposed with it.
    /// <para>由本租约持有、随租约一同释放的已打开传输。</para>
    /// </summary>
    /// <value>The async-disposable transport handle. <para>可异步释放的传输句柄。</para></value>
    public IAsyncDisposable Transport { get; }

    /// <summary>
    /// Per-device command pool for this lease; discrete commands submitted here run one at a
    /// time and their results are returned to the submitter.
    /// <para>该租约的每设备指令池；入队的离散指令逐个执行，结果回传给提交方。</para>
    /// </summary>
    /// <value>A live dispatcher cancelled and drained when the lease is disposed.
    /// <para>租约释放时被取消并清空的活动调度器。</para></value>
    public DeviceCommandDispatcher Commands { get; }

    /// <summary>
    /// Protocol session built by a session-kind open; <c>null</c> for plain transport leases.
    /// <para>按会话类型租用时的协议会话；纯传输租约为 <c>null</c>。</para>
    /// </summary>
    /// <value>The boxed session instance, or <see langword="null"/>. <para>装箱后的会话实例，或 <see langword="null"/>。</para></value>
    public object? Session { get; }

    /// <summary>
    /// Gets the typed protocol session when the lease carries one.
    /// <para>租约携带协议会话时按其类型取出。</para>
    /// </summary>
    /// <typeparam name="T">
    /// The session contract to match. <para>待匹配的会话契约。</para>
    /// </typeparam>
    /// <param name="session">
    /// The session when this method returns <see langword="true"/>; otherwise <see langword="null"/>.
    /// <para>方法返回 <see langword="true"/> 时为会话实例；否则为 <see langword="null"/>。</para>
    /// </param>
    /// <returns><see langword="true"/> when the lease carries a session assignable to <typeparamref name="T"/>.
    /// <para>租约携带可赋给 <typeparamref name="T"/> 的会话时为 <see langword="true"/>。</para></returns>
    public bool TryGetSession<T>([NotNullWhen(true)] out T? session) where T : class
    {
        session = Session as T;
        return session is not null;
    }

    /// <summary>
    /// Tears the lease down once: drains the command pool, disposes the session (async first, then
    /// synchronous), the transport, and finally the endpoint reservation; later calls are no-ops.
    /// <para>仅执行一次清理：排空指令池，释放会话（先按异步、再按同步方式）与传输，最后归还端点
    /// 占用；重复调用无操作。</para>
    /// </summary>
    /// <returns>A task that completes after the reservation has been returned.
    /// <para>占用登记归还后完成的任务。</para></returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            try
            {
                await Commands.DisposeAsync().ConfigureAwait(false);
                switch (Session)
                {
                    case IAsyncDisposable asyncSession:
                        await asyncSession.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable syncSession:
                        syncSession.Dispose();
                        break;
                }
            }
            finally
            {
                await Transport.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _release();
        }
    }

    /// <summary>
    /// Synchronous disposal for sync scopes; the lease teardown itself is async.
    /// <para>供同步作用域使用的同步释放；租约清理本身是异步的。</para>
    /// </summary>
    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
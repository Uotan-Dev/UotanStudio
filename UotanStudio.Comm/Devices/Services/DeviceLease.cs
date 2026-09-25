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

    public DeviceEndpoint Endpoint { get; }
    public IAsyncDisposable Transport { get; }

    /// <summary>
    /// Per-device command pool for this lease; discrete commands submitted here run one at a
    /// time and their results are returned to the submitter.
    /// <para>该租约的每设备指令池；入队的离散指令逐个执行，结果回传给提交方。</para>
    /// </summary>
    public DeviceCommandDispatcher Commands { get; }

    /// <summary>Protocol session built by a session-kind open; <c>null</c> for plain transport leases.
    /// 按会话类型租用时的协议会话；纯传输租约为 <c>null</c>。</summary>
    public object? Session { get; }

    /// <summary>Gets the typed protocol session when the lease carries one.
    /// 租约携带协议会话时按其类型取出。</summary>
    public bool TryGetSession<T>([NotNullWhen(true)] out T? session) where T : class
    {
        session = Session as T;
        return session is not null;
    }

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
                // Settle the command pool first: queued commands are cancelled and the running
                // one finishes before the session and transport it may use are torn down.
                // 先清算指令池：排队指令取消、在执行指令结束后，才释放其可能正在使用的会话与传输。
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

    /// <summary>Synchronous disposal for sync scopes; the lease teardown itself is async.
    /// 供同步作用域使用的同步释放；租约清理本身是异步的。</summary>
    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
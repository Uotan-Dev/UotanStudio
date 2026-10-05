using System.Diagnostics.CodeAnalysis;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Owns the opened transport (and the protocol session built on it) until disposed.
/// <para>持有已打开的传输（及在其上构建的协议会话）直到被释放。</para>
/// Directly opened streaming resources register through <see cref="RegisterStream"/> and are closed
/// before the session and transport, so a lease force-released during streaming ends those streams
/// in an orderly way instead of pulling the session out from under them.
/// <para>直开的流式资源经 <see cref="RegisterStream"/> 登记，并先于会话与传输被关闭，
/// 因此流式工作进行中被强制释放的租约会先有序终结这些流，而不是把会话从流底下抽走。</para>
/// Disposal releases the endpoint reservation exactly once, after every owned resource has been released.
/// <para>释放仅在全部所持有资源归还之后执行一次端点占用释放。</para>
/// </summary>
public sealed class DeviceLease : IAsyncDisposable, IDisposable
{
    private readonly Action _release;
    private readonly Lock _lifecycleGate = new();
    private readonly List<IAsyncDisposable> _streams = [];
    private Task? _teardown;

    internal DeviceLease(DeviceEndpoint endpoint, IAsyncDisposable transport, object? session, Action release,
        TransportGeneration? generation = null)
    {
        Endpoint = endpoint;
        Transport = transport;
        Session = session;
        Commands = new DeviceCommandDispatcher();
        Generation = generation;
        _release = release;
    }

    /// <summary>
    /// The endpoint this lease reserves; its identity is returned to the controller on release.
    /// <para>本租约所占用的端点；释放时其身份归还给控制器。</para>
    /// </summary>
    /// <value>The exact leased endpoint. <para>被租用的精确端点。</para></value>
    public DeviceEndpoint Endpoint { get; }

    /// <summary>
    /// The transport instantiation this lease was opened on, when the controller could derive a
    /// stable physical identity; a lease without one never claims generation equivalence.
    /// <para>本租约打开时所处的传输实例化代际（控制器可推导稳定物理身份时提供）；
    /// 缺少代际的租约绝不声称代际等价。</para>
    /// </summary>
    /// <value>The stamped generation, or <see langword="null"/> when identity evidence was absent.
    /// <para>盖印的代际；身份证据缺失时为 <see langword="null"/>。</para></value>
    public TransportGeneration? Generation { get; }

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
    /// Registers a directly opened streaming resource (logcat capture, screen mirroring) with this lease.
    /// <para>把直开的流式资源（logcat 捕获、投屏）登记到本租约。</para>
    /// Registered streams are disposed before the session and the transport when the lease tears down,
    /// and registration after teardown has started fails, so a stream can never outlive the resources
    /// it reads from.
    /// <para>登记的流在租约清理时先于会话与传输被释放；清理已开始后登记会失败，
    /// 因此流绝不会活得比它所读取的资源更久。</para>
    /// </summary>
    /// <param name="stream">
    /// The stream to close together with the lease. <para>随租约一同关闭的流。</para>
    /// </param>
    /// <returns>A handle that removes the stream from the registry when it is disposed; disposing it
    /// does not close the stream. <para>释放时把该流从登记表移除的句柄；释放该句柄不会关闭流本身。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ObjectDisposedException">Teardown of this lease has already started.
    /// <para>本租约的清理已经开始。</para></exception>
    public IDisposable RegisterStream(IAsyncDisposable stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (_lifecycleGate)
        {
            if (_teardown is not null)
            {
                throw new ObjectDisposedException(nameof(DeviceLease), "The lease is already being torn down.");
            }

            _streams.Add(stream);
        }

        return new StreamRegistration(this, stream);
    }

    /// <summary>
    /// Tears the lease down once: closes registered streams, drains the command pool, disposes the
    /// session (async first, then synchronous) and the transport, and finally returns the endpoint
    /// reservation. Concurrent and later calls await the same teardown instead of starting a second one.
    /// <para>仅执行一次清理：关闭已登记的流、排空指令池、释放会话（先按异步、再按同步方式）
    /// 与传输，最后归还端点占用。并发与后续调用等待同一次清理，而不会开启第二次。</para>
    /// </summary>
    /// <returns>A task that completes after the reservation has been returned.
    /// <para>占用登记归还后完成的任务。</para></returns>
    public ValueTask DisposeAsync()
    {
        Task teardown;
        lock (_lifecycleGate)
        {
            _teardown ??= DisposeCoreAsync();
            teardown = _teardown;
        }

        return new ValueTask(teardown);
    }

    private async Task DisposeCoreAsync()
    {
        List<IAsyncDisposable> streams;
        lock (_lifecycleGate)
        {
            streams = [.. _streams];
            _streams.Clear();
        }

        try
        {
            try
            {
                foreach (var stream in streams)
                {
                    try
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        UsbTrace.LogError($"Lease stream disposal failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

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

    private sealed class StreamRegistration(DeviceLease lease, IAsyncDisposable stream) : IDisposable
    {
        public void Dispose() => lease.UnregisterStream(stream);
    }

    private void UnregisterStream(IAsyncDisposable stream)
    {
        lock (_lifecycleGate)
        {
            var index = _streams.FindIndex(candidate => ReferenceEquals(candidate, stream));
            if (index >= 0)
            {
                _streams.RemoveAt(index);
            }
        }
    }
}
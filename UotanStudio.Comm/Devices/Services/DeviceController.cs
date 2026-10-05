using System.Collections.Immutable;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Coordinates discovery and exclusive, generation-checked endpoint leases.
/// <para>协调设备发现与按代数校验的独占端点租约。</para>
/// Owning an endpoint reservation means the endpoint cannot be opened again until the lease that holds
/// it has finished tearing its resources down; disposal of the controller releases every outstanding
/// reservation, watch and lease.
/// <para>持有端点占用意味着在该租约完成资源清理之前该端点无法再次被打开；
/// 释放控制器会归还所有未完成的占用、监视与租约。</para>
/// </summary>
public sealed partial class DeviceController : IDisposable, IAsyncDisposable
{
    private readonly ICommKit? _kit;
    private readonly ImmutableArray<IDeviceDiscoverySource> _sources;
    private readonly IDeviceTransportOpener _opener;
    private readonly ImmutableDictionary<DeviceSessionKind, IDeviceSessionFactory> _factories;
    private readonly Lock _gate = new();
    private DeviceSnapshot _snapshot = new(0, []);
    private long _requestedGeneration;
    private int _lifecycle;
    private readonly HashSet<DeviceEndpointId> _leased = [];
    private readonly Dictionary<DeviceEndpointId, DeviceLease> _openLeases = [];
    private readonly Dictionary<string, ulong> _transportGenerations = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// Raised after a physical USB device disappeared and its leases were released.
    /// <para>物理 USB 设备消失且其租约被释放后触发。</para>
    /// </summary>
    public event Action<DeviceEndpoint>? DeviceRemoved;

    /// <summary>
    /// Raised when a physical USB device changed its metadata (e.g. a mode switch).
    /// <para>物理 USB 设备元数据变化（如模式切换）时触发。</para>
    /// </summary>
    public event Action<DeviceEndpoint>? DeviceChanged;

    /// <summary>
    /// <para>每次清单提交后触发：当同一物理身份（容器 ID 优先，其次可用物理端口路径）在新旧快照
    /// 中观测到不同的协议集合时发布一条模式迁移。关联绝不使用 VID/PID、枚举顺序或列表位置。</para>
    /// Raised after each committed inventory: one transition per physical identity whose observed
    /// protocol set changed between the previous and the new snapshot, correlated by container id
    /// first and usable physical port path second; never by VID/PID, enumeration order, or position.
    /// </summary>
    /// <remarks>
    /// Transitions are observational evidence for cross-protocol flows such as Sahara to Firehose
    /// relink or MTK BROM to DA migration; a consumer must still verify identity on the opened
    /// handle before binding a new session.
    /// <para>迁移只是观测证据，供 Sahara→Firehose 重链或 MTK BROM→DA 迁移等跨协议流程消费；
    /// 绑定新会话前仍须在已打开句柄上复核身份。</para>
    /// </remarks>
    public event Action<ModeTransition>? ModeTransitionsObserved;

    /// <summary>
    /// Per-device static property cache shared by all controller consumers.
    /// <para>所有消费方共享的每设备静态参数缓存。</para>
    /// </summary>
    /// <value>The cache; entries are keyed by <see cref="PropertyCacheKey(DeviceEndpoint)"/> and are
    /// dropped for an endpoint when its device is removed.
    /// <para>缓存；条目按 <see cref="PropertyCacheKey(DeviceEndpoint)"/> 键控，设备移除时丢弃对应端点的条目。</para></value>
    public DevicePropertyCache Properties { get; } = new();

    internal DeviceController(IEnumerable<IDeviceDiscoverySource> sources, IDeviceTransportOpener opener)
        : this(sources, opener, DefaultDeviceSessionFactories.All)
    {
    }

    internal DeviceController(IEnumerable<IDeviceDiscoverySource> sources, IDeviceTransportOpener opener,
        IEnumerable<IDeviceSessionFactory> factories, ICommKit? kit = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(factories);
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _kit = kit;
        _sources = [.. sources];
        if (_sources.Any(source => source is null) ||
            _sources.Select(source => source.Name).Distinct(StringComparer.Ordinal).Count() != _sources.Length ||
            _sources.Any(source => string.IsNullOrWhiteSpace(source.Name)))
        {
            throw new ArgumentException("Discovery source names must be nonempty and unique.", nameof(sources));
        }

        var builder = ImmutableDictionary.CreateBuilder<DeviceSessionKind, IDeviceSessionFactory>();
        foreach (var factory in factories)
        {
            if (factory is null || builder.ContainsKey(factory.Kind))
            {
                throw new ArgumentException("Session factories must be nonnull with unique kinds.", nameof(factories));
            }

            builder[factory.Kind] = factory;
        }

        _factories = builder.ToImmutable();
    }

    /// <summary>
    /// The latest committed inventory: a generation number plus the endpoints from the most recent
    /// successful refresh.
    /// <para>最近一次提交的清单：代数编号加最近一次成功刷新得到的端点集合。</para>
    /// </summary>
    /// <value>A consistent snapshot safe to read without locking; stale snapshots are rejected when opening.
    /// <para>无需加锁即可读取的一致性快照；过期快照在开启时会被拒绝。</para></value>
    public DeviceSnapshot Snapshot
    {
        get { lock (_gate) { return _snapshot; } }
    }

    /// <summary>
    /// Runs every discovery source in registration order and commits the combined inventory as a
    /// new generation. Refreshes are single-flight: a caller that queued behind an in-flight refresh
    /// returns the snapshot committed while it waited instead of enumerating again, so every wait is
    /// bounded by real enumeration rounds and each round sees the true discovery result. A concurrent
    /// later request wins the commit, so the returned snapshot may be newer than this call's result.
    /// <para>按注册顺序运行每个发现源，并把合并后的清单提交为新的一代。刷新为单飞串行：
    /// 在已启动的刷新之后排队的调用方会直接返回等待期间已提交的快照而不再枚举，
    /// 因此等待语义始终由真实枚举轮次界定，每一轮看到的都是真实发现结果。
    /// 并发的后发请求仍可能赢得提交，因此返回的快照可能比本次调用的结果更新。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Passed to each source and re-checked after every source. <para>传递给每个发现源，并在每个源结束后再次检查。</para>
    /// </param>
    /// <returns>The currently committed snapshot. <para>当前已提交的快照。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// A source returned a null, foreign-sourced, malformed or duplicate endpoint.
    /// <para>某发现源返回了 null、来源不符、格式非法或重复的端点。</para></exception>
    /// <exception cref="ObjectDisposedException">
    /// The controller has been disposed, including while the call queued behind an in-flight refresh.
    /// <para>控制器已被释放，包括在已启动刷新之后排队期间被释放。</para></exception>
    public async Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var observedGeneration = Snapshot.Generation;
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DeviceSnapshot result;
        ModeTransition[] transitions = [];
        try
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                result = _snapshot;
            }

            if (result.Generation > observedGeneration)
            {
                return result;
            }

            var generation = Interlocked.Increment(ref _requestedGeneration);
            var endpoints = ImmutableArray.CreateBuilder<DeviceEndpoint>();
            var seen = new HashSet<DeviceEndpointId>();
            foreach (var source in _sources)
            {
                var discovered = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var endpoint in discovered)
                {
                    if (endpoint is null || endpoint.Source != source.Name ||
                        string.IsNullOrWhiteSpace(endpoint.Transport) || string.IsNullOrWhiteSpace(endpoint.Address) ||
                        !seen.Add(endpoint.Id))
                    {
                        throw new InvalidOperationException("Discovery returned an invalid or duplicate endpoint.");
                    }

                    endpoints.Add(endpoint);
                }
            }

            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation == _requestedGeneration)
                {
                    var committed = new DeviceSnapshot(generation, endpoints.ToImmutable());
                    transitions = ObserveModeTransitionsLocked(_snapshot, committed);
                    _snapshot = committed;
                }

                result = _snapshot;
            }
        }
        finally
        {
            _refreshGate.Release();
        }

        foreach (var transition in transitions)
        {
            ModeTransitionsObserved?.Invoke(transition);
        }

        return result;
    }

    /// <summary>
    /// Leases the exact endpoint and opens only its transport, without building a protocol session.
    /// <para>租用精确端点并仅打开其传输，不构建协议会话。</para>
    /// </summary>
    /// <param name="snapshot">
    /// The live snapshot the endpoint was taken from; a stale snapshot is rejected. <para>端点所来自的当前快照；过期快照会被拒绝。</para>
    /// </param>
    /// <param name="id">
    /// Exact endpoint identity to lease. <para>待租用的精确端点身份。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the open before the transport is acquired; a leased endpoint is released on cancellation.
    /// <para>在传输取得之前取消开启；取消时已租用的端点会被归还。</para>
    /// </param>
    /// <returns>A transport-only lease whose <see cref="DeviceLease.Session"/> is null.
    /// <para>纯传输租约，其 <see cref="DeviceLease.Session"/> 为 null。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// The snapshot is stale, the endpoint is already leased, or the opener returned no transport.
    /// <para>快照过期、端点已被租用，或开启器未返回传输。</para></exception>
    /// <exception cref="KeyNotFoundException">
    /// The endpoint is not present in the snapshot. <para>快照中不存在该端点。</para></exception>
    /// <exception cref="ObjectDisposedException">
    /// The controller has been disposed. <para>控制器已被释放。</para></exception>
    public async Task<DeviceLease> OpenAsync(DeviceSnapshot snapshot, DeviceEndpointId id,
        CancellationToken cancellationToken = default)
    {
        var (endpoint, transport, generation) = await OpenTransportAsync(snapshot, id, cancellationToken).ConfigureAwait(false);
        DeviceLease? lease = null;
        try
        {
            ThrowIfDisposed();
            lease = new DeviceLease(endpoint, transport, session: null, () => Release(id, lease!), generation);
            lock (_gate)
            {
                _openLeases[id] = lease;
            }

            return lease;
        }
        catch
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Release(id, null);
            }

            throw;
        }
    }

    /// <summary>
    /// <para>把一条已构建的协议会话纳入控制器租约管理：占用按端点身份登记，释放时归还，
    /// 传输槽为无操作占位（会话自身持有其传输）。用于 server 会话等没有可独占 USB 端点的路由。</para>
    /// Adopts an already-built protocol session into a controller lease: the reservation is
    /// registered under the endpoint identity and returned on release, while the transport slot is
    /// a no-op placeholder (the session owns its transport). Intended for server routes that have
    /// no exclusively ownable USB endpoint.
    /// </summary>
    /// <param name="endpoint">
    /// The endpoint whose identity carries the reservation. <para>承载占用登记、提供身份的端点。</para>
    /// </param>
    /// <param name="session">
    /// The already-built protocol session to expose through the lease. <para>已经构建好、经租约对外暴露的协议会话。</para>
    /// </param>
    /// <returns>A lease whose transport slot is inert and whose release returns the reservation.
    /// <para>传输槽为无操作占位、释放时归还占用登记的租约。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// The endpoint is already leased. <para>该端点已被租用。</para></exception>
    /// <exception cref="ObjectDisposedException">
    /// The controller has been disposed. <para>控制器已被释放。</para></exception>
    public DeviceLease AdoptSession(DeviceEndpoint endpoint, object session)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_leased.Add(endpoint.Id))
            {
                throw new InvalidOperationException("Endpoint is already leased.");
            }
        }

        DeviceLease? lease = null;
        try
        {
            lease = new DeviceLease(endpoint, NoopTransport.Instance, session, () => Release(endpoint.Id, lease!));
            lock (_gate)
            {
                _openLeases[endpoint.Id] = lease;
            }

            return lease;
        }
        catch
        {
            Release(endpoint.Id, null);
            throw;
        }
    }

    private sealed class NoopTransport : IAsyncDisposable
    {
        public static readonly NoopTransport Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task<(DeviceEndpoint Endpoint, IAsyncDisposable Transport, TransportGeneration? Generation)> OpenTransportAsync(
        DeviceSnapshot snapshot, DeviceEndpointId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        DeviceEndpoint endpoint;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (snapshot.Generation != _snapshot.Generation || !ReferenceEquals(snapshot, _snapshot))
            {
                throw new InvalidOperationException("Device snapshot is stale.");
            }

            endpoint = snapshot.Endpoints.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new KeyNotFoundException("Endpoint is not present in the snapshot.");
            if (!_leased.Add(id))
            {
                throw new InvalidOperationException("Endpoint is already leased.");
            }
        }

        try
        {
            var transport = await _opener.OpenAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (transport is null)
            {
                throw new InvalidOperationException("Transport opener returned no transport.");
            }

            lock (_gate)
            {
                if (!cancellationToken.IsCancellationRequested && ReferenceEquals(snapshot, _snapshot))
                {
                    return (endpoint, transport, NextTransportGenerationLocked(endpoint.UsbInfo));
                }
            }

            await transport.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Device snapshot became stale while opening.");
        }
        catch
        {
            Release(id, null);
            throw;
        }
    }

    /// <summary>
    /// Reads the current transport generation observed for a physical identity, without opening
    /// anything or advancing it. A consumer holding an older generation must treat its cached
    /// observations and leases as retired. Generation entries are pruned when the OS reports the
    /// physical device removed, so a vanished identity reads as never observed.
    /// <para>读取某物理身份当前已观测的传输代际，不打开任何资源，也不推进代数。持有较旧代际的
    /// 消费方必须把其缓存观测与租约视为已废弃。操作系统报告物理设备移除时代际条目会被清除，
    /// 因此已消失的身份会被读成从未观测。</para>
    /// </summary>
    /// <param name="identity">
    /// The physical identity to look up. <para>待查询的物理身份。</para>
    /// </param>
    /// <param name="generation">
    /// The latest generation stamped by an open or an observed mode transition.
    /// <para>由打开或已观测模式迁移盖印的最近代际。</para>
    /// </param>
    /// <returns><see langword="false"/> when no transport instantiation was ever observed for the identity.
    /// <para>从未观测到该身份的任何传输实例化时返回 <see langword="false"/>。</para></returns>
    public bool TryPeekTransportGeneration(PhysicalDeviceIdentity identity, out TransportGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ulong value;
        lock (_gate)
        {
            if (!_transportGenerations.TryGetValue(identity.Key, out value))
            {
                generation = default;
                return false;
            }
        }

        generation = new TransportGeneration(value);
        return true;
    }

    private TransportGeneration? NextTransportGenerationLocked(UsbDeviceInfo? info)
    {
        var identity = info is null ? null : PhysicalDeviceIdentity.From(info);
        if (identity is null)
        {
            return null;
        }

        return new TransportGeneration(
            _transportGenerations[identity.Key] = _transportGenerations.GetValueOrDefault(identity.Key) + 1);
    }

    /// <summary>
    /// <para>读取某物理身份最近一次被推进的传输代际（由传输打开或模式迁移盖印），本身绝不推进代数；
    /// 按钥匙开启的租约因此盖的是“打开前已观测”的代际，持有更旧代际的消费方只会更早把自己的
    /// 缓存观测与租约判为过期，判定方向不变。身份缺失或该身份的代际已随设备移除被清除时
    /// 返回 <see langword="null"/>。</para>
    /// Reads the last advanced transport generation observed for a physical identity (stamped by a
    /// transport open or a mode transition) without advancing it; a by-key lease therefore carries the
    /// generation as of before its own open, and a consumer holding an older generation only treats
    /// its cached observations and leases as retired sooner, never later. Returns
    /// <see langword="null"/> when identity evidence is absent or the generation was pruned with the
    /// device's removal.
    /// </summary>
    private TransportGeneration? CurrentGenerationFor(UsbDeviceInfo? info)
    {
        var identity = info is null ? null : PhysicalDeviceIdentity.From(info);
        if (identity is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _transportGenerations.TryGetValue(identity.Key, out var value)
                ? new TransportGeneration(value)
                : null;
        }
    }

    /// <summary>
    /// <para>设备移除时按物理身份键清除已缓存的传输代际：代际字典随 USB 拓扑无界增长的唯一
    /// 回收点。移除后 <see cref="TryPeekTransportGeneration(PhysicalDeviceIdentity, out TransportGeneration)"/>
    /// 对该身份报告“从未观测”，持有旧代际的 parked 租约据此更早过期；操作系统日后把同一容器
    /// 身份分配给新硬件时代际从 1 重新起算，这是设备消失又重现时的既定语义。</para>
    /// Drops cached transport generations by physical identity when a device is removed: the single
    /// reclamation point against unbounded growth of the generation map with USB topology churn.
    /// After pruning, <see cref="TryPeekTransportGeneration(PhysicalDeviceIdentity, out TransportGeneration)"/>
    /// reports the identity as never observed, so parked leases holding an older generation expire
    /// earlier; if the OS later reuses the same container identity for fresh hardware the generation
    /// restarts at 1, which is the intended semantics for a device that disappeared and returned.
    /// </summary>
    private void PruneTransportGenerationsLocked(UsbDeviceInfo removedDevice, DeviceEndpoint[] matchedEndpoints)
    {
        if (PhysicalDeviceIdentity.From(removedDevice) is { } removedIdentity)
        {
            _transportGenerations.Remove(removedIdentity.Key);
        }

        foreach (var endpoint in matchedEndpoints)
        {
            if (endpoint.UsbInfo is { } info && PhysicalDeviceIdentity.From(info) is { } identity)
            {
                _transportGenerations.Remove(identity.Key);
            }
        }
    }

    private ModeTransition[] ObserveModeTransitionsLocked(DeviceSnapshot previous, DeviceSnapshot current)
    {
        var before = ModeProjections(previous.Endpoints);
        if (before.Count == 0)
        {
            return [];
        }

        List<ModeTransition> transitions = [];
        foreach (var (key, (identity, protocols)) in ModeProjections(current.Endpoints))
        {
            if (!before.TryGetValue(key, out var old) || string.Equals(old.Protocols, protocols, StringComparison.Ordinal))
            {
                continue;
            }

            var evidence = identity.ContainerId is not null
                ? ModeTransitionEvidence.Container
                : ModeTransitionEvidence.PhysicalPort;
            var generation = new TransportGeneration(
                _transportGenerations[key] = _transportGenerations.GetValueOrDefault(key) + 1);
            transitions.Add(new ModeTransition(identity, old.Protocols, protocols, generation, evidence, DateTimeOffset.UtcNow));
        }

        return [.. transitions];
    }

    private static Dictionary<string, (PhysicalDeviceIdentity Identity, string Protocols)> ModeProjections(
        ImmutableArray<DeviceEndpoint> endpoints)
    {
        var raw = new Dictionary<string, (PhysicalDeviceIdentity Identity, SortedSet<string> Protocols)>(
            StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            if (endpoint.UsbInfo is not { } info ||
                PhysicalDeviceIdentity.From(info) is not { } identity ||
                identity.ContainerId is null && identity.PhysicalPortPath is null ||
                string.IsNullOrEmpty(endpoint.Protocol))
            {
                continue;
            }

            if (!raw.TryGetValue(identity.Key, out var entry))
            {
                raw[identity.Key] = entry = (identity, new SortedSet<string>(StringComparer.Ordinal));
            }

            entry.Protocols.Add(endpoint.Protocol);
        }

        var joined = new Dictionary<string, (PhysicalDeviceIdentity, string)>(StringComparer.Ordinal);
        foreach (var pair in raw)
        {
            joined[pair.Key] = (pair.Value.Identity, string.Join('+', pair.Value.Protocols));
        }

        return joined;
    }

    /// <summary>
    /// <para>归还一次端点占用。占用登记只在没有更新租约接管该端点时被清除：强制分离
    /// （设备移除、server 消失）会先把租约从 <c>_openLeases</c> 摘除，但把 <c>_leased</c>
    /// 条目保留为“释放中”占位，直到该租约完成其全部异步资源清理并由本方法归还占用；
    /// 因此并发的 <c>OpenAsync</c> 在旧租约仍持有传输期间绝不会抢占同一端点。
    /// 对同一占用可重复调用（幂等）。</para>
    /// Returns one endpoint reservation. The reservation is cleared only when no newer lease has taken
    /// over the endpoint: forced detach (device removal, vanished server) removes the lease from the
    /// open-lease table first while the <c>_leased</c> entry stays as a releasing placeholder until that
    /// lease has finished tearing down its resources and calls back here, so a concurrent open never
    /// preempts an endpoint whose previous transport is still held. Repeated calls for the same
    /// reservation are no-ops.
    /// </summary>
    private void Release(DeviceEndpointId id, DeviceLease? lease)
    {
        lock (_gate)
        {
            var current = _openLeases.GetValueOrDefault(id);
            if (current is null || ReferenceEquals(current, lease))
            {
                _leased.Remove(id);
            }

            if (lease is null || ReferenceEquals(current, lease))
            {
                _openLeases.Remove(id);
            }
        }
    }

    /// <summary>
    /// <para>推导 <see cref="DevicePropertyCache"/> 使用的端点键：优先为物理身份键（容器 ID 优先，
    /// 其次序列号推导的身份键），使同一物理设备跨模式切换与接口重编号共享一份缓存；
    /// 身份证据缺失时退回由源、传输与地址组成的完整端点身份字符串，绝不退回裸地址或
    /// 会被操作系统回收复用的设备路径。</para>
    /// Derives the <see cref="DevicePropertyCache"/> key for one endpoint: the physical identity key
    /// when available (container id first, serial-derived identity second) so one physical device
    /// keeps a single cache entry across mode switches and interface renumbering; falls back to the
    /// full endpoint identity composed of source, transport and address when identity evidence is
    /// absent, never to the bare address or an OS device path that gets recycled.
    /// </summary>
    /// <param name="endpoint">
    /// The endpoint whose cache key is derived. <para>待推导缓存键的端点。</para>
    /// </param>
    /// <returns>The cache key accepted by <see cref="DevicePropertyCache"/>.
    /// <para><see cref="DevicePropertyCache"/> 可接受的缓存键。</para></returns>
    internal static string PropertyCacheKey(DeviceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.UsbInfo is { } info && PhysicalDeviceIdentity.From(info) is { } identity
            ? identity.Key
            : $"{endpoint.Id.Source}|{endpoint.Id.Transport}|{endpoint.Id.Address}";
    }

    /// <summary>
    /// Forces disposal of every open lease bound to a removed physical USB device and drops
    /// its cached properties. Called by the removal watch when the OS reports the device gone.
    /// <para>强制释放绑定到已消失物理 USB 设备的所有租约，并丢弃其缓存参数。由移除监视在
    /// 操作系统报告设备消失时调用。</para>
    /// </summary>
    /// <remarks>
    /// Detaching a lease keeps its endpoint reservation as a releasing placeholder; the reservation is
    /// returned by the lease's own release callback once streams, session and transport have been torn
    /// down. The <see cref="DeviceLease.DisposeAsync"/> teardown is a single shared operation, so the
    /// background release raced here against a concurrent user disposal completes exactly once.
    /// <para>摘除租约时其端点占用保留为“释放中”占位；占用在流、会话与传输完成清理后由租约
    /// 自身的释放回调归还。<see cref="DeviceLease.DisposeAsync"/> 的清理是一次性共享操作，
    /// 因此此处派生的后台释放与并发的用户释放只会真正执行一次。</para>
    /// </remarks>
    internal void HandleDeviceRemoved(UsbDeviceInfo removedDevice, bool metadataChanged)
    {
        DeviceLease[] leases;
        DeviceEndpoint[] endpoints;
        lock (_gate)
        {
            var matches = _openLeases.Where(pair => MatchesRemovedDevice(pair.Value.Endpoint, removedDevice)).ToList();
            foreach (var (id, _) in matches)
            {
                _openLeases.Remove(id);
            }

            leases = [.. matches.Select(pair => pair.Value)];
            endpoints = [.. matches.Select(pair => pair.Value.Endpoint)];
            if (!metadataChanged)
            {
                PruneTransportGenerationsLocked(removedDevice, endpoints);
            }
        }

        foreach (var lease in leases)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    UsbTrace.Log($"Lease removal dispose failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        foreach (var endpoint in endpoints)
        {
            if (metadataChanged)
            {
                DeviceChanged?.Invoke(endpoint);
            }
            else
            {
                Properties.Invalidate(PropertyCacheKey(endpoint));
                DeviceRemoved?.Invoke(endpoint);
            }
        }
    }

    /// <summary>
    /// <para>判定某端点是否对应被移除（或元数据变化）的那台物理 USB 设备。优先使用
    /// <see cref="PhysicalDeviceIdentity.CorrelatesWith"/>：容器 ID 优先、可用物理端口链次之；
    /// 容器 ID 与端口链都缺失时回退到 OS 设备路径，而路径会被操作系统回收复用，所以路径
    /// 相等只在关联不确定时成立，且绝不覆盖确定性的身份判定。</para>
    /// Decides whether an endpoint belongs to the physical USB device that was removed (or whose
    /// metadata changed). Correlation runs through <see cref="PhysicalDeviceIdentity.CorrelatesWith"/>
    /// with container id first and usable physical-port path second; the OS device path is consulted
    /// only when that correlation is indeterminate, because bus/device paths are recycled by the OS
    /// and a path match never overrides a decisive container or port verdict.
    /// </summary>
    private static bool MatchesRemovedDevice(DeviceEndpoint endpoint, UsbDeviceInfo removedDevice)
    {
        if (CorrelatePhysicalObservation(endpoint.UsbInfo, removedDevice) is { } correlated)
        {
            return correlated;
        }

        var path = endpoint.Usb?.Path;
        return !string.IsNullOrEmpty(path)
            && string.Equals(path, removedDevice.DevicePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <para>在容器 ID 优先、物理端口链次之的证据下判定两个 USB 观测是否指向同一物理硬件；
    /// 证据缺失或无法推导身份时返回 <see langword="null"/>，由调用方决定其不确定兜底
    /// （设备路径或拒绝操作），绝不使用 VID/PID 或枚举顺序。</para>
    /// Decides whether two USB observations describe the same physical hardware from container-id
    /// first and physical-port-path evidence second; returns <see langword="null"/> when evidence is
    /// missing or no identity can be derived, leaving the indeterminate fallback (device path or
    /// refusing the operation) to the caller. Never uses VID/PID or enumeration order.
    /// </summary>
    private static bool? CorrelatePhysicalObservation(UsbDeviceInfo? observed, UsbDeviceInfo? candidate)
    {
        if (observed is null || candidate is null ||
            PhysicalDeviceIdentity.From(observed) is not { } first ||
            PhysicalDeviceIdentity.From(candidate) is not { } second)
        {
            return null;
        }

        return first.CorrelatesWith(second) switch
        {
            DeviceCorrelationResult.Same => true,
            DeviceCorrelationResult.Different => false,
            _ => null,
        };
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _lifecycle) != 0)
        {
            throw new ObjectDisposedException(nameof(DeviceController), "The device controller has been disposed.");
        }
    }

    /// <summary>
    /// <para>释放控制器持有的全部资源：停止 server 监视与 USB 移除监视，等待所有活动租约
    /// 完成清理（登记的流、指令池、会话、传输）并归还占用。重复调用无操作。</para>
    /// Releases every resource the controller owns: stops the server-lifecycle watch and the USB
    /// removal watch, then awaits the teardown of all open leases (registered streams, command pools,
    /// sessions and transports) and returns their reservations. Repeated calls are no-ops.
    /// </summary>
    /// <returns>A task that completes after every outstanding lease has been torn down.
    /// <para>所有未完成的租约完成清理后返回的任务。</para></returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _lifecycle, 1) != 0)
        {
            return;
        }

        var (leases, serverWatch, removalWatch) = DetachOwnedResourcesLocked();
        StopWatches(serverWatch, removalWatch);
        await DisposeLeasesAsync(leases).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>同步释放：就地停止 server 监视与 USB 移除监视，并把租约清理移交后台线程池，
    /// 绝不等待异步释放（不做 sync-over-async）。需要观察清理完成的调用方应使用
    /// <see cref="DisposeAsync"/>。</para>
    /// Synchronous teardown: stops the server-lifecycle watch and the USB removal watch inline and hands
    /// lease teardown to the thread pool without ever waiting on asynchronous release. Callers that
    /// must observe completed teardown use <see cref="DisposeAsync"/> instead.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _lifecycle, 1) != 0)
        {
            return;
        }

        var (leases, serverWatch, removalWatch) = DetachOwnedResourcesLocked();
        StopWatches(serverWatch, removalWatch);
        _ = Task.Run(() => DisposeLeasesAsync(leases));
    }

    private (List<DeviceLease> Leases, CancellationTokenSource? ServerWatch, IDisposable? RemovalWatch) DetachOwnedResourcesLocked()
    {
        lock (_gate)
        {
            var leases = new List<DeviceLease>(_openLeases.Values);
            _openLeases.Clear();
            return (leases, _serverWatch, _removalWatch);
        }
    }

    private static void StopWatches(CancellationTokenSource? serverWatch, IDisposable? removalWatch)
    {
        removalWatch?.Dispose();
        serverWatch?.Cancel();
        serverWatch?.Dispose();
    }

    private static async Task DisposeLeasesAsync(List<DeviceLease> leases)
    {
        foreach (var lease in leases)
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                UsbTrace.LogError($"Lease disposal failed during controller teardown: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
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
/// </summary>
public sealed partial class DeviceController
{
    private readonly ICommKit? _kit;
    private readonly ImmutableArray<IDeviceDiscoverySource> _sources;
    private readonly IDeviceTransportOpener _opener;
    private readonly ImmutableDictionary<DeviceSessionKind, IDeviceSessionFactory> _factories;
    private readonly object _gate = new();
    private DeviceSnapshot _snapshot = new(0, []);
    private long _requestedGeneration;
    private readonly HashSet<DeviceEndpointId> _leased = [];
    private readonly Dictionary<DeviceEndpointId, DeviceLease> _openLeases = [];
    private readonly Dictionary<string, ulong> _transportGenerations = [];

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
    /// <value>The cache; entries for a device address are dropped when the device is removed.
    /// <para>缓存；设备移除时丢弃按设备地址登记的条目。</para></value>
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
    /// new generation. A concurrent later request wins the commit, so the returned snapshot may be
    /// newer than this call's result.
    /// <para>按注册顺序运行每个发现源，并把合并后的清单提交为新的一代。并发的后发请求会赢得提交，
    /// 因此返回的快照可能比本次调用的结果更新。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Passed to each source and re-checked after every source. <para>传递给每个发现源，并在每个源结束后再次检查。</para>
    /// </param>
    /// <returns>The currently committed snapshot. <para>当前已提交的快照。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// A source returned a null, foreign-sourced, malformed or duplicate endpoint.
    /// <para>某发现源返回了 null、来源不符、格式非法或重复的端点。</para></exception>
    public async Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        DeviceSnapshot result;
        ModeTransition[] transitions = [];
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
    public async Task<DeviceLease> OpenAsync(DeviceSnapshot snapshot, DeviceEndpointId id,
        CancellationToken cancellationToken = default)
    {
        var (endpoint, transport, generation) = await OpenTransportAsync(snapshot, id, cancellationToken).ConfigureAwait(false);
        DeviceLease? lease = null;
        try
        {
            lease = new DeviceLease(endpoint, transport, session: null, () => Release(id, lease!), generation);
            lock (_gate)
            {
                _openLeases[id] = lease;
            }

            return lease;
        }
        catch
        {
            Release(id, null);
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
    public DeviceLease AdoptSession(DeviceEndpoint endpoint, object session)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
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
    /// anything. A consumer holding an older generation must treat its cached observations and
    /// leases as retired.
    /// <para>读取某物理身份当前已观测的传输代际，不打开任何资源。持有较旧代际的消费方必须把
    /// 其缓存观测与租约视为已废弃。</para>
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

    private TransportGeneration? CurrentGenerationFor(UsbDeviceInfo? info)
    {
        lock (_gate)
        {
            return NextTransportGenerationLocked(info);
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

    private void Release(DeviceEndpointId id, DeviceLease? lease)
    {
        lock (_gate)
        {
            _leased.Remove(id);
            if (lease is null || ReferenceEquals(_openLeases.GetValueOrDefault(id), lease))
            {
                _openLeases.Remove(id);
            }
        }
    }

    /// <summary>
    /// Forces disposal of every open lease bound to a removed physical USB device and drops
    /// its cached properties. Called by the removal watch when the OS reports the device gone.
    /// <para>强制释放绑定到已消失物理 USB 设备的所有租约，并丢弃其缓存参数。由移除监视在
    /// 操作系统报告设备消失时调用。</para>
    /// </summary>
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
                _leased.Remove(id);
            }

            leases = [.. matches.Select(pair => pair.Value)];
            endpoints = [.. matches.Select(pair => pair.Value.Endpoint)];
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
                Properties.Invalidate(endpoint.Address);
                DeviceRemoved?.Invoke(endpoint);
            }
        }
    }

    private static bool MatchesRemovedDevice(DeviceEndpoint endpoint, UsbDeviceInfo removedDevice)
    {
        var usb = endpoint.Usb;
        if (usb is null || string.IsNullOrEmpty(usb.Path))
        {
            return false;
        }

        return string.Equals(usb.Path, removedDevice.DevicePath, StringComparison.OrdinalIgnoreCase);
    }
}
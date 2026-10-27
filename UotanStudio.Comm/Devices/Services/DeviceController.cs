using System.Collections.Immutable;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Hash.Fnv1a;

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
    private readonly Dictionary<DeviceEndpointId, long> _placeholderSince = [];
    private readonly Dictionary<DeviceEndpointId, DeviceLease> _openLeases = [];
    private readonly Dictionary<string, ulong> _transportGenerations = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Dictionary<string, (PhysicalDeviceIdentity Identity, string Protocols)>? _modeProjection;
    private (ulong Sum, ulong Xor, int Count) _modeProjectionDigest;
    private long _modeProjectionGeneration = -1;

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
    /// Raised once per batch of OS USB topology changes observed by the removal watch, after leases tied to
    /// removed or changed devices were released. Subscribers observe the change set; exceptions are isolated.
    /// <para>移除监视每观察到一批 USB 拓扑变化、且已释放被移除或变更设备的租约后触发。
    /// 订阅方只观察变化集；其异常会被隔离。</para>
    /// </summary>
    public event Action<IReadOnlyList<UsbDeviceChange>>? UsbTopologyChanged;

    /// <summary>
    /// Raised after each committed inventory: one transition per physical identity whose observed
    /// protocol set changed between the previous and the new snapshot, correlated by container id
    /// first and usable physical port path second; never by VID/PID, enumeration order, or position.
    /// <para>每次清单提交后触发：当同一物理身份（容器 ID 优先，其次可用物理端口路径）在新旧快照
    /// 中观测到不同的协议集合时发布一条模式迁移。关联绝不使用 VID/PID、枚举顺序或列表位置。</para>
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

    /// <summary>Runs every discovery source in registration order and commits the combined inventory as a new generation. Refreshes are single-flight, so a caller queued behind an in-flight refresh returns the snapshot committed while it waited.
    /// <para>按注册顺序执行所有发现源，并把合并后的设备清单提交为新的一代。刷新是单飞式的：排在在途刷新之后的调用方，返回其等待期间已提交的那份快照。</para></summary>
    /// <param name="cancellationToken">Passed to each source and re-checked after every source.
    /// <para>传递给每个发现源，并在每个源结束后再次检查。</para></param>
    /// <returns>The currently committed snapshot.<para>当前已提交的快照。</para></returns>
    /// <exception cref="InvalidOperationException">A source returned a null, foreign-sourced, malformed or duplicate endpoint.
    /// <para>某发现源返回了 null、来源不符、格式非法或重复的端点。</para></exception>
    /// <exception cref="ObjectDisposedException">The controller has been disposed, including while the call queued behind an in-flight refresh.
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
                IReadOnlyList<DeviceEndpoint> discovered;
                try
                {
                    discovered = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    var previous = result.Endpoints
                        .Where(endpoint => endpoint.Source == source.Name)
                        .ToArray();
                    if (previous.Length == 0)
                    {
                        throw;
                    }

                    discovered = previous;
                }

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

    /// <summary>Leases the exact endpoint and opens only its transport, without building a protocol session.
    /// <para>租用精确端点并仅打开其传输，不构建协议会话。</para></summary>
    /// <param name="snapshot">The live snapshot the endpoint was taken from; a stale snapshot is rejected.
    /// <para>端点所来自的当前快照；过期快照会被拒绝。</para></param>
    /// <param name="id">Exact endpoint identity to lease.<para>待租用的精确端点身份。</para></param>
    /// <param name="cancellationToken">Cancels the open before the transport is acquired; a leased endpoint is released on cancellation.
    /// <para>在传输取得之前取消开启；取消时已租用的端点会被归还。</para></param>
    /// <returns>A transport-only lease whose <see cref="DeviceLease.Session"/> is null.
    /// <para>纯传输租约，其 <see cref="DeviceLease.Session"/> 为 null。</para></returns>
    /// <exception cref="InvalidOperationException">The snapshot is stale, the endpoint is already leased, or the opener returned no transport.
    /// <para>快照过期、端点已被租用，或开启器未返回传输。</para></exception>
    /// <exception cref="KeyNotFoundException">The endpoint is not present in the snapshot.
    /// <para>快照中不存在该端点。</para></exception>
    /// <exception cref="ObjectDisposedException">The controller has been disposed.
    /// <para>控制器已被释放。</para></exception>
    public async Task<DeviceLease> OpenAsync(DeviceSnapshot snapshot, DeviceEndpointId id,
        CancellationToken cancellationToken = default)
    {
        (DeviceEndpoint Endpoint, IAsyncDisposable Transport, TransportGeneration? Generation) opened;
        try
        {
            opened = await OpenTransportAsync(snapshot, id, cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceEndpointBusyException busy)
        {
            CommTrace.State("lease",
                $"busy: {id.Source}/{id.Transport} {id.Address} is held by another lease (snapshot generation {snapshot.Generation}); {busy.Message} Reservations: {string.Join(", ", DescribeReservations(id.Address))}.");
            throw;
        }

        var (endpoint, transport, generation) = opened;
        DeviceLease? lease = null;
        try
        {
            ThrowIfDisposed();
            lease = new DeviceLease(endpoint, transport, session: null, () => Release(id, lease!), generation);
            lock (_gate)
            {
                _openLeases[id] = lease;
            }

            CommTrace.Debug("lease",
                $"leased {endpoint.Protocol}/{endpoint.Route} {endpoint.Address} (generation {(generation.HasValue ? generation.Value.ToString() : "none")}).");
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

    /// <summary>Adopts an already-built protocol session into a controller lease: the reservation is registered under the endpoint identity and returned on release, while the transport slot is a no-op placeholder (the session owns its transport). Intended for server routes that have no exclusively ownable USB endpoint.
    /// <para>把一条已构建的协议会话纳入控制器租约管理：占用按端点身份登记，释放时归还， 传输槽为无操作占位（会话自身持有其传输）。用于 server 会话等没有可独占 USB 端点的路由。</para></summary>
    /// <param name="endpoint">The endpoint whose identity carries the reservation.
    /// <para>承载占用登记、提供身份的端点。</para></param>
    /// <param name="session">The already-built protocol session to expose through the lease.
    /// <para>已经构建好、经租约对外暴露的协议会话。</para></param>
    /// <returns>A lease whose transport slot is inert and whose release returns the reservation.
    /// <para>传输槽为无操作占位、释放时归还占用登记的租约。</para></returns>
    /// <exception cref="InvalidOperationException">The endpoint is already leased.
    /// <para>该端点已被租用。</para></exception>
    /// <exception cref="ObjectDisposedException">The controller has been disposed.
    /// <para>控制器已被释放。</para></exception>
    public DeviceLease AdoptSession(DeviceEndpoint endpoint, object session)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateRouteForOpen(endpoint);
            if (!_leased.Add(endpoint.Id) && !ReclaimStalePlaceholderLocked(endpoint.Id, endpoint))
            {
                throw new DeviceEndpointBusyException(endpoint);
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
            ValidateRouteForOpen(endpoint);
            if (!_leased.Add(id) && !ReclaimStalePlaceholderLocked(id, endpoint))
            {
                throw new DeviceEndpointBusyException(endpoint);
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

    /// <summary>Reads the current transport generation observed for a physical identity without opening anything or advancing it. A consumer holding an older generation must treat its cached observations and leases as retired; entries are pruned when the OS reports the device removed.
    /// <para>读取某物理身份当前观测到的传输代次，不打开设备也不推进代次。持有较旧代次的消费方须把其缓存观测与租约视为已退役；当操作系统报告设备移除时，相应条目会被清除。</para></summary>
    /// <param name="identity">The physical identity to look up.<para>待查询的物理身份。</para></param>
    /// <param name="generation">The latest generation stamped by an open or an observed mode transition.
    /// <para>由打开或已观测模式迁移盖印的最近代际。</para></param>
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
    /// Reads the last advanced transport generation observed for a physical identity (stamped by a
    /// transport open or a mode transition) without advancing it; a by-key lease therefore carries the
    /// generation as of before its own open, and a consumer holding an older generation only treats
    /// its cached observations and leases as retired sooner, never later. Returns
    /// <see langword="null"/> when identity evidence is absent or the generation was pruned with the
    /// device's removal.
    /// <para>读取某物理身份最近一次被推进的传输代际（由传输打开或模式迁移盖印），本身绝不推进代数；
    /// 按钥匙开启的租约因此盖的是“打开前已观测”的代际，持有更旧代际的消费方只会更早把自己的
    /// 缓存观测与租约判为过期，判定方向不变。身份缺失或该身份的代际已随设备移除被清除时
    /// 返回 <see langword="null"/>。</para>
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
    /// Drops cached transport generations by physical identity when a device is removed: the single
    /// reclamation point against unbounded growth of the generation map with USB topology churn.
    /// After pruning, <see cref="TryPeekTransportGeneration(PhysicalDeviceIdentity, out TransportGeneration)"/>
    /// reports the identity as never observed, so parked leases holding an older generation expire
    /// earlier; if the OS later reuses the same container identity for fresh hardware the generation
    /// restarts at 1, which is the intended semantics for a device that disappeared and returned.
    /// <para>设备移除时按物理身份键清除已缓存的传输代际：代际字典随 USB 拓扑无界增长的唯一
    /// 回收点。移除后 <see cref="TryPeekTransportGeneration(PhysicalDeviceIdentity, out TransportGeneration)"/>
    /// 对该身份报告“从未观测”，持有旧代际的 parked 租约据此更早过期；操作系统日后把同一容器
    /// 身份分配给新硬件时代际从 1 重新起算，这是设备消失又重现时的既定语义。</para>
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

    /// <summary>
    /// Emits one transition per physical identity whose observed protocol set changed. The previous
    /// projection is reused while it was produced for the same committed generation, and both
    /// projections are skipped when the current projection inputs hash to the previous digest.
    /// Callers hold <c>_gate</c>, so the cached generation, digest and projection move together.
    /// <para>为每个观测协议集合发生变化的物理身份发布一条迁移。上一次投影若对应同一个已提交代际
    /// 则直接复用；新旧投影输入摘要相同时连投影本身也跳过。调用方持有 <c>_gate</c>，
    /// 因此缓存的代际、摘要与投影总是一致更新。</para>
    /// </summary>
    private ModeTransition[] ObserveModeTransitionsLocked(DeviceSnapshot previous, DeviceSnapshot current)
    {
        var before = ResolveModeProjectionLocked(previous, out var beforeDigest);
        if (before.Count == 0)
        {
            _modeProjection = null;
            _modeProjectionGeneration = -1;
            return [];
        }

        var currentDigest = ComputeModeDigest(current.Endpoints);
        if (currentDigest == beforeDigest)
        {
            StoreModeProjectionLocked(current.Generation, currentDigest, before);
            return [];
        }

        var after = BuildModeProjection(current.Endpoints, out _);
        List<ModeTransition> transitions = [];
        foreach (var (key, (identity, protocols)) in after)
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

        StoreModeProjectionLocked(current.Generation, currentDigest, after);
        return [.. transitions];
    }

    /// <summary>
    /// Returns the previous snapshot's projection, reusing the cached instance when it was built for
    /// that same generation, and reports the digest of the inputs it was built from.
    /// <para>返回上一份快照的投影：缓存由同一代际建立时直接复用，并给出其所依据输入的摘要。</para>
    /// </summary>
    private Dictionary<string, (PhysicalDeviceIdentity Identity, string Protocols)> ResolveModeProjectionLocked(
        DeviceSnapshot previous,
        out (ulong Sum, ulong Xor, int Count) digest)
    {
        if (_modeProjection is { } cached && _modeProjectionGeneration == previous.Generation)
        {
            digest = _modeProjectionDigest;
            return cached;
        }

        return BuildModeProjection(previous.Endpoints, out digest);
    }

    /// <summary>
    /// Records the projection of one committed generation together with the digest of its inputs.
    /// <para>记录某个已提交代际的投影及其输入摘要。</para>
    /// </summary>
    private void StoreModeProjectionLocked(
        long generation,
        (ulong Sum, ulong Xor, int Count) digest,
        Dictionary<string, (PhysicalDeviceIdentity Identity, string Protocols)> projection)
    {
        _modeProjection = projection;
        _modeProjectionDigest = digest;
        _modeProjectionGeneration = generation;
    }

    /// <summary>
    /// Hashes only the projection inputs (physical identity key plus protocol) into an
    /// order-independent digest, so a refresh that leaves them untouched skips projection work.
    /// Two accumulators plus the entry count make an accidental match effectively impossible.
    /// <para>只对投影输入（物理身份键与协议）计算与顺序无关的摘要，使未改动它们的刷新跳过投影
    /// 计算。双累加器加条目计数使偶然相同实际上不可能发生。</para>
    /// </summary>
    private static (ulong Sum, ulong Xor, int Count) ComputeModeDigest(ImmutableArray<DeviceEndpoint> endpoints)
    {
        ulong sum = 0;
        ulong xor = 0;
        var count = 0;
        foreach (var endpoint in endpoints)
        {
            if (ModeIdentityOf(endpoint) is not { } identity || string.IsNullOrEmpty(endpoint.Protocol))
            {
                continue;
            }

            var item = MixModeItem(identity.Key, endpoint.Protocol);
            unchecked
            {
                sum += item;
                xor ^= item;
            }

            count++;
        }

        return (sum, xor, count);
    }

    /// <summary>
    /// FNV-1a mixing of one identity key and protocol pair, used only for digest comparison.
    /// <para>对单个身份键与协议对做 FNV-1a 混合，仅用于摘要比较。</para>
    /// </summary>
    private static ulong MixModeItem(string key, string protocol)
    {
        var hash = Fnv1a64.Compute(key.AsSpan());
        hash = Fnv1a64.Mix(hash, 0x1F);
        return Fnv1a64.Compute(protocol.AsSpan(), hash);
    }

    /// <summary>
    /// Returns the physical identity the mode projection uses for one endpoint, or <c>null</c> when the
    /// endpoint carries no usable identity evidence. Shared by the digest and the projection so both
    /// consume exactly the same inputs; callers still reject endpoints without a protocol.
    /// <para>返回模式投影为某端点使用的物理身份；端点没有可用身份证据时返回 <c>null</c>。
    /// 摘要与投影共用它，从而消费完全相同的输入；协议为空仍由调用方剔除。</para>
    /// </summary>
    private static PhysicalDeviceIdentity? ModeIdentityOf(DeviceEndpoint endpoint)
    {
        if (endpoint.UsbInfo is not { } info ||
            PhysicalDeviceIdentity.From(info) is not { } identity ||
            identity.ContainerId is null && identity.PhysicalPortPath is null)
        {
            return null;
        }

        return identity;
    }

    /// <summary>
    /// Builds the per-identity protocol projection and, in the same pass, the digest of the inputs it
    /// was built from, so a cached predecessor never needs a second derivation pass.
    /// <para>在同一次遍历中构建按物理身份分组的协议投影及其输入摘要，使缓存的前一份投影不需要
    /// 第二次推导遍历。</para>
    /// </summary>
    /// <param name="endpoints">The endpoints to project. <para>待投影的端点集合。</para></param>
    /// <param name="digest">Receives the digest of the projection inputs. <para>接收投影输入摘要。</para></param>
    /// <returns>The identity-keyed protocol projection. <para>按身份键分组的协议投影。</para></returns>
    private static Dictionary<string, (PhysicalDeviceIdentity Identity, string Protocols)> BuildModeProjection(
        ImmutableArray<DeviceEndpoint> endpoints,
        out (ulong Sum, ulong Xor, int Count) digest)
    {
        var raw = new Dictionary<string, (PhysicalDeviceIdentity Identity, SortedSet<string> Protocols)>(
            StringComparer.Ordinal);
        ulong sum = 0;
        ulong xor = 0;
        var count = 0;
        foreach (var endpoint in endpoints)
        {
            if (ModeIdentityOf(endpoint) is not { } identity || string.IsNullOrEmpty(endpoint.Protocol))
            {
                continue;
            }

            var item = MixModeItem(identity.Key, endpoint.Protocol);
            unchecked
            {
                sum += item;
                xor ^= item;
            }

            count++;
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

        digest = (sum, xor, count);
        return joined;
    }

    /// <summary>
    /// Validates that an endpoint may still be opened under the fixed route: its stamped epoch must
    /// match the current snapshot, the mode must allow its route, and no open lease on the same
    /// physical device may already hold the other route. Callers hold <c>_gate</c>.
    /// <para>校验端点仍可在固定路由下打开：其盖章 epoch 必须与当前快照一致，模式必须允许该路由，
    /// 且同一物理设备上不能已有租约持有另一条路由。调用方持有 <c>_gate</c>。</para>
    /// </summary>
    /// <param name="endpoint">The endpoint about to be leased. <para>即将被租用的端点。</para></param>
    /// <exception cref="IOException">The route snapshot changed after discovery.
    /// <para>发现之后路由快照已改变。</para></exception>
    /// <exception cref="InvalidOperationException">The route mode forbids the endpoint's route, or the device is held by the other route.
    /// <para>路由模式禁止该端点的路由，或设备已被另一条路由持有。</para></exception>
    private void ValidateRouteForOpen(DeviceEndpoint endpoint)
    {
        if (endpoint.RouteEpoch != 0 && endpoint.RouteEpoch != DebugBridgeRoute.Current.Epoch)
        {
            throw new IOException(
                "The debug bridge route changed after discovery; refresh the device list and retry.");
        }

        if (string.Equals(endpoint.Route, "server", StringComparison.Ordinal) &&
            DebugBridgeRoute.Mode == DebugBridgeRouteMode.DirectOnly)
        {
            throw new InvalidOperationException(
                "The DirectOnly route mode forbids debug-bridge server sessions.");
        }

        if (HasConflictingRouteLeaseLocked(endpoint))
        {
            throw new InvalidOperationException(
                "The device is already held by another route; release that lease before opening this endpoint.");
        }
    }

    /// <summary>
    /// Decides whether an open lease already holds the same physical device through a different
    /// route, which would let two writers touch one debug interface. Correlation uses container or
    /// physical-port evidence first, then the exact OS device path, and finally the exact protocol
    /// serial for server-backed leases that carry no USB metadata. Callers hold <c>_gate</c>.
    /// <para>判定是否已有活动租约通过另一条路由持有同一物理设备——那会让两个写入方操作同一调试接口。
    /// 关联优先使用容器或物理端口证据，其次精确操作系统设备路径，最后对没有 USB 元数据的 server 租约
    /// 使用精确协议序列号。调用方持有 <c>_gate</c>。</para>
    /// </summary>
    /// <param name="candidate">The endpoint about to be leased. <para>即将被租用的端点。</para></param>
    /// <returns><see langword="true"/> when another route already holds the device.
    /// <para>另一条路由已持有该设备时为 <see langword="true"/>。</para></returns>
    private bool HasConflictingRouteLeaseLocked(DeviceEndpoint candidate)
    {
        foreach (var lease in _openLeases.Values)
        {
            var existing = lease.Endpoint;
            if (existing.Id == candidate.Id ||
                string.Equals(existing.Route, candidate.Route, StringComparison.Ordinal))
            {
                continue;
            }

            if (RoutesConflict(existing, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RoutesConflict(DeviceEndpoint existing, DeviceEndpoint candidate)
    {
        if (CorrelatePhysicalObservation(existing.UsbInfo, candidate.UsbInfo) is { } correlated)
        {
            return correlated;
        }

        if (existing.Usb is { Path.Length: > 0 } first && candidate.Usb is { Path.Length: > 0 } second &&
            string.Equals(first.Path, second.Path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(existing.Serial) &&
            string.Equals(existing.Serial, candidate.Serial, StringComparison.Ordinal) &&
            string.Equals(existing.Protocol, candidate.Protocol, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns one endpoint reservation. The reservation is cleared only when no newer lease has taken
    /// over the endpoint: forced detach (device removal, vanished server) removes the lease from the
    /// open-lease table first while the <c>_leased</c> entry stays as a releasing placeholder until that
    /// lease has finished tearing down its resources and calls back here, so a concurrent open never
    /// preempts an endpoint whose previous transport is still held. Repeated calls for the same
    /// reservation are no-ops.
    /// <para>归还一次端点占用。占用登记只在没有更新租约接管该端点时被清除：强制分离
    /// （设备移除、server 消失）会先把租约从 <c>_openLeases</c> 摘除，但把 <c>_leased</c>
    /// 条目保留为“释放中”占位，直到该租约完成其全部异步资源清理并由本方法归还占用；
    /// 因此并发的 <c>OpenAsync</c> 在旧租约仍持有传输期间绝不会抢占同一端点。
    /// 对同一占用可重复调用（幂等）。</para>
    /// </summary>
    private void Release(DeviceEndpointId id, DeviceLease? lease)
    {
        var returned = false;
        var detached = false;
        lock (_gate)
        {
            var current = _openLeases.GetValueOrDefault(id);
            if (current is null || ReferenceEquals(current, lease))
            {
                returned = _leased.Remove(id);
                _placeholderSince.Remove(id);
            }

            if (lease is null || ReferenceEquals(current, lease))
            {
                detached = _openLeases.Remove(id);
                if (!returned && detached)
                {
                    _placeholderSince[id] = Environment.TickCount64;
                }
            }
        }

        if (returned)
        {
            CommTrace.Debug("lease", $"released reservation for {id.Source}/{id.Transport} {id.Address}.");
        }
        else if (detached)
        {
            CommTrace.Debug("lease",
                $"reservation for {id.Source}/{id.Transport} {id.Address} stays as a releasing placeholder until its teardown finishes.");
        }
    }

    /// <summary>
    /// Derives the <see cref="DevicePropertyCache"/> key for one endpoint: the physical identity key
    /// when available (container id first, serial-derived identity second) so one physical device
    /// keeps a single cache entry across mode switches and interface renumbering; falls back to the
    /// full endpoint identity composed of source, transport and address when identity evidence is
    /// absent, never to the bare address or an OS device path that gets recycled.
    /// <para>推导 <see cref="DevicePropertyCache"/> 使用的端点键：优先为物理身份键（容器 ID 优先，
    /// 其次序列号推导的身份键），使同一物理设备跨模式切换与接口重编号共享一份缓存；
    /// 身份证据缺失时退回由源、传输与地址组成的完整端点身份字符串，绝不退回裸地址或
    /// 会被操作系统回收复用的设备路径。</para>
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

    private const long PlaceholderReclaimMilliseconds = 1000;

    /// <summary>
    /// Reclaims a reservation that has been stuck as a releasing placeholder longer than
    /// <see cref="PlaceholderReclaimMilliseconds"/>, so a teardown that never completes cannot make one endpoint
    /// permanently unopenable. Runs under <c>_gate</c>; a live lease is never preempted.
    /// <para>回收停留为“释放中”占位超过 <see cref="PlaceholderReclaimMilliseconds"/> 的占用，使永不完成的清理不会让
    /// 某个端点永久无法打开。在 <c>_gate</c> 下运行；绝不抢占仍存活的租约。</para>
    /// </summary>
    /// <param name="id">Endpoint whose reservation is tested. <para>被测占用登记的端点。</para></param>
    /// <param name="endpoint">Endpoint used for the diagnostic line. <para>用于诊断行的端点。</para></param>
    /// <returns><see langword="true"/> when the reservation was reclaimed and may be taken again.
    /// <para>占用已回收、可以重新取得时为 <see langword="true"/>。</para></returns>
    private bool ReclaimStalePlaceholderLocked(DeviceEndpointId id, DeviceEndpoint endpoint)
    {
        if (_openLeases.ContainsKey(id) || !_placeholderSince.TryGetValue(id, out var since))
        {
            return false;
        }

        var age = Environment.TickCount64 - since;
        if (age < PlaceholderReclaimMilliseconds)
        {
            return false;
        }

        CommTrace.Error("lease",
            $"reclaiming {id.Source}/{id.Transport} {id.Address} from a releasing placeholder stuck for {age} ms; " +
            $"its teardown never finished, so {endpoint.Protocol}/{endpoint.Route} could not be opened again.");
        _placeholderSince.Remove(id);
        return _leased.Remove(id) && _leased.Add(id);
    }

    /// <summary>Describes the leases currently bound to one endpoint address, so a caller that cannot acquire the
    /// endpoint can name what still holds it.
    /// <para>列出当前绑定到某个端点地址的租约，使无法取得端点的调用方能说出占用者是谁。</para>
    /// </summary>
    /// <param name="address">Endpoint address to inspect. <para>要检查的端点地址。</para></param>
    /// <returns>One <c>protocol/route</c> label per matching lease, in endpoint order; empty when nothing holds it.
    /// <para>每个匹配租约一个 <c>protocol/route</c> 标签，按端点顺序排列；没有占用时为空。</para></returns>
    public IReadOnlyList<string> DescribeReservations(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        lock (_gate)
        {
            var live = _openLeases.Values
                .Where(lease => string.Equals(lease.Endpoint.Address, address, StringComparison.OrdinalIgnoreCase))
                .Select(lease => $"{lease.Endpoint.Protocol}/{lease.Endpoint.Route}")
                .ToList();
            foreach (var id in _leased)
            {
                if (string.Equals(id.Address, address, StringComparison.OrdinalIgnoreCase) && !_openLeases.ContainsKey(id))
                {
                    live.Add($"{id.Source}/{id.Transport} releasing placeholder");
                }
            }

            return [.. live];
        }
    }

    /// <summary>Force-detaches every lease still bound to one endpoint address, so a stuck host operation cannot
    /// block a user action forever. Detached leases keep their reservation as a releasing placeholder until their
    /// teardown finishes, exactly like a device removal.
    /// <para>强制摘除仍绑定到某个端点地址的全部租约，使卡住的宿主操作无法永久阻塞用户操作。被摘除的租约与其他
    /// 设备移除路径一样，在清理完成前保留"释放中"占位。</para>
    /// </summary>
    /// <param name="address">Endpoint address whose leases are detached. <para>要摘除租约的端点地址。</para></param>
    /// <returns>The number of detached leases. <para>被摘除的租约数量。</para></returns>
    public int ForceDetachReservations(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        DeviceLease[] leases;
        lock (_gate)
        {
            var matches = _openLeases
                .Where(pair => string.Equals(pair.Value.Endpoint.Address, address, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var (id, _) in matches)
            {
                _openLeases.Remove(id);
            }

            leases = [.. matches.Select(pair => pair.Value)];
        }

        if (leases.Length > 0)
        {
            CommTrace.State("lease",
                $"force-detached {leases.Length} reservation(s) for {address}: {string.Join(", ", leases.Select(lease => $"{lease.Endpoint.Protocol}/{lease.Endpoint.Route}"))}.");
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
                    UsbTrace.Log($"Forced lease detach failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        return leases.Length;
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
    /// Decides whether an endpoint belongs to the physical USB device that was removed (or whose
    /// metadata changed). Correlation runs through <see cref="PhysicalDeviceIdentity.CorrelatesWith"/>
    /// with container id first and usable physical-port path second; the OS device path is consulted
    /// only when that correlation is indeterminate, because bus/device paths are recycled by the OS
    /// and a path match never overrides a decisive container or port verdict.
    /// <para>判定某端点是否对应被移除（或元数据变化）的那台物理 USB 设备。优先使用
    /// <see cref="PhysicalDeviceIdentity.CorrelatesWith"/>：容器 ID 优先、可用物理端口链次之；
    /// 容器 ID 与端口链都缺失时回退到 OS 设备路径，而路径会被操作系统回收复用，所以路径
    /// 相等只在关联不确定时成立，且绝不覆盖确定性的身份判定。</para>
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
    /// Decides whether two USB observations describe the same physical hardware from container-id
    /// first and physical-port-path evidence second; returns <see langword="null"/> when evidence is
    /// missing or no identity can be derived, leaving the indeterminate fallback (device path or
    /// refusing the operation) to the caller. Never uses VID/PID or enumeration order.
    /// <para>在容器 ID 优先、物理端口链次之的证据下判定两个 USB 观测是否指向同一物理硬件；
    /// 证据缺失或无法推导身份时返回 <see langword="null"/>，由调用方决定其不确定兜底
    /// （设备路径或拒绝操作），绝不使用 VID/PID 或枚举顺序。</para>
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
    /// Releases every resource the controller owns: stops the server-lifecycle watch and the USB
    /// removal watch, then awaits the teardown of all open leases (registered streams, command pools,
    /// sessions and transports) and returns their reservations. Repeated calls are no-ops.
    /// <para>释放控制器持有的全部资源：停止 server 监视与 USB 移除监视，等待所有活动租约
    /// 完成清理（登记的流、指令池、会话、传输）并归还占用。重复调用无操作。</para>
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
    /// Synchronous teardown: stops the server-lifecycle watch and the USB removal watch inline and hands
    /// lease teardown to the thread pool without ever waiting on asynchronous release. Callers that
    /// must observe completed teardown use <see cref="DisposeAsync"/> instead.
    /// <para>同步释放：就地停止 server 监视与 USB 移除监视，并把租约清理移交后台线程池，
    /// 绝不等待异步释放（不做 sync-over-async）。需要观察清理完成的调用方应使用
    /// <see cref="DisposeAsync"/>。</para>
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

    /// <summary>Budget the host uses when it releases USB occupancy during application shutdown.
    /// <para>宿主在应用关闭期间解除 USB 占用时使用的预算。</para></summary>
    public static readonly TimeSpan ShutdownReleaseBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Detaches and disposes every open lease — the direct USB interfaces included — and waits up to
    /// <paramref name="timeout"/> for that teardown to finish, so a closing application returns the device to the
    /// operating system instead of leaving it claimed until the process dies. Repeating the call is a no-op.
    /// <para>摘除并释放全部未完成租约（含直连 USB 接口），最多等待 <paramref name="timeout"/> 让清理完成，
    /// 使正在关闭的应用把设备交还给操作系统，而不是一直占用到进程结束。重复调用为空操作。</para>
    /// </summary>
    /// <param name="timeout">Upper bound on the wait; zero or negative is clamped to one second.
    /// <para>等待上限；零或负值按一秒处理。</para></param>
    /// <returns><see langword="true"/> when every lease was disposed within the budget, <see langword="false"/> when
    /// the wait expired and the remainder finishes in the background.
    /// <para>全部租约在预算内释放完成时为 <see langword="true"/>；等待超时、剩余部分转后台完成时为 <see langword="false"/>。</para></returns>
    public bool ReleaseAllLeases(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _lifecycle, 1) != 0)
        {
            return true;
        }

        var (leases, serverWatch, removalWatch) = DetachOwnedResourcesLocked();
        StopWatches(serverWatch, removalWatch);
        if (leases.Count == 0)
        {
            return true;
        }

        var budget = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(1);
        var teardown = DisposeLeasesAsync(leases);
        try
        {
            teardown.WaitAsync(budget).GetAwaiter().GetResult();
            return true;
        }
        catch (TimeoutException)
        {
            UsbTrace.LogError(
                $"Releasing {leases.Count} device lease(s) did not finish within {budget.TotalMilliseconds:0} ms; the remainder continues in the background.");
            return false;
        }
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
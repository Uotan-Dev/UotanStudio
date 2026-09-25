using System.Collections.Immutable;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>Coordinates discovery and exclusive, generation-checked endpoint leases.</summary>
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

    /// <summary>Raised after a physical USB device disappeared and its leases were released.
    /// 物理 USB 设备消失且其租约被释放后触发。</summary>
    public event Action<DeviceEndpoint>? DeviceRemoved;

    /// <summary>Raised when a physical USB device changed its metadata (e.g. a mode switch).
    /// 物理 USB 设备元数据变化（如模式切换）时触发。</summary>
    public event Action<DeviceEndpoint>? DeviceChanged;

    /// <summary>Gets the per-device static property cache shared by all controller consumers.
    /// 获取所有消费方共享的每设备静态参数缓存。</summary>
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

    public DeviceSnapshot Snapshot
    {
        get { lock (_gate) { return _snapshot; } }
    }

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

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation == _requestedGeneration)
            {
                _snapshot = new DeviceSnapshot(generation, endpoints.ToImmutable());
            }

            return _snapshot;
        }
    }

    public async Task<DeviceLease> OpenAsync(DeviceSnapshot snapshot, DeviceEndpointId id,
        CancellationToken cancellationToken = default)
    {
        var (endpoint, transport) = await OpenTransportAsync(snapshot, id, cancellationToken).ConfigureAwait(false);
        DeviceLease? lease = null;
        try
        {
            lease = new DeviceLease(endpoint, transport, () => Release(id, lease!));
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

    private async Task<(DeviceEndpoint Endpoint, IAsyncDisposable Transport)> OpenTransportAsync(
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
                    return (endpoint, transport);
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
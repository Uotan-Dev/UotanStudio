using System.Collections.Immutable;
using UotanStudio.Comm.Devices.Contracts;
using UotanStudio.Comm.Devices.Discovery;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>Coordinates discovery and exclusive, generation-checked endpoint leases.</summary>
public sealed class DeviceController
{
    private readonly ImmutableArray<IDeviceDiscoverySource> _sources;
    private readonly IDeviceTransportOpener _opener;
    private readonly object _gate = new();
    private DeviceSnapshot _snapshot = new(0, []);
    private long _requestedGeneration;
    private readonly HashSet<DeviceEndpointId> _leased = [];

    public DeviceController(IEnumerable<IDeviceDiscoverySource> sources, IDeviceTransportOpener opener)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _sources = [.. sources];
        if (_sources.Any(source => source is null) ||
            _sources.Select(source => source.Name).Distinct(StringComparer.Ordinal).Count() != _sources.Length ||
            _sources.Any(source => string.IsNullOrWhiteSpace(source.Name)))
        {
            throw new ArgumentException("Discovery source names must be nonempty and unique.", nameof(sources));
        }
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
                    return new DeviceLease(endpoint, transport, () => Release(id));
                }
            }

            await transport.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Device snapshot became stale while opening.");
        }
        catch
        {
            Release(id);
            throw;
        }
    }

    private void Release(DeviceEndpointId id)
    {
        lock (_gate)
        {
            _leased.Remove(id);
        }
    }
}

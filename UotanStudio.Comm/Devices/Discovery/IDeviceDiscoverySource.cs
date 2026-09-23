using UotanStudio.Comm.Devices.Contracts;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>Returns only endpoints whose exact addresses can be opened by the configured opener.</summary>
public interface IDeviceDiscoverySource
{
    string Name { get; }
    Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken);
}

using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>Returns only endpoints whose exact addresses can be opened by the configured opener.</summary>
internal interface IDeviceDiscoverySource
{
    string Name { get; }
    Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken);
}
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>Returns only endpoints whose exact addresses can be opened by the configured opener.
/// <para>只返回配置的开启器能够按其精确地址打开的端点。</para></summary>
internal interface IDeviceDiscoverySource
{
    string Name { get; }
    Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken);
}
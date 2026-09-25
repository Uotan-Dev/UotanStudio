using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>Discovers server-owned targets, including offline and unauthorized serials.</summary>
public sealed class AdbServerDiscoverySource(string host = "127.0.0.1", int port = 5037) : IDeviceDiscoverySource
{
    public string Name => $"adb-server@{host}:{port}";

    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!await DebugBridgeRoute.IsListeningAsync(host, port, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        // The existing smart-socket list API is synchronous; await its completion before publishing.
        var targets = await Task.Run(() => new AdbServerServices(string.Empty, host, port).ListTargets(),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return targets.Select(target => new DeviceEndpoint
        {
            Source = Name,
            Transport = "adb-server",
            Address = target.Serial,
            Serial = target.Serial,
            DisplayName = target.Serial,
            Protocol = "adb",
            Route = "server",
            State = target.State
        }).ToArray();
    }
}
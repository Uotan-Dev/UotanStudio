using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Comm.Devices.Discovery;

/// <summary>Discovers authoritative HDC server connect keys and target states.</summary>
public sealed class HdcServerDiscoverySource(string host = "127.0.0.1", int? port = null) : IDeviceDiscoverySource
{
    private readonly int _port = port ?? HdcServerClient.DefaultServerPort;
    public string Name => $"hdc-server@{host}:{_port}";

    public async Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!await DebugBridgeRoute.IsListeningAsync(host, _port, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        using var client = new HdcServerClient(host, _port);
        var output = await client.ExecuteAsync(string.Empty, "list targets -v",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        // Matches HdcServerClient.ListTargetDetails without its synchronous wrapper.
        return HdcTargetInfo.Parse(Encoding.UTF8.GetString(output)).Select(target => new DeviceEndpoint
        {
            Source = Name,
            Transport = "hdc-server",
            Address = target.ConnectKey,
            Serial = target.ConnectKey,
            DisplayName = target.Name,
            Protocol = "hdc",
            Route = "server",
            State = target.State,
            TargetTransport = target.Transport
        }).ToArray();
    }
}
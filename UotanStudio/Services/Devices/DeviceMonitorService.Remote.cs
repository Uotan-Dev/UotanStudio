using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    internal static bool TryNormalizeNetworkAddress(DeviceProtocolKind protocol, string? input, out string address)
    {
        address = string.Empty;
        return protocol is DeviceProtocolKind.Adb or DeviceProtocolKind.Hdc &&
               DebugBridgeNetworkAddress.TryNormalize(input, out address) &&
               (protocol != DeviceProtocolKind.Hdc || Encoding.UTF8.GetByteCount(address) < 32);
    }

    /// <summary>Connects a network device through the configured debug bridge server.</summary>
    public async Task ConnectNetworkDeviceAsync(DeviceProtocolKind protocol, string address,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeNetworkAddress(protocol, address, out var target))
        {
            throw new ArgumentException("A valid ADB or HDC device host:port address is required.", nameof(address));
        }

        if (protocol == DeviceProtocolKind.Adb)
        {
            await AdbServerServices.ConnectAsync(target, DebugBridgeEndpoints.ServerHost,
                DebugBridgeEndpoints.AdbServerPort, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var server = new HdcServerClient(DebugBridgeEndpoints.ServerHost, DebugBridgeEndpoints.HdcServerPort);
            await server.ConnectTargetAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        await ForceRefreshAsync().ConfigureAwait(false);
    }
}
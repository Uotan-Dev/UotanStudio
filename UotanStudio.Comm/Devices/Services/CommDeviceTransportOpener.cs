using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>Opens exactly the discovered USB key or server target; never changes routes.</summary>
internal sealed class CommDeviceTransportOpener(
    ICommKit kit, string adbHost = "127.0.0.1", int adbPort = 5037,
    string hdcHost = "127.0.0.1", int? hdcPort = null) : IDeviceTransportOpener
{
    private readonly int _hdcPort = hdcPort ?? HdcServerClient.DefaultServerPort;

    public async Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint.Source == UsbDiscoverySource.SourceName && endpoint.Route == "direct-usb" &&
            endpoint.Usb is { } usb && usb.Backend != UsbApiKind.Auto &&
            endpoint.Transport == usb.Backend.ToString() &&
            UsbDeviceIdentity.TryParseKeyFilter(endpoint.Address) is { } filter &&
            !string.IsNullOrWhiteSpace(filter.DevicePathEquals) &&
            string.Equals(filter.DevicePathEquals, usb.Path, StringComparison.OrdinalIgnoreCase) &&
            filter.VendorId == usb.VendorId && filter.ProductId == usb.ProductId &&
            filter.InterfaceClass == usb.InterfaceClass &&
            filter.InterfaceSubClass == usb.InterfaceSubClass &&
            filter.InterfaceProtocol == usb.InterfaceProtocol)
        {
            DebugBridgeSessionOpener.RequireDirectUsb(usb.InterfaceClass, usb.InterfaceSubClass, usb.InterfaceProtocol, endpoint.Serial);
            var session = kit.OpenUsbDeviceSessionByKey(endpoint.Address, usb.Backend)
                ?? throw new IOException("Exact USB endpoint is unavailable.");
            if (session.DeviceInfo.SourceApiKind != usb.Backend ||
                !UsbDeviceIdentity.MatchesOpenedKey(endpoint.Address, session.DeviceInfo.DeviceKey))
            {
                session.Dispose();
                throw new IOException("USB opener returned a different endpoint.");
            }
            if (cancellationToken.IsCancellationRequested)
            {
                session.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return new OwnedDeviceTransport<IUsbDeviceSession>(session);
        }

        if (endpoint.Source == $"adb-server@{adbHost}:{adbPort}" &&
            endpoint.Transport == "adb-server" && endpoint.Route == "server" &&
            !string.IsNullOrWhiteSpace(endpoint.Address) && endpoint.Serial == endpoint.Address)
        {
            if (!await DebugBridgeRoute.IsListeningAsync(adbHost, adbPort, cancellationToken).ConfigureAwait(false))
                throw new IOException("ADB server is unavailable.");
            await Task.Run(() => new AdbServerServices(endpoint.Address, adbHost, adbPort).ValidateTarget(),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new OwnedDeviceTransport<AdbSession>(
                AdbSession.ForServer(endpoint.Address, adbHost, adbPort));
        }

        if (endpoint.Source == $"hdc-server@{hdcHost}:{_hdcPort}" &&
            endpoint.Transport == "hdc-server" && endpoint.Route == "server" &&
            !string.IsNullOrWhiteSpace(endpoint.Address) && endpoint.Serial == endpoint.Address)
        {
            if (!await DebugBridgeRoute.IsListeningAsync(hdcHost, _hdcPort, cancellationToken).ConfigureAwait(false))
                throw new IOException("HDC server is unavailable.");
            using var client = new HdcServerClient(hdcHost, _hdcPort);
            var output = await client.ExecuteAsync(string.Empty, "list targets -v",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!HdcTargetInfo.Parse(Encoding.UTF8.GetString(output))
                .Any(target => string.Equals(target.ConnectKey, endpoint.Address, StringComparison.Ordinal)))
                throw new IOException("Exact HDC server target is unavailable.");
            cancellationToken.ThrowIfCancellationRequested();
            return new OwnedDeviceTransport<HdcSession>(client.CreateSession(endpoint.Address));
        }

        throw new NotSupportedException("Endpoint route is not configured for this opener.");
    }
}
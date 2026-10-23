using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>Opens a fresh authenticated direct HDC session with its controller lease.
/// <para>打开一条由控制器租约持有的全新已认证 HDC 直连会话。</para></summary>
internal static class HdcDirectConnection
{
    internal static (DeviceLease Lease, HdcSession Session) Open(DeviceMonitorService monitor, DetectedDevice device)
    {
        if (DeviceMonitorService.UseHdcServerFor(device))
            throw new IOException("HDC server appeared; direct access is prohibited. Retry through the server.");

        if (device.Transport == DeviceTransportKind.Tcp)
        {
            if (!DebugBridgeNetworkAddress.TrySplitEndpoint(device.Serial, out var host, out var port))
                throw new IOException("A direct HDC network target needs a host:port address.");
            var networkSession = monitor.OpenNetworkHdcSession(host, port);
            try
            {
                networkSession.Connect();
            }
            catch
            {
                networkSession.Dispose();
                throw;
            }

            var endpoint = new DeviceEndpoint
            {
                Source = "hdc-tcp",
                Transport = "hdc-tcp",
                Address = device.Serial,
                DisplayName = device.Serial,
                Serial = device.Serial,
                Protocol = "hdc",
                Route = "direct",
                RouteEpoch = DebugBridgeRoute.Current.Epoch
            };
            return (monitor.AdoptHdcSession(endpoint, networkSession), networkSession);
        }

        var direct = monitor.ResolveDirectUsbDevice(device);
        if (direct.Transport != DeviceTransportKind.Usb)
            throw new IOException("HDC USB interface could not be opened; another host may own it.");
        var lease = monitor.OpenLeaseByKey(direct.Key, DeviceSessionKind.Hdc)
            ?? throw new IOException("HDC USB interface could not be opened; another host may own it.");
        if (!lease.TryGetSession<HdcSession>(out var session))
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new IOException("The device does not expose an HDC interface.");
        }
        try
        {
            session.Connect();
            return (lease, session);
        }
        catch
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}
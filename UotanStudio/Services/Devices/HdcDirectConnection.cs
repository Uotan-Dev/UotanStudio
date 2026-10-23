using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>Opens a fresh authenticated direct HDC session with its controller lease; server-owned targets and
/// ServerOnly mode are rejected instead of bypassing the server.
/// <para>打开一条由控制器租约持有的全新已认证 HDC 直连会话；server 拥有目标或仅 server 模式时直接拒绝，不会
/// 绕过 server。</para></summary>
internal static class HdcDirectConnection
{
    internal static (DeviceLease Lease, HdcSession Session) Open(
        DeviceMonitorService monitor, DetectedDevice device)
    {
        if (DebugBridgeRoute.Mode == DebugBridgeRouteMode.ServerOnly)
        {
            throw new IOException(
                "The ServerOnly route mode forbids direct HDC access; open the target through the HDC server.");
        }

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

        var direct = ResolveUsbWithFreshCache(monitor, device);
        if (direct.Transport != DeviceTransportKind.Usb)
            throw new IOException(
                $"HDC USB interface for {device.Serial} is not directly routable; another host may own it.");
        var lease = monitor.OpenLeaseByKey(direct.Key, DeviceSessionKind.Hdc);
        if (lease is null)
        {
            // Stopping the HDC server can rebind or re-enumerate the USB interface; the cached device key
            // is then stale. Re-resolve once before reporting that another host owns the interface.
            monitor.InvalidateUsbInventoryCache();
            direct = monitor.ResolveDirectUsbDevice(device);
            lease = monitor.OpenLeaseByKey(direct.Key, DeviceSessionKind.Hdc);
        }

        if (lease is null)
            throw new IOException(
                $"HDC USB interface for {device.Serial} ({direct.Key}) could not be opened; another host may own it.");
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

    private static DetectedDevice ResolveUsbWithFreshCache(DeviceMonitorService monitor, DetectedDevice device)
    {
        try
        {
            var direct = monitor.ResolveDirectUsbDevice(device);
            if (direct.Transport == DeviceTransportKind.Usb)
                return direct;
        }
        catch (IOException)
        {
            // The cached inventory may still contain a pre-rebind key; retry once below.
        }

        monitor.InvalidateUsbInventoryCache();
        return monitor.ResolveDirectUsbDevice(device);
    }
}
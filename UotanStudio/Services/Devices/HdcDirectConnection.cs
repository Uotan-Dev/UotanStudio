using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>Leases the shared authenticated direct HDC session for one target; server-owned targets and
/// ServerOnly mode are rejected instead of bypassing the server. A WinUSB-bound HDC interface accepts a single
/// open handle, so concurrent consumers share one connection and open their own channels instead of reopening it.
/// <para>为单个目标租用共享的已认证 HDC 直连会话；server 拥有目标或仅 server 模式时直接拒绝，不会绕过 server。
/// 绑定 WinUSB 的 HDC 接口只接受一个句柄，因此并发消费方共享一条连接并各自打开通道，而不是重新打开接口。</para></summary>
internal static class HdcDirectConnection
{
    internal static (HdcSessionHandle Lease, HdcSession Session) Open(
        DeviceMonitorService monitor, DetectedDevice device)
        => HdcDirectSessionPool.Acquire(monitor, device);

    /// <summary>Opens one new authenticated direct HDC session with its own controller lease, without sharing.
    /// <para>打开一条由独立控制器租约持有、不参与共享的全新已认证 HDC 直连会话。</para></summary>
    /// <param name="monitor">The monitor owning the controller lease.<para>持有控制器租约的监视器。</para></param>
    /// <param name="device">The target to open.<para>要打开的目标。</para></param>
    /// <returns>The owned lease and its connected session.<para>所持有的租约与其已连接会话。</para></returns>
    internal static (DeviceLease Lease, HdcSession Session) OpenFresh(
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
            monitor.InvalidateUsbInventoryCache();
            direct = monitor.ResolveDirectUsbDevice(device);
            lease = monitor.OpenLeaseByKey(direct.Key, DeviceSessionKind.Hdc);
        }

        if (lease is null)
            throw new IOException(
                $"HDC USB interface for {device.Serial} ({direct.Key}) could not be opened. "
                + "Stop any other adb/hdc/DevEco process that may hold the interface, confirm the device is bound "
                + "to WinUSB, or switch the USB backend to libusb in Settings. "
                + $"无法打开 HDC USB 接口；请停止占用该接口的其它 adb/hdc/DevEco 进程，确认设备绑定 WinUSB，"
                + "或在设置中切换为 libusb USB 后端。");
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
        }

        monitor.InvalidateUsbInventoryCache();
        return monitor.ResolveDirectUsbDevice(device);
    }
}
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Opens a fresh authenticated direct-USB HDC session with its controller lease.
/// <para>打开一条由控制器租约持有的、全新的、已认证的 USB 直连 HDC 会话。</para>
/// Exclusivity is enforced by the caller's disposal of the lease and the controller's per-endpoint
/// reservation; there is no cross-operation connection sharing.
/// <para>独占性由调用方释放租约与控制器按端点占用登记共同保证；不存在跨操作的连接共享。</para>
/// </summary>
internal static class HdcDirectConnection
{
    internal static (DeviceLease Lease, HdcSession Session) Open(DetectedDevice device)
    {
        // A server appearing mid-operation must revoke direct access; never open USB behind the server's back.
        // 操作过程中 server 一旦出现即须收回直连权限；绝不在 server 背后打开 USB。
        if (DeviceMonitorService.UseHdcServerFor(device))
            throw new IOException("HDC server appeared; direct USB access is prohibited. Retry through the server.");

        var direct = DeviceMonitorService.ResolveDirectUsbDevice(device);
        // The controller reopens the exact physical key by lease and encapsulates the native transport
        // wrapper; the HDC server guard above already covers route ownership for this direct path.
        // 控制器按精确物理键以租约方式重开并封装原生传输包装；上方的 server 守卫已覆盖该直连路径的路由归属。
        if (direct.Transport != DeviceTransportKind.Usb)
            throw new IOException("HDC USB interface could not be opened; another host may own it.");
        var lease = App.DeviceMonitor.OpenLeaseByKey(direct.Key, DeviceSessionKind.Hdc)
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
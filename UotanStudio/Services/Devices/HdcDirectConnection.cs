using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Services.Devices;

/// <summary>Opens a fresh authenticated direct-USB HDC session with its controller lease.
/// <para>打开一条由控制器租约持有的全新已认证 USB 直连 HDC 会话。</para></summary>
internal static class HdcDirectConnection
{
    internal static (DeviceLease Lease, HdcSession Session) Open(DetectedDevice device)
    {
        if (DeviceMonitorService.UseHdcServerFor(device))
            throw new IOException("HDC server appeared; direct USB access is prohibited. Retry through the server.");

        var direct = DeviceMonitorService.ResolveDirectUsbDevice(device);
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
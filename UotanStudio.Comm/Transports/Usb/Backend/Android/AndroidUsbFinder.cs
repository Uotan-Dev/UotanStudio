#if ANDROID
using Android.Content;
using Android.Hardware.Usb;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.Android;

/// <summary>
/// Enumerates USB devices attached to the Android host via UsbManager.
/// <para>通过 UsbManager 枚举以 OTG/主机模式接入 Android 主机的 USB 设备。</para>
/// Devices without a granted permission are still listed (metadata only); the session
/// open path fails for them until the user accepts the UsbManager permission prompt.
/// <para>未授权的设备仍会列出（仅元数据）；在用户接受 UsbManager 授权弹窗前，
/// 会话打开会失败。</para>
/// </summary>
internal static class AndroidUsbFinder
{
    /// <summary>
    /// Gets whether the UsbManager service resolved on this process (set on the first
    /// enumeration). False means no Android context is available yet.
    /// <para>获取本进程内 UsbManager 服务是否已解析（首次枚举时置位）。为 false 表示
    /// Android 上下文尚不可用。</para>
    /// </summary>
    public static bool UsbManagerAvailable { get; private set; }

    public static List<UsbDevice> FindDevice(UsbDeviceFilter? filter = null)
    {
        var devices = new List<UsbDevice>();
        var manager = GetUsbManager();
        if (manager is null)
        {
            return devices;
        }

        UsbManagerAvailable = true;

        var deviceList = manager.DeviceList;
        if (deviceList is null)
        {
            return devices;
        }

        foreach (var (deviceId, usbDevice) in deviceList)
        {
            if (usbDevice is null)
            {
                continue;
            }

            var backend = new AndroidUsbDevice(manager, usbDevice, filter?.InterfaceNumber);
            var info = ToFilterProbe(backend);
            if (filter is null || Matches(filter, info))
            {
                devices.Add(backend);
            }
            else
            {
                backend.Dispose();
            }
        }

        return devices;
    }

    private static UsbDeviceInfo ToFilterProbe(AndroidUsbDevice device) => new()
    {
        VendorId = device.VendorId,
        ProductId = device.ProductId,
        SerialNumber = device.SerialNumber,
        DevicePath = device.DevicePath,
        InterfaceClass = device.InterfaceClass,
        InterfaceSubClass = device.InterfaceSubClass,
        InterfaceProtocol = device.InterfaceProtocol,
        InterfaceMetadataObserved = device.InterfaceMetadataObserved,
        Interfaces = device.Interfaces,
        Speed = device.Speed
    };

    private static bool Matches(UsbDeviceFilter filter, UsbDeviceInfo info)
    {
        // Intentionally does not use UsbDeviceFilter.Matches: that helper may consult
        // backend-only fields; Android enumeration runs pre-open, so only the identity
        // and interface metadata captured in the probe are evaluated here.
        // <para>有意不使用 UsbDeviceFilter.Matches：该辅助方法可能依赖仅后端可用的字段；
        // Android 枚举发生在打开之前，因此只评估探测对象中的身份与接口元数据。</para>
        if (filter.VendorId is { } vid && info.VendorId != vid)
        {
            return false;
        }

        if (filter.ProductId is { } pid && info.ProductId != pid)
        {
            return false;
        }

        if (filter.SerialNumber is { } serial && !string.Equals(info.SerialNumber, serial, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.DevicePathEquals is { } pathEquals && !string.Equals(info.DevicePath, pathEquals, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.DevicePathContains is { } pathContains && !info.DevicePath.Contains(pathContains, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.InterfaceClass is { } cls && info.InterfaceClass != cls)
        {
            return false;
        }

        if (filter.InterfaceSubClass is { } sub && info.InterfaceSubClass != sub)
        {
            return false;
        }

        if (filter.InterfaceProtocol is { } proto && info.InterfaceProtocol != proto)
        {
            return false;
        }

        return true;
    }

    internal static UsbManager? GetUsbManager()
    {
        var context = global::Android.App.Application.Context;
        if (context is null)
        {
            return null;
        }

        var manager = context.GetSystemService(Context.UsbService) as UsbManager;
        return manager;
    }
}
#endif
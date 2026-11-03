using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal static partial class WinUSBFinder
{
    /// <summary>
    /// Opens exactly the interface the given path names, without walking the device tree: the path carries the
    /// instance id, VID/PID and interface number, and the inventory cache supplies the classification an
    /// earlier pass read from the registry. Returns <see langword="null"/> whenever that cannot be answered —
    /// unknown path, no remembered interface, or the recorded backend no longer opens — so the caller falls
    /// back to the full enumeration, which is also what re-derives evidence after a driver rebind.
    /// <para>只打开给定路径所指的接口，不遍历设备树：路径本身携带实例 ID、VID/PID 与接口编号，清单缓存则提供
    /// 更早一轮从注册表读到的分类。凡是无法据此作答的情况——路径未知、没有记住的接口、或已记录的后端已打不开——
    /// 一律返回 <see langword="null"/>，由调用方回退到完整枚举；而重新推导证据（例如驱动重绑定之后）也正是由这条
    /// 完整路径完成。</para>
    /// </summary>
    /// <param name="devicePath">Exact interface path taken from a physical device key.
    /// <para>取自物理设备键的精确接口路径。</para></param>
    /// <returns>The opened backend device, or <see langword="null"/> to request a full enumeration.
    /// <para>已打开的后端设备；需要回退到完整枚举时返回 <see langword="null"/>。</para></returns>
    internal static UsbDevice? TryOpenSingleDevice(string devicePath)
    {
        (var vid, var pid) = TryParseVidPid(devicePath);
        var instanceId = TryDeriveInstanceId(devicePath);
        if (vid is null || pid is null || instanceId is null)
        {
            return null;
        }

        if (!WinUsbInventoryCache.TryGet(instanceId, out var cached))
        {
            // Without a remembered interface there is no cheap answer for its class triple, container id or
            // backend, and guessing them from the path alone would report a device the walk never confirmed.
            return null;
        }

        var metadata = CreateCachedMetadataDevice(vid.Value, pid.Value, cached);
        if (metadata is null)
        {
            return null;
        }

        var separator = instanceId.LastIndexOf('\\');
        var candidate = separator >= 0 ? instanceId[(separator + 1)..] : null;
        var serial = ResolveEnumeratedSerial(devicePath, instanceId, candidate, metadata);
        UsbAccessPolicy.RequireDirect(
            metadata.InterfaceClass, metadata.InterfaceSubClass, metadata.InterfaceProtocol, serial);

        var device = OpenKnownBackend(cached.BackendKind, devicePath, null, serial, vid, pid);
        if (device is null)
        {
            return null;
        }

        device.DeviceInstanceId = instanceId;
        var containerId = cached.ContainerRead ? cached.ContainerId : ReadContainerId(instanceId);
        device.ContainerId = containerId;
        if (!string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            UsbSerialCache.Remember(devicePath, instanceId, device.SerialNumber, Environment.TickCount64);
        }

        WinUsbInventoryCache.RememberInterfaceMetadata(
            instanceId,
            vid.Value,
            pid.Value,
            device.InterfaceClass ?? metadata.InterfaceClass,
            device.InterfaceSubClass ?? metadata.InterfaceSubClass,
            device.InterfaceProtocol ?? metadata.InterfaceProtocol,
            device.InterfaceMetadataObserved,
            containerId,
            AttachedBackendName(device));
        return device;
    }

    private static UsbDevice? ProbeDevice(
        string path,
        UsbSessionOptions? options,
        string? serial,
        WinUsbInventoryCache.Record? cached)
    {
        (var vid, var pid) = TryParseVidPid(path);
        var metadata = CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0);
        if (options?.MetadataOnly == true)
        {
            return AttachEnumeratedSerial(metadata, serial);
        }
        UsbAccessPolicy.RequireDirect(metadata.InterfaceClass, metadata.InterfaceSubClass, metadata.InterfaceProtocol, serial);

        // Detection opens the path twice: once to ask whether the legacy driver answers, then again through
        // the backend it chose. A warm pass that already recorded which backend served this interface goes
        // straight there, and only falls back to the full sequence when that backend no longer opens — which
        // is what a driver rebind looks like.
        if (cached?.BackendKind is { Length: > 0 } knownBackend &&
            OpenKnownBackend(knownBackend, path, options, serial, vid, pid) is { } known)
        {
            return known;
        }

        var hDevice = Win32API.SimpleCreateHandle(path);
        if (hDevice == Win32API.INVALID_HANDLE_VALUE)
        {
            UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice cannot open [{path}] - reported with metadata only.");
            return AttachEnumeratedSerial(CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0), serial);
        }

        var isLegacy = false;
        try
        {
            var buffer = new byte[256];
            if (Win32API.DeviceIoControl(hDevice, LegacyUsbDevice.IoGetSerialCode, null, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero))
            {
                isLegacy = true;
            }
        }
        finally
        {
            Win32API.CloseHandle(hDevice);
        }

        if (isLegacy)
        {
            var dev = new LegacyUsbDevice
            {
                DevicePath = path,
                VendorId = vid ?? 0,
                ProductId = pid ?? 0
            };
            if (dev.CreateHandle() == 0)
            {
                return dev;
            }

            UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice legacy open failed for [{path}] - reported with metadata only.");
            return dev;
        }

        if (vid == RockchipVendorId)
        {
            var rockDev = new RockusbDevice
            {
                DevicePath = path,
                VendorId = vid ?? 0,
                ProductId = pid ?? 0,
                SessionOptions = options
            };
            if (rockDev.CreateHandle() == 0)
            {
                if (!string.IsNullOrWhiteSpace(serial))
                {
                    rockDev.SerialNumber = serial;
                }

                UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice opened [{path}] through rockusb.sys.");
                return rockDev;
            }

            rockDev.Dispose();
            UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice rockusb open failed for [{path}] - trying WinUSB.");
        }

        var winDev = new WinUSBDevice
        {
            DevicePath = path,
            VendorId = vid ?? 0,
            ProductId = pid ?? 0,
            SessionOptions = options
        };
        if (winDev.CreateHandle() == 0)
        {
            return winDev;
        }

        UsbTrace.LogFormatted($"WinUSBFinder: ProbeDevice WinUSB open failed for [{path}] - reported with metadata only.");
        return AttachEnumeratedSerial(winDev, serial);
    }

    /// <summary>
    /// Opens the backend an earlier pass recorded for this interface, skipping the detection round-trip.
    /// Returns <see langword="null"/> when that backend no longer opens, which lets the caller run the full
    /// detection sequence — a rebound driver shows up exactly this way.
    /// <para>打开更早一轮为该接口记录的后端，跳过探测往返。该后端此刻已打不开时返回
    /// <see langword="null"/>，由调用方执行完整探测序列——驱动重新绑定正是以这种方式显现。</para>
    /// </summary>
    private static UsbDevice? OpenKnownBackend(
        string backend,
        string path,
        UsbSessionOptions? options,
        string? serial,
        ushort? vid,
        ushort? pid)
    {
        switch (backend)
        {
            case BackendLegacy:
                {
                    var legacy = new LegacyUsbDevice
                    {
                        DevicePath = path,
                        VendorId = vid ?? 0,
                        ProductId = pid ?? 0
                    };
                    _ = legacy.CreateHandle();

                    // The detection path reports a legacy node even when its handle failed, because the node is
                    // still real; the remembered backend must answer the same way.
                    return legacy;
                }

            case BackendRockusb:
                {
                    var rockusb = new RockusbDevice
                    {
                        DevicePath = path,
                        VendorId = vid ?? 0,
                        ProductId = pid ?? 0,
                        SessionOptions = options
                    };
                    if (rockusb.CreateHandle() != 0)
                    {
                        rockusb.Dispose();
                        return null;
                    }

                    if (!string.IsNullOrWhiteSpace(serial))
                    {
                        rockusb.SerialNumber = serial;
                    }

                    UsbTrace.LogFormatted($"WinUSBFinder: opened [{path}] through the remembered rockusb.sys backend.");
                    return rockusb;
                }

            default:
                {
                    var winUsb = new WinUSBDevice
                    {
                        DevicePath = path,
                        VendorId = vid ?? 0,
                        ProductId = pid ?? 0,
                        SessionOptions = options
                    };
                    return winUsb.CreateHandle() == 0
                        ? winUsb
                        : null;
                }
        }
    }

    private static UsbDevice? TryOpenWinUSB(string path, UsbSessionOptions? options, string? serial)
    {
        (var vid, var pid) = TryParseVidPid(path);
        var metadata = CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0);
        if (options?.MetadataOnly == true)
        {
            return AttachEnumeratedSerial(metadata, serial);
        }
        UsbAccessPolicy.RequireDirect(metadata.InterfaceClass, metadata.InterfaceSubClass, metadata.InterfaceProtocol, serial);

        var dev = new WinUSBDevice
        {
            DevicePath = path,
            VendorId = vid ?? 0,
            ProductId = pid ?? 0,
            SessionOptions = options
        };
        if (dev.CreateHandle() == 0)
        {
            return dev;
        }

        UsbTrace.LogFormatted($"WinUSBFinder: TryOpenWinUSB open failed for [{path}] - reported with metadata only.");
        return AttachEnumeratedSerial(dev, serial);
    }

}
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

internal static class LibUsbFinder
{
    // LibUsbDotNet 3.0.224 no longer bundles the native libusb runtime. Creating a
    // UsbContext when the native library is absent leaves a half-initialized context
    // whose finalizer NullReferenceExceptions (known upstream issue). Probe for the
    // runtime first so we never create a context without the native library present.
    [DllImport("libusb-1.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int libusb_init(out IntPtr context);

    [DllImport("libusb-1.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libusb_exit(IntPtr context);

    private static bool IsNativeRuntimePresent()
    {
        try
        {
            // Resolves the entry points without invoking them; throws when the
            // native library (or the entry point) is absent.
            Marshal.PrelinkAll(typeof(LibUsbFinder));
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetBulkInterface(
        LibUsbDotNet.LibUsb.UsbDevice device,
        UsbDeviceFilter? filter,
        out bool descriptorReadFailed,
        out byte interfaceId,
        out byte inEndpoint,
        out byte outEndpoint,
        out byte interfaceClass,
        out byte interfaceSubClass,
        out byte interfaceProtocol,
        out IReadOnlyList<UsbInterfaceInfo> interfaces)
    {
        descriptorReadFailed = false;
        interfaceId = 0;
        inEndpoint = 0;
        outEndpoint = 0;
        interfaceClass = 0;
        interfaceSubClass = 0;
        interfaceProtocol = 0;
        interfaces = [];

        try
        {
            // Collect the FULL interface list first (all configs, all interfaces), then
            // match the filter in a separate pass. Matching while collecting truncated
            // the reported list: the first matching interface returned early with only
            // the interfaces enumerated so far, so later interfaces never made it into
            // the list (e.g. FT2232H: interface 0 matched, interface 1 was dropped).
            // <para>先完整收集所有配置下的全部接口，再单独进行过滤器匹配。边收集边匹配会在
            // 首个命中接口处提前返回，导致后续接口（如 FT2232H 的接口 1）从列表中丢失。</para>
            var configs = device.Configs.ToList();
            var collected = new List<UsbInterfaceInfo>();
            foreach (LibUsbDotNet.Info.UsbConfigInfo? config in configs)
            {
                foreach (LibUsbDotNet.Info.UsbInterfaceInfo? ifc in config.Interfaces)
                {
                    var endpoints = new List<UsbEndpointInfo>();
                    foreach (LibUsbDotNet.Info.UsbEndpointInfo? endpoint in ifc.Endpoints)
                    {
                        endpoints.Add(new UsbEndpointInfo
                        {
                            EndpointAddress = (byte)endpoint.EndpointAddress,
                            Attributes = (byte)endpoint.Attributes,
                            MaxPacketSize = (ushort)endpoint.MaxPacketSize,
                            Interval = (byte)endpoint.Interval
                        });
                    }
                    collected.Add(new UsbInterfaceInfo
                    {
                        InterfaceNumber = (byte)ifc.Number,
                        Class = (byte)ifc.Class,
                        SubClass = (byte)ifc.SubClass,
                        Protocol = (byte)ifc.Protocol,
                        Endpoints = endpoints
                    });
                }
            }

            foreach (LibUsbDotNet.Info.UsbConfigInfo? config in configs)
            {
                foreach (LibUsbDotNet.Info.UsbInterfaceInfo? ifc in config.Interfaces)
                {
                    if (filter?.InterfaceClass is byte c && (byte)ifc.Class != c) continue;
                    if (filter?.InterfaceSubClass is byte s && (byte)ifc.SubClass != s) continue;
                    if (filter?.InterfaceProtocol is byte p && (byte)ifc.Protocol != p) continue;
                    if (filter?.InterfaceNumber is byte n && (byte)ifc.Number != n) continue;

                    var hasIn = false;
                    var hasOut = false;
                    byte candidateIn = 0;
                    byte candidateOut = 0;
                    foreach (LibUsbDotNet.Info.UsbEndpointInfo? endpoint in ifc.Endpoints)
                    {
                        // Collect bulk endpoints first (the session I/O path), then interrupt
                        // endpoints as fallback candidates so devices with only interrupt pipes
                        // (e.g. HID) can still be opened when the filter requests explicit
                        // endpoint addresses (interrupt-test).
                        var epType = endpoint.Attributes & 0x03;
                        if (epType != 0x02 && epType != 0x03) continue;

                        if ((endpoint.EndpointAddress & 0x80) != 0)
                        {
                            hasIn = true;
                            if (candidateIn == 0) candidateIn = endpoint.EndpointAddress;
                        }
                        else
                        {
                            hasOut = true;
                            if (candidateOut == 0) candidateOut = endpoint.EndpointAddress;
                        }
                    }

                    if (hasIn && hasOut)
                    {
                        // Honor an explicit endpoint requirement: the interface must contain
                        // the requested addresses (e.g. Rockchip loader on 0x82/0x02), and the
                        // requested endpoints win over the first bulk pair when both exist.
                        var inOk = filter?.EndpointAddressIn is not byte reqIn ||
                            ifc.Endpoints.Any(e => (e.EndpointAddress & 0x80) != 0 && e.EndpointAddress == reqIn);
                        var outOk = filter?.EndpointAddressOut is not byte reqOut ||
                            ifc.Endpoints.Any(e => (e.EndpointAddress & 0x80) == 0 && e.EndpointAddress == reqOut);
                        if (inOk && outOk)
                        {
                            interfaceId = (byte)ifc.Number;
                            inEndpoint = filter?.EndpointAddressIn ?? candidateIn;
                            outEndpoint = filter?.EndpointAddressOut ?? candidateOut;
                            interfaceClass = (byte)ifc.Class;
                            interfaceSubClass = (byte)ifc.SubClass;
                            interfaceProtocol = (byte)ifc.Protocol;
                            interfaces = collected;
                            return true;
                        }
                    }
                    else if (hasIn && filter is { EndpointAddressIn: byte inOnlyEp, EndpointAddressOut: null })
                    {
                        // IN-only match: interrupt-test on HID devices that expose no OUT pipe.
                        interfaceId = (byte)ifc.Number;
                        inEndpoint = inOnlyEp;
                        outEndpoint = 0;
                        interfaceClass = (byte)ifc.Class;
                        interfaceSubClass = (byte)ifc.SubClass;
                        interfaceProtocol = (byte)ifc.Protocol;
                        interfaces = collected;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Reading the configuration descriptors failed (e.g. the device's driver
            // state is corrupted after a failed session read). This is DIFFERENT from
            // "no interface matches the filter": the caller must keep the device
            // visible instead of silently dropping it from enumeration.
            // <para>读取配置描述符失败（例如会话读失败后设备驱动状态损坏）。这与
            // "没有接口匹配过滤器"不同：调用方必须保留该设备，而不是将其静默地从
            // 枚举中丢弃。</para>
            descriptorReadFailed = true;
            UsbTrace.Log("LibUsbFinder: failed to inspect interface descriptors.");
            return false;
        }

        return false;
    }

    public static List<global::UotanStudio.Comm.Transports.Usb.Backend.UsbDevice> FindDevice(UsbDeviceFilter? filter = null)
    {
        var devices = new List<global::UotanStudio.Comm.Transports.Usb.Backend.UsbDevice>();
        if (!IsNativeRuntimePresent())
        {
            UsbTrace.Log("LibUsb backend unavailable: native libusb runtime not found.");
            return devices;
        }

        using (var context = new UsbContext())
        using (UsbDeviceCollection deviceList = context.List())
        {
            // NOTE: LibUsbDotNet's UsbContext is NOT thread-safe; a Parallel.For over
            // device descriptor reads caused the CLI to hang (features/pull tests
            // timed out at 30 s). Descriptor reads stay serial — enumeration latency
            // is dominated by libusb's own List()/descriptor cache, not our loop.
            // <para>注意：LibUsbDotNet 的 UsbContext 不是线程安全的；对设备描述符
            // 读取使用 Parallel.For 会导致 CLI 挂起（features/pull 测试 30 秒超时）。
            // 描述符读取保持串行——枚举延迟主要由 libusb 自身的 List()/描述符缓存
            // 决定，而非我们的循环。</para>
            foreach (IUsbDevice? device in deviceList)
            {
                var libUsbDevice = device as LibUsbDotNet.LibUsb.UsbDevice;
                if (libUsbDevice == null) continue;
                if (filter?.VendorId is ushort filterVid && (ushort)device.VendorId != filterVid) continue;
                if (filter?.ProductId is ushort filterPid && (ushort)device.ProductId != filterPid) continue;

                UsbDevice? built = BuildDevice(libUsbDevice, filter);
                if (built != null) devices.Add(built);
            }
        }
        return devices;
    }

    /// <summary>
    /// Builds a backend device (or metadata-only entry) for a single libusb device,
    /// reading its configuration descriptors. Thread-safe: no shared mutable state.
    /// <para>为单个 libusb 设备构建后端设备（或仅元数据条目），读取其配置描述符。
    /// 线程安全：无共享可变状态。</para>
    /// </summary>
    private static global::UotanStudio.Comm.Transports.Usb.Backend.UsbDevice? BuildDevice(
        LibUsbDotNet.LibUsb.UsbDevice libUsbDevice,
        UsbDeviceFilter? filter)
    {
        if (!TryGetBulkInterface(
            libUsbDevice,
            filter,
            out var descriptorReadFailed,
            out var interfaceId,
            out var readEndpoint,
            out var writeEndpoint,
            out var interfaceClass,
            out var interfaceSubClass,
            out var interfaceProtocol,
            out IReadOnlyList<UsbInterfaceInfo> interfaces))
        {
            if (!descriptorReadFailed)
            {
                // Normal case: the device simply has no interface matching the
                // filter — skip it, keep scanning.
                // <para>正常情况：设备只是没有匹配过滤器的接口——跳过并继续扫描。</para>
                return null;
            }

            // Descriptor read failed (e.g. driver state corrupted after a failed
            // session read). Keep the device visible with metadata-only (VID/PID/
            // path) instead of silently dropping it from enumeration; sessions
            // over a non-open device are skipped later by ToSessions.
            // <para>描述符读取失败（例如会话读失败后驱动状态损坏）。以仅元数据
            // （VID/PID/路径）保留设备可见，而不是将其静默地从枚举中丢弃；
            // 基于未打开设备的会话稍后由 ToSessions 跳过。</para>
            var degradedBus = libUsbDevice.BusNumber;
            var degradedAddress = libUsbDevice.Address;
            var degraded = new LibUsbDevice
            {
                Vid = (ushort)libUsbDevice.VendorId,
                Pid = (ushort)libUsbDevice.ProductId,
                BusNumber = degradedBus,
                DeviceAddress = degradedAddress,
                InterfaceMetadataObserved = false,
                Speed = MapSpeed(libUsbDevice.Speed),
                DevicePath = $"Bus {degradedBus} Device {degradedAddress}: {libUsbDevice.VendorId:X4}:{libUsbDevice.ProductId:X4}",
                UsbDeviceType = global::UotanStudio.Comm.Transports.Usb.Backend.UsbDeviceType.LibUSB
            };

            UsbTrace.Log($"LibUsbFinder: device {libUsbDevice.VendorId:X4}:{libUsbDevice.ProductId:X4} descriptor read failed - reported with metadata only.");
            return degraded;
        }

        var busNumber = libUsbDevice.BusNumber;
        var address = libUsbDevice.Address;

        var usbDevice = new LibUsbDevice
        {
            Vid = (ushort)libUsbDevice.VendorId,
            Pid = (ushort)libUsbDevice.ProductId,
            BusNumber = busNumber,
            DeviceAddress = address,
            InterfaceId = interfaceId,
            ReadEndpointId = readEndpoint,
            WriteEndpointId = writeEndpoint,
            InterfaceClass = interfaceClass,
            InterfaceSubClass = interfaceSubClass,
            InterfaceProtocol = interfaceProtocol,
            InterfaceMetadataObserved = true,
            Interfaces = interfaces,
            Speed = MapSpeed(libUsbDevice.Speed),
            DevicePath = $"Bus {busNumber} Device {address}: {libUsbDevice.VendorId:X4}:{libUsbDevice.ProductId:X4}",
            UsbDeviceType = global::UotanStudio.Comm.Transports.Usb.Backend.UsbDeviceType.LibUSB
        };

        // Keep the device even when the handle cannot be opened (e.g. the interface
        // is claimed by another session or process). Enumeration must reflect the
        // current device state: the metadata was already collected above, and a busy
        // device must not silently disappear from the list.
        // <para>即使句柄无法打开（例如接口已被其他会话或进程声明）也保留该设备。
        // 枚举必须反映当前设备状态：元数据已在上方收集，被占用的设备不应静默地从
        // 列表中消失。</para>
        // Note: we do NOT open the handle here. Enumeration is metadata discovery;
        // opening is deferred to session creation (UsbProviderProjection.ToSessions
        // opens on demand). Opening during enumeration would be wasted work for the
        // info-only path (ToInfos projects then disposes the device).
        // <para>注意：此处不打开句柄。枚举是元数据发现；打开延迟到会话创建
        // （UsbProviderProjection.ToSessions 按需打开）。枚举阶段打开对仅需信息的
        // 路径（ToInfos 投影后即释放设备）是白费工作。</para>
        return usbDevice;
    }

    /// <summary>
    /// Maps LibUsbDotNet's <see cref="Speed"/> to the library's <see cref="UsbDeviceSpeed"/>.
    /// <para>将 LibUsbDotNet 的 <see cref="Speed"/> 映射到本库的 <see cref="UsbDeviceSpeed"/>。</para>
    /// LibUsbDotNet does not distinguish Super vs SuperPlus, so both report as Super.
    /// <para>LibUsbDotNet 不区分 Super 与 SuperPlus，两者都报告为 Super。</para>
    /// </summary>
    private static UsbDeviceSpeed MapSpeed(Speed speed)
    {
        return speed switch
        {
            Speed.Low => UsbDeviceSpeed.Low,
            Speed.Full => UsbDeviceSpeed.Full,
            Speed.High => UsbDeviceSpeed.High,
            Speed.Super => UsbDeviceSpeed.Super,
            _ => UsbDeviceSpeed.Unknown
        };
    }

    public static bool IsRuntimeAvailable(out string? reason)
    {
        reason = null;
        if (!IsNativeRuntimePresent())
        {
            reason = "native libusb runtime not found";
            UsbTrace.Log($"LibUsb runtime probe failed: {reason}");
            return false;
        }

        // Verify with the raw native API instead of constructing a LibUsbDotNet UsbContext:
        // when libusb_init fails, LibUsbDotNet leaves a half-initialized context whose
        // finalizer throws NullReferenceException (known upstream issue) - on CI this fails
        // the whole test run even when every test passes. libusb_init/libusb_exit probe the
        // runtime without creating any LibUsbDotNet object.
        if (!ProbeNativeInit())
        {
            reason = "libusb_init failed";
            UsbTrace.Log($"LibUsb runtime probe failed: {reason}");
            return false;
        }

        return true;
    }

    private static bool ProbeNativeInit()
    {
        try
        {
            if (libusb_init(out var context) == 0 && context != IntPtr.Zero)
            {
                libusb_exit(context);
                return true;
            }

            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }


}




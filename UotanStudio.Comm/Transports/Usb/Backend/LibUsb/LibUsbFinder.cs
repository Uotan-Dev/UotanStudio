using System.Runtime.InteropServices;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.LibUsb;

internal static partial class LibUsbFinder
{
    [LibraryImport("libusb-1.0")]
    private static partial int libusb_init(out IntPtr context);

    [LibraryImport("libusb-1.0")]
    private static partial void libusb_exit(IntPtr context);

    private static bool IsNativeRuntimePresent()
    {
        try
        {
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
            var configs = device.Configs.ToList();
            var collected = new List<UsbInterfaceInfo>();
            foreach (var config in configs)
            {
                foreach (var ifc in config.Interfaces)
                {
                    var endpoints = new List<UsbEndpointInfo>();
                    foreach (var endpoint in ifc.Endpoints)
                    {
                        endpoints.Add(new UsbEndpointInfo
                        {
                            EndpointAddress = endpoint.EndpointAddress,
                            Attributes = endpoint.Attributes,
                            MaxPacketSize = endpoint.MaxPacketSize,
                            Interval = endpoint.Interval
                        });
                    }
                    collected.Add(new UsbInterfaceInfo
                    {
                        InterfaceNumber = (byte)ifc.Number,
                        Class = (byte)ifc.Class,
                        SubClass = ifc.SubClass,
                        Protocol = ifc.Protocol,
                        Endpoints = endpoints
                    });
                }
            }

            foreach (var config in configs)
            {
                foreach (var ifc in config.Interfaces)
                {
                    if (filter?.InterfaceClass is byte c && (byte)ifc.Class != c)
                    {
                        continue;
                    }

                    if (filter?.InterfaceSubClass is byte s && ifc.SubClass != s)
                    {
                        continue;
                    }

                    if (filter?.InterfaceProtocol is byte p && ifc.Protocol != p)
                    {
                        continue;
                    }

                    if (filter?.InterfaceNumber is byte n && (byte)ifc.Number != n)
                    {
                        continue;
                    }

                    var hasIn = false;
                    var hasOut = false;
                    byte candidateIn = 0;
                    byte candidateOut = 0;
                    foreach (var endpoint in ifc.Endpoints)
                    {
                        var epType = endpoint.Attributes & 0x03;
                        if (epType is not 0x02 and not 0x03)
                        {
                            continue;
                        }

                        if ((endpoint.EndpointAddress & 0x80) != 0)
                        {
                            hasIn = true;
                            if (candidateIn == 0)
                            {
                                candidateIn = endpoint.EndpointAddress;
                            }
                        }
                        else
                        {
                            hasOut = true;
                            if (candidateOut == 0)
                            {
                                candidateOut = endpoint.EndpointAddress;
                            }
                        }
                    }

                    if (hasIn && hasOut)
                    {
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
                            interfaceSubClass = ifc.SubClass;
                            interfaceProtocol = ifc.Protocol;
                            interfaces = collected;
                            return true;
                        }
                    }
                    else if (hasIn && filter is { EndpointAddressIn: byte inOnlyEp, EndpointAddressOut: null })
                    {
                        interfaceId = (byte)ifc.Number;
                        inEndpoint = inOnlyEp;
                        outEndpoint = 0;
                        interfaceClass = (byte)ifc.Class;
                        interfaceSubClass = ifc.SubClass;
                        interfaceProtocol = ifc.Protocol;
                        interfaces = collected;
                        return true;
                    }
                }
            }
        }
        catch
        {
            descriptorReadFailed = true;
            UsbTrace.Log("LibUsbFinder: failed to inspect interface descriptors.");
            return false;
        }

        return false;
    }

    public static List<UsbDevice> FindDevice(UsbDeviceFilter? filter = null)
    {
        var devices = new List<UsbDevice>();
        if (!IsNativeRuntimePresent())
        {
            UsbTrace.Log("LibUsb backend unavailable: native libusb runtime not found.");
            return devices;
        }

        using (var context = new UsbContext())
        using (var deviceList = context.List())
        {
            foreach (var device in deviceList)
            {
                if (device is not LibUsbDotNet.LibUsb.UsbDevice libUsbDevice)
                {
                    continue;
                }

                if (filter?.VendorId is ushort filterVid && device.VendorId != filterVid)
                {
                    continue;
                }

                if (filter?.ProductId is ushort filterPid && device.ProductId != filterPid)
                {
                    continue;
                }

                var built = BuildDevice(libUsbDevice, filter);
                if (built != null)
                {
                    devices.Add(built);
                }
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
    private static UsbDevice? BuildDevice(
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
            out var interfaces))
        {
            if (!descriptorReadFailed)
            {
                return null;
            }

            var degradedBus = libUsbDevice.BusNumber;
            var degradedAddress = libUsbDevice.Address;
            var degraded = new LibUsbDevice
            {
                Vid = libUsbDevice.VendorId,
                Pid = libUsbDevice.ProductId,
                BusNumber = degradedBus,
                DeviceAddress = degradedAddress,
                PhysicalPortPath = LibUsbPortIdentity.Observe(libUsbDevice),
                InterfaceMetadataObserved = false,
                Speed = MapSpeed(libUsbDevice.Speed),
                DevicePath = $"Bus {degradedBus} Device {degradedAddress}: {libUsbDevice.VendorId:X4}:{libUsbDevice.ProductId:X4}",
                UsbDeviceType = UsbDeviceType.LibUSB
            };

            UsbTrace.Log($"LibUsbFinder: device {libUsbDevice.VendorId:X4}:{libUsbDevice.ProductId:X4} descriptor read failed - reported with metadata only.");
            return degraded;
        }

        var busNumber = libUsbDevice.BusNumber;
        var address = libUsbDevice.Address;

        var usbDevice = new LibUsbDevice
        {
            Vid = libUsbDevice.VendorId,
            Pid = libUsbDevice.ProductId,
            BusNumber = busNumber,
            DeviceAddress = address,
            PhysicalPortPath = LibUsbPortIdentity.Observe(libUsbDevice),
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
            UsbDeviceType = UsbDeviceType.LibUSB
        };

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
            Speed.Unknown => UsbDeviceSpeed.Unknown,
            _ => UsbDeviceSpeed.Unknown
        };
    }

    /// <summary>
    /// Probes whether the libusb backend can run here. The runtime must be the file the caller
    /// registered or the platform library copied next to the application; a libusb that is only
    /// installed elsewhere on the system is deliberately not accepted, and
    /// <c>libusb_init</c> must then succeed.
    /// <para>探测 libusb 后端在当前环境是否可运行。运行时必须是调用者注册的文件或复制到程序目录的
    /// 平台库；仅安装在系统其他位置的 libusb 有意不被接受，随后 <c>libusb_init</c> 必须成功。</para>
    /// </summary>
    /// <param name="reason">A short failure reason, or <c>null</c> on success.
    /// <para>失败原因的简短说明；成功时为 <c>null</c>。</para></param>
    /// <returns><c>true</c> when the runtime is loaded and initializes.
    /// <para>运行时已加载且初始化成功时为 <c>true</c>。</para></returns>
    public static bool IsRuntimeAvailable(out string? reason)
    {
        reason = null;
        if (!LibUsbNativeLoader.TryEnsureLoaded(out var libraryPath))
        {
            reason = "native libusb runtime is neither next to the application nor explicitly registered";
            UsbTrace.Log($"LibUsb runtime probe failed: {reason}");
            return false;
        }

        if (!IsNativeRuntimePresent())
        {
            reason = $"native libusb runtime did not resolve from '{libraryPath}'";
            UsbTrace.Log($"LibUsb runtime probe failed: {reason}");
            return false;
        }

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
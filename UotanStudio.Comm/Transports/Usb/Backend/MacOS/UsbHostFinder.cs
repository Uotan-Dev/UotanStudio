using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// Enumerates macOS USB devices for the IOUSBHost backend. Discovery runs through the
/// IORegistry against IOUSBDevice services with DEVICE-LEVEL scalar property reads
/// only (IdVendor/IdProduct/serial/service path) plus child-service enumeration —
/// it deliberately does NOT build a global IOUSBInterface table: iterating all
/// interface services and reading their properties has been observed to block
/// indefinitely inside the IORegistry MIG call when a device is wedged (verified by
/// stash-controlled A/B testing on this machine; the IOKit classic finder still uses
/// that table and can hang in the same situation). A device whose enumeration never
/// completes stays unpublished, and class matching — IOUSBDevice and IOUSBHostDevice
/// alike — only returns published services, so such a device is invisible here until
/// its port is power-cycled.
/// <para>IOUSBHost 后端的 macOS USB 设备枚举器。发现经 IORegistry 针对 IOUSBDevice
/// 服务进行，仅做设备级标量属性读取（IdVendor/IdProduct/序列号/服务路径）加子服务
/// 枚举——刻意不构建全局 IOUSBInterface 表：迭代全部接口服务并读取其属性时，若某
/// 设备处于异常状态，IORegistry MIG 调用会无限阻塞（已在本机经 stash 对照 A/B 实验
/// 证实；IOKit 经典查找器仍使用该表，同样情形下会挂起）。枚举始终未完成而停在未发布
/// 状态的设备对类匹配不可见——IOUSBDevice 与 IOUSBHostDevice 皆只返回已发布服务——
/// 其端口断电前本查找器看不到它。</para>
/// </summary>
/// <remarks>
/// Enumeration is metadata-only and never opens device user clients; sessions open
/// lazily via <see cref="UsbDevice.CreateHandle"/>, which discovers bulk pipes from
/// descriptors at open time. Interface class/subclass/protocol metadata is therefore
/// not available at enumeration time (<c>InterfaceMetadataObserved=false</c>);
/// interface-code filters are enforced leniently (kept when metadata is absent).
/// <para>枚举仅读取元数据，绝不打开设备用户客户端；会话经
/// <see cref="UsbDevice.CreateHandle"/> 惰性打开，打开时再从描述符发现批量管道。
/// 因此枚举期没有接口类/子类/协议元数据（<c>InterfaceMetadataObserved=false</c>）；
/// 接口码过滤器宽松执行（元数据缺失时保留设备）。</para>
/// </remarks>
internal static partial class UsbHostFinder
{
    /// <summary>
    /// Whether the last enumeration saw the IOUSBHost ObjC classes. Surfaced for
    /// diagnostics; not cached negatively — availability is re-checked on demand.
    /// <para>上次枚举时 IOUSBHost ObjC 类是否可用。用于诊断；不缓存负结果——
    /// 可用性按需重新检查。</para>
    /// </summary>
    internal static bool LastUsbHostAvailable { get; private set; } = true;

    /// <summary>
    /// Number of IOUSBDevice services observed on the last scan, before filtering.
    /// <para>上次扫描观测到的 IOUSBDevice 服务数（过滤前）。</para>
    /// </summary>
    internal static int LastScannedDeviceCount { get; private set; }

    /// <summary>
    /// Number of devices that matched the filter on the last scan.
    /// <para>上次扫描匹配过滤器的设备数。</para>
    /// </summary>
    internal static int LastMatchedDeviceCount { get; private set; }

    /// <summary>
    /// Enumerates macOS IOUSBDevice services via the IORegistry and returns matching
    /// <see cref="UsbHostDevice"/> objects. The IOUSBHost ObjC classes must be
    /// loadable — otherwise an empty list is returned (the provider falls back to
    /// the IOKit classic finder).
    /// <para>经 IORegistry 枚举 macOS IOUSBDevice 服务并返回匹配的
    /// <see cref="UsbHostDevice"/> 对象。IOUSBHost ObjC 类必须可加载——否则返回空
    /// 列表（提供器回退到 IOKit 经典查找器）。</para>
    /// </summary>
    /// <param name="filter">
    /// Optional vendor/product/serial filter; null keeps every service with a known
    /// VID or PID. A blank <see cref="UsbDeviceFilter.SerialNumber"/> is ignored and a
    /// provided one matches case-insensitively.
    /// <para>可选的厂商/产品/序列号过滤器；null 时保留所有已知 VID 或 PID 的服务。
    /// <see cref="UsbDeviceFilter.SerialNumber"/> 为空时忽略该项，提供时按不区分大小写匹配。</para>
    /// </param>
    public static List<UsbDevice> FindDevice(UsbDeviceFilter? filter = null)
    {
        List<UsbDevice> devices = [];
        LastScannedDeviceCount = 0;
        LastMatchedDeviceCount = 0;

        if (!UsbHostAPI.IsAvailable)
        {
            UsbTrace.Log("UsbHostFinder: IOUSBHost ObjC classes unavailable.");
            LastUsbHostAvailable = false;
            return devices;
        }

        LastUsbHostAvailable = true;

        var matching = IntPtr.Zero;
        var iterator = IntPtr.Zero;
        var scanned = 0;

        try
        {
            matching = IOKitUsbAPI.IOServiceMatching(IOKitUsbAPI.IOUSBDeviceClassName);
            UsbTrace.Log($"UsbHostFinder: IOServiceMatching(IOUSBDevice) = {matching}");
            if (matching == IntPtr.Zero)
            {
                return devices;
            }

            var kr = IOKitUsbAPI.IOServiceGetMatchingServices(IntPtr.Zero, matching, out var iter);
            UsbTrace.Log($"UsbHostFinder: IOServiceGetMatchingServices kr={kr} iter={iter}");
            matching = IntPtr.Zero;
            if (kr != IOKitUsbAPI.KIOReturnSuccess || iter == IntPtr.Zero)
            {
                return devices;
            }

            iterator = iter;

            uint next;
            while ((next = IOKitUsbAPI.IOIteratorNext(iterator)) != 0)
            {
                scanned++;
                LastScannedDeviceCount = scanned;
                var service = new IntPtr(next);
                try
                {
                    var vid = ReadCFNumberUshort(service, "idVendor");
                    var pid = ReadCFNumberUshort(service, "idProduct");
                    if (vid == 0 && pid == 0)
                    {
                        continue;
                    }

                    var serial = ReadCFString(service, "USB Serial Number")
                                 ?? ReadCFString(service, "serial number");
                    var path = TryGetServicePath(service) ?? $"IOKit:{vid:X4}:{pid:X4}";

                    if (filter?.VendorId is ushort filterVid && vid != filterVid)
                    {
                        continue;
                    }

                    if (filter?.ProductId is ushort filterPid && pid != filterPid)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(filter?.SerialNumber) &&
                        !string.Equals(serial, filter.SerialNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var device = new UsbHostDevice
                    {
                        DevicePath = path,
                        PhysicalPortPath = MacUsbPortIdentity.Observe(service),
                        VendorId = vid,
                        ProductId = pid,
                        SerialNumber = serial ?? string.Empty,
                        InterfaceMetadataObserved = false,
                        Interfaces = [],
                        UsbDeviceType = UsbDeviceType.MacOS,
                    };
                    ApplyInterfaceTriple(service, device);
                    devices.Add(device);
                    UsbTrace.Log($"UsbHostFinder: dev vid={vid:X4} pid={pid:X4} serial='{serial}' path='{path}'");
                }
                catch (Exception ex)
                {
                    UsbTrace.Log($"UsbHostFinder: device read failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    IOKitUsbAPI.IOObjectRelease(service);
                }
            }
        }
        catch (Exception ex)
        {
            UsbTrace.Log($"UsbHostFinder: enumeration failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (matching != IntPtr.Zero)
            {
                IOKitUsbAPI.CFRelease(matching);
            }

            if (iterator != IntPtr.Zero)
            {
                IOKitUsbAPI.IOObjectRelease(iterator);
            }
        }

        AppendUnclaimedPortDevices(devices, filter, ref scanned);

        LastScannedDeviceCount = scanned;
        LastMatchedDeviceCount = devices.Count;
        UsbTrace.Log($"UsbHostFinder: done, scanned {scanned}, matched {devices.Count}");
        return devices;
    }

    /// <summary>
    /// Appends devices reached through the USB port nodes that class matching missed.
    /// <para>补充经 USB 端口节点到达、但被类匹配漏掉的设备。</para>
    /// </summary>
    /// <remarks>
    /// A device with no matching kernel personality never appears in the
    /// <c>IOUSBDevice</c>/<c>IOUSBHostDevice</c> class match even though it enumerated and
    /// exposes readable properties — a Qualcomm EDL target in <c>QUSB__BULK</c> state is the
    /// observed case (verified on macOS 27: the node resolves by path, its descriptors are
    /// complete, and the IOUSBDevice plug-in interface opens and selects configuration 1,
    /// yet both class matches report it absent). Those devices are exactly the ones the EDL
    /// bootstrap needs, so the port walk runs after the class match and skips services it
    /// already published.
    /// <para>没有匹配内核 personality 的设备即使完成枚举并暴露可读属性，也永远不出现在
    /// <c>IOUSBDevice</c>/<c>IOUSBHostDevice</c> 类匹配结果里——实测为处于 <c>QUSB__BULK</c>
    /// 状态的高通 EDL 目标（在 macOS 27 上验证：节点可按路径解析、描述符完整、IOUSBDevice
    /// 插件接口可打开并成功选择配置 1，但两个类匹配都报告它不存在）。这些设备正是 EDL
    /// 引导所需的，因此在类匹配之后补跑端口遍历，并跳过已发布的重复服务。</para>
    /// </remarks>
    private static void AppendUnclaimedPortDevices(List<UsbDevice> devices, UsbDeviceFilter? filter, ref int scanned)
    {
        List<uint> services;
        try
        {
            services = UsbHostAPI.GetPortAttachedDeviceServices();
        }
        catch (Exception ex)
        {
            UsbTrace.Log($"UsbHostFinder: port walk unavailable: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        try
        {
            foreach (var serviceValue in services)
            {
                var service = new IntPtr(serviceValue);
                try
                {
                    scanned++;
                    var vid = ReadCFNumberUshort(service, "idVendor");
                    var pid = ReadCFNumberUshort(service, "idProduct");
                    if (vid == 0 && pid == 0)
                    {
                        continue;
                    }

                    var path = TryGetServicePath(service) ?? $"IOKit:{vid:X4}:{pid:X4}";
                    if (devices.Any(existing => string.Equals(existing.DevicePath, path, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    if (filter?.VendorId is ushort filterVid && vid != filterVid)
                    {
                        continue;
                    }

                    if (filter?.ProductId is ushort filterPid && pid != filterPid)
                    {
                        continue;
                    }

                    var serial = ReadCFString(service, "USB Serial Number")
                                 ?? ReadCFString(service, "serial number");
                    if (!string.IsNullOrWhiteSpace(filter?.SerialNumber) &&
                        !string.Equals(serial, filter.SerialNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var device = new UsbHostDevice
                    {
                        DevicePath = path,
                        PhysicalPortPath = MacUsbPortIdentity.Observe(service),
                        VendorId = vid,
                        ProductId = pid,
                        SerialNumber = serial ?? string.Empty,
                        InterfaceMetadataObserved = false,
                        Interfaces = [],
                        UsbDeviceType = UsbDeviceType.MacOS,
                    };
                    ApplyInterfaceTriple(service, device);
                    devices.Add(device);
                    UsbTrace.Log($"UsbHostFinder: port-walk dev vid={vid:X4} pid={pid:X4} serial='{serial}' path='{path}'");
                }
                catch (Exception ex)
                {
                    UsbTrace.Log($"UsbHostFinder: port-walk device read failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var serviceValue in services)
            {
                IOKitUsbAPI.IOObjectRelease((IntPtr)serviceValue);
            }
        }
    }

    private static ushort ReadCFNumberUshort(IntPtr service, string propertyName)
        => TryReadCFNumberRawU32(service, propertyName, out var raw)
            ? unchecked((ushort)raw)
            : (ushort)0;

    /// <summary>
    /// Publishes the class/subclass/protocol of this device's own interface children, preferring a vendor-specific
    /// (0xFF) interface. Discovery needs the triple to classify a direct-USB ADB, fastboot or EDL port, and only the
    /// per-device children are read — the global interface sweep is what can block on a wedged device.
    /// <para>公布本设备自身接口子项的 class/subclass/protocol，并优先选用厂商自定义（0xFF）接口。发现层需要该三元组
    /// 才能把直连 USB 的 adb、fastboot 或 EDL 端口分类出来；这里只读本设备的子项——会在异常设备上阻塞的是全局接口扫描。</para>
    /// </summary>
    /// <param name="deviceService">The device service whose children are read. <para>待读取子项的设备服务。</para></param>
    /// <param name="device">The device record to publish into. <para>接收结果的设备记录。</para></param>
    private static void ApplyInterfaceTriple(IntPtr deviceService, UsbHostDevice device)
    {
        List<uint> children;
        try
        {
            children = UsbHostAPI.GetInterfaceServices((uint)deviceService.ToInt32());
        }
        catch (Exception ex)
        {
            UsbTrace.Log($"UsbHostFinder: interface children unavailable: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        byte? fallbackClass = null;
        byte? fallbackSubClass = null;
        byte? fallbackProtocol = null;
        try
        {
            foreach (var child in children)
            {
                var childService = new IntPtr(child);
                var interfaceClass = ReadByteProperty(childService, "bInterfaceClass");
                var interfaceSubClass = ReadByteProperty(childService, "bInterfaceSubClass");
                var interfaceProtocol = ReadByteProperty(childService, "bInterfaceProtocol");
                if (interfaceClass is null)
                {
                    continue;
                }

                if (interfaceClass == 0xFF)
                {
                    device.InterfaceClass = interfaceClass;
                    device.InterfaceSubClass = interfaceSubClass;
                    device.InterfaceProtocol = interfaceProtocol;
                    device.InterfaceMetadataObserved = true;
                    return;
                }

                fallbackClass ??= interfaceClass;
                fallbackSubClass ??= interfaceSubClass;
                fallbackProtocol ??= interfaceProtocol;
            }
        }
        finally
        {
            foreach (var child in children)
            {
                IOKitUsbAPI.IOObjectRelease((IntPtr)child);
            }
        }

        if (fallbackClass is null)
        {
            return;
        }

        device.InterfaceClass = fallbackClass;
        device.InterfaceSubClass = fallbackSubClass;
        device.InterfaceProtocol = fallbackProtocol;
        device.InterfaceMetadataObserved = true;
    }

    /// <summary>
    /// Reads one byte-sized IORegistry property, or <see langword="null"/> when it is absent.
    /// <para>读取一个字节大小的 IORegistry 属性；缺失时为 <see langword="null"/>。</para>
    /// </summary>
    private static byte? ReadByteProperty(IntPtr service, string propertyName)
        => TryReadCFNumberRawU32(service, propertyName, out var raw) ? unchecked((byte)raw) : null;

    private static bool TryReadCFNumberRawU32(IntPtr service, string propertyName, out uint value)
    {
        value = 0;
        var key = IOKitUsbAPI.CFStringCreateWithCString(IntPtr.Zero, propertyName, IOKitUsbAPI.KCFStringEncodingUTF8);
        if (key == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var cfValue = IOKitUsbAPI.IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
            if (cfValue == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                switch (CFNumberGetType(cfValue))
                {
                    case 1:
                        ushort v16 = 0;
                        if (!CFNumberGetValueU16(cfValue, 1, ref v16))
                        {
                            return false;
                        }

                        value = v16;
                        return true;
                    case 3:
                        uint v32 = 0;
                        if (!CFNumberGetValueU32(cfValue, 3, ref v32))
                        {
                            return false;
                        }

                        value = v32;
                        return true;
                    case 4:
                        ulong v64 = 0;
                        if (!CFNumberGetValueU64(cfValue, 4, ref v64))
                        {
                            return false;
                        }

                        value = unchecked((uint)v64);
                        return true;
                    default:
                        uint fallback = 0;
                        if (!CFNumberGetValueU32(cfValue, 3, ref fallback))
                        {
                            return false;
                        }

                        value = fallback;
                        return true;
                }
            }
            finally
            {
                IOKitUsbAPI.CFRelease(cfValue);
            }
        }
        finally
        {
            IOKitUsbAPI.CFRelease(key);
        }
    }

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFNumberGetType")]
    private static partial int CFNumberGetType(IntPtr number);

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValueU16(IntPtr number, int theType, ref ushort valuePtr);

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValueU32(IntPtr number, int theType, ref uint valuePtr);

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValueU64(IntPtr number, int theType, ref ulong valuePtr);

    private static string? ReadCFString(IntPtr service, string propertyName)
    {
        var key = IOKitUsbAPI.CFStringCreateWithCString(IntPtr.Zero, propertyName, IOKitUsbAPI.KCFStringEncodingUTF8);
        if (key == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var cfValue = IOKitUsbAPI.IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
            if (cfValue == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var buffer = new byte[256];
                bool ok;
                unsafe
                {
                    fixed (byte* p = buffer)
                    {
                        ok = CFStringGetCString(cfValue, (IntPtr)p, buffer.Length, IOKitUsbAPI.KCFStringEncodingUTF8);
                    }
                }

                if (ok)
                {
                    var len = Array.IndexOf(buffer, (byte)0);
                    return len <= 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(buffer, 0, len);
                }

                return null;
            }
            finally
            {
                IOKitUsbAPI.CFRelease(cfValue);
            }
        }
        finally
        {
            IOKitUsbAPI.CFRelease(key);
        }
    }

    [LibraryImport(IOKitUsbAPI.CoreFoundation, EntryPoint = "CFStringGetCString")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(IntPtr theString, IntPtr buffer, int maxLength, int encoding);

    private static string? TryGetServicePath(IntPtr service)
    {
        try
        {
            var path = new byte[512];
            int result;
            unsafe
            {
                fixed (byte* p = path)
                {
                    result = IOKitUsbAPI.IORegistryEntryGetPath(service, IOKitUsbAPI.IOServicePlane, (IntPtr)p);
                }
            }

            if (result != IOKitUsbAPI.KIOReturnSuccess)
            {
                return null;
            }

            var len = Array.IndexOf(path, (byte)0);
            return len <= 0 ? null : System.Text.Encoding.UTF8.GetString(path, 0, len);
        }
        catch
        {
            return null;
        }
    }
}
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.MacOS;

/// <summary>
/// Enumerates USB devices on macOS via the IOKit.framework classic API
/// (<c>IOServiceMatching("IOUSBDevice")</c> + <c>IORegistryEntryCreateCFProperty</c>),
/// replacing the IOUSBHost.framework path that is unloadable on current macOS.
/// <para>在 macOS 上通过 IOKit.framework 经典 API
/// （<c>IOServiceMatching("IOUSBDevice")</c> + <c>IORegistryEntryCreateCFProperty</c>）
/// 枚举 USB 设备，替替当前 macOS 上不可加载的 IOUSBHost.framework 路径。</para>
/// </summary>
internal static partial class IOKitUsbFinder
{
    // Cached allocator handle (kCFAllocatorDefault = NULL) reused for every
    // IORegistryEntryCreateCFProperty / CFStringCreateWithCString call. Exposed
    // as a static readonly field (not const) because IntPtr.Zero is not a
    // compile-Time constant under all TFMs.
    // <para>缓存的分配器句柄（kCFAllocatorDefault = NULL），被每次
    // IORegistryEntryCreateCFProperty / CFStringCreateWithCString 调用复用。
    // 以静态只读字段（非 const）暴露，因为 IntPtr.Zero 在所有 TFM 下非编译期常量。</para>
    private static readonly IntPtr DefaultAllocator = IntPtr.Zero;

    // Registry entry IDs are stable across re-enumeration; used as the reopen key.
    // <para>注册表项 ID 跨再枚举稳定；用作重开键。</para>
    // private const string RegistryIdProperty = "RegistryEntryID";

    /// <summary>
    /// Whether the last IOServiceGetMatchingServices call succeeded. Surfaced
    /// through diagnostics like <c>copy-devices</c>.
    /// <para>最后一次 IOServiceGetMatchingServices 调用是否成功。通过
    /// <c>copy-devices</c> 等诊断暴露。</para>
    /// </summary>
    internal static bool LastCopyDevicesSucceeded { get; private set; } = true;

    /// <summary>
    /// Number of IOUSBDevice services observed on the last scan, before filtering.
    /// <para>上次扫描观测到的 IOUSBDevice 服务数，过滤前。</para>
    /// </summary>
    internal static int LastScannedDeviceCount { get; private set; }

    /// <summary>
    /// Number of devices that matched the filter on the last scan.
    /// <para>上次扫描匹配过滤器的设备数。</para>
    /// </summary>
    internal static int LastMatchedDeviceCount { get; private set; }

    /// <summary>
    /// Enumerates macOS IOUSBDevice services, reads each device's VID/PID/serial/
    /// interface metadata from the IORegistry, and returns the matching
    /// <see cref="IOKitUsbDevice"/> objects. When <paramref name="filter"/> is
    /// non-null, devices whose VID/PID/interface-class do not match are skipped.
    /// <para>枚举 macOS IOUSBDevice 服务，从 IORegistry 读取每个设备的 VID/PID/
    /// 序列号/接口元数据，并返回匹配的 <see cref="IOKitUsbDevice"/> 对象。
    /// <paramref name="filter"/> 非空时，VID/PID/接口类不匹配的设备被跳过。</para>
    /// </summary>
    public static List<UsbDevice> FindDevice(UsbDeviceFilter? filter = null)
    {
        List<UsbDevice> devices = [];
        LastScannedDeviceCount = 0;
        LastMatchedDeviceCount = 0;

        var interfaceTable = BuildInterfaceTable();

        var matching = IntPtr.Zero;
        var iterator = IntPtr.Zero;

        try
        {
            matching = IOKitUsbAPI.IOServiceMatching(IOKitUsbAPI.IOUSBDeviceClassName);
            UsbTrace.Log($"IOKitUsbFinder: IOServiceMatching(IOUSBDevice) = {matching}");
            if (matching == IntPtr.Zero)
            {
                LastCopyDevicesSucceeded = false;
                return devices;
            }

            var kr = IOKitUsbAPI.IOServiceGetMatchingServices(IntPtr.Zero, matching, out var iter);
            UsbTrace.Log($"IOKitUsbFinder: IOServiceGetMatchingServices kr={kr} iter={iter}");
            matching = IntPtr.Zero;
            if (kr != IOKitUsbAPI.KIOReturnSuccess || iter == IntPtr.Zero)
            {
                LastCopyDevicesSucceeded = false;
                return devices;
            }
            iterator = iter;
            LastCopyDevicesSucceeded = true;

            IntPtr service;
            var iterCount = 0;
            var next = IOKitUsbAPI.IOIteratorNext(iterator);
            UsbTrace.Log($"IOKitUsbFinder: first IOIteratorNext(iter) = {next}");
            while (next != 0)
            {
                service = new IntPtr(next);
                iterCount++;
                LastScannedDeviceCount++;
                UsbTrace.Log($"IOKitUsbFinder: processing service #{iterCount} = {service}");
                try
                {
                    if (!TryReadDeviceMetadata(
                            service,
                            interfaceTable,
                            out var vid,
                            out var pid,
                            out var serial,
                            out var registryEntryId,
                            out var interfaces,
                            out var ifcClass,
                            out var ifcSubClass,
                            out var ifcProtocol))
                    {
                        UsbTrace.Log($"IOKitUsbFinder: TryReadDeviceMetadata FAILED for service, skipping.");
                        continue;
                    }

                    UsbTrace.Log($"IOKitUsbFinder: dev rid={registryEntryId} vid={vid:X4} pid={pid:X4} serial='{serial}' ifClass={ifcClass}");

                    if (filter?.VendorId is ushort filterVid && vid != filterVid)
                    {
                        continue;
                    }

                    if (filter?.ProductId is ushort filterPid && pid != filterPid)
                    {
                        continue;
                    }

                    if (filter?.InterfaceClass is byte filterClass && ifcClass != filterClass)
                    {
                        continue;
                    }

                    if (filter?.InterfaceSubClass is byte filterSubClass && ifcSubClass != filterSubClass)
                    {
                        continue;
                    }

                    if (filter?.InterfaceProtocol is byte filterProtocol && ifcProtocol != filterProtocol)
                    {
                        continue;
                    }

                    var dev = new IOKitUsbDevice
                    {
                        RegistryEntryId = registryEntryId,
                        DevicePath = TryGetServicePath(service) ?? $"IOKit:{registryEntryId}",
                        VendorId = vid,
                        ProductId = pid,
                        SerialNumber = serial,
                        InterfaceClass = ifcClass,
                        InterfaceSubClass = ifcSubClass,
                        InterfaceProtocol = ifcProtocol,
                        InterfaceMetadataObserved = interfaces.Count > 0,
                        Interfaces = interfaces,
                        UsbDeviceType = UsbDeviceType.MacOS,
                    };

                    devices.Add(dev);
                }
                finally
                {
                    next = IOKitUsbAPI.IOIteratorNext(iterator);
                }
            }
            UsbTrace.Log($"IOKitUsbFinder: scan loop exhausted, iterCount={iterCount}");
        }
        catch (DllNotFoundException ex)
        {
            UsbTrace.Log($"IOKitUsbFinder: DllNotFoundException caught: {ex.Message}");
            LastCopyDevicesSucceeded = false;
            return devices;
        }
        catch (EntryPointNotFoundException ex)
        {
            UsbTrace.Log($"IOKitUsbFinder: EntryPointNotFoundException caught: {ex.Message}");
            LastCopyDevicesSucceeded = false;
            return devices;
        }
        catch (Exception ex)
        {
            UsbTrace.Log($"IOKitUsbFinder: unexpected exception in scan loop: {ex.GetType().Name}: {ex.Message}");
            LastCopyDevicesSucceeded = false;
            return devices;
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

        LastMatchedDeviceCount = devices.Count;
        return devices;
    }

    // Reads VID/PID/serial/RegistryEntryID/interface descriptors for one IOUSBDevice
    // service. Returns false when the service is no longer valid. All CF objects
    // returned by IORegistryEntryCreateCFProperty are +1 and released here.
    // <para>读取一个 IOUSBDevice 服务的 VID/PID/序列号/RegistryEntryID/接口描述符。
    // 服务不再有效时返回 false。IORegistryEntryCreateCFProperty 返回的所有 CF 对象
    // 为 +1，在此释放。</para>
    private static bool TryReadDeviceMetadata(
        IntPtr service,
        Dictionary<(ushort Vid, ushort Pid), List<UsbInterfaceInfo>> interfaceTable,
        out ushort vid,
        out ushort pid,
        out string? serial,
        out ulong registryEntryId,
        out IReadOnlyList<UsbInterfaceInfo> interfaces,
        out byte? ifcClass,
        out byte? ifcSubClass,
        out byte? ifcProtocol)
    {
        vid = 0;
        pid = 0;
        serial = null;
        registryEntryId = 0;
        interfaces = [];
        ifcClass = null;
        ifcSubClass = null;
        ifcProtocol = null;

        UsbTrace.Log($"IOKitUsbFinder: TryReadDeviceMetadata entry, service={service}");
        registryEntryId = (uint)service.ToInt64();

        vid = ReadCFNumberUshort(service, "IdVendor");
        pid = ReadCFNumberUshort(service, "IdProduct");

        serial = ReadCFString(service, "USB Serial Number") ?? ReadCFString(service, "serial number");

        if (interfaceTable.TryGetValue((vid, pid), out var pairedInterfaces) && pairedInterfaces.Count > 0)
        {
            interfaces = pairedInterfaces;
            var first = pairedInterfaces[0];
            ifcClass = first.Class;
            ifcSubClass = first.SubClass;
            ifcProtocol = first.Protocol;
        }

        return true;
    }

    // Reads a CFNumber-valued registry property as a ushort. Returns 0 when the
    // property is absent or not a CFNumber.
    // <para>读取 CFNumber 值的注册表属性为 ushort。属性不存在或非 CFNumber 时返回 0。</para>
    private static ushort ReadCFNumberUshort(IntPtr service, string propertyName)
        => TryReadCFNumberRawU32(service, propertyName, out var raw)
            ? unchecked((ushort)raw)
            : (ushort)0;

    // Reads the CFNumber as a uint32 by first querying its actual storage type
    // via CFNumberGetType and dispatching to the matching CFNumberGetValue
    // accessor (kCFNumberSInt16Type=1, kCFNumberSInt32Type=3, kCFNumberSInt64Type=4).
    // CFNumberGetValue REQUIRES the type argument to match the CFNumber's actual
    // storage type — passing kCFNumberShortType=5 to a kCFNumberSInt32Type=3
    // CFNumber returns the low 16 bits with garbage in the high byte (verified:
    // IdVendor=0x18D1 read as 0x8000 when wrong type).
    // <para>先经 CFNumberGetType 咨询真实存储类型，再派发到匹配的 CFNumberGetValue
    // 访问器（kCFNumberSInt16Type=1、kCFNumberSInt32Type=3、kCFNumberSInt64Type=4）以
    // uint32 读取 CFNumber。CFNumberGetValue 要求类型参数匹配 CFNumber 的实际存储类型
    // ——对 kCFNumberSInt32Type=3 的 CFNumber 传 kCFNumberShortType=5 会返回低位 16 比特
    // 且高位字节为垃圾（已验证：IdVendor=0x18D1 在错误类型下读为 0x8000）。</para>
    private static bool TryReadCFNumberRawU32(IntPtr service, string propertyName, out uint value)
    {
        value = 0;
        var key = IOKitUsbAPI.CFStringCreateWithCString(DefaultAllocator, propertyName, IOKitUsbAPI.KCFStringEncodingUTF8);
        if (key == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var cfValue = IOKitUsbAPI.IORegistryEntryCreateCFProperty(service, key, DefaultAllocator, 0);
            if (cfValue == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var cfType = CFNumberGetType(cfValue);
                switch (cfType)
                {
                    case 1: // kCFNumberSInt16Type
                        ushort v16 = 0;
                        if (!CFNumberGetValueU16(cfValue, 1, ref v16))
                        {
                            return false;
                        }

                        value = v16;
                        return true;
                    case 3: // kCFNumberSInt32Type
                        uint v32 = 0;
                        if (!CFNumberGetValueU32(cfValue, 3, ref v32))
                        {
                            return false;
                        }

                        value = v32;
                        return true;
                    case 4: // kCFNumberSInt64Type
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

    // Reads a CFString-valued registry property as a UTF-8 string. Returns null
    // when the property is absent or not a CFString.
    // <para>读取 CFString 值的注册表属性为 UTF-8 字符串。属性不存在或非 CFString 时返回 null。</para>
    private static string? ReadCFString(IntPtr service, string propertyName)
    {
        var key = IOKitUsbAPI.CFStringCreateWithCString(DefaultAllocator, propertyName, IOKitUsbAPI.KCFStringEncodingUTF8);
        if (key == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var cfValue = IOKitUsbAPI.IORegistryEntryCreateCFProperty(service, key, DefaultAllocator, 0);
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

    // Builds a (vid,pid) -> interfaces[] table by enumerating the IOUSBInterface
    // plane ONCE. IOUSBInterface services inherit IdVendor/IdProduct from their
    // parent IOUSBDevice (verified by probe), so interfaces pair to devices by
    // VID/PID without IORegistryEntryGetParent (not exported on current macOS) or
    // IORegistryEntryGetRegistryEntryID (segfaults against IOUSBDevice). Class/
    // subclass/protocol are read via CFNumberGetType dispatch.
    // <para>枚举 IOUSBInterface 平面一次，构建 (vid,pid) -> interfaces[] 表。
    // IOUSBInterface 服务从父 IOUSBDevice 继承 IdVendor/IdProduct（已由探针验证），
    // 故可按 VID/PID 将接口配对到设备，无需 IORegistryEntryGetParent（当前 macOS
    // 不导出）或 IORegistryEntryGetRegistryEntryID（对 IOUSBDevice 段错误）。类/
    // 子类/协议经 CFNumberGetType 派发读取。</para>
    private static Dictionary<(ushort Vid, ushort Pid), List<UsbInterfaceInfo>> BuildInterfaceTable()
    {
        var table = new Dictionary<(ushort Vid, ushort Pid), List<UsbInterfaceInfo>>();
        UsbTrace.Log("IOKitUsbFinder: BuildInterfaceTable entry");
        var ifaceCount = 0;

        var matching = IntPtr.Zero;
        var iterator = IntPtr.Zero;

        try
        {
            matching = IOKitUsbAPI.IOServiceMatching("IOUSBInterface");
            UsbTrace.Log($"IOKitUsbFinder: BuildInterfaceTable IOServiceMatching(IOUSBInterface) = {matching}");
            if (matching == IntPtr.Zero)
            {
                return table;
            }

            var kr = IOKitUsbAPI.IOServiceGetMatchingServices(IntPtr.Zero, matching, out var iter);
            UsbTrace.Log($"IOKitUsbFinder: BuildInterfaceTable GetMatchingServices kr={kr} iter={iter}");
            matching = IntPtr.Zero;
            if (kr != IOKitUsbAPI.KIOReturnSuccess || iter == IntPtr.Zero)
            {
                return table;
            }
            iterator = iter;

            var next = IOKitUsbAPI.IOIteratorNext(iterator);
            while (next != 0)
            {
                ifaceCount++;
                var ifaceService = new IntPtr(next);
                try
                {
                    var vid = ReadCFNumberUshort(ifaceService, "IdVendor");
                    var pid = ReadCFNumberUshort(ifaceService, "IdProduct");
                    if (vid == 0 || pid == 0)
                    {
                        continue;
                    }

                    var info = new UsbInterfaceInfo
                    {
                        Class = ReadCFNumberByte(ifaceService, "BInterfaceClass") ?? 0,
                        SubClass = ReadCFNumberByte(ifaceService, "BInterfaceSubClass") ?? 0,
                        Protocol = ReadCFNumberByte(ifaceService, "BInterfaceProtocol") ?? 0,
                    };

                    var key = (vid, pid);
                    if (!table.TryGetValue(key, out var list))
                    {
                        list = [];
                        table[key] = list;
                    }
                    list.Add(info);
                }
                finally
                {
                }

                next = IOKitUsbAPI.IOIteratorNext(iterator);
            }
        }
        catch
        {
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

        UsbTrace.Log($"IOKitUsbFinder: BuildInterfaceTable done, scanned {ifaceCount} IOUSBInterface services, table has {table.Count} (vid,pid) keys");
        return table;
    }

    private static byte? ReadCFNumberByte(IntPtr service, string propertyName)
        => TryReadCFNumberRawU32(service, propertyName, out var raw)
            ? unchecked((byte)raw)
            : null;

    // Reads the IOService-plane path of a device service via
    // IORegistryEntryGetPath. This is the path IOKitUsbDevice.CreateHandle feeds
    // to IORegistryEntryFromPath to reopen the device by identity. Returns null
    // when the path cannot be read — the finder then falls back to a synthetic
    // "IOKit:{rid}" path so enumeration still reports the device, but that
    // synthetic path is NOT a valid IOService plane path: IORegistryEntryFromPath
    // cannot reopen it, so such devices are list-only (session open returns -1).
    // <para>经 IORegistryEntryGetPath 读取设备服务的 IOService 平面路径。这正是
    // IOKitUsbDevice.CreateHandle 传给 IORegistryEntryFromPath 以按标识重开设备的
    // 路径。路径不可读时返回 null——finder 随后回退到合成的 "IOKit:{rid}" 路径，
    // 使枚举仍能报告设备；但合成路径不是有效的 IOService 平面路径，
    // IORegistryEntryFromPath 无法用它重开，因此此类设备只能枚举、无法打开会话
    // （CreateHandle 返回 -1）。</para>
    private static string? TryGetServicePath(IntPtr service)
    {
        try
        {
            var path = new byte[512]; // io_string_t is 512 chars
            int pathResult;
            unsafe
            {
                fixed (byte* p = path)
                {
                    pathResult = IOKitUsbAPI.IORegistryEntryGetPath(service, IOKitUsbAPI.IOServicePlane, (IntPtr)p);
                }
            }
            if (pathResult != IOKitUsbAPI.KIOReturnSuccess)
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
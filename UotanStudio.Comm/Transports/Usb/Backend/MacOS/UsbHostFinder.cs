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
/// that table and can hang in the same situation).
/// <para>IOUSBHost 后端的 macOS USB 设备枚举器。发现经 IORegistry 针对 IOUSBDevice
/// 服务进行，仅做设备级标量属性读取（IdVendor/IdProduct/序列号/服务路径）加子服务
/// 枚举——刻意不构建全局 IOUSBInterface 表：迭代全部接口服务并读取其属性时，若某
/// 设备处于异常状态，IORegistry MIG 调用会无限阻塞（已在本机经 stash 对照 A/B 实验
/// 证实；IOKit 经典查找器仍使用该表，同样情形下会挂起）。</para>
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
                    devices.Add(device);
                    UsbTrace.Log($"UsbHostFinder: dev vid={vid:X4} pid={pid:X4} serial='{serial}' path='{path}'");
                }
                catch (Exception ex)
                {
                    UsbTrace.Log($"UsbHostFinder: device read failed: {ex.GetType().Name}: {ex.Message}");
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

        LastMatchedDeviceCount = devices.Count;
        UsbTrace.Log($"UsbHostFinder: done, scanned {scanned}, matched {devices.Count}");
        return devices;
    }

    private static ushort ReadCFNumberUshort(IntPtr service, string propertyName)
        => TryReadCFNumberRawU32(service, propertyName, out var raw)
            ? unchecked((ushort)raw)
            : (ushort)0;

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
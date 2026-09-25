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
    private static readonly IntPtr DefaultAllocator = IntPtr.Zero;

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

    private static ushort ReadCFNumberUshort(IntPtr service, string propertyName)
        => TryReadCFNumberRawU32(service, propertyName, out var raw)
            ? unchecked((ushort)raw)
            : (ushort)0;

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

    private static string? TryGetServicePath(IntPtr service)
    {
        try
        {
            var path = new byte[512];
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
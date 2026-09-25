using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal static partial class WinUSBFinder
{
    private static string GetDeviceInstanceId(IntPtr devInfoSet, ref Win32API.SpDevInfoData devInfoData)
    {
        Win32API.SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, IntPtr.Zero, 0, out var required);
        if (required == 0)
        {
            return string.Empty;
        }

        var buffer = new char[required];
        unsafe
        {
            fixed (char* p = buffer)
            {
                return Win32API.SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, (IntPtr)p, (uint)buffer.Length, out required)
                    ? new string(buffer, 0, (int)required - 1)
                    : string.Empty;
            }
        }
    }

    /// <summary>
    /// Reads the DeviceInterfaceGUIDs (REG_MULTI_SZ) registered for a device node under
    /// HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;\Device Parameters, falling back
    /// to the DeviceClasses reverse map when the node-level value is absent (winusb-driven
    /// interfaces such as ADB often register their interface GUID only there).
    /// <para>读取设备节点在 HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;\Device Parameters
    /// 下注册的 DeviceInterfaceGUIDs（REG_MULTI_SZ）；节点级值缺失时回退到 DeviceClasses
    /// 反向映射（winusb 驱动的接口如 ADB 通常只在那里注册接口 GUID）。</para>
    /// </summary>
    private static IEnumerable<Guid> ReadDeviceInterfaceGuids(
        string instanceId,
        Func<Dictionary<string, List<Guid>>> deviceClassGuidsFactory)
    {
        var result = new List<Guid>();
        var subKey = @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters";
        if (Win32API.RegOpenKeyExW(Win32API.HKEY_LOCAL_MACHINE, subKey, 0, Win32API.KEY_READ, out var key) != 0)
        {
            return FallbackToDeviceClassGuids(result, instanceId, deviceClassGuidsFactory);
        }

        try
        {
            uint size = 0;
            if (Win32API.RegQueryValueExW(key, "DeviceInterfaceGUIDs", IntPtr.Zero, out var type, IntPtr.Zero, ref size) != 0 ||
                size == 0)
            {
                return FallbackToDeviceClassGuids(result, instanceId, deviceClassGuidsFactory);
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Win32API.RegQueryValueExW(key, "DeviceInterfaceGUIDs", IntPtr.Zero, out type, buffer, ref size) == 0)
                {
                    var multi = Marshal.PtrToStringUni(buffer, (int)size / 2) ?? string.Empty;
                    foreach (var part in multi.Split('\0'))
                    {
                        var trimmed = part.Trim();
                        if (Guid.TryParse(trimmed.Trim('{', '}'), out var guid))
                        {
                            result.Add(guid);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Win32API.RegCloseKey(key);
        }

        return result.Count == 0 ? FallbackToDeviceClassGuids(result, instanceId, deviceClassGuidsFactory) : (IEnumerable<Guid>)result;
    }

    /// <summary>
    /// Builds a reverse map from device instance id (lowercase, '\' separated) to the
    /// interface GUIDs registered for it under HKLM\SYSTEM\CurrentControlSet\Control\
    /// DeviceClasses. WinUSB drivers register the device interface class GUID in the
    /// INF; the interface symbol links then live under the GUID key here.
    /// <para>构建从设备实例 ID（小写、'\\' 分隔）到其在 HKLM\SYSTEM\CurrentControlSet\
    /// Control\DeviceClasses 下注册的接口 GUID 的反向映射。WinUSB 驱动在 INF 中注册
    /// 设备接口类 GUID，接口符号链接随后位于此处的 GUID 键下。</para>
    /// </summary>
    private static Dictionary<string, List<Guid>> BuildDeviceClassGuidMap()
    {
        var map = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        const string classesSubKey = @"SYSTEM\CurrentControlSet\Control\DeviceClasses";
        if (Win32API.RegOpenKeyExW(Win32API.HKEY_LOCAL_MACHINE, classesSubKey, 0, Win32API.KEY_READ, out var classesKey) != 0)
        {
            return map;
        }

        try
        {
            uint guidIndex = 0;
            while (true)
            {
                var guidName = new char[64];
                var guidNameCapacity = (uint)guidName.Length;
                int guidEnumResult;
                unsafe
                {
                    fixed (char* p = guidName)
                    {
                        guidEnumResult = Win32API.RegEnumKeyExW(
                            classesKey, guidIndex++, (IntPtr)p, ref guidNameCapacity,
                            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                if (guidEnumResult != 0)
                {
                    break;
                }

                var guidNameStr = new string(guidName, 0, (int)guidNameCapacity);
                if (!Guid.TryParse(guidNameStr.Trim('{', '}'), out var guid))
                {
                    continue;
                }

                if (Win32API.RegOpenKeyExW(Win32API.HKEY_LOCAL_MACHINE, classesSubKey + @"\" + guidNameStr, 0, Win32API.KEY_READ, out var guidKey) != 0)
                {
                    continue;
                }

                try
                {
                    uint linkIndex = 0;
                    while (true)
                    {
                        var linkName = new char[512];
                        var linkNameCapacity = (uint)linkName.Length;
                        int linkEnumResult;
                        unsafe
                        {
                            fixed (char* p = linkName)
                            {
                                linkEnumResult = Win32API.RegEnumKeyExW(
                                    guidKey, linkIndex++, (IntPtr)p, ref linkNameCapacity,
                                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            }
                        }
                        if (linkEnumResult != 0)
                        {
                            break;
                        }

                        var link = new string(linkName, 0, (int)linkNameCapacity);
                        var hashHashQuestion = link.IndexOf("?#", StringComparison.Ordinal);
                        if (hashHashQuestion < 0)
                        {
                            continue;
                        }

                        var instancePath = link[(hashHashQuestion + 2)..];
                        var guidBrace = instancePath.IndexOf("#{", StringComparison.Ordinal);
                        if (guidBrace >= 0)
                        {
                            instancePath = instancePath[..guidBrace];
                        }

                        if (instancePath.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        var instanceId = instancePath.Replace('#', '\\').ToLowerInvariant();
                        if (!map.TryGetValue(instanceId, out var guids))
                        {
                            guids = [];
                            map[instanceId] = guids;
                        }

                        if (!guids.Contains(guid))
                        {
                            guids.Add(guid);
                        }
                    }
                }
                finally
                {
                    Win32API.RegCloseKey(guidKey);
                }
            }
        }
        finally
        {
            Win32API.RegCloseKey(classesKey);
        }

        return map;
    }

    private static IReadOnlyList<Guid> FallbackToDeviceClassGuids(
        List<Guid> result,
        string instanceId,
        Func<Dictionary<string, List<Guid>>> deviceClassGuidsFactory)
    {
        IReadOnlyDictionary<string, List<Guid>> deviceClassGuids = deviceClassGuidsFactory();
        if (deviceClassGuids.TryGetValue(instanceId.ToLowerInvariant(), out var fallback))
        {
            foreach (var guid in fallback)
            {
                if (!result.Contains(guid))
                {
                    result.Add(guid);
                }
            }

            UsbTrace.Log($"WinUSBFinder: fell back to DeviceClasses GUIDs for [{instanceId}]: {string.Join(", ", fallback)}");
        }

        return result;
    }

    private static Guid? ReadContainerId(string instanceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(instanceId)) return null;
        try
        {
            using var node = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            return node?.GetValue("ContainerID") is string value &&
                Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static bool PathMatchesFilter(string path, UsbDeviceFilter? filter)
    {
        if (filter == null)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(filter.DevicePathEquals) &&
            !string.Equals(path, filter.DevicePathEquals, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(filter.DevicePathContains) &&
            path.IndexOf(filter.DevicePathContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (!filter.VendorId.HasValue && !filter.ProductId.HasValue)
        {
            return true;
        }

        (var vid, var pid) = TryParseVidPid(path);

        return (!filter.VendorId.HasValue || (vid.HasValue && vid.Value == filter.VendorId.Value)) && (!filter.ProductId.HasValue || (pid.HasValue && pid.Value == filter.ProductId.Value));
    }

    private static (ushort? vid, ushort? pid) TryParseVidPid(string path)
    {
        var vidMatch = Regex.Match(path, @"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
        var pidMatch = Regex.Match(path, @"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);

        ushort? vid = null;
        ushort? pid = null;

        if (vidMatch.Success && ushort.TryParse(vidMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var parsedVid))
        {
            vid = parsedVid;
        }

        if (pidMatch.Success && ushort.TryParse(pidMatch.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var parsedPid))
        {
            pid = parsedPid;
        }

        return (vid, pid);
    }

    /// <summary>
    /// Creates a metadata-only WinUSB device (no open handle) so a busy device stays
    /// visible in enumeration with the VID/PID parsed from its interface path. Interface
    /// class/subclass/protocol are recovered from the device node's CompatibleIDs (e.g.
    /// <c>USB\COMPAT_VID_12d1&amp;Class_ff&amp;SubClass_42&amp;Prot_01</c>) so an interface
    /// filter (ADB FF/42/01) still matches the metadata-only device.
    /// <para>创建仅元数据的 WinUSB 设备（无打开句柄），使被占用设备以从接口路径解析的
    /// VID/PID 保持在枚举中可见。接口类/子类/协议从设备节点的 CompatibleIDs 恢复
    /// （例如 <c>USB\COMPAT_VID_12d1&amp;Class_ff&amp;SubClass_42&amp;Prot_01</c>），
    /// 使接口过滤器（ADB FF/42/01）仍能匹配该仅元数据设备。</para>
    /// </summary>
    private static WinUSBDevice CreateMetadataOnlyDevice(string path, ushort vid, ushort pid)
    {
        var dev = new WinUSBDevice
        {
            DevicePath = path,
            VendorId = vid,
            ProductId = pid,
            InterfaceMetadataObserved = false,
            Interfaces = []
        };

        if (TryReadCompatibleIdInterface(path, out var ifClass, out var ifSubClass, out var ifProtocol))
        {
            dev.InterfaceClass = ifClass;
            dev.InterfaceSubClass = ifSubClass;
            dev.InterfaceProtocol = ifProtocol;
            dev.InterfaceMetadataObserved = true;
            dev.Interfaces =
            [
                new UsbInterfaceInfo
                {
                    InterfaceNumber = 0,
                    Class = ifClass,
                    SubClass = ifSubClass,
                    Protocol = ifProtocol,
                    Endpoints = []
                }
            ];
        }

        return dev;
    }

    /// <summary>
    /// Recovers the interface class/subclass/protocol of a winusb interface from its
    /// device node's CompatibleIDs (REG_MULTI_SZ) when the interface cannot be opened to
    /// read the configuration descriptor directly. CompatibleIDs entries carry the
    /// standard ADB triple as <c>Class_ff&amp;SubClass_42&amp;Prot_01</c>.
    /// <para>当接口无法打开以直接读取配置描述符时，从设备节点的 CompatibleIDs
    /// （REG_MULTI_SZ）恢复 winusb 接口的类/子类/协议。CompatibleIDs 条目以
    /// <c>Class_ff&amp;SubClass_42&amp;Prot_01</c> 形式携带标准 ADB 三元组。</para>
    /// </summary>
    private static bool TryReadCompatibleIdInterface(string path, out byte interfaceClass, out byte interfaceSubClass, out byte interfaceProtocol)
    {
        interfaceClass = 0;
        interfaceSubClass = 0;
        interfaceProtocol = 0;

        const string prefix = @"\\?\usb#";
        var rest = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
        var guidBrace = rest.IndexOf("#{", StringComparison.Ordinal);
        if (guidBrace >= 0)
        {
            rest = rest[..guidBrace];
        }

        if (string.IsNullOrWhiteSpace(rest) || rest.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        var instanceId = @"USB\" + rest.Replace('#', '\\');
        var subKey = @"SYSTEM\CurrentControlSet\Enum\" + instanceId;
        if (Win32API.RegOpenKeyExW(Win32API.HKEY_LOCAL_MACHINE, subKey, 0, Win32API.KEY_READ, out var key) != 0)
        {
            return false;
        }

        try
        {
            uint size = 0;
            if (Win32API.RegQueryValueExW(key, "CompatibleIDs", IntPtr.Zero, out var type, IntPtr.Zero, ref size) != 0 ||
                size == 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Win32API.RegQueryValueExW(key, "CompatibleIDs", IntPtr.Zero, out type, buffer, ref size) != 0)
                {
                    return false;
                }

                var multi = Marshal.PtrToStringUni(buffer, (int)size / 2) ?? string.Empty;
                foreach (var part in multi.Split('\0'))
                {
                    var match = Regex.Match(
                        part,
                        @"Class_([0-9a-fA-F]{2})&SubClass_([0-9a-fA-F]{2})&Prot_([0-9a-fA-F]{2})",
                        RegexOptions.IgnoreCase);
                    if (match.Success &&
                        byte.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var c) &&
                        byte.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out var s) &&
                        byte.TryParse(match.Groups[3].Value, System.Globalization.NumberStyles.HexNumber, null, out var p))
                    {
                        interfaceClass = c;
                        interfaceSubClass = s;
                        interfaceProtocol = p;
                        return true;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Win32API.RegCloseKey(key);
        }

        return false;
    }

    private static string BuildDeviceKey(UsbDevice device)
    {
        var interfaceTag = string.Empty;
        var miMatch = Regex.Match(device.DevicePath, @"mi_([0-9a-fA-F]{2})", RegexOptions.IgnoreCase);
        if (miMatch.Success)
        {
            interfaceTag = $"|mi:{miMatch.Groups[1].Value.ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            return $"serial:{device.SerialNumber}{interfaceTag}";
        }

        return $"path:{device.DevicePath}|vid:{device.VendorId:X4}|pid:{device.ProductId:X4}|type:{device.GetType().Name}";
    }

    /// <summary>
    /// <para>为仅元数据的枚举结果填充序列号，语义对齐 Google adb 的 Windows 实现
    /// （AdbWinApi 的 <c>GetSerialNumber</c> 取设备实例 ID 最后一段）：实例 ID 后缀是第一
    /// 证据——Windows 在首次插入读到 iSerial 时会把它编入实例路径；当后缀是位置式字符串
    /// （Windows 未能缓存序列号）时，对厂商自定义（0xFF）调试接口做一次有界、不认领接口的
    /// 描述符窥视，从 USB 字符串描述符读取真实 iSerial。窥视不打开管道、不认领接口，
    /// server 或其他进程占用接口时同样安全。</para>
    /// Fills the enumerated serial for a metadata-only device, aligned with Google adb's Windows
    /// implementation (AdbWinApi's <c>GetSerialNumber</c> takes the last segment of the device
    /// instance ID): the instance-ID suffix is the first evidence — Windows embeds the iSerial
    /// into the instance path when it read one at first plug; when the suffix is a location
    /// string (Windows never cached a serial), a bounded, non-claiming descriptor peek reads the
    /// real iSerial from the USB string descriptor for vendor-specific (0xFF) debug interfaces.
    /// The peek opens no pipes and never claims the interface, so it stays safe while a server
    /// or another process owns the interface.
    /// </summary>
    internal static WinUSBDevice AttachEnumeratedSerial(WinUSBDevice device, string? instanceSerial)
    {
        if (!string.IsNullOrWhiteSpace(instanceSerial))
        {
            device.SerialNumber = instanceSerial;
            return device;
        }

        if (device.InterfaceMetadataObserved && device.InterfaceClass == 0xff &&
            string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            device.SerialNumber = PeekSerialNumber(device.DevicePath);
        }

        return device;
    }

    /// <summary>
    /// <para>短暂打开 WinUSB 句柄读取设备描述符与 iSerial 字符串描述符；不查询管道、
    /// 不设置策略、不认领接口，句柄随即关闭。读取失败一律返回 <c>null</c>。</para>
    /// Briefly opens a WinUSB handle to read the device descriptor and the iSerial string
    /// descriptor; no pipe queries, no policies, no interface claim, and the handle is closed
    /// immediately. Every read failure yields <c>null</c>.
    /// </summary>
    private static string? PeekSerialNumber(string path)
    {
        // WinUsb_Initialize issues overlapped internal I/O and fails with ERROR_INVALID_HANDLE
        // on a non-overlapped handle, so the peek must open with FILE_FLAG_OVERLAPPED.
        // WinUsb_Initialize 的内部 I/O 依赖重叠句柄，非重叠句柄会以 ERROR_INVALID_HANDLE 失败，
        // 因此窥视必须以 FILE_FLAG_OVERLAPPED 打开。
        var hUsb = Win32API.SimpleCreateHandle(path, true);
        if (hUsb == Win32API.INVALID_HANDLE_VALUE)
        {
            return null;
        }

        try
        {
            if (!WinUSBAPI.WinUsb_Initialize(hUsb, out var winUsbHandle))
            {
                return null;
            }

            using var safeHandle = new SafeWinUsbHandle(winUsbHandle);
            var ptr = Marshal.AllocHGlobal(18);
            try
            {
                if (!WinUSBAPI.WinUsb_GetDescriptor(safeHandle.DangerousGetHandle(), WinUSBAPI.USB_DEVICE_DESCRIPTOR_TYPE,
                    0, 0, ptr, 18, out _))
                {
                    return null;
                }

                var iSerial = Marshal.PtrToStructure<Win32API.USBDeviceDescriptor>(ptr).ISerialNumber;
                if (iSerial == 0)
                {
                    return null;
                }

                if (!WinUSBAPI.WinUsb_GetDescriptor(safeHandle.DangerousGetHandle(), WinUSBAPI.USB_STRING_DESCRIPTOR_TYPE,
                    iSerial, 0x0409, ptr, 254, out var transferred) || transferred <= 2)
                {
                    return null;
                }

                return Marshal.PtrToStringUni(ptr + 2, (int)(transferred - 2) / 2)?.TrimEnd('\0');
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        finally
        {
            Win32API.CloseHandle(hUsb);
        }
    }

}
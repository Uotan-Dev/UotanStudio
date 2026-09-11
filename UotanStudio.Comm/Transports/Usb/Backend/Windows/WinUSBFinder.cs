using UotanStudio.Comm.Protocols.Common;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal static class WinUSBFinder
{
    // Heuristic: prefer the WinUSB path for Google ADB-Style devices (VID_18D1).
    // Kept as a named constant so it can be lifted into configuration later.
    private const string PreferWinUsbVidPid = "vid_18d1&pid_d00d";

    /// <summary>
    /// Gets whether the SetupDi all-classes enumeration handle opened successfully during
    /// the last enumeration. Lets upper layers distinguish "SetupDi failed" from
    /// "SetupDi ran but found no devices" on device-less CI.
    /// <para>获取上次枚举中 SetupDi 全类枚举句柄是否成功打开。让上层区分
    /// "SetupDi 失败"与"SetupDi 运行但未发现设备"（无设备 CI 场景）。</para>
    /// </summary>
    public static bool LastSetupDiSucceeded { get; private set; }

    /// <summary>
    /// Gets the number of USB device nodes (instance ids containing VID_) walked during
    /// the last enumeration. A non-zero value proves the scan loop actually ran.
    /// <para>获取上次枚举中遍历的 USB 设备节点数（含 VID_ 的实例 ID）。
    /// 非零值证明扫描循环确实执行过。</para>
    /// </summary>
    public static int LastScannedNodeCount { get; private set; }

    /// <summary>
    /// Gets the number of devices matched during the last enumeration.
    /// <para>获取上次枚举中匹配到的设备数。</para>
    /// </summary>
    public static int LastMatchedDeviceCount { get; private set; }

    /// <summary>
    /// Enumerates WinUSB/Legacy devices, optionally applying the filter and session options.
    /// <para>枚举 WinUSB/Legacy 设备，可选地应用过滤器与会话选项。</para>
    /// Session options (timeouts, RAW_IO) are assigned to each newly created
    /// <see cref="WinUSBDevice"/> BEFORE its handle is created, so the pipe policies
    /// take effect for the session.
    /// <para>会话选项（超时、RAW_IO）会在句柄创建前赋给每个新建的
    /// <see cref="WinUSBDevice"/>，使管道策略对会话生效。</para>
    /// </summary>
    /// <param name="filter">Optional device filter. <para>可选设备过滤器。</para></param>
    /// <param name="options">Optional session tuning options. <para>可选的会话调优选项。</para></param>
    /// <returns>The discovered backend devices. <para>发现的后端设备。</para></returns>
    public static List<UsbDevice> FindDevice(UsbDeviceFilter? filter = null, UsbSessionOptions? options = null)
    {
        var devices = new List<UsbDevice>();
        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LastScannedNodeCount = 0;
        LastMatchedDeviceCount = 0;

        // Enumerate ALL device nodes (no interface-GUID whitelist), then for each USB
        // node read its registered DeviceInterfaceGUIDs from the registry and enumerate
        // the interfaces under those GUIDs. This discovers any driver (WinUSB/Zadig/
        // libusb-win32/vendor) without a hardcoded GUID list.
        var devInfoSet = Win32API.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero,
            Win32API.DIGCF_PRESENT | Win32API.DIGCF_ALLCLASSES);
        LastSetupDiSucceeded = devInfoSet != -1;
        if (devInfoSet == -1)
        {
            UsbTrace.Log($"WinUSBFinder: SetupDiGetClassDevsW(all classes) failed err={Marshal.GetLastWin32Error()}");
            return devices;
        }

        // Some winusb-driven interfaces (e.g. standard ADB interfaces on winusb.sys)
        // do not write a DeviceInterfaceGUIDs value under their node's Device Parameters
        // even though the interface registration exists under
        // HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses. Build a reverse map
        // (device instance -> interface GUIDs) LAZILY, only when a node actually lacks
        // the node-level value: most devices carry their GUIDs directly, so the full
        // DeviceClasses registry walk is skipped in the common case.
        // <para>部分 winusb 驱动的接口（如 winusb.sys 上的标准 ADB 接口）不会在节点
        // Device Parameters 下写入 DeviceInterfaceGUIDs 值，但接口注册实际存在于
        // HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses。反向映射（设备实例 ->
        // 接口 GUID）按需惰性构建——仅当节点确实缺少节点级值时构建；大多数设备自带
        // GUID，常见情况下可完全跳过 DeviceClasses 注册表遍历。</para>
        Func<Dictionary<string, List<Guid>>> lazyDeviceClassGuids = BuildDeviceClassGuidMap;

        try
        {
            var seenGuids = new HashSet<Guid>();
            uint memberIndex = 0;
            while (true)
            {
                var devInfoData = new Win32API.SpDevInfoData();
                devInfoData.CbSize = (uint)Marshal.SizeOf(devInfoData);
                if (!Win32API.SetupDiEnumDeviceInfo(devInfoSet, memberIndex++, ref devInfoData))
                {
                    break;
                }

                var instanceId = GetDeviceInstanceId(devInfoSet, ref devInfoData);
                if (string.IsNullOrEmpty(instanceId) ||
                    instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                LastScannedNodeCount++;
                UsbTrace.Log($"WinUSBFinder: node [{instanceId}]");
                foreach (var guid in ReadDeviceInterfaceGuids(instanceId, lazyDeviceClassGuids))
                {
                    if (!seenGuids.Add(guid))
                    {
                        continue;
                    }

                    UsbTrace.Log($"WinUSBFinder:   interface GUID {guid}");
                    EnumerateInterfacesForGuid(guid, filter, options, devices, uniqueKeys);
                }
            }
        }
        finally
        {
            Win32API.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        LastMatchedDeviceCount = devices.Count;
        return devices;
    }

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
                    // REG_MULTI_SZ: NUL-separated strings ending with double NUL.
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
                    break; // ERROR_NO_MORE_ITEMS (or an unexpected failure) ends the walk.
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

                        // Link name format (lowercase):
                        // ##?#USB#VID_12D1&PID_107D&MI_02#a&f92a917&2&0002#{dee824ef-...}
                        // <para>链接名格式（小写）：
                        // ##?#USB#VID_12D1&PID_107D&MI_02#a&f92a917&2&0002#{dee824ef-...}</para>
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
        // Build the DeviceClasses reverse map only on first actual need (a node
        // without node-level DeviceInterfaceGUIDs); most nodes carry their GUIDs
        // directly, so the full registry walk is skipped in the common case.
        // <para>仅在首次真正需要时构建 DeviceClasses 反向映射（节点缺少节点级
        // DeviceInterfaceGUIDs 时）；大多数节点自带 GUID，常见情况下跳过整个
        // 注册表遍历。</para>
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

    private static void EnumerateInterfacesForGuid(
        Guid guid,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys)
    {
        var apiGuid = guid;
        var devInfo = Win32API.SetupDiGetClassDevsW(ref apiGuid, null, IntPtr.Zero,
            Win32API.DIGCF_PRESENT | Win32API.DIGCF_DEVICEINTERFACE);
        if (devInfo == -1)
        {
            UsbTrace.Log($"WinUSBFinder: SetupDiGetClassDevsW({guid}) failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            EnumerateInterfaces(devInfo, ref apiGuid, filter, options, devices, uniqueKeys);
        }
        finally
        {
            Win32API.SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    private delegate bool InterfaceEnumerator(uint index, ref Win32API.SpDeviceInterfaceData interfaceData);

    private static void EnumerateInterfaces(
        IntPtr devInfo,
        ref Guid interfaceClassGuid,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys)
    {
        // Copy to a local so the local function can capture it (ref parameters cannot
        // be captured); SetupDiEnumDeviceInterfaces only reads the GUID per call.
        var guid = interfaceClassGuid;
        bool Enumerate(uint index, ref Win32API.SpDeviceInterfaceData iface)
        {
            return Win32API.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, index, ref iface);
        }

        EnumerateInterfacesCore(devInfo, Enumerate, filter, options, devices, uniqueKeys);
    }

    private static void EnumerateInterfacesCore(
        IntPtr devInfo,
        InterfaceEnumerator enumerator,
        UsbDeviceFilter? filter,
        UsbSessionOptions? options,
        List<UsbDevice> devices,
        HashSet<string> uniqueKeys)
    {
        uint index = 0;
        var interfaceData = new Win32API.SpDeviceInterfaceData();
        interfaceData.CbSize = (uint)Marshal.SizeOf(interfaceData);

        while (enumerator(index++, ref interfaceData))
        {
            Win32API.SetupDiGetDeviceInterfaceDetailW(devInfo, ref interfaceData, IntPtr.Zero, 0, out var detailSize, IntPtr.Zero);

            var detailBuffer = Marshal.AllocHGlobal((int)detailSize);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize (DWORD, 4 bytes) is followed
                // immediately by DevicePath (WCHAR[]) — the field offset is 4 on BOTH
                // x86 and x64 (no pointer padding inside the struct). Using 8 on x64
                // skips the leading "\\" of "\\?\..." and makes CreateFile fail (err=123).
                var cbSize = IntPtr.Size == 8 ? 8 : 6;
                var pathOffset = 4;
                Marshal.WriteInt32(detailBuffer, cbSize);
                if (Win32API.SetupDiGetDeviceInterfaceDetailW(devInfo, ref interfaceData, detailBuffer, detailSize, out _, IntPtr.Zero))
                {
                    var path = Marshal.PtrToStringUni(new IntPtr(detailBuffer.ToInt64() + pathOffset)) ?? "";
                    var lowerPath = path.ToLower();

                    if (!PathMatchesFilter(path, filter))
                    {
                        continue;
                    }

                    // Check the bound interface before opening any candidate handle.
                    var identity = CreateMetadataOnlyDevice(path, TryParseVidPid(path).vid ?? 0, TryParseVidPid(path).pid ?? 0);
                    if (filter?.InterfaceClass is byte cls && identity.InterfaceClass != cls ||
                        filter?.InterfaceSubClass is byte sub && identity.InterfaceSubClass != sub ||
                        filter?.InterfaceProtocol is byte protocol && identity.InterfaceProtocol != protocol)
                        continue;
                    // Prefer WinUSB for Google devices
                    var isGoogleWinUsb = lowerPath.Contains(PreferWinUsbVidPid);

                    UsbDevice? device = null;
                    if (isGoogleWinUsb)
                    {
                        UsbTrace.Log($"Prefers WinUSB for Google device: {path}");
                        device = TryOpenWinUSB(path, options);
                    }

                    device ??= ProbeDevice(path, options);

                    if (device != null)
                    {
                        var key = BuildDeviceKey(device);
                        if (uniqueKeys.Add(key))
                        {
                            UsbTrace.Log($"Confirmed device added: key={key} using {(device is WinUSBDevice ? "WinUSB" : "Legacy")}");
                            devices.Add(device);
                        }
                        else
                        {
                            device.Dispose();
                        }
                    }
                    else
                    {
                        UsbTrace.Log($"WinUSBFinder: ProbeDevice returned null for [{path}]");
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }

        UsbTrace.Log($"WinUSBFinder: EnumerateInterfacesCore done, lastErr={Marshal.GetLastWin32Error()}, devices={devices.Count}");
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

    private static UsbDevice? ProbeDevice(string path, UsbSessionOptions? options)
    {
        (var vid, var pid) = TryParseVidPid(path);
        var metadata = CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0);
        if (options?.MetadataOnly == true) return metadata;
        DebugBridgeRoute.RequireDirectUsb(metadata.InterfaceClass, metadata.InterfaceSubClass, metadata.InterfaceProtocol);


        // winusb.sys allows only one open handle per interface: when the interface is
        // claimed by another session/process, CreateFile fails with access denied. Keep
        // the device visible with metadata parsed from the interface path instead of
        // silently dropping it from enumeration; sessions over a non-open device are
        // skipped later by UsbProviderProjection.ToSessions.
        // <para>winusb.sys 每个接口只允许一个打开句柄：当接口被其他会话/进程声明时，
        // CreateFile 会以访问拒绝失败。以从接口路径解析的元数据保留设备，而不是将其
        // 静默地从枚举中丢弃；基于未打开设备的会话稍后由
        // UsbProviderProjection.ToSessions 跳过。</para>
        var hDevice = Win32API.SimpleCreateHandle(path);
        if (hDevice == Win32API.INVALID_HANDLE_VALUE)
        {
            UsbTrace.Log($"WinUSBFinder: ProbeDevice cannot open [{path}] - reported with metadata only.");
            return CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0);
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

            // Legacy open failed (busy/unopenable): keep the device visible with
            // metadata only. <para>Legacy 打开失败（被占用/无法打开）：以仅元数据
            // 形式保留设备可见。</para>
            UsbTrace.Log($"WinUSBFinder: ProbeDevice legacy open failed for [{path}] - reported with metadata only.");
            return dev;
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

        // WinUSB open failed (busy/unopenable): keep the device visible with metadata
        // only. <para>WinUSB 打开失败（被占用/无法打开）：以仅元数据形式保留设备可见。</para>
        UsbTrace.Log($"WinUSBFinder: ProbeDevice WinUSB open failed for [{path}] - reported with metadata only.");
        return winDev;
    }

    private static UsbDevice? TryOpenWinUSB(string path, UsbSessionOptions? options)
    {
        (var vid, var pid) = TryParseVidPid(path);
        var metadata = CreateMetadataOnlyDevice(path, vid ?? 0, pid ?? 0);
        if (options?.MetadataOnly == true) return metadata;
        DebugBridgeRoute.RequireDirectUsb(metadata.InterfaceClass, metadata.InterfaceSubClass, metadata.InterfaceProtocol);

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

        // WinUSB open failed (busy/unopenable): keep the device visible with metadata
        // only. <para>WinUSB 打开失败（被占用/无法打开）：以仅元数据形式保留设备可见。</para>
        UsbTrace.Log($"WinUSBFinder: TryOpenWinUSB open failed for [{path}] - reported with metadata only.");
        return dev;
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

        // Convert a device interface path "\\?\usb#vid_12d1&pid_107d&mi_02#a&f92a917&2&0002#{guid}"
        // into a registry device instance id "USB\VID_12D1&PID_107D&MI_02\A&F92A917&2&0002"
        // (registry key names are case-insensitive, so case does not need to match exactly).
        // <para>将设备接口路径 "\\?\usb#vid_12d1&pid_107d&mi_02#a&f92a917&2&0002#{guid}"
        // 转换为注册表设备实例 ID "USB\VID_12D1&PID_107D&MI_02\A&F92A917&2&0002"
        // （注册表键名不区分大小写，大小写无需完全一致）。</para>
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
        // CompatibleIDs is a REG_MULTI_SZ VALUE of the device node key, not a subkey —
        // open the node key and query the value directly.
        // <para>CompatibleIDs 是设备节点键下的 REG_MULTI_SZ 值，不是子键——直接打开
        // 节点键并查询该值。</para>
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

                // REG_MULTI_SZ: NUL-separated strings ending with double NUL.
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
        // Composite devices (e.g. FT2232HL) expose the same serial number on every
        // interface (MI_00/MI_01). The interface number must be part of the dedup key
        // so each WinUSB-bound interface enumerates as its own device.
        // <para>复合设备（例如 FT2232HL）在每个接口（MI_00/MI_01）上暴露相同的序列号。
        // 接口号必须参与去重键，使每个 WinUSB 绑定的接口都被枚举为独立设备。</para>
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

        // Fallback for devices that do not expose serial numbers.
        return $"path:{device.DevicePath}|vid:{device.VendorId:X4}|pid:{device.ProductId:X4}|type:{device.GetType().Name}";
    }

}
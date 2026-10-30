using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

internal static partial class WinUSBFinder
{
    private const int UsbDeviceDescriptorBytes = 18;
    private const int UsbStringDescriptorBufferBytes = 256;

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
    /// The fallback map is shared only within one enumeration, so hotplug changes are visible on the next scan.
    /// <para>回退映射仅在单轮枚举内共享，后续扫描仍能观察到热插拔变化。</para>
    /// </summary>
    internal static IEnumerable<Guid> ReadDeviceInterfaceGuids(
        string instanceId,
        Lazy<Dictionary<string, List<Guid>>> deviceClassGuids)
    {
        var result = new List<Guid>();
        var subKey = @"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters";
        if (Win32API.RegOpenKeyExW(Win32API.HKEY_LOCAL_MACHINE, subKey, 0, Win32API.KEY_READ, out var key) != 0)
        {
            return FallbackToDeviceClassGuids(result, instanceId, deviceClassGuids);
        }

        try
        {
            uint size = 0;
            if (Win32API.RegQueryValueExW(key, "DeviceInterfaceGUIDs", IntPtr.Zero, out var type, IntPtr.Zero, ref size) != 0 ||
                size == 0)
            {
                return FallbackToDeviceClassGuids(result, instanceId, deviceClassGuids);
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

        return result.Count == 0 ? FallbackToDeviceClassGuids(result, instanceId, deviceClassGuids) : (IEnumerable<Guid>)result;
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
            var guidName = new char[64];
            var linkName = new char[512];
            uint guidIndex = 0;
            while (true)
            {
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
        Lazy<Dictionary<string, List<Guid>>> deviceClassGuids)
    {
        if (deviceClassGuids.Value.TryGetValue(instanceId, out var fallback))
        {
            foreach (var guid in fallback)
            {
                if (!result.Contains(guid))
                {
                    result.Add(guid);
                }
            }

            UsbTrace.LogFormatted($"WinUSBFinder: fell back to DeviceClasses GUIDs for [{instanceId}]: {string.Join(", ", fallback)}");
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

    private static bool PathMatchesFilter(string path, UsbDeviceFilter? filter, ushort? vid, ushort? pid)
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

        return (!filter.VendorId.HasValue || (vid.HasValue && vid.Value == filter.VendorId.Value)) && (!filter.ProductId.HasValue || (pid.HasValue && pid.Value == filter.ProductId.Value));
    }

    private static (ushort? vid, ushort? pid) TryParseVidPid(string path)
    {
        var vidMatch = VidPattern().Match(path);
        var pidMatch = PidPattern().Match(path);

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

    [GeneratedRegex("VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPattern();

    [GeneratedRegex("PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PidPattern();

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
    /// Returns the short backend name a device is opened through, for scan diagnostics.
    /// <para>返回设备实际使用的后端简称，用于扫描诊断日志。</para>
    /// </summary>
    /// <param name="device">The opened device. <para>已打开的设备。</para></param>
    /// <returns><c>Rockusb</c>, <c>WinUSB</c> or <c>Legacy</c>. <para><c>Rockusb</c>、<c>WinUSB</c> 或 <c>Legacy</c>。</para></returns>
    private static string AttachedBackendName(UsbDevice device)
        => device switch
        {
            RockusbDevice => "Rockusb",
            WinUSBDevice => "WinUSB",
            _ => "Legacy"
        };

    /// <summary>Serializes descriptor probes so one scan never opens the same interface twice at once.
    /// <para>串行化描述符探测，使一次扫描不会同时打开同一个接口两次。</para></summary>
    private static readonly FifoGate SerialProbeGate = new();

    /// <summary>Interface class of the vendor-specific debug interfaces whose iSerial exists only in the descriptor.
    /// <para>厂商自定义调试接口的接口类，其 iSerial 只存在于描述符中。</para>
    /// </summary>
    private const byte VendorSpecificInterfaceClass = 0xFF;

    /// <summary>Interface class a Rockchip mask ROM or loader port exposes: mass storage with the bulk-only protocol, which
    /// carries no serial-only evidence but does carry the <c>bcdUSB</c> bit that separates the two stages.
    /// <para>瑞芯微 mask ROM 或 loader 端口暴露的接口类：大容量存储 + bulk-only 协议，它没有“仅凭序列号”可用的证据，
    /// 但携带区分两个阶段的 <c>bcdUSB</c> 位。</para>
    /// </summary>
    private const byte MaskRomStorageInterfaceClass = 0x08;

    /// <summary>Rockchip vendor id: a boot ROM node reports no interface class worth trusting before its driver is bound.
    /// <para>瑞芯微厂商 ID：boot ROM 节点在驱动绑定前不报告可用的接口类。</para>
    /// </summary>
    private const ushort RockchipVendorId = 0x2207;

    /// <summary>What one bounded descriptor probe read from an interface.
    /// <para>一次有界描述符探测从接口读到的内容。</para>
    /// </summary>
    /// <param name="Serial">The iSerial string descriptor value, or <see langword="null"/> when unread.
    /// <para>iSerial 字符串描述符取值；未读到时为 <see langword="null"/>。</para></param>
    /// <param name="BcdUsb">The device descriptor's <c>bcdUSB</c> field, or <see langword="null"/> when unread.
    /// <para>设备描述符的 <c>bcdUSB</c> 字段；未读到时为 <see langword="null"/>。</para></param>
    private readonly record struct DescriptorProbe(string? Serial, ushort? BcdUsb);

    /// <summary>Whether this interface is worth one non-claiming descriptor probe: vendor-specific debug interfaces carry
    /// their serial only there, and Rockchip boot ROM ports carry the <c>bcdUSB</c> stage bit only there. Every other
    /// device is left alone so a scan never opens handles it has no question to answer with.
    /// <para>该接口是否值得做一次不认领接口的描述符探测：厂商自定义调试接口只有在那里才有序列号，瑞芯微 boot ROM 端口
    /// 只有在那里才有 <c>bcdUSB</c> 阶段位。其余设备不探测，使一次扫描不会打开它无需问询的句柄。</para>
    /// </summary>
    /// <param name="device">The enumerated device. <para>已枚举的设备。</para></param>
    /// <returns><see langword="true"/> when a probe may answer one of those questions.
    /// <para>探测能回答其中任一问题时为 <see langword="true"/>。</para></returns>
    private static bool ShouldProbeDeviceDescriptor(UsbDevice device)
        => device.InterfaceMetadataObserved
            ? device.InterfaceClass is VendorSpecificInterfaceClass or MaskRomStorageInterfaceClass
            : device.VendorId == RockchipVendorId;

    /// <summary>
    /// Resolves the serial of one enumerated interface in the order Windows itself prefers: an instance-id suffix
    /// that carries the serial, then a serial remembered for this instance, then a single gated descriptor probe.
    /// A failed probe is remembered briefly and keeps the last read serial, so an interface that another
    /// process or this application currently owns does not degrade the device to a <c>VID:PID</c> label.
    /// <para>按 Windows 自身的优先级解析一个枚举接口的序列号：先看携带序列号的实例 ID 后缀，其次看为该实例记下的
    /// 序列号，最后做一次受控闸门的描述符探测。探测失败会被短暂记住并保留上次读到的序列号，因此被其它进程或本
    /// 程序占用的接口不会让设备降级成 <c>VID:PID</c> 标签。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id observed now. <para>本次观测到的设备实例 ID。</para></param>
    /// <param name="instanceCandidate">Last segment of the instance id, which is the serial when Windows cached it.
    /// <para>实例 ID 的最后一段；Windows 缓存过序列号时它就是序列号。</para></param>
    /// <param name="probe">Metadata-only probe device carrying the interface class of the same path.
    /// <para>携带同一路径接口类的仅元数据探测设备。</para></param>
    /// <returns>The resolved serial, or <see langword="null"/> when no evidence is available.
    /// <para>解析出的序列号；没有任何证据时为 <see langword="null"/>。</para></returns>
    internal static string? ResolveEnumeratedSerial(string path, string? instanceId, string? instanceCandidate,
        UsbDevice probe)
    {
        if (IsInstanceSerial(instanceCandidate))
        {
            UsbSerialCache.Remember(path, instanceId, instanceCandidate!, Environment.TickCount64);
            return instanceCandidate;
        }

        if (UsbSerialCache.TryGet(path, instanceId, Environment.TickCount64, out var cached))
        {
            return cached;
        }

        return PeekSerialNumberFor(probe, path, instanceId);
    }

    /// <summary>Whether an instance-id suffix is a real serial rather than a location string such as <c>6&amp;1a2b&amp;0&amp;2</c>.
    /// <para>判断实例 ID 后缀是真正的序列号而不是 <c>6&amp;1a2b&amp;0&amp;2</c> 之类的定位字符串。</para>
    /// </summary>
    /// <param name="candidate">Last segment of the instance id. <para>实例 ID 的最后一段。</para></param>
    /// <returns><see langword="true"/> when the suffix can be used as the serial.
    /// <para>后缀可作为序列号使用时为 <see langword="true"/>。</para></returns>
    internal static bool IsInstanceSerial(string? candidate)
        => !string.IsNullOrWhiteSpace(candidate) && !candidate.Contains('&');

    private static string? PeekSerialNumberFor(UsbDevice probe, string path, string? instanceId)
        => ShouldProbeDeviceDescriptor(probe) ? ProbeDescriptors(path, instanceId).Serial : null;

    /// <summary>
    /// Runs one gated descriptor probe and remembers what it read, marking the interface unavailable for a short window
    /// when the probe read nothing at all.
    /// <para>在闸门下执行一次描述符探测并记住读到的内容；探测什么都没读到时，把该接口标记为短时不可用。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <param name="instanceId">Device instance id observed now. <para>本次观测到的设备实例 ID。</para></param>
    /// <returns>The values the probe read; both members are <see langword="null"/> when it read nothing.
    /// <para>探测读到的取值；什么都没读到时两个成员均为 <see langword="null"/>。</para></returns>
    private static DescriptorProbe ProbeDescriptors(string path, string? instanceId)
    {
        var nowTicks = Environment.TickCount64;
        if (UsbSerialCache.IsRecentlyUnavailable(path, instanceId, nowTicks))
        {
            return default;
        }

        using (SerialProbeGate.Enter())
        {
            var peeked = PeekDeviceDescriptors(path);
            if (peeked.Serial is null && peeked.BcdUsb is null)
            {
                UsbSerialCache.RememberUnavailable(path, instanceId, Environment.TickCount64);
                return default;
            }

            UsbSerialCache.RememberDescriptors(path, instanceId, peeked.Serial, peeked.BcdUsb,
                Environment.TickCount64);
            return peeked;
        }
    }

    /// <summary>
    /// Fills the device descriptor's <c>bcdUSB</c> value for an enumerated device when the device was already opened
    /// (the handle carries it), when one bounded WinUSB probe may read it, or, for a device serviced by
    /// <c>rockusb.sys</c> that no WinUSB handle can reach, from the hub port data that the hub reports for it. This is
    /// the only evidence that separates a Rockchip mask ROM device from its loader stage: both report the same vendor
    /// and product ids, so a product-id table alone cannot tell them apart.
    /// <para>为已枚举设备补齐设备描述符的 <c>bcdUSB</c> 取值：设备已打开时直接取句柄里的值，有界 WinUSB 探测能读到时
    /// 取探测值；由 <c>rockusb.sys</c> 服务、WinUSB 句柄无法打开的瑞芯微设备则取集线器为该端口报告的取值。这是区分
    /// 瑞芯微 mask ROM 设备与其 loader 阶段的唯一证据：两者报告相同的厂商与产品 ID，仅凭产品 ID 表无法区分。</para>
    /// </summary>
    /// <param name="device">The enumerated device to complete. <para>待补齐的已枚举设备。</para></param>
    private static void AttachObservedDescriptor(UsbDevice device)
    {
        if (device.BcdUsb is not null)
        {
            return;
        }

        var path = device.DevicePath;
        var instanceId = device.DeviceInstanceId;
        var nowTicks = Environment.TickCount64;
        if (UsbSerialCache.TryGetBcdUsb(path, instanceId, nowTicks, out var remembered))
        {
            device.BcdUsb = remembered;
            return;
        }

        if (device is not RockusbDevice && ShouldProbeDeviceDescriptor(device))
        {
            var peeked = ProbeDescriptors(path, instanceId);
            device.BcdUsb = peeked.BcdUsb;
            if (string.IsNullOrWhiteSpace(device.SerialNumber) && peeked.Serial is { Length: > 0 } serial)
            {
                device.SerialNumber = serial;
            }
        }

        if (device.BcdUsb is null && device.VendorId == RockchipVendorId &&
            UsbHubDescriptorReader.TryReadBcdUsb(instanceId, device.VendorId, device.ProductId, out var hubBcdUsb))
        {
            device.BcdUsb = hubBcdUsb;
            UsbSerialCache.RememberDescriptors(path, instanceId, serial: null, hubBcdUsb, nowTicks);
        }
    }

    /// <summary>
    /// Fills the enumerated serial for a metadata-only device, aligned with Google adb's Windows
    /// implementation (AdbWinApi's <c>GetSerialNumber</c> takes the last segment of the device
    /// instance ID): the instance-ID suffix is the first evidence — Windows embeds the iSerial
    /// into the instance path when it read one at first plug; when the suffix is a location
    /// string (Windows never cached a serial), a bounded, non-claiming descriptor peek reads the
    /// real iSerial from the USB string descriptor for vendor-specific (0xFF) debug interfaces.
    /// The peek opens no pipes and never claims the interface, so it stays safe while a server
    /// or another process owns the interface.
    /// <para>为仅元数据的枚举结果填充序列号，语义对齐 Google adb 的 Windows 实现
    /// （AdbWinApi 的 <c>GetSerialNumber</c> 取设备实例 ID 最后一段）：实例 ID 后缀是第一
    /// 证据——Windows 在首次插入读到 iSerial 时会把它编入实例路径；当后缀是位置式字符串
    /// （Windows 未能缓存序列号）时，对厂商自定义（0xFF）调试接口做一次有界、不认领接口的
    /// 描述符窥视，从 USB 字符串描述符读取真实 iSerial。窥视不打开管道、不认领接口，
    /// server 或其他进程占用接口时同样安全。</para>
    /// </summary>
    internal static WinUSBDevice AttachEnumeratedSerial(WinUSBDevice device, string? instanceSerial)
    {
        if (!string.IsNullOrWhiteSpace(instanceSerial))
        {
            device.SerialNumber = instanceSerial;
            UsbSerialCache.Remember(device.DevicePath, device.DeviceInstanceId, instanceSerial,
                Environment.TickCount64);
            return device;
        }

        if (device.InterfaceMetadataObserved && device.InterfaceClass == 0xff &&
            string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            device.SerialNumber = PeekSerialNumberFor(device, device.DevicePath, device.DeviceInstanceId);
        }

        AttachObservedDescriptor(device);
        return device;
    }

    /// <summary>
    /// Briefly opens a WinUSB handle to read the device descriptor (<c>bcdUSB</c> and the iSerial index) and the iSerial
    /// string descriptor; no pipe queries, no policies, no interface claim, and the handle is closed immediately. A
    /// device whose driver is not WinUSB cannot be opened this way, so every value is absent rather than guessed.
    /// <para>短暂打开 WinUSB 句柄读取设备描述符（<c>bcdUSB</c> 与 iSerial 索引）及 iSerial 字符串描述符；不查询管道、
    /// 不设置策略、不认领接口，句柄随即关闭。驱动不是 WinUSB 的设备无法这样打开，因此取值为缺省而非猜测。</para>
    /// </summary>
    /// <param name="path">Device interface path. <para>设备接口路径。</para></param>
    /// <returns>Whatever the probe could read; both members are <see langword="null"/> when the handle could not be
    /// opened. <para>探测能读到的一切；句柄无法打开时两个成员均为 <see langword="null"/>。</para></returns>
    private static unsafe DescriptorProbe PeekDeviceDescriptors(string path)
    {
        var hUsb = Win32API.SimpleCreateHandle(path, true);
        if (hUsb == Win32API.INVALID_HANDLE_VALUE)
        {
            return default;
        }

        try
        {
            if (!WinUSBAPI.WinUsb_Initialize(hUsb, out var winUsbHandle))
            {
                return default;
            }

            using var safeHandle = new SafeWinUsbHandle(winUsbHandle);
            byte* buffer = stackalloc byte[UsbStringDescriptorBufferBytes];
            if (!WinUSBAPI.WinUsb_GetDescriptor(safeHandle.DangerousGetHandle(), WinUSBAPI.USB_DEVICE_DESCRIPTOR_TYPE,
                0, 0, (IntPtr)buffer, UsbDeviceDescriptorBytes, out _))
            {
                return default;
            }

            var descriptor = Marshal.PtrToStructure<Win32API.USBDeviceDescriptor>((IntPtr)buffer);
            ushort? bcdUsb = descriptor.BcdUSB;
            var iSerial = descriptor.ISerialNumber;
            if (iSerial == 0)
            {
                return new DescriptorProbe(null, bcdUsb);
            }

            if (!WinUSBAPI.WinUsb_GetDescriptor(safeHandle.DangerousGetHandle(), WinUSBAPI.USB_STRING_DESCRIPTOR_TYPE,
                iSerial, 0x0409, (IntPtr)buffer, UsbStringDescriptorBufferBytes - 1, out var transferred) || transferred <= 2)
            {
                return new DescriptorProbe(null, bcdUsb);
            }

            var serial = Marshal.PtrToStringUni((IntPtr)(buffer + 2), (int)(transferred - 2) / 2)?.TrimEnd('\0');
            return new DescriptorProbe(string.IsNullOrWhiteSpace(serial) ? null : serial, bcdUsb);
        }
        finally
        {
            Win32API.CloseHandle(hUsb);
        }
    }

}
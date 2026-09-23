using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Protocols.Scrcpy;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Flash;


namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// <para>全量扫描：枚举所有 USB 设备并按接口类/VID-PID 分类为 ADB/Fastboot/EDL/RKDL，
    /// 并合并 adb server 中的网络设备。结果缓存至下一次设备变更（启动窗的首次扫描结果
    /// 可直接供设备管理器复用，避免重复枚举）。</para>
    /// Full scan: enumerates all USB devices, classifies them as ADB/Fastboot/EDL/RKDL by
    /// interface class or VID/PID, and merges network devices from the adb server. The result
    /// is cached until the next device change (the splash's initial scan is reused by the
    /// device manager, avoiding a duplicate enumeration).
    /// </summary>
    public IReadOnlyList<DetectedDevice> Scan()
    {
        var cached = _cachedScan;
        if (cached is not null)
        {
            return cached;
        }

        var devices = ScanCore();
        HdcDirectSessionPool.Retain(devices);
        _cachedScan = devices;
        return devices;
    }

    /// <summary>
    /// <para>执行实际的全量枚举与分类。</para>
    /// Performs the actual full enumeration and classification.
    /// </summary>
    private IReadOnlyList<DetectedDevice> ScanCore()
    {
        var devices = new List<DetectedDevice>();
        var usbInfos = _comm.EnumerateUsbDevices();
        // Server identities are authoritative; never join unrelated enumeration orders.
        var adbClient = AdbServerClient;
        var adbDevices = adbClient?.GetDevices().ToList();
        using var hdcServer = ProbeHdcServer();
        var hdcTargets = hdcServer?.ListTargetDetails();
        foreach (var info in usbInfos)
        {
            var protocol = Classify(info);
            if (protocol is null)
            {
                continue;
            }

            var physicalId = PhysicalUsbId(info, info.SerialNumber);
            if (protocol == DeviceProtocolKind.Adb && adbDevices is not null &&
                (adbDevices.Any(target =>
                    string.Equals(target.Serial, info.SerialNumber, StringComparison.Ordinal) ||
                    physicalId is not null && string.Equals(physicalId,
                        FindServerUsbIdentity(usbInfos, target.Serial, DeviceProtocolKind.Adb),
                        StringComparison.Ordinal)) ||
                    string.IsNullOrWhiteSpace(info.SerialNumber) && adbDevices.Count != 0))
                continue;
            if (protocol == DeviceProtocolKind.Hdc && hdcServer is not null)
                continue;
            if (protocol == DeviceProtocolKind.Hdc && HdcDirectSessionPool.FindOwnedDevice(info.DevicePath) is { } owned)
            {
                if (!devices.Any(d => d.Key == owned.Key)) devices.Add(owned);
                continue;
            }
            var serial = info.SerialNumber;
            if (protocol == DeviceProtocolKind.Fastboot && string.IsNullOrWhiteSpace(serial))
                serial = ReadFastbootSerial(info);
            devices.Add(new DetectedDevice
            {
                Key = info.DeviceKey,
                Serial = serial ?? $"{info.VendorId:X4}:{info.ProductId:X4}",
                PhysicalId = PhysicalUsbId(info,
                    !string.IsNullOrWhiteSpace(info.SerialNumber) ? info.SerialNumber : serial),
                Protocol = protocol.Value,
                Transport = DeviceTransportKind.Usb,
                State = protocol.Value switch
                {
                    DeviceProtocolKind.Adb => "device",
                    DeviceProtocolKind.Fastboot => "fastboot",
                    DeviceProtocolKind.Edl => "edl",
                    DeviceProtocolKind.Rkdl => RkdlState(info),
                    DeviceProtocolKind.Hdc => "device",
                    _ => string.Empty,
                },
                VendorId = info.VendorId,
                ProductId = info.ProductId,
            });
        }

        // hdc server 通道（系统内存在 hdc server 时，作为 client 列举其管理的全部目标，
        // 与 adb server 网络设备合并方式一致）。USB 直连枚举命中同一物理设备时以
        // The host server owns HDC target identity and physical transport metadata.
        if (hdcTargets is not null)
        {
            foreach (var target in hdcTargets)
            {
                if (!IsSupportedHdcTarget(target)) continue;
                devices.Add(new DetectedDevice
                {
                    Key = "hdc:" + target.ConnectKey,
                    Serial = target.ConnectKey,
                    PhysicalId = target.Transport == "USB"
                        ? FindServerUsbIdentity(usbInfos, target.ConnectKey, DeviceProtocolKind.Hdc) : null,
                    Protocol = DeviceProtocolKind.Hdc,
                    Transport = target.Transport switch
                    {
                        "USB" => DeviceTransportKind.Usb,
                        "TCP" => DeviceTransportKind.Tcp,
                        "UART" => DeviceTransportKind.Serial,
                        _ => DeviceTransportKind.Unknown,
                    },
                    State = target.State.Equals("Connected", StringComparison.OrdinalIgnoreCase) ? "device"
                        : target.State.Equals("Unauthorized", StringComparison.OrdinalIgnoreCase) ? "unauthorized" : "offline",
                });
            }
        }

        if (adbDevices is not null)
        {
            foreach (var d in adbDevices)
            {
                devices.Add(new DetectedDevice
                {
                    Key = "adb:" + d.Serial,
                    Serial = d.Serial,
                    PhysicalId = !d.Serial.Contains(':')
                        ? FindServerUsbIdentity(usbInfos, d.Serial, DeviceProtocolKind.Adb) : null,
                    Protocol = DeviceProtocolKind.Adb,
                    Transport = d.Serial.Contains(':') ? DeviceTransportKind.Tcp : DeviceTransportKind.Usb,
                    State = d.State.ToString().ToLowerInvariant(),
                });

                // 网络设备来自 adb server 通道（非 USB 直连），明确记录以便用户区分。
                // Network devices come from the adb server channel (not direct USB); log them
                // explicitly so the transport is obvious.
                LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                    $"adb server channel device: {d.Serial}");
            }
        }

        return CoalesceSameEndpoint(devices);
    }

    internal static bool IsSupportedHdcTarget(HdcTargetInfo target)
        => target.Transport is "USB" or "TCP";

    private static string? PhysicalUsbId(ushort vendorId, ushort productId, string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial == "UNKNOWN" ||
            serial.Length > 128 || serial.Any(char.IsControl))
            return null;
        return $"usb:{vendorId:X4}:{productId:X4}:{serial}";
    }

    internal static string? PhysicalUsbId(UsbDeviceInfo info, string? serial)
        => info.ContainerId is { } container && container != Guid.Empty
            ? $"usb-container:{container:N}"
            : PhysicalUsbId(info.VendorId, info.ProductId, serial);

    internal static string? FindServerUsbIdentity(IReadOnlyList<UsbDeviceInfo> infos, string serial,
        DeviceProtocolKind protocol)
    {
        var ids = infos.Where(info => Classify(info) == protocol &&
                (string.Equals(info.SerialNumber, serial, StringComparison.Ordinal) ||
                    info.ContainerId.HasValue && InstanceIdMatchesSerial(info.DeviceInstanceId, serial)))
            .Select(info => PhysicalUsbId(info, info.SerialNumber))
            .Where(id => id is not null).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }

    private static bool InstanceIdMatchesSerial(string? instanceId, string serial)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        var separator = instanceId.LastIndexOf('\\');
        return separator >= 0 && string.Equals(instanceId[(separator + 1)..], serial,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<IReadOnlyList<DetectedDevice>> GroupPhysicalDevices(
        IEnumerable<DetectedDevice> endpoints)
        => endpoints.GroupBy(device => device.PhysicalId ?? device.Key, StringComparer.Ordinal)
            .Select(group => (IReadOnlyList<DetectedDevice>)group.ToArray()).ToArray();

    internal static IReadOnlyList<DetectedDevice> CoalesceSameEndpoint(IEnumerable<DetectedDevice> candidates)
    {
        var byKey = new Dictionary<string, DetectedDevice>(StringComparer.Ordinal);
        foreach (var device in candidates)
        {
            if (byKey.TryGetValue(device.Key, out var existing))
            {
                if (EndpointStateRank(device.State) > EndpointStateRank(existing.State))
                    byKey[device.Key] = device;
            }
            else
            {
                byKey.Add(device.Key, device);
            }
        }
        return byKey.Values.ToArray();
    }

    private static int EndpointStateRank(string state) => state switch
    {
        "device" or "online" or "fastboot" or "loader" => 2,
        "unauthorized" => 1,
        _ => 0
    };

    private string? ReadFastbootSerial(UsbDeviceInfo info)
    {
        IUsbDeviceSession? usb = null;
        FastbootSession? session = null;
        try
        {
            usb = _comm.OpenUsbDeviceSessionByKey(info.DeviceKey);
            if (usb is null || !new FastbootFactory(2000).TryCreate(usb, out var protocolSession) ||
                protocolSession is not FastbootSession fastboot)
                return null;
            session = fastboot;
            var serial = session.GetVariable("serialno", useCache: false).Trim();
            return serial.Length is > 0 and <= 128 &&
                   serial.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
                ? serial : null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
                $"Fastboot serial unavailable for {info.DeviceKey}: {ex.Message}");
            return null;
        }
        finally
        {
            session?.Dispose();
            if (session is null) usb?.Dispose();
        }
    }

    /// <summary>
    /// <para>手动强制刷新：重新执行全量扫描并广播变更。</para>
    /// Manual force refresh: reruns the full scan and broadcasts the change.
    /// </summary>
    public Task ForceRefreshAsync()
    {
        LogService.Instance.Write(LogLevel.Info, "DeviceMonitor", "Manual device refresh requested.");
        ScheduleChange();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <para>按序列号查找当前扫描结果中的设备；找不到时返回 <c>null</c>。</para>
    /// Finds a device from the current scan result by serial; returns <c>null</c> when absent.
    /// </summary>
    public DetectedDevice? FindDeviceBySerial(string serial)
    {
        var matches = Scan().Where(d => d.Serial == serial).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Device serial is ambiguous; select a physical device explicitly.");
        return matches.FirstOrDefault();
    }

    /// <summary>HDC USB 接口子类（上游 developtools_hdc harmonySubClass = 0x50）。 HDC USB interface subclass (upstream harmonySubClass = 0x50).</summary>
    private const byte HdcSubClass = 0x50;

    /// <summary>HDC USB 接口协议（上游 harmonyProtocol = 0x01）。 HDC USB interface protocol (upstream harmonyProtocol = 0x01).</summary>
    private const byte HdcInterfaceProtocol = 0x01;

    /// <summary>
    /// <para>按接口类/VID-PID 将 USB 设备分类到协议类型；不匹配任何协议时返回 <c>null</c>。</para>
    /// Classifies a USB device into a protocol kind by interface class / VID-PID;
    /// returns <c>null</c> when nothing matches.
    /// </summary>
    private static DeviceProtocolKind? Classify(UsbDeviceInfo info)
    {
        // EDL：高通 9008 族（VID/PID 匹配）。
        if (info.VendorId == 0x05C6 && info.ProductId is 0x9008 or 0x9006 or 0x900E or 0x901D or 0x9307)
        {
            return DeviceProtocolKind.Edl;
        }

        // RKDL：瑞芯微 bootrom/loader。
        if (info.VendorId == 0x2207 && ((info.ProductId >> 8) != 0 || IsKnownRkdlLoader(info.ProductId)))
        {
            return DeviceProtocolKind.Rkdl;
        }

        // HDC：对齐上游 developtools_hdc IsDebuggableDev（host_usb.cpp）——接口类 0xFF、
        // 子类 0x50、协议 0x01（两个 bulk 端点）。HDC 无固定 VID/PID，接口三元组为准。
        // HDC: aligned with upstream developtools_hdc IsDebuggableDev (host_usb.cpp) —
        // interface class 0xFF, subclass 0x50, protocol 0x01 (two bulk endpoints). HDC has
        // no fixed VID/PID; the interface triple is authoritative.
        foreach (var iface in info.Interfaces)
        {
            if (iface.Class == HdcConstants.InterfaceClass &&
                iface.SubClass == HdcSubClass &&
                iface.Protocol == HdcInterfaceProtocol)
            {
                return DeviceProtocolKind.Hdc;
            }
        }
        if (info.InterfaceMetadataObserved &&
            info.InterfaceClass == HdcConstants.InterfaceClass &&
            info.InterfaceSubClass == HdcSubClass &&
            info.InterfaceProtocol == HdcInterfaceProtocol)
        {
            return DeviceProtocolKind.Hdc;
        }

        // ADB / Fastboot：接口类 0xFF/0x42，协议 0x01 / 0x03。
        foreach (var iface in info.Interfaces)
        {
            if (iface.Class == 0xFF && iface.SubClass == 0x42)
            {
                if (iface.Protocol == 0x01)
                {
                    return DeviceProtocolKind.Adb;
                }

                if (iface.Protocol == 0x03)
                {
                    return DeviceProtocolKind.Fastboot;
                }
            }
        }
        if (info.InterfaceMetadataObserved &&
            info.InterfaceClass == 0xFF && info.InterfaceSubClass == 0x42)
        {
            if (info.InterfaceProtocol == 0x01)
            {
                return DeviceProtocolKind.Adb;
            }

            if (info.InterfaceProtocol == 0x03)
            {
                return DeviceProtocolKind.Fastboot;
            }
        }
        return null;
    }

    private static bool IsKnownRkdlLoader(ushort productId)
        => RkdlConstants.KnownLoaderProductIds.Contains(productId);

    private static string RkdlState(UsbDeviceInfo info)
        => RkdlConstants.KnownLoaderProductIds.Contains(info.ProductId) ? "loader" : "maskrom";

}
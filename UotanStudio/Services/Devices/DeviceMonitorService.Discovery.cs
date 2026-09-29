using UotanStudio.Comm;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Mirroring;


namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// <para>全量扫描：直连 USB 端点由 Comm 的发现层提供（某协议的调试桥 server 在监听时，该协议的
    /// 直连端点已不在端点清单内），再把 adb 与 HDC server 的目标并入结果。结果缓存至下一次设备变更
    /// （启动窗的首次扫描结果可直接供设备管理器复用，避免重复枚举）。</para>
    /// Full scan: consumes the direct-USB endpoints Comm's discovery layer reports (once a protocol's
    /// debug bridge server is listening, that protocol's direct endpoints are already absent) and appends
    /// the adb and HDC server targets. The result is cached until the next device change (the splash's
    /// initial scan is reused by the device manager, avoiding a duplicate enumeration).
    /// </summary>
    public IReadOnlyList<DetectedDevice> Scan()
    {
        var cached = _cachedScan;
        if (cached is not null)
        {
            return cached;
        }

        var devices = ScanCore();
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
        var endpoints = _controller.RefreshAsync().GetAwaiter().GetResult().Endpoints;
        var usbInfos = endpoints.Where(endpoint => endpoint.UsbInfo is not null)
            .Select(endpoint => endpoint.UsbInfo!).ToArray();
        // The server's serial/connect key is authoritative; USB association requires unique identity evidence.
        var adbDevices = endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
            endpoint.Route == "server").ToArray();
        var hdcTargets = endpoints.Where(endpoint => endpoint.Protocol == "hdc" &&
            endpoint.Route == "server").ToArray();
        foreach (var info in usbInfos)
        {
            var protocol = Classify(info);
            if (protocol is null)
            {
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
                EdlProtocol = protocol == DeviceProtocolKind.Edl
                    ? _edlConnections.GetState(info.DeviceKey) : EdlProtocolState.Unknown,
            });
        }

        // hdc server 通道（系统内存在 hdc server 时，作为 client 列举其管理的全部目标，
        // 与 adb server 网络设备合并方式一致）。USB 直连枚举命中同一物理设备时以
        // The host server owns HDC target identity and physical transport metadata.
        foreach (var target in hdcTargets)
        {
            if (target.TargetTransport is not ("USB" or "TCP")) continue;
            devices.Add(new DetectedDevice
            {
                Key = "hdc:" + target.Address,
                Serial = target.Address,
                PhysicalId = target.TargetTransport == "USB"
                    ? FindServerUsbIdentity(usbInfos, target.Address, DeviceProtocolKind.Hdc) : null,
                Protocol = DeviceProtocolKind.Hdc,
                Transport = target.TargetTransport switch
                {
                    "USB" => DeviceTransportKind.Usb,
                    "TCP" => DeviceTransportKind.Tcp,
                    _ => DeviceTransportKind.Unknown,
                },
                State = target.State?.Equals("Connected", StringComparison.OrdinalIgnoreCase) == true ? "device"
                    : target.State?.Equals("Unauthorized", StringComparison.OrdinalIgnoreCase) == true
                        ? "unauthorized" : "offline",
            });
        }

        foreach (var target in adbDevices)
        {
            devices.Add(new DetectedDevice
            {
                Key = "adb:" + target.Address,
                Serial = target.Address,
                PhysicalId = !target.Address.Contains(':')
                    ? FindServerUsbIdentity(usbInfos, target.Address, DeviceProtocolKind.Adb) : null,
                Protocol = DeviceProtocolKind.Adb,
                Transport = target.Address.Contains(':') ? DeviceTransportKind.Tcp : DeviceTransportKind.Usb,
                State = target.State?.ToLowerInvariant() ?? "offline",
            });

            LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                $"adb server channel device: {target.Address}");
        }

        _edlConnections.RetainDevices(devices.Where(device => device.Protocol == DeviceProtocolKind.Edl)
            .Select(device => device.Key).ToHashSet(StringComparer.Ordinal));
        return CoalesceSameEndpoint(devices);
    }

    internal static bool IsSupportedHdcTarget(HdcTargetInfo target)
        => target.Transport is "USB" or "TCP";

    private static string ProtocolToken(DeviceProtocolKind protocol) => protocol switch
    {
        DeviceProtocolKind.Adb => "adb",
        DeviceProtocolKind.Hdc => "hdc",
        DeviceProtocolKind.Fastboot => "fastboot",
        DeviceProtocolKind.Edl => "edl",
        DeviceProtocolKind.Rkdl => "rkdl",
        _ => string.Empty
    };

    // Physical identity binding is owned by Comm's discovery layer; the host only consumes it.
    // 物理身份绑定由 Comm 的发现层拥有；宿主仅消费其结果。
    internal static string? PhysicalUsbId(UsbDeviceInfo info, string? serial)
        => UotanStudio.Comm.Devices.Discovery.UsbPhysicalIdentity.Of(info, serial);

    internal static string? FindServerUsbIdentity(IReadOnlyList<UsbDeviceInfo> infos, string serial,
        DeviceProtocolKind protocol)
        => UotanStudio.Comm.Devices.Discovery.UsbPhysicalIdentity.ServerPhysicalId(infos, serial, ProtocolToken(protocol));

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
        // The getvar/claim mechanism now lives in Comm's discovery layer; the host only consumes the result.
        // getvar/认领机制已移入 Comm 发现层；宿主仅消费结果。
        var serial = _controller.ReadFastbootSerial(info);
        if (serial is null)
        {
            LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
                $"Fastboot serial unavailable for {info.DeviceKey}.");
        }
        return serial;
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

    /// <summary>
    /// <para>按接口类/VID-PID 将 USB 设备分类到协议类型；分类事实由 Comm 的
    /// <c>UsbProtocolClassifier</c> 唯一提供，此处仅做字符串到枚举的映射。</para>
    /// Classifies a USB device into a protocol kind by interface class / VID-PID; the classification
    /// facts come solely from Comm's <c>UsbProtocolClassifier</c>; this only maps the string to the enum.
    /// </summary>
    private static DeviceProtocolKind? Classify(UsbDeviceInfo info)
        => UsbProtocolClassifier.Classify(info) switch
        {
            "adb" => DeviceProtocolKind.Adb,
            "hdc" => DeviceProtocolKind.Hdc,
            "fastboot" => DeviceProtocolKind.Fastboot,
            "edl" => DeviceProtocolKind.Edl,
            "rkdl" => DeviceProtocolKind.Rkdl,
            _ => null
        };

    private static string RkdlState(UsbDeviceInfo info)
        => RkdlConstants.KnownLoaderProductIds.Contains(info.ProductId) ? "loader" : "maskrom";

}
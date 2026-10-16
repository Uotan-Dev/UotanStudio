using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Full scan of the direct-USB endpoints plus the adb and HDC server targets, cached until the
    /// next device change.
    /// <para>全量扫描直连 USB 端点并并入 adb 与 HDC server 目标，结果缓存至下一次设备变更。</para>
    /// </summary>
    /// <returns>The detected devices from this scan or from the cache. <para>本次扫描或缓存中的设备列表。</para></returns>
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
    /// Performs the actual full enumeration and classification.
    /// <para>执行实际的全量枚举与分类。</para>
    /// </summary>
    /// <returns>The classified device list with same-endpoint entries coalesced. <para>分类后的设备列表，同端点条目已合并。</para></returns>
    private IReadOnlyList<DetectedDevice> ScanCore()
    {
        List<DetectedDevice> devices = [];
        var endpoints = _controller.RefreshAsync().GetAwaiter().GetResult().Endpoints;
        var usbInfos = endpoints.Where(endpoint => endpoint.UsbInfo is not null)
            .Select(endpoint => endpoint.UsbInfo!).ToArray();
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
                    DeviceProtocolKind.Mtk => MtkState(info),
                    DeviceProtocolKind.Hdc => "device",
                    _ => string.Empty,
                },
                VendorId = info.VendorId,
                ProductId = info.ProductId,
                EdlProtocol = protocol == DeviceProtocolKind.Edl
                    ? _edlConnections.GetState(info.DeviceKey) : EdlProtocolState.Unknown,
            });
        }

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
                Transport = MapServerTransport(target.TargetTransport),
                State = target.State?.Equals("Connected", StringComparison.OrdinalIgnoreCase) == true ? "device"
                    : target.State?.Equals("Unauthorized", StringComparison.OrdinalIgnoreCase) == true
                        ? "unauthorized" : "offline",
            });
        }

        var resolvedEndpoints = adbDevices
            .Where(target => target.AliasAddress is not null)
            .Select(target => target.AliasAddress!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var target in adbDevices)
        {
            devices.Add(AdbDevice(target, usbInfos,
                target.AliasAddress ?? (resolvedEndpoints.Contains(target.Address) ? target.Address : null)));
        }

        foreach (var target in ManualNetworkTargets)
        {
            if (devices.Any(entry => string.Equals(entry.Serial, target, StringComparison.Ordinal)))
            {
                continue;
            }

            devices.Add(new DetectedDevice
            {
                Key = "adb-tcp:" + target,
                Serial = target,
                NetworkAlias = resolvedEndpoints.Contains(target) ? target : null,
                Protocol = DeviceProtocolKind.Adb,
                Transport = DeviceTransportKind.Tcp,
                State = "device",
            });
        }

        _edlConnections.RetainDevices(devices.Where(device => device.Protocol == DeviceProtocolKind.Edl)
            .Select(device => device.Key).ToHashSet(StringComparer.Ordinal));
        return CoalesceSameEndpoint(devices);
    }

    internal static bool IsSupportedHdcTarget(HdcTargetInfo target)
        => target.Transport is "USB" or "TCP";

    /// <summary>
    /// Projects one adb server target into a device row keyed by the target address.
    /// <para>把一条 adb server 目标投影为以目标地址为键的设备行。</para>
    /// </summary>
    /// <param name="target">The adb server endpoint to project. <para>要投影的 adb server 端点。</para></param>
    /// <param name="usbInfos">Direct-USB device information used to resolve a physical identity. <para>用于解析物理身份的直接 USB 设备信息。</para></param>
    /// <param name="networkAlias">Network endpoint resolved for this target, or null when absent. <para>为该目标解析出的网络端点；缺失时为 null。</para></param>
    /// <returns>The device row for the target. <para>该目标对应的设备行。</para></returns>
    private static DetectedDevice AdbDevice(DeviceEndpoint target, IReadOnlyList<UsbDeviceInfo> usbInfos,
        string? networkAlias)
    {
        var transport = MapServerTransport(target.TargetTransport);
        LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
            $"adb server channel device: {target.Address} ({transport})");
        return new DetectedDevice
        {
            Key = "adb:" + target.Address,
            Serial = target.Address,
            PhysicalId = transport == DeviceTransportKind.Usb
                ? FindServerUsbIdentity(usbInfos, target.Address, DeviceProtocolKind.Adb) : null,
            NetworkAlias = networkAlias,
            Protocol = DeviceProtocolKind.Adb,
            Transport = transport,
            State = target.State?.ToLowerInvariant() ?? "offline",
        };
    }

    /// <summary>
    /// Maps server-reported link text to a transport kind, returning Unknown when there is no link
    /// evidence rather than guessing USB from the serial shape.
    /// <para>把 server 上报的链路文本映射为传输类型；没有链路证据时返回 Unknown，而不按序列号形状猜测 USB。</para>
    /// </summary>
    /// <param name="targetTransport">The server-reported link text. <para>server 上报的链路文本。</para></param>
    /// <returns>The transport kind for the link text. <para>该链路文本对应的传输类型。</para></returns>
    internal static DeviceTransportKind MapServerTransport(string? targetTransport) => targetTransport switch
    {
        "USB" => DeviceTransportKind.Usb,
        "TCP" => DeviceTransportKind.Tcp,
        _ => DeviceTransportKind.Unknown,
    };

    private static string ProtocolToken(DeviceProtocolKind protocol) => protocol switch
    {
        DeviceProtocolKind.Adb => "adb",
        DeviceProtocolKind.Hdc => "hdc",
        DeviceProtocolKind.Fastboot => "fastboot",
        DeviceProtocolKind.Edl => "edl",
        DeviceProtocolKind.Rkdl => "rkdl",
        DeviceProtocolKind.Mtk => "mtk",
        _ => string.Empty
    };

    internal static string? PhysicalUsbId(UsbDeviceInfo info, string? serial)
        => UsbPhysicalIdentity.Of(info, serial);

    internal static string? FindServerUsbIdentity(IReadOnlyList<UsbDeviceInfo> infos, string serial,
        DeviceProtocolKind protocol)
        => UsbPhysicalIdentity.ServerPhysicalId(infos, serial, ProtocolToken(protocol));

    internal static IReadOnlyList<IReadOnlyList<DetectedDevice>> GroupPhysicalDevices(
        IEnumerable<DetectedDevice> endpoints)
        => [.. endpoints.GroupBy(device => device.GroupId, StringComparer.Ordinal).Select(group => OrderGroupLeader([.. group]))];

    /// <summary>
    /// Puts the row carrying a dialable address first so a merged group defaults to the endpoint the
    /// server lists directly; discovery order is kept when there is no alias.
    /// <para>把带可拨号地址的行放到组首，使合并组默认使用 server 直接列出的端点；没有别名时保持发现顺序。</para>
    /// </summary>
    /// <param name="group">The device rows sharing one physical identity. <para>属于同一物理身份的设备行。</para></param>
    /// <returns>The reordered device rows. <para>重排后的设备行。</para></returns>
    private static IReadOnlyList<DetectedDevice> OrderGroupLeader(IReadOnlyList<DetectedDevice> group)
    {
        var alias = group.FirstOrDefault(device => device.NetworkAlias is not null)?.NetworkAlias;
        return alias is null || group[0].Serial == alias
            ? group
            : [.. group.OrderBy(device => device.Serial == alias ? 0 : 1)];
    }

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
        return [.. byKey.Values];
    }

    private static int EndpointStateRank(string state) => state switch
    {
        "device" or "online" or "fastboot" or "loader" => 2,
        "unauthorized" => 1,
        _ => 0
    };

    private string? ReadFastbootSerial(UsbDeviceInfo info)
    {
        var serial = _controller.ReadFastbootSerial(info);
        if (serial is null)
        {
            LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
                $"Fastboot serial unavailable for {info.DeviceKey}.");
        }
        return serial;
    }

    /// <summary>
    /// Manual force refresh: reruns the full scan and broadcasts the change.
    /// <para>手动强制刷新：重新执行全量扫描并广播变更。</para>
    /// </summary>
    public Task ForceRefreshAsync()
    {
        LogService.Instance.Write(LogLevel.Info, "DeviceMonitor", "Manual device refresh requested.");
        ScheduleChange();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Finds a device from the current scan result by serial; returns <c>null</c> when absent.
    /// <para>按序列号查找当前扫描结果中的设备；找不到时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="serial">The device serial to look up. <para>要查找的设备序列号。</para></param>
    /// <returns>The matching device, or <see langword="null"/> when absent. <para>匹配的设备；缺失时为 <see langword="null"/>。</para></returns>
    public DetectedDevice? FindDeviceBySerial(string serial)
    {
        var matches = Scan().Where(d => d.Serial == serial).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Device serial is ambiguous; select a physical device explicitly.");
        return matches.FirstOrDefault();
    }

    /// <summary>
    /// Maps a USB device to a protocol kind by interface class and VID/PID.
    /// <para>按接口类与 VID/PID 把 USB 设备映射为协议类型。</para>
    /// </summary>
    /// <param name="info">The USB device information to classify. <para>要分类的 USB 设备信息。</para></param>
    /// <returns>The protocol kind, or <see langword="null"/> when unclassified. <para>协议类型；无法分类时为 <see langword="null"/>。</para></returns>
    private static DeviceProtocolKind? Classify(UsbDeviceInfo info)
        => UsbProtocolClassifier.Classify(info) switch
        {
            "adb" => DeviceProtocolKind.Adb,
            "hdc" => DeviceProtocolKind.Hdc,
            "fastboot" => DeviceProtocolKind.Fastboot,
            "edl" => DeviceProtocolKind.Edl,
            "rkdl" => DeviceProtocolKind.Rkdl,
            "mtk" => DeviceProtocolKind.Mtk,
            _ => null
        };

    private static string RkdlState(UsbDeviceInfo info)
        => RkdlConstants.KnownLoaderProductIds.Contains(info.ProductId) ? "loader" : "maskrom";

    private static string MtkState(UsbDeviceInfo info)
        => MtkModeClassifier.Classify(info).Mode switch
        {
            MtkUsbMode.BootRom => "brom",
            MtkUsbMode.Preloader => "preloader",
            MtkUsbMode.DaRuntime => "da",
            MtkUsbMode.LoggingOnly => "logging",
            MtkUsbMode.AgentlessAdb => "adb",
            _ => string.Empty,
        };

}
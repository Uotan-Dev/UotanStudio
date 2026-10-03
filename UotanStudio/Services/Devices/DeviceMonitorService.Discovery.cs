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
    /// <remarks>
    /// <para>
    /// adb 为自己经 mDNS 自动建立的传输按 DNS-SD 实例名命名，因此同一台设备还会以该名字解析出的
    /// <c>host:port</c> 再列一次；两行都带上这个端点作为别名，随后并入同一张卡。
    /// </para>
    /// <para>
    /// adb keys a transport it auto-connected through mDNS by the DNS-SD instance name, so the same device
    /// is listed again under the <c>host:port</c> that name resolves to; both rows carry that endpoint as
    /// their alias and merge into one card.
    /// </para>
    /// </remarks>
    private IReadOnlyList<DetectedDevice> ScanCore()
    {
        var devices = new List<DetectedDevice>();
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
    /// <para>把一条 adb server 目标投影为设备行；<paramref name="networkAlias"/> 是 server 为其解析出的
    /// 网络端点，用于把同一设备的多条传输并入一张卡。</para>
    /// Projects one adb server target into a device row; <paramref name="networkAlias"/> is the network
    /// endpoint the server resolved for it, used to fold several transports of one device into a card.
    /// </summary>
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
    /// <para>把 server 上报的链路文本映射为传输类型；缺少链路证据时返回 <c>Unknown</c>，不按序列号形状猜测
    /// USB。adb server 只在真实 USB 传输的行上打印 <c>usb:</c> 描述符。</para>
    /// Maps the server-reported link text to a transport kind; returns <c>Unknown</c> without link evidence
    /// instead of guessing USB from the serial shape, because the adb server prints the <c>usb:</c>
    /// descriptor only on genuine USB transport lines.
    /// </summary>
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
        => UotanStudio.Comm.Devices.Discovery.UsbPhysicalIdentity.Of(info, serial);

    internal static string? FindServerUsbIdentity(IReadOnlyList<UsbDeviceInfo> infos, string serial,
        DeviceProtocolKind protocol)
        => UotanStudio.Comm.Devices.Discovery.UsbPhysicalIdentity.ServerPhysicalId(infos, serial, ProtocolToken(protocol));

    internal static IReadOnlyList<IReadOnlyList<DetectedDevice>> GroupPhysicalDevices(
        IEnumerable<DetectedDevice> endpoints)
        => endpoints.GroupBy(device => device.GroupId, StringComparer.Ordinal)
            .Select(group => OrderGroupLeader(group.ToArray())).ToArray();

    /// <summary>
    /// <para>把可拨号地址那一行放到组首，使合并后的卡片默认使用 server 直接列出的 <c>host:port</c> 端点，
    /// 而不是按实例名拨号的重复传输；无别名时保持发现顺序。</para>
    /// Puts the dial-address row first so a merged card defaults to the <c>host:port</c> endpoint the
    /// server lists directly rather than the instance-name duplicate; discovery order is kept without
    /// an alias.
    /// </summary>
    private static IReadOnlyList<DetectedDevice> OrderGroupLeader(IReadOnlyList<DetectedDevice> group)
    {
        var alias = group.FirstOrDefault(device => device.NetworkAlias is not null)?.NetworkAlias;
        return alias is null || group[0].Serial == alias
            ? group
            : group.OrderBy(device => device.Serial == alias ? 0 : 1).ToArray();
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
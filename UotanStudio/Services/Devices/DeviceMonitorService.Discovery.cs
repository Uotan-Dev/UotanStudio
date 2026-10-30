using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    private const int SlowScanMilliseconds = 400;
    private static readonly TimeSpan ScanLockWait = TimeSpan.FromMilliseconds(250);
    private readonly Lock _scanLock = new();
    private volatile IReadOnlyList<DetectedDevice>? _lastScan;
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

        if (!_scanLock.TryEnter(ScanLockWait))
        {
            var previous = _lastScan;
            LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
                $"Scan skipped after waiting {ScanLockWait.TotalMilliseconds:F0} ms: another scan is still running; " +
                $"returning the previous {previous?.Count ?? 0} device(s) instead of blocking the caller.");
            return previous ?? [];
        }

        try
        {
            cached = _cachedScan;
            if (cached is not null)
            {
                return cached;
            }

            var version = Interlocked.Read(ref _scanVersion);
            var scanWatch = System.Diagnostics.Stopwatch.StartNew();
            var devices = ScanCore();
            scanWatch.Stop();
            _lastScan = devices;
            if (scanWatch.ElapsedMilliseconds >= SlowScanMilliseconds)
            {
                LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                    $"Full scan took {scanWatch.ElapsedMilliseconds} ms for {devices.Count} device(s); callers waiting on the " +
                    "scan lock stall for that long.");
            }
            if (version == Interlocked.Read(ref _scanVersion))
            {
                _cachedScan = devices;
            }

            return devices;
        }
        finally
        {
            _scanLock.Exit();
        }
    }

    /// <summary>
    /// Performs the actual full enumeration and classification.
    /// <para>执行实际的全量枚举与分类。</para>
    /// </summary>
    /// <returns>The classified device list with same-endpoint entries coalesced. <para>分类后的设备列表，同端点条目已合并。</para></returns>
    private IReadOnlyList<DetectedDevice> ScanCore()
    {
        List<DetectedDevice> devices = [];
        var refreshWatch = System.Diagnostics.Stopwatch.StartNew();
        var endpoints = _controller.RefreshAsync().GetAwaiter().GetResult().Endpoints;
        refreshWatch.Stop();
        if (refreshWatch.ElapsedMilliseconds >= SlowScanMilliseconds)
        {
            LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                $"Controller refresh took {refreshWatch.ElapsedMilliseconds} ms for {endpoints.Length} endpoint(s).");
        }
        var usbInfos = endpoints.Where(endpoint => endpoint.UsbInfo is not null)
            .Select(endpoint => endpoint.UsbInfo!).ToArray();
        var adbDevices = endpoints.Where(endpoint => endpoint.Protocol == "adb" &&
            endpoint.Route == "server").ToArray();
        var hdcTargets = endpoints.Where(endpoint => endpoint.Protocol == "hdc" &&
            endpoint.Route == "server").ToArray();
        ReconcileManualNetworkTargets(adbDevices);
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
            var hdcTransport = MapServerTransport(target.TargetTransport);
            if (hdcTransport == DeviceTransportKind.Unknown) continue;
            devices.Add(new DetectedDevice
            {
                Key = "hdc:" + target.Address,
                Serial = target.Address,
                PhysicalId = hdcTransport == DeviceTransportKind.Usb
                    ? FindServerUsbIdentity(usbInfos, target.Address, DeviceProtocolKind.Hdc) : null,
                Protocol = DeviceProtocolKind.Hdc,
                Transport = hdcTransport,
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

        foreach (var target in ManualNetworkTargets.OrderByDescending(IsManualNetworkTargetVerified))
        {
            if (devices.Any(entry => string.Equals(entry.Serial, target, StringComparison.Ordinal)) ||
                HasAdbNetworkHost(devices, target))
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
                State = ManualNetworkTargetState(IsManualNetworkTargetVerified(target)),
            });
        }

        foreach (var target in ManualHdcTargets)
        {
            if (devices.Any(entry => string.Equals(entry.Serial, target, StringComparison.Ordinal)))
            {
                continue;
            }

            devices.Add(new DetectedDevice
            {
                Key = "hdc-tcp:" + target,
                Serial = target,
                NetworkAlias = resolvedEndpoints.Contains(target) ? target : null,
                Protocol = DeviceProtocolKind.Hdc,
                Transport = DeviceTransportKind.Tcp,
                State = ManualNetworkTargetState(IsManualHdcTargetVerified(target)),
            });
        }

        foreach (var target in ManualFastbootTargets)
        {
            if (devices.Any(entry => string.Equals(entry.Serial, target, StringComparison.Ordinal) &&
                    entry.Protocol == DeviceProtocolKind.Fastboot &&
                    entry.Transport == DeviceTransportKind.Tcp))
            {
                continue;
            }

            devices.Add(new DetectedDevice
            {
                Key = "fastboot-tcp:" + target,
                Serial = target,
                Protocol = DeviceProtocolKind.Fastboot,
                Transport = DeviceTransportKind.Tcp,
                State = ManualNetworkTargetState(IsManualFastbootTargetVerified(target)),
            });
        }

        foreach (var target in ManualFastbootUdpTargets)
        {
            if (devices.Any(entry => string.Equals(entry.Serial, target, StringComparison.Ordinal) &&
                    entry.Protocol == DeviceProtocolKind.Fastboot &&
                    entry.Transport == DeviceTransportKind.Udp))
            {
                continue;
            }

            devices.Add(new DetectedDevice
            {
                Key = "fastboot-udp:" + target,
                Serial = target,
                Protocol = DeviceProtocolKind.Fastboot,
                Transport = DeviceTransportKind.Udp,
                State = ManualNetworkTargetState(IsManualFastbootUdpTargetVerified(target)),
            });
        }

        _edlConnections.RetainDevices(devices.Where(device => device.Protocol == DeviceProtocolKind.Edl)
            .Select(device => device.Key).ToHashSet(StringComparer.Ordinal));
        return CoalesceSameEndpoint(devices);
    }

    internal static bool IsSupportedHdcTarget(HdcTargetInfo target)
        => MapServerTransport(target.Transport) is not DeviceTransportKind.Unknown;

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
    /// Maps server-reported link text to a transport kind, matching the label case-insensitively and
    /// returning Unknown only for a link class this host does not serve.
    /// <para>把 server 上报的链路文本映射为传输类型；标签按不区分大小写匹配，只有本宿主不支持的链路类别才返回 Unknown。</para>
    /// </summary>
    /// <param name="targetTransport">The server-reported link text. <para>server 上报的链路文本。</para></param>
    /// <returns>The transport kind for the link text. <para>该链路文本对应的传输类型。</para></returns>
    internal static DeviceTransportKind MapServerTransport(string? targetTransport) =>
        targetTransport?.Trim().ToUpperInvariant() switch
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

    /// <summary>
    /// Reports whether a live ADB network endpoint already uses the same device host as
    /// <paramref name="target"/>, so a remembered direct endpoint never becomes a second row for a phone the
    /// server (or another transport) already lists at its current port. Offline rows do not count: when the
    /// server gave up on the device, the remembered endpoint is the one worth showing.
    /// <para>判断已有可用的 ADB 网络端点是否与 <paramref name="target"/> 属于同一设备主机，使已记住的直连端点不会为
    /// server（或另一条传输）已按当前端口列出的手机再生成一行。离线行不计入：server 已放弃该设备时，真正值得显示的是
    /// 已记住的端点。</para>
    /// </summary>
    /// <param name="devices">The rows enumerated so far. <para>目前已枚举出的行。</para></param>
    /// <param name="target">The remembered <c>host:port</c> value. <para>已记住的 <c>host:port</c> 值。</para></param>
    /// <returns><see langword="true"/> when another ADB TCP row uses that host.
    /// <para>已有另一条 ADB TCP 行使用该主机时为 <see langword="true"/>。</para></returns>
    internal static bool HasAdbNetworkHost(IReadOnlyList<DetectedDevice> devices, string target)
    {
        if (!DebugBridgeNetworkAddress.TrySplitEndpoint(target, out var host, out _))
        {
            return false;
        }

        foreach (var device in devices)
        {
            if (device.Protocol != DeviceProtocolKind.Adb || device.Transport != DeviceTransportKind.Tcp ||
                string.Equals(device.State, "offline", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (DebugBridgeNetworkAddress.TrySplitEndpoint(device.Serial, out var serialHost, out _) &&
                string.Equals(serialHost, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (DebugBridgeNetworkAddress.TrySplitEndpoint(device.NetworkAlias, out var aliasHost, out _) &&
                string.Equals(aliasHost, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
            "apple" => DeviceProtocolKind.Ios,
            _ => null
        };

    /// <summary>
    /// Labels the Rockchip boot ROM stage from the device descriptor's <c>bcdUSB</c> bit, falling back to <c>bootrom</c>
    /// when the backend could not read that bit.
    /// <para>依据设备描述符的 <c>bcdUSB</c> 位标注瑞芯微 boot ROM 阶段；后端读不到该位时退回 <c>bootrom</c>。</para>
    /// </summary>
    /// <param name="info">The enumerated USB device. <para>已枚举的 USB 设备。</para></param>
    /// <returns>The stage label shown in the device list. <para>设备列表中显示阶段标签。</para></returns>
    private static string RkdlState(UsbDeviceInfo info)
        => RkdlConstants.ClassifyMode(info.ProductId, info.BcdUsb) switch
        {
            RkdlDeviceMode.Maskrom => "maskrom",
            RkdlDeviceMode.Loader => "loader",
            _ => "bootrom",
        };

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
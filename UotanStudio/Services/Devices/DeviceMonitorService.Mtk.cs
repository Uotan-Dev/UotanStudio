using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    private readonly MtkConnectionCache _mtkConnections;

    /// <summary>
    /// Identities captured during automatic MTK handshakes, kept after the transient port closes so the details
    /// page can display what was already read without another handshake on a vanished endpoint.
    /// <para>自动 MTK 握手期间捕获的身份；短暂端口消失后仍保留，使详情页无需在已消失端点上再次握手即可展示
    /// 已经读到的数据。</para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MtkDeviceInfo> _mtkHandshakeIdentities =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Ensures the lease-generation liveness probe is installed before a parked MTK lease is reused, so a
    /// stored lease is only consumed while its transport generation still matches the current one.
    /// <para>在复用暂存 MTK 租约之前安装租约代际判活探针，使已存放的租约仅在其传输代际与当前代际一致时被消费。</para>
    /// </summary>
    private void EnsureMtkLivenessProbe() => _mtkConnections.LeaseCurrentProbe ??= IsMtkLeaseGenerationCurrent;

    private bool IsMtkLeaseGenerationCurrent(DeviceLease lease) =>
        lease.Generation is not { } generation
        || lease.Endpoint.UsbInfo is not { } info
        || PhysicalDeviceIdentity.From(info) is not { } identity
        || !_controller.TryPeekTransportGeneration(identity, out var current)
        || current == generation;

    /// <summary>
    /// Handshakes the endpoint immediately, reusing the lease the automatic probe parked with a completed
    /// boot handshake; a preloader/BROM port only accepts the complemented handshake for a short window after
    /// enumeration, so this entry point exists for callers that must not pay the window cost again.
    /// <para>立即握手该端点，复用自动探测暂存的已完成 boot 握手的租约；preloader/BROM 端口仅在枚举后的
    /// 短窗口内接受反码握手，因此为不应再次支付窗口成本的调用方保留该入口。</para>
    /// </summary>
    /// <param name="device">The MTK endpoint to handshake. <para>要握手的 MTK 端点。</para></param>
    /// <param name="cancellationToken">The cancellation token. <para>取消令牌。</para></param>
    /// <returns>The lease holding the handshaken session. <para>持有已完成握手会话的租约。</para></returns>
    internal Task<DeviceLease> HandshakeMtkAsync(DetectedDevice device, CancellationToken cancellationToken = default)
    {
        EnsureMtkLivenessProbe();
        return _mtkConnections.HandshakeAsync(device, cancellationToken);
    }

    /// <summary>
    /// Returns the MTK identity already captured by the automatic handshake, or <see langword="null"/> when no
    /// live handshaken session covers this endpoint. Reading it exchanges no bytes with the device.
    /// <para>返回自动握手已捕获的 MTK 身份；没有覆盖该端点的在线握手会话时返回 <see langword="null"/>。
    /// 该读取不与设备交换任何字节。</para>
    /// </summary>
    internal MtkDeviceInfo? GetMtkHandshakeInfo(DetectedDevice device)
    {
        if (_mtkHandshakeIdentities.TryGetValue(device.Key, out var captured))
        {
            return captured;
        }

        // A startup handshake is keyed by the bare COM port while the device row uses its USB key. Bridge them
        // through the PnP-reported VID/PID of that port, matching the same evidence the startup scan used.
        // 启动握手以裸 COM 口为键，而设备行使用 USB 键。通过 PnP 上报的该端口 VID/PID 桥接，
        // 与启动扫描采用相同的证据。
        if (OperatingSystem.IsWindows() && device.VendorId != 0)
        {
            try
            {
                foreach (var port in _controller.EnumerateFastBootPorts())
                {
                    if (port.VendorId == device.VendorId && port.ProductId == device.ProductId &&
                        _mtkHandshakeIdentities.TryGetValue(port.PortName, out var fromPort))
                    {
                        _mtkHandshakeIdentities[device.Key] = fromPort;
                        return fromPort;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
                    $"MTK identity port match failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (_mtkConnections.GetParkedDeviceInfo(device) is { } parked)
        {
            _mtkHandshakeIdentities[device.Key] = parked;
            return parked;
        }

        return null;
    }

    /// <summary>
    /// Handles an OS arrival path: when it names a MediaTek download port, handshakes it immediately over that
    /// exact path. This runs before any inventory pass, which is the only way to meet a preloader's few-second
    /// window; the handler must therefore return at once and let the work run in the background.
    /// <para>处理操作系统到达路径：当它指向联发科下载端口时，立即在该精确路径上握手。本处理早于任何清单遍历，
    /// 是赶上 preloader 数秒窗口的唯一方式；因此处理程序必须立即返回，让工作在后台运行。</para>
    /// </summary>
    private void OnUsbArrivalPath(string devicePath)
    {
        // The canonical Comm classifier is the only vendor/protocol authority. A path that names no known
        // boot protocol stays untouched for its own discovery path instead of being forced into MTK.
        // 通用 Comm 分类器是厂商与协议的唯一判定入口；不指向已知启动协议的路径保持原样，
        // 交给自己的发现路径，而不会被强推成 MTK。
        var token = UsbProtocolClassifier.ClassifyPath(devicePath);
        var protocol = ProtocolKindFromToken(token);
        if (protocol is null)
        {
            return;
        }

        LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
            $"{protocol.Value} arrival path observed: '{devicePath}'.");
        _ = _arrivalPipeline.Submit(new DeviceArrival(
            devicePath,
            protocol,
            ArrivalQuality.ArrivalPath,
            NeedsHandshake(protocol.Value),
            null,
            devicePath));
    }

    private async Task HandshakeArrivalPathInBackgroundAsync(string devicePath)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(AutomaticMtkHandshakeTimeoutSeconds));
            var lease = await _controller.HandshakeMtkArrivalPathAsync(devicePath, AutomaticMtkBaudRate, budget.Token)
                .ConfigureAwait(false);
            if (lease is not null)
            {
                await _mtkConnections.ParkArrivalLeaseAsync(lease, devicePath, budget.Token)
                    .ConfigureAwait(false);
                LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                    $"Automatic MTK handshake completed on arrival path '{devicePath}' and parked for reuse.");
                return;
            }

            LogService.Instance.Write(LogLevel.Warn, "DeviceMonitor",
                $"MTK arrival path '{devicePath}' answered neither VCOM nor USB.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogService.Instance.Write(LogLevel.Warn, "DeviceMonitor",
                $"MTK arrival handshake on '{devicePath}' failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_scheduledMtkHandshakesGate)
            {
                _scheduledMtkHandshakes.Remove(devicePath);
            }
        }
    }
    private FastComPortProbe? _comPortProbe;

    /// <summary>
    /// Starts the serial-port watcher, which handshakes a VCOM preloader the moment its port appears. This is the
    /// only watcher that can meet the few-second window: a full USB inventory walk takes longer than the window.
    /// <para>启动串口监视器，在 VCOM preloader 端口出现的瞬间握手。这是唯一能赶上数秒窗口的监视方式：
    /// 整棵 USB 清单遍历比该窗口更慢。</para>
    /// </summary>
    private void StartFastComPortProbe()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_comPortProbe is not null)
            {
                return;
            }
        }

        var probe = new FastComPortProbe(
            OnComPortArrived,
            ex => LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
                $"MTK COM port probe callback failed: {ex.GetType().Name}: {ex.Message}"));
        lock (_sync)
        {
            if (_disposed)
            {
                probe.Dispose();
                return;
            }

            _comPortProbe = probe;
        }

        probe.Start();
        LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
            "Fast boot COM port watcher started; a classified boot port is submitted as soon as it appears.");
    }

    /// <summary>
    /// Handshakes MediaTek boot VCOM ports that were already present when the monitor started. Vendor VCOM
    /// bindings hide these endpoints from the USB inventory, so the PnP hardware ids are the only way to find
    /// them; unrelated serial devices are never probed.
    /// <para>握手监视器启动时已存在的联发科启动 VCOM 端口。厂商 VCOM 绑定使这些端点不出现在 USB 清单中，
    /// 因此 PnP 硬件 ID 是唯一的识别途径；无关串口设备绝不探测。</para>
    /// </summary>
    private void SubmitAlreadyPresentBootPorts()
    {
        IReadOnlyList<UotanStudio.Comm.Transports.Usb.Backend.Windows.FastBootPort> bootPorts;
        try
        {
            bootPorts = _controller.EnumerateFastBootPorts();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
                $"Fast boot port enumeration failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (var port in bootPorts)
        {
            var protocol = ProtocolKindFromToken(port.Protocol);
            if (protocol is null || !NeedsHandshake(protocol.Value))
            {
                continue;
            }

            LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                $"{protocol.Value} boot port '{port.PortName}' ({port.VendorId:X4}:{port.ProductId:X4}) was already present at startup; submitting handshake.");
            _ = _arrivalPipeline.Submit(new DeviceArrival(
                port.PortName, protocol, ArrivalQuality.ComPort, true, null, port.PortName));
        }
    }

    /// <summary>
    /// Handshakes one newly appeared COM port. Ports that do not answer the boot handshake are simply left alone:
    /// a Bluetooth serial port is a legitimate COM device on this machine and must not be treated as a failure.
    /// <para>对刚出现的 COM 口执行握手。不应答 boot 握手的端口会被直接放过：蓝牙串口在本机是合法的 COM 设备，
    /// 不得当作失败。</para>
    /// </summary>
    private void OnComPortArrived(string port)
    {
        OnComPortArrivedForPipeline(port);
    }

    /// <summary>
    /// Handshakes one newly appeared COM port and parks the lease under its port name. Ports that do not answer
    /// the boot handshake are simply left alone: a Bluetooth serial port is a legitimate COM device on this
    /// machine and must not be treated as a failure. Disposing a completed handshake instead of parking it would
    /// burn the preloader's few-second window for every later consumer.
    /// <para>对刚出现的 COM 口执行握手，并按端口名暂存租约。不应答 boot 握手的端口会被直接放过：蓝牙串口在
    /// 本机是合法的 COM 设备，不得当作失败。若握手完成后直接释放而不暂存，就会为后续所有消费方烧掉
    /// preloader 的数秒窗口。</para>
    /// </summary>
    private async Task HandshakeComPortInBackgroundAsync(string port)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(AutomaticMtkHandshakeTimeoutSeconds));
            var lease = await _mtkConnections.HandshakePortAsync(port,
                token => _controller.HandshakeMtkSerialPortAsync(port, AutomaticMtkBaudRate, token),
                budget.Token).ConfigureAwait(false);
            if (lease is not null)
            {
                LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                    $"MTK VCOM handshake completed on '{port}' and parked for reuse.");
                return;
            }

            LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
                $"COM port '{port}' was skipped: another handshake owns it.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor",
                $"COM port '{port}' did not answer the MTK boot handshake: {ex.GetType().Name}: {ex.Message}");
        }
    }
    /// <summary>
    /// Starts a boot handshake as soon as a USB topology <c>Added</c> event names an MTK BROM/preloader port.
    /// The event carries the fresh descriptors, so this path does not wait for the debounced full scan that a
    /// short-lived preloader cannot survive.
    /// <para>USB 拓扑 <c>Added</c> 事件一报告 MTK BROM/preloader 端口就立刻发起 boot 握手。事件携带最新描述符，
    /// 因此不必等待短暂 preloader 无法存活的去抖动全量扫描。</para>
    /// </summary>
    /// <param name="info">The freshly enumerated USB device from the topology event.
    /// <para>来自拓扑事件的刚枚举出的 USB 设备。</para></param>
    private void ScheduleImmediateMtkHandshake(UsbDeviceInfo info)
    {
        if (!ShouldHandshakeArrival(info))
        {
            return;
        }

        LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
            $"MTK arrival: submitting descriptor for '{info.DeviceKey}' ({info.VendorId:X4}:{info.ProductId:X4}).");
        OnDeviceArrived(info, ArrivalQuality.Descriptor);
    }

    /// <summary>
    /// Decides whether an arriving USB device must be handshaken immediately. BROM and preloader ports (and ports
    /// whose interface metadata is not complete yet) qualify; stages that provably must not receive boot bytes do not.
    /// <para>判断到达的 USB 设备是否必须立即握手。BROM、preloader 端口（以及接口元数据尚不完整的端口）符合条件；
    /// 确证不能接收 boot 字节的阶段不符合。</para>
    /// </summary>
    internal static bool ShouldHandshakeArrival(UsbDeviceInfo info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.DeviceKey)
            || !string.Equals(UsbProtocolClassifier.Classify(info), "mtk", StringComparison.Ordinal))
        {
            return false;
        }

        var mode = MtkModeClassifier.Classify(info).Mode;
        return mode is not (MtkUsbMode.LoggingOnly or MtkUsbMode.DaRuntime or MtkUsbMode.AgentlessAdb);
    }
    /// <summary>
    /// Handshakes a just-arrived MTK device over its VCOM serial port directly from the topology descriptors,
    /// without waiting for the debounced inventory refresh that a short-lived preloader cannot survive.
    /// <para>直接用拓扑描述符在刚到达 MTK 设备的 VCOM 串口上握手，不等短暂 preloader 无法承受的去抖动清单刷新。</para>
    /// </summary>
    private async Task HandshakeArrivalInBackgroundAsync(UsbDeviceInfo info)
    {
        var deviceKey = info.DeviceKey;
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(AutomaticMtkHandshakeTimeoutSeconds));
            var device = new DetectedDevice
            {
                Key = deviceKey,
                Serial = info.SerialNumber ?? $"{info.VendorId:X4}:{info.ProductId:X4}",
                PhysicalId = null,
                Endpoint = null,
                Protocol = DeviceProtocolKind.Mtk,
                Transport = DeviceTransportKind.Usb,
                VendorId = info.VendorId,
                ProductId = info.ProductId,
            };
            // Try the VCOM port first: a serial-bound preloader has no openable USB interface at all. A WINUSB-bound
            // preloader (Class=USBDevice, Service=WINUSB) publishes no COM port, so a miss must fall back to opening
            // the USB interfaces directly rather than abandoning the handshake.
            // 先试 VCOM 口：串口绑定的 preloader 完全没有可打开的 USB 接口。WINUSB 绑定的 preloader
            // （Class=USBDevice、Service=WINUSB）不公布 COM 口，因此未命中必须回退到直接打开 USB 接口，
            // 而不是放弃握手。
            var lease = await _mtkConnections.HandshakeVcomAsync(info, device, AutomaticMtkBaudRate, budget.Token)
                .ConfigureAwait(false);
            if (lease is not null)
            {
                LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                    $"Automatic MTK VCOM handshake completed for '{deviceKey}' ({info.VendorId:X4}:{info.ProductId:X4}).");
                return;
            }

            await _mtkConnections.HandshakeAsync(device, budget.Token, info).ConfigureAwait(false);
            LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                $"Automatic MTK USB handshake completed for '{deviceKey}' ({info.VendorId:X4}:{info.ProductId:X4}).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogService.Instance.Write(LogLevel.Warn, "DeviceMonitor",
                $"Automatic MTK VCOM handshake for '{deviceKey}' failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_scheduledMtkHandshakesGate)
            {
                _scheduledMtkHandshakes.Remove(deviceKey);
            }
        }
    }

    /// <summary>
    /// Runs one automatic boot handshake in the background for an MTK BROM/preloader endpoint found by a scan.
    /// A device that was already attached when the application started never produces a topology Added event, so
    /// this path must handshake it too — over the VCOM port when the endpoint carries a USB device path, which is
    /// how a serial-bound preloader is reached.
    /// <para>为扫描发现的 MTK BROM/preloader 端点在后台执行一次自动 boot 握手。应用启动时已插入的设备不会产生
    /// 拓扑 Added 事件，因此本路径也必须握手——端点带 USB 设备路径时优先走 VCOM 串口，这正是访问串口绑定
    /// preloader 的方式。</para>
    /// </summary>
    /// <param name="device">The MTK endpoint to classify. <para>要分类的 MTK 端点。</para></param>
    private async Task HandshakeMtkInBackgroundAsync(DetectedDevice device)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(AutomaticMtkHandshakeTimeoutSeconds));
            // Prefer the VCOM port: a serial-bound preloader has no openable USB interface, and reaching it by
            // device key would first walk the whole USB inventory.
            // 优先走 VCOM 串口：串口绑定的 preloader 没有可打开的 USB 接口，按键打开会先遍历整棵 USB 清单。
            DeviceLease? lease = null;
            if (device.Endpoint?.UsbInfo is { } info)
            {
                lease = await _mtkConnections.HandshakeVcomAsync(info, device, AutomaticMtkBaudRate, budget.Token)
                    .ConfigureAwait(false);
            }

            if (lease is not null)
            {
                LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                    $"Automatic MTK VCOM handshake completed for '{device.Key}' ({device.State}).");
                return;
            }

            await _mtkConnections.HandshakeAsync(device, budget.Token).ConfigureAwait(false);
            LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                $"Automatic MTK boot handshake completed for '{device.Key}' ({device.State}).");
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogService.Instance.Write(LogLevel.Warn, "DeviceMonitor",
                $"Automatic MTK handshake for '{device.Key}' failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        lock (_scheduledMtkHandshakesGate)
        {
            _scheduledMtkHandshakes.Remove(device.Key);
        }
    }

    /// <summary>
    /// Schedules automatic handshakes for every newly discovered MTK BROM/preloader endpoint; already scheduled
    /// or handshaken endpoints are skipped by the key set.
    /// <para>为每个新发现的 MTK BROM/preloader 端点调度自动握手；已调度或已握手的端点由键集跳过。</para>
    /// </summary>
    /// <param name="devices">The devices produced by this scan. <para>本次扫描得到的设备。</para></param>
    private void ScheduleMtkHandshakes(IReadOnlyList<DetectedDevice> devices)
    {
        foreach (var device in devices)
        {
            if (device.Protocol != DeviceProtocolKind.Mtk
                || !IsBootHandshakeStage(device))
            {
                continue;
            }

            lock (_scheduledMtkHandshakesGate)
            {
                if (!_scheduledMtkHandshakes.Add(device.Key))
                {
                    continue;
                }
            }

            if (device.Endpoint?.UsbInfo is { } info)
            {
                OnDeviceArrived(info, ArrivalQuality.Inventoried);
            }
            else
            {
                _ = _arrivalPipeline.Submit(new DeviceArrival(
                    device.Key, DeviceProtocolKind.Mtk, ArrivalQuality.Inventoried,
                    NeedsHandshake(DeviceProtocolKind.Mtk), null));
            }
        }
    }

    /// <summary>
    /// Reports whether a projected MTK device sits in a stage that accepts the boot handshake. The projection's
    /// state text can be empty when interface metadata was not observed during enumeration, so the live USB
    /// descriptors are classified again instead of trusting that string.
    /// <para>报告投影出的 MTK 设备是否处于接受 boot 握手的阶段。枚举时未观测到接口元数据时投影状态文本可能为空，
    /// 因此重新分类实时 USB 描述符，而不盲信该字符串。</para>
    /// </summary>
    private static bool IsBootHandshakeStage(DetectedDevice device)
    {
        if (device.Endpoint?.UsbInfo is { } info)
        {
            var mode = MtkModeClassifier.Classify(info).Mode;
            return mode is not (MtkUsbMode.LoggingOnly or MtkUsbMode.DaRuntime or MtkUsbMode.AgentlessAdb);
        }

        return device.State is "brom" or "preloader";
    }
    /// <summary>
    /// Forgets scheduled handshakes whose endpoint disappeared, so a reconnected preloader port is handshaken
    /// again instead of inheriting the scheduling state of a previous attachment.
    /// <para>忘记端点已消失的握手记录，使重新连接的 preloader 端口再次被握手，而不是继承上一次连接的计划状态。</para>
    /// </summary>
    /// <param name="devices">The devices produced by this scan. <para>本次扫描得到的设备。</para></param>
    private void ForgetRemovedMtkHandshakes(IReadOnlyList<DetectedDevice> devices)
    {
        var live = devices.Where(device => device.Protocol == DeviceProtocolKind.Mtk)
            .Select(device => device.Key)
            .ToHashSet(StringComparer.Ordinal);
        lock (_scheduledMtkHandshakesGate)
        {
            _scheduledMtkHandshakes.RemoveWhere(key => !live.Contains(key));
        }
    }
}
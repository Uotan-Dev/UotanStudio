using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    private IDisposable? _usbMonitor;
    private Timer? _debounce;
    private CancellationTokenSource? _debugBridgeMonitorLifetime;

    private async Task MonitorDebugBridgeTargetsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        string? previous = null;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var adb = await ReadAdbSnapshotAsync(ct).ConfigureAwait(false);
                var hdc = await ReadHdcSnapshotAsync(ct).ConfigureAwait(false);
                var snapshot = adb + "\nHDC\n" + hdc;
                if (snapshot == previous)
                {
                    continue;
                }
                previous = snapshot;
                lock (_sync)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    ScheduleChange();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static async Task<string> ReadAdbSnapshotAsync(CancellationToken ct)
    {
        try
        {
            if (!DebugBridgeRoute.AdbServerAvailable)
            {
                return "absent";
            }
            var targets = ListAdbServerTargets();
            return "listening:" + string.Join("\n", targets.OrderBy(t => t.Serial, StringComparer.Ordinal)
                .Select(t => $"{t.Serial}:{t.State}"));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return "unavailable";
        }
    }

    private static async Task<string> ReadHdcSnapshotAsync(CancellationToken ct)
    {
        try
        {
            if (!DebugBridgeRoute.UseHdcServer())
            {
                return "absent";
            }
            using var server = new HdcServerClient();
            var response = await server.ExecuteAsync(string.Empty, "list targets -v", 1500, ct).ConfigureAwait(false);
            return "listening:" + System.Text.Encoding.UTF8.GetString(response);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return "unavailable";
        }
    }
    private volatile bool _started;
    private volatile IReadOnlyList<DetectedDevice>? _cachedScan;
    private readonly Lock _sync = new();

    /// <summary>
    /// <para>设备列表发生变化（热插拔、adb server 变更或强制刷新完成）时触发。</para>
    /// Raised when the device list changed (hotplug, adb-server change or completed force refresh).
    /// </summary>
    public event EventHandler? DevicesChanged;

    private DeviceMonitorService()
    {
        _controller = UotanStudio.Comm.Devices.Services.DeviceControllerFactory.CreateDefault();
        _edlConnections = new EdlConnectionCache((device, token) => Task.Run(
            () => LeaseEndpoint(device, sessionKind: UotanStudio.Comm.Contracts.DeviceSessionKind.Edl,
                cancellationToken: token), token), ScheduleChange);
        RestorePairedNetworkTargets();
    }

    /// <summary>
    /// <para>启动服务：立即执行一次全量扫描，并开始监听 USB 热插拔与 adb server 设备变更。
    /// 幂等，可安全多次调用。</para>
    /// Starts the service: performs one full scan immediately and begins monitoring USB
    /// hotplug and adb-server device changes. Idempotent.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _debugBridgeMonitorLifetime = new CancellationTokenSource();
            _ = MonitorDebugBridgeTargetsAsync(_debugBridgeMonitorLifetime.Token);
        }

        LogService.Instance.Write(LogLevel.Info, "DeviceMonitor", "Device monitor starting (initial full USB scan).");

        _usbMonitor = _controller.WatchUsb(changes =>
        {
            LogDeviceChanges(changes);
            if (changes.Any(static c => IsRelevantDevice(c.Device)))
            {
                ScheduleChange();
            }
        }, onError: _ => { });

    }

    /// <summary>
    /// <para>将 USB 设备增删/变更写入统一日志。与工具相关的设备记录为 Info；
    /// 无关设备（摄像头、键鼠等）仅记录 Debug，避免日志刷屏。</para>
    /// Writes USB device add/remove/change events into the unified log. Relevant
    /// devices are logged at Info; irrelevant ones (webcams, HID, etc.) at Debug
    /// to avoid log spam.
    /// </summary>
    private static void LogDeviceChanges(IReadOnlyList<UsbDeviceChange> changes)
    {
        foreach (var change in changes)
        {
            var d = change.Device;
            var identity = $"{d.VendorId:X4}:{d.ProductId:X4}" +
                (string.IsNullOrEmpty(d.SerialNumber) ? string.Empty : $" ({d.SerialNumber})");
            var level = IsRelevantDevice(d) ? LogLevel.Info : LogLevel.Debug;
            switch (change.Kind)
            {
                case UsbDeviceChangeKind.Added:
                    LogService.Instance.Write(level, "DeviceMonitor", $"USB device added: {identity}");
                    break;
                case UsbDeviceChangeKind.Removed:
                    LogService.Instance.Write(level, "DeviceMonitor", $"USB device removed: {identity}");
                    break;
                case UsbDeviceChangeKind.Changed:
                    LogService.Instance.Write(LogLevel.Debug, "DeviceMonitor", $"USB device changed: {identity}");
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// <para>判断 USB 设备是否与本工具相关（ADB/Fastboot/EDL/RKDL）。不相关设备的
    /// 变化（如复合摄像头接口节点的动态插拔）不会触发设备列表重扫。</para>
    /// Determines whether a USB device is relevant to the tool (ADB/Fastboot/EDL/RKDL).
    /// Changes of irrelevant devices (e.g. dynamic interface churn of composite webcams)
    /// do not trigger a device-list rescan.
    /// </summary>
    private static bool IsRelevantDevice(UsbDeviceInfo info) => Classify(info) is not null;

    /// <summary>
    /// <para>调度一次去抖动的设备列表刷新通知。</para>
    /// Schedules a debounced device-list change notification.
    /// </summary>
    private void ScheduleChange()
    {
        lock (_sync)
        {
            _cachedScan = null;
            _debounce ??= new Timer(_ => DevicesChanged?.Invoke(this, EventArgs.Empty), null, 300, Timeout.Infinite);
            _debounce.Change(300, Timeout.Infinite);
        }
    }

    /// <summary>
    /// <para>停止监视并释放资源，包括设备控制器自身的移除监视、server 监视与未完成的租约。</para>
    /// Stops monitoring and releases resources, including the device controller's own removal watch,
    /// server-lifecycle watch and outstanding leases.
    /// </summary>
    public void Dispose()
    {
        _edlConnections.Dispose();
        lock (_sync)
        {
            _usbMonitor?.Dispose();
            _usbMonitor = null;
            _debounce?.Dispose();
            _debounce = null;
            _debugBridgeMonitorLifetime?.Cancel();
            _debugBridgeMonitorLifetime?.Dispose();
            _debugBridgeMonitorLifetime = null;
            _started = false;
        }
        _controller.Dispose();
    }
}
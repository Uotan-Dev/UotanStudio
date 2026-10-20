using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    private static readonly TimeSpan DebugBridgeMonitorShutdownTimeout = TimeSpan.FromSeconds(2);

    private Action<IReadOnlyList<UsbDeviceChange>>? _usbTopologyHandler;
    private Timer? _debounce;
    private CancellationTokenSource? _debugBridgeMonitorLifetime;
    private Task? _debugBridgeMonitorTask;
    private volatile bool _disposed;
    private long _scanVersion;

    private async Task MonitorDebugBridgeTargetsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        string? previous = null;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var adb = ReadAdbSnapshot(ct);
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
        catch (ObjectDisposedException) { }
    }

    private static string ReadAdbSnapshot(CancellationToken ct)
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
    /// Raised when the device list changed (hotplug, adb-server change or completed force refresh).
    /// <para>设备列表发生变化（热插拔、adb server 变更或强制刷新完成）时触发。</para>
    /// </summary>
    public event EventHandler? DevicesChanged;

    private DeviceMonitorService()
    {
        _controller = Comm.Devices.Services.DeviceControllerFactory.CreateDefault();
        _edlConnections = new EdlConnectionCache((device, token) => Task.Run(
            () => LeaseEndpoint(device, sessionKind: Comm.Contracts.DeviceSessionKind.Edl,
                cancellationToken: token), token), ScheduleChange);
        RestorePairedNetworkTargets();
    }

    /// <summary>
    /// Starts the service: performs one full scan immediately and begins monitoring USB
    /// hotplug and adb-server device changes. Idempotent.
    /// <para>启动服务：立即执行一次全量扫描，并开始监听 USB 热插拔与 adb server 设备变更。
    /// 幂等，可安全多次调用。</para>
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _debugBridgeMonitorLifetime = new CancellationTokenSource();
            _debugBridgeMonitorTask = MonitorDebugBridgeTargetsAsync(_debugBridgeMonitorLifetime.Token);
        }

        LogService.Instance.Write(LogLevel.Info, "DeviceMonitor", "Device monitor starting (initial full USB scan).");

        Action<IReadOnlyList<UsbDeviceChange>> handler = changes =>
        {
            LogDeviceChanges(changes);
            if (changes.Any(static c => IsRelevantDevice(c.Device)))
            {
                ScheduleChange();
            }
        };
        _controller.UsbTopologyChanged += handler;
        lock (_sync)
        {
            if (_disposed)
            {
                _controller.UsbTopologyChanged -= handler;
                return;
            }

            _usbTopologyHandler = handler;
        }
    }

    /// <summary>
    /// Writes USB device add, remove and change events into the unified log, logging relevant
    /// devices at Info and the rest at Debug.
    /// <para>把 USB 设备的增删与变更写入统一日志，相关设备记为 Info，其余记为 Debug。</para>
    /// </summary>
    /// <param name="changes">The device change events to log. <para>要记录的变更事件。</para></param>
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
    /// Determines whether a USB device classifies to a supported protocol such as ADB, Fastboot,
    /// EDL or RKDL.
    /// <para>判断 USB 设备能否归类为受支持的协议，例如 ADB、Fastboot、EDL 或 RKDL。</para>
    /// </summary>
    /// <param name="info">The USB device information to test. <para>要判断的 USB 设备信息。</para></param>
    /// <returns>True when the device classifies to a supported protocol. <para>设备能归类为受支持协议时为 true。</para></returns>
    private static bool IsRelevantDevice(UsbDeviceInfo info) => Classify(info) is not null;

    /// <summary>
    /// Schedules a debounced device-list change notification.
    /// <para>调度一次去抖动的设备列表刷新通知。</para>
    /// </summary>
    private void ScheduleChange()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _scanVersion);
            _cachedScan = null;
            _debounce ??= new Timer(_ => RaiseDevicesChanged(), null, 300, Timeout.Infinite);
            _debounce.Change(300, Timeout.Infinite);
        }
    }

    private void RaiseDevicesChanged()
    {
        if (_disposed)
        {
            return;
        }

        var handlers = DevicesChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LogService.Instance.Write(LogLevel.Error, "DeviceMonitor",
                    $"A DevicesChanged subscriber failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stops monitoring and releases resources, including the device controller's own removal watch,
    /// server-lifecycle watch and outstanding leases.
    /// <para>停止监视并释放资源，包括设备控制器自身的移除监视、server 监视与未完成的租约。</para>
    /// </summary>
    public void Dispose()
    {
        Task? monitorTask;
        CancellationTokenSource? monitorLifetime;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_usbTopologyHandler is not null)
            {
                _controller.UsbTopologyChanged -= _usbTopologyHandler;
                _usbTopologyHandler = null;
            }

            _debounce?.Dispose();
            _debounce = null;
            monitorTask = _debugBridgeMonitorTask;
            _debugBridgeMonitorTask = null;
            monitorLifetime = _debugBridgeMonitorLifetime;
            _debugBridgeMonitorLifetime = null;
            _started = false;
        }

        monitorLifetime?.Cancel();
        if (monitorTask is not null)
        {
            try
            {
                monitorTask.WaitAsync(DebugBridgeMonitorShutdownTimeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                LogService.Instance.Write(LogLevel.Warn, "DeviceMonitor",
                    "The debug bridge monitor did not stop within the shutdown timeout.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                LogService.Instance.Write(LogLevel.Warn, "DeviceMonitor",
                    $"The debug bridge monitor faulted during shutdown: {ex.GetType().Name}: {ex.Message}");
            }
        }

        monitorLifetime?.Dispose();
        _edlConnections.Dispose();
        _controller.Dispose();
    }
}
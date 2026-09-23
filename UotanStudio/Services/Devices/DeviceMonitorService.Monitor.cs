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
    private IDisposable? _usbMonitor;
    private DeviceMonitor? _adbMonitor;
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
                // Poll both servers even when they were absent at startup. A listening server
                // with no targets still changes USB ownership and must invalidate discovery.
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
            if (!await DeviceCommunicationConfiguration.ProbeAdbServerAsync(ct).ConfigureAwait(false))
            {
                return "absent";
            }
            var server = new AdbClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var devices = await server.GetDevicesAsync(timeout.Token).ConfigureAwait(false);
            return "listening:" + string.Join("\n", devices.OrderBy(d => d.Serial, StringComparer.Ordinal)
                .Select(d => $"{d.Serial}:{d.State}"));
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
            if (!await DeviceCommunicationConfiguration.ProbeHdcServerAsync(ct).ConfigureAwait(false))
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
        _controller = new UotanStudio.Comm.Devices.Services.DeviceController(
            [
                new UotanStudio.Comm.Devices.Discovery.UsbDiscoverySource(_comm, UotanStudio.Comm.Transports.Usb.Abstractions.UsbApiKind.Auto),
                new UotanStudio.Comm.Devices.Discovery.AdbServerDiscoverySource(),
                new UotanStudio.Comm.Devices.Discovery.HdcServerDiscoverySource()
            ],
            new UotanStudio.Comm.Devices.Services.CommDeviceTransportOpener(_comm));
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

        // USB 热插拔监视（Windows 走 WM_DEVICECHANGE 原生事件，其他平台回退轮询快照）。
        // 仅当变化涉及 ADB/Fastboot/EDL/RKDL 相关设备时才触发重扫：摄像头、键鼠等与
        // 本工具无关的设备（如 0C45:6362）在总线上频繁插拔/接口重枚举时，不应反复触发
        // 全量扫描与列表刷新。
        // USB hotplug monitoring (native WM_DEVICECHANGE on Windows, polling snapshot
        // elsewhere). Only changes of ADB/Fastboot/EDL/RKDL-relevant devices trigger a
        // rescan: irrelevant devices such as webcams (e.g. 0C45:6362) that churn on the
        // bus / re-enumerate interfaces must not repeatedly trigger full scans.
        _usbMonitor = _comm.MonitorUsbDevices(changes =>
        {
            LogDeviceChanges(changes);
            if (changes.Any(static c => IsRelevantDevice(c.Device)))
            {
                ScheduleChange();
            }
        }, onError: _ => { });

        // adb server 设备变更监视（server 未运行时静默跳过，不拉起外部进程）。
        try
        {
            var client = ProbeAdbServer(); // 探测 5037 端口是否可达（短缓存）
            if (client is null)
            {
                LogService.Instance.Write(LogLevel.Info, "DeviceMonitor", "Official adb server not running; USB-direct sources only.");
                return;
            }
            var monitor = new DeviceMonitor(new AdbSocket(client.EndPoint));
            monitor.DeviceChanged += (_, _) =>
            {
                // adb server 通道的设备变更（连接/断开/状态切换）明确写入全局日志，
                // 与 USB 热插拔日志区分来源，便于排查设备发现链路。
                // Log adb-server channel device changes (connect/disconnect/state) explicitly
                // so they are distinguishable from USB hotplug events when tracing discovery.
                LogService.Instance.Write(LogLevel.Info, "DeviceMonitor",
                    "adb server channel device change detected; rescanning device list.");
                ScheduleChange();
            };
            monitor.Start();
            _adbMonitor = monitor;
            LogService.Instance.Write(LogLevel.Info, "DeviceMonitor", "Official adb server detected; network devices will be merged.");
        }
        catch
        {
            // 官方 adb server 未运行：网络设备源不可用，USB 直连不受影响。
            LogService.Instance.Write(LogLevel.Info, "DeviceMonitor", "Official adb server not running; USB-direct sources only.");
        }
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
            // 设备列表即将变化：失效扫描缓存，使下一次 Scan() 重新枚举。
            // The device list is about to change: invalidate the scan cache so the next
            // Scan() re-enumerates.
            _cachedScan = null;
            _debounce ??= new Timer(_ => DevicesChanged?.Invoke(this, EventArgs.Empty), null, 300, Timeout.Infinite);
            _debounce.Change(300, Timeout.Infinite);
        }
    }

    /// <summary>
    /// <para>停止监视并释放资源。</para>
    /// Stops monitoring and releases resources.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _usbMonitor?.Dispose();
            _usbMonitor = null;
            _adbMonitor?.Dispose();
            _adbMonitor = null;
            _debounce?.Dispose();
            _debounce = null;
            _debugBridgeMonitorLifetime?.Cancel();
            _debugBridgeMonitorLifetime?.Dispose();
            _debugBridgeMonitorLifetime = null;
            HdcDirectSessionPool.Clear();
            _started = false;
        }
    }
}

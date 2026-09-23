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

/// <summary>
/// <para>设备传输类型。</para>
/// Device transport kind.
/// </summary>
public enum DeviceTransportKind
{
    /// <summary>Transport not recognized by this client.</summary>
    Unknown = -1,
    /// <summary>USB 直连。 Direct USB.</summary>
    Usb,
    /// <summary>网络（adb server / TCP）。 Network (adb server / TCP).</summary>
    Tcp,
    /// <summary>串口。 Serial port.</summary>
    Serial,
}

/// <summary>
/// <para>设备协议类型。</para>
/// Device protocol kind.
/// </summary>
public enum DeviceProtocolKind
{
    /// <summary>ADB（system/recovery/sideload）。 ADB mode.</summary>
    Adb,
    /// <summary>Fastboot（bootloader/fastbootd）。 Fastboot mode.</summary>
    Fastboot,
    /// <summary>高通 EDL（9008）。 Qualcomm EDL.</summary>
    Edl,
    /// <summary>瑞芯微 bootrom/loader（rockusb）。 Rockchip bootrom/loader.</summary>
    Rkdl,
    /// <summary>OpenHarmony HDC（USB 直连或经本机 hdc server）。 OpenHarmony HDC (USB direct or via the local hdc server).</summary>
    Hdc,
}

/// <summary>
/// <para>由设备监视服务枚举出的一台设备。</para>
/// A single device enumerated by the device monitor service.
/// </summary>
public sealed class DetectedDevice
{
    /// <summary>稳定键（USB DeviceKey 或 "tcp:host:port"）。 Stable key.</summary>
    public required string Key { get; init; }
    /// <summary>序列号或显示标识。 Serial or display identity.</summary>
    public required string Serial { get; init; }
    /// <summary>协议类型。 Protocol kind.</summary>
    public required DeviceProtocolKind Protocol { get; init; }
    /// <summary>传输类型。 Transport kind.</summary>
    public required DeviceTransportKind Transport { get; init; }
    /// <summary>Verified physical identity shared by protocol endpoints, or null when unavailable.</summary>
    public string? PhysicalId { get; init; }
    /// <summary>状态文本（device/offline/fastboot/edl/maskrom/loader…）。 State text.</summary>
    public string State { get; set; } = string.Empty;
    /// <summary>USB 厂商 ID（非 USB 为 0）。 USB vendor ID.</summary>
    public ushort VendorId { get; init; }
    /// <summary>USB 产品 ID（非 USB 为 0）。 USB product ID.</summary>
    public ushort ProductId { get; init; }
    /// <summary>显示名称。 Display name.</summary>
    public string DisplayName => Transport == DeviceTransportKind.Tcp
        ? $"{Serial} (network)"
        : $"{Serial} ({State})";

    /// <summary>Connection choice shown when one physical device exposes several protocols.</summary>
    public string ConnectionLabel => $"{Protocol} · {Transport} · {Serial}";
}

/// <summary>
/// <para>设备监视服务：启动时全量扫描 USB 设备，随后响应 USB 热插拔与 adb server 设备变更，
/// 并提供手动强制刷新。设备枚举与协议会话完全基于 UotanStudio.Comm；与 Google 官方
/// adb server 的交互（网络设备、状态查询、重启）通过 AdvancedSharpAdbClient 完成。</para>
/// Device monitor service: performs a full USB scan on startup, then reacts to USB
/// hotplug events and adb-server device changes, plus a manual force-refresh. Enumeration
/// and protocol sessions go through UotanStudio.Comm; interaction with the official Google
/// adb server (network devices, state queries, reboots) uses AdvancedSharpAdbClient.
/// </summary>
public sealed partial class DeviceMonitorService : IDisposable
{
    /// <summary>
    /// <para>单例实例。</para>
    /// Singleton instance.
    /// </summary>
    public static DeviceMonitorService Instance { get; } = new();

    private readonly CommKit _comm = CommKit.Default;
    private static HdcDeviceService HdcOperations => HdcDeviceService.FromSettings();

    /// <summary>Runs a command on the selected HDC target.</summary>
    public Task<string> HdcShellAsync(DetectedDevice device, string command, CancellationToken cancellationToken = default)
        => HdcOperations.ShellAsync(device, command, cancellationToken);

    /// <summary>Reads a parameter from the selected HDC target.</summary>
    public Task<string> HdcPropertyAsync(DetectedDevice device, string name, CancellationToken cancellationToken = default)
        => HdcOperations.GetPropertyAsync(device, name, cancellationToken);

    /// <summary>Sends a file to the selected HDC target.</summary>
    public Task<string> HdcSendFileAsync(DetectedDevice device, string localPath, string remotePath, CancellationToken cancellationToken = default)
        => HdcOperations.SendFileAsync(device, localPath, remotePath, cancellationToken);

    /// <summary>Receives a file from the selected HDC target.</summary>
    public Task<string> HdcReceiveFileAsync(DetectedDevice device, string remotePath, string localPath, CancellationToken cancellationToken = default)
        => HdcOperations.ReceiveFileAsync(device, remotePath, localPath, cancellationToken);

    /// <summary>Installs a package on the selected HDC target.</summary>
    public Task<string> HdcInstallAsync(DetectedDevice device, string localPath, CancellationToken cancellationToken = default)
        => HdcOperations.InstallAsync(device, localPath, cancellationToken);

    /// <summary>Uninstalls a package from the selected HDC target.</summary>
    public Task<string> HdcUninstallAsync(DetectedDevice device, string bundle, CancellationToken cancellationToken = default)
        => HdcOperations.UninstallAsync(device, bundle, cancellationToken);

    /// <summary>Disconnects the selected HDC network target.</summary>
    public Task<string> HdcDisconnectAsync(DetectedDevice device, CancellationToken cancellationToken = default)
        => HdcOperations.DisconnectAsync(device, cancellationToken);

    /// <summary>Enumerates serial endpoints without assigning an unverified device protocol.</summary>
    public IReadOnlyList<string> EnumerateSerialPorts()
        => SerialTransport.GetAvailablePorts().OrderBy(port => port, StringComparer.Ordinal).ToArray();

    internal static void RequireSerialTarget(SerialConnectionOptions selected, IReadOnlyList<string> ports)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentException.ThrowIfNullOrWhiteSpace(selected.PortName);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!ports.Any(port => string.Equals(port, selected.PortName, comparison)))
            throw new IOException("The selected serial target is no longer present. Refresh the device list.");
    }

    /// <summary>Runs a preflighted Firehose bundle on the selected EDL serial target.</summary>
    public Task FlashEdlSerialAsync(SerialConnectionOptions serial, string folder, string? programmerPath,
        FirehoseConfiguration? configuration = null, bool includeUfsProvision = false,
        CancellationToken cancellationToken = default)
    {
        RequireSerialTarget(serial, EnumerateSerialPorts());
        return FlashEdlBundleService.FlashSerialCoreAsync(
            token => EdlSerialBootstrap.ConnectAsync(serial, programmerPath, configuration, token),
            folder, includeUfsProvision, cancellationToken);
    }

    /// <summary>Runs a preflighted bundle against the selected USB EDL device.</summary>
    public Task FlashEdlBundleAsync(DetectedDevice device, string folder, string programmerPath,
        CancellationToken cancellationToken = default)
        => FlashEdlBundleService.FlashAsync(device, folder, programmerPath, cancellationToken);

    /// <summary>Routes deployment and connection through the selected ADB server or native USB target.</summary>
    public ManagedMirrorSession StartMirror(DetectedDevice device,
        string jarPath, ScrcpyServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        var session = OpenAdbSession(device)
            ?? throw new InvalidOperationException($"ADB session unavailable: {device.Serial}.");
        if (session.Connection.UsesServer)
        {
            using (session)
            {
                var mirror = StartAdbServerMirror(device, jarPath, options);
                return new ManagedMirrorSession(mirror.Client, mirror.Shell, null);
            }
        }

        // A native client keeps the session alive until the presentation owner releases it.
        try
        {
            session.Connect();
            var mirror = StartAdbUsbMirror(session, jarPath, options);
            return new ManagedMirrorSession(mirror.Client, mirror.Shell, session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private (ScrcpyClient Client, Stream Shell) StartAdbServerMirror(DetectedDevice device, string jarPath,
        ScrcpyServerOptions options)
    {
        RequireAdbServerDevice(device);
        ArgumentNullException.ThrowIfNull(options);
        var server = new AdbServerServices(device.Serial, DeviceCommunicationConfiguration.ServerHost,
            DeviceCommunicationConfiguration.AdbServerPort);
        server.Push(jarPath, ScrcpyConstants.ServerDevicePath);
        var shell = server.Open("shell:" + options.BuildShellCommand());
        try { return (new ScrcpyClient(server, options), shell); }
        catch { shell.Dispose(); throw; }
    }

    /// <summary>Deploys and opens a native USB mirror stream on a manager-created session.</summary>
    private static (ScrcpyClient Client, Stream Shell) StartAdbUsbMirror(AdbSession session, string jarPath,
        ScrcpyServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        if (session.Connection.UsesServer)
            throw new ArgumentException("A native USB ADB session is required.", nameof(session));
        ScrcpyServerManager.PushServer(session, jarPath);
        var shell = new AdbServiceStream(ScrcpyServerManager.StartServer(session, options));
        try { return (new ScrcpyClient(session, options), shell); }
        catch { shell.Dispose(); throw; }
    }

    /// <summary>Opens an external shell after verifying the selected server target.</summary>
    public void OpenInteractiveShell(DetectedDevice device)
        => DeviceShellService.OpenInteractiveShell(device);

    /// <summary>Relays a selected ADB or HDC shell to the embedded terminal process.</summary>
    public async Task RunInteractiveShellAsync(DetectedDevice device, Stream input, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol == DeviceProtocolKind.Adb)
        {
            using var session = OpenAdbSession(device)
                ?? throw new IOException("The selected ADB target is unavailable.");
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            using var shell = new AdbServiceStream(session.OpenStream("shell:"))
            {
                ReadTimeout = 1000
            };
            await RelayShellAsync(shell, input, output, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            if (!UseHdcServerFor(device)) throw new IOException("HDC server is required for the interactive shell.");
            using var server = new HdcServerClient(DeviceCommunicationConfiguration.ServerHost,
                HdcServerClient.DefaultServerPort);
            var target = server.ListTargetDetails().SingleOrDefault(item =>
                string.Equals(item.ConnectKey, device.Serial, StringComparison.Ordinal));
            if (target is null || !string.Equals(target.State, "Connected", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The HDC server target is not ready.");
            await server.RunInteractiveShellAsync(device.Serial, input, output, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new ArgumentException("An ADB or HDC target is required.", nameof(device));
    }

    private static async Task RelayShellAsync(Stream shell, Stream input, Stream output, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var writer = Task.Run(async () =>
        {
            try { await input.CopyToAsync(shell, lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
        }, lifetime.Token);
        try
        {
            var buffer = new byte[8192];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count;
                try { count = await shell.ReadAsync(buffer, token).ConfigureAwait(false); }
                catch (TimeoutException) when (!token.IsCancellationRequested) { continue; }
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            shell.Dispose();
            await writer.ConfigureAwait(false);
        }
    }

    /// <summary>Stops the selected host server and refreshes the managed device list.</summary>
    public Task StopAdbServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeServerControl.StopAdbAsync(cancellationToken);

    /// <summary>Stops the selected host server and refreshes the managed device list.</summary>
    public Task StopHdcServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeServerControl.StopHdcAsync(cancellationToken);

    /// <summary>Disconnects only the selected network ADB target.</summary>
    public Task DisconnectAdbAsync(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb || device.Transport != DeviceTransportKind.Tcp)
            throw new ArgumentException("A network ADB target is required.", nameof(device));
        var client = AdbServerClient;
        if (client is null) return Task.CompletedTask;
        _ = RequireAdbServerTarget(client, device.Serial);
        return client.DisconnectAsync(device.Serial);
    }
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

    private DeviceMonitorService() { }

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

    /// <summary>
    /// <para>为设备打开 USB 设备会话（按键重开，支持模式切换后 VID/PID 变化）。</para>
    /// Opens the USB device session for the device (reopen by key, tolerating VID/PID
    /// changes across mode switches).
    /// </summary>
    public IUsbDeviceSession? OpenUsbSession(DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol == DeviceProtocolKind.Adb &&
            (device.Key.StartsWith("adb:", StringComparison.Ordinal) ||
                DebugBridgeRoute.AdbServerOwns(device.Serial)))
            throw new IOException("ADB server owns this target; direct USB access is prohibited.");
        if (device.Protocol == DeviceProtocolKind.Hdc && DebugBridgeRoute.UseHdcServer())
            throw new IOException("HDC server owns this protocol; direct USB access is prohibited.");
        var direct = ResolveDirectUsbDevice(device);
        return direct.Transport == DeviceTransportKind.Usb
            ? _comm.OpenUsbDeviceSessionByKey(direct.Key) : null;
    }

    internal static DetectedDevice ResolveDirectUsbDevice(DetectedDevice device)
    {
        if (!device.Key.StartsWith("adb:", StringComparison.Ordinal) &&
            !device.Key.StartsWith("hdc:", StringComparison.Ordinal))
        {
            return device;
        }
        var matches = CommKit.Default.EnumerateUsbDevices()
            .Where(info => Classify(info) == device.Protocol &&
                string.Equals(info.SerialNumber, device.Serial, StringComparison.Ordinal)).ToArray();
        var match = RequireUniqueUsbIdentity(matches);
        return new DetectedDevice
        {
            Key = match.DeviceKey,
            Serial = device.Serial,
            Protocol = device.Protocol,
            Transport = DeviceTransportKind.Usb,
            State = "device",
            VendorId = match.VendorId,
            ProductId = match.ProductId
        };
    }

    internal static UsbDeviceInfo RequireUniqueUsbIdentity(IReadOnlyList<UsbDeviceInfo> matches)
    {
        if (matches.Count != 1)
        {
            throw new IOException("The server target cannot be uniquely matched to a physical USB device. Refresh the device list and select the USB target.");
        }
        return matches[0];
    }

    /// <summary>Opens a server service session when a server exists, otherwise the exact USB device.</summary>
    public AdbSession? OpenAdbSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb) return null;
        if (device.Key.StartsWith("adb:", StringComparison.Ordinal))
        {
            if (!DebugBridgeRoute.UseAdbServer())
                throw new IOException("The selected ADB server target is unavailable. Refresh and select the USB target for direct access.");
            return new AdbSession(new AdbServerServices(device.Serial,
                DeviceCommunicationConfiguration.ServerHost, DeviceCommunicationConfiguration.AdbServerPort));
        }
        if (device.Transport == DeviceTransportKind.Tcp && !DebugBridgeRoute.UseAdbServer())
            throw new IOException("A network ADB target requires the ADB server.");
        return DebugBridgeRoute.OpenAdb(device.Serial, () =>
        {
            var usb = OpenUsbSession(device);
            if (usb is null) return null; // server 不存在且 USB 设备无法打开时视为不可用，而非抛异常
            try { return new AdbSession(new UsbDeviceTransport(usb)); }
            catch { usb.Dispose(); throw; }
        });
    }

    /// <summary>
    /// <para>为设备打开 Fastboot 协议会话。</para>
    /// Opens a Fastboot protocol session for the device.
    /// </summary>
    public FastbootSession? OpenFastbootSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
            throw new ArgumentException("A Fastboot target is required.", nameof(device));

        var usb = OpenUsbSession(device);
        if (usb is null) return null;
        var factory = new FastbootFactory();
        if (!factory.TryCreate(usb, out var protocolSession) || protocolSession is not FastbootSession session)
        {
            usb.Dispose();
            throw new InvalidOperationException("The device does not expose a fastboot interface.");
        }

        if (!session.IsConnected)
        {
            session.Dispose();
            throw new InvalidOperationException("Fastboot session is not connected.");
        }
        return session;
    }

    internal static bool UseHdcServerFor(DetectedDevice device, string host = DeviceCommunicationConfiguration.ServerHost, int? port = null)
    {
        if (device.Protocol != DeviceProtocolKind.Hdc || string.IsNullOrWhiteSpace(device.Serial))
            throw new ArgumentException("An HDC target with a connect key is required.", nameof(device));
        if (DebugBridgeRoute.UseHdcServer(host, port)) return true;
        if (device.Transport != DeviceTransportKind.Usb)
            throw new IOException("A non-USB HDC target requires the HDC server.");
        return false;
    }

    /// <summary>
    /// <para>为设备打开 EDL 协议会话。</para>
    /// Opens an EDL protocol session for the device.
    /// </summary>
    public EdlSession? OpenEdlSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Edl)
            throw new ArgumentException("An EDL target is required.", nameof(device));
        var usb = OpenUsbSession(device);
        return usb is null ? null : new EdlSession(new UsbDeviceTransport(usb));
    }

    /// <summary>
    /// <para>为设备打开 RKDL 协议会话。</para>
    /// Opens an RKDL protocol session for the device.
    /// </summary>
    public RkdlSession? OpenRkdlSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Rkdl)
            throw new ArgumentException("An RKDL target is required.", nameof(device));
        var usb = OpenUsbSession(device);
        return usb is null ? null : new RkdlSession(new UsbDeviceTransport(usb));
    }

    /// <summary>
    /// <para>为设备打开 HDC 协议会话：系统内存在 hdc server 时作为 client 经 server 交互
    /// （与 ADB server 模式一致），否则回退 USB 直连通信。</para>
    /// Opens an HDC protocol session for the device: when a local hdc server is present the
    /// toolbox talks to the device as a server client (mirroring the ADB server mode),
    /// otherwise it falls back to direct USB communication.
    /// </summary>
    public HdcSession? OpenHdcSession(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Hdc) return null;
        _ = UseHdcServerFor(device);
        // A host channel is required even for USB targets managed by the official server.
        // Do not feed host frames into the legacy direct-device session transport.
        return DebugBridgeRoute.OpenHdc(device.Serial, () => HdcDirectSessionPool.Acquire(device));
    }

    /// <summary>
    /// <para>获取与 Google 官方 adb server 交互的客户端；server 未运行时返回 <c>null</c>。</para>
    /// Gets a client for the official Google adb server, or <c>null</c> when it is not running.
    /// </summary>
    private AdbClient? AdbServerClient => ProbeAdbServer();

    /// <summary>Executes a shell command against the exact selected ADB server target.</summary>
    public string ExecuteAdbServerShell(DetectedDevice device, string command)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("An ADB target is required.", nameof(device));
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var client = AdbServerClient ?? throw new IOException("The ADB server is unavailable.");
        var target = RequireAdbServerTarget(client, device.Serial);
        var receiver = new ConsoleOutputReceiver();
        client.ExecuteRemoteCommand(command, target, receiver);
        return receiver.ToString() ?? string.Empty;
    }

    /// <summary>Checks whether the ADB server can manage the selected target.</summary>
    public bool HasAdbServer => AdbServerClient is not null;

    /// <summary>Requires the exact server-owned ADB target without opening a direct USB session.</summary>
    public void RequireAdbServerDevice(DetectedDevice device)
    {
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("An ADB target is required.", nameof(device));
        var client = AdbServerClient ?? throw new IOException("The ADB server is unavailable.");
        _ = RequireAdbServerTarget(client, device.Serial);
    }

    /// <summary>Probes the selected ADB target, releasing the session after the check.</summary>
    public bool ProbeAdb(DetectedDevice device)
    {
        try
        {
            using var session = OpenAdbSession(device);
            if (session is null) return false;
            session.Connect();
            return session.Connection.IsConnected;
        }
        catch { return false; }
    }

    /// <summary>Probes the selected Fastboot target and reads its product variable.</summary>
    public (bool Ready, string? Product) ProbeFastboot(DetectedDevice device)
    {
        using var session = OpenFastbootSession(device);
        if (session is null) return (false, null);
        try { return (true, session.GetVariable("product", useCache: false)); }
        catch { return (false, null); }
    }

    /// <summary>Checks whether the selected RKDL target is responsive.</summary>
    public bool ProbeRkdl(DetectedDevice device)
    {
        using var session = OpenRkdlSession(device);
        if (session is null) return false;
        try { return session.IsRockchipDevice() && session.TestDeviceReady(); }
        catch { return false; }
    }

    /// <summary>Probes on demand; server errors never grant direct USB access.</summary>
    private AdbClient? ProbeAdbServer()
    {
        if (!DebugBridgeRoute.UseAdbServer()) return null;
        var client = new AdbClient();
        _ = client.GetAdbVersion();
        return client;
    }

    internal static DeviceData RequireAdbServerTarget(AdbClient client, string serial)
        => client.GetDevices().FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal))
            ?? throw new IOException("The ADB server does not contain the selected target. Direct USB fallback is prohibited while the server is running.");

    private static DeviceData? ResolveAdbServerTarget(AdbClient? client, DetectedDevice device)
    {
        if (client is null) return null;
        var target = client.GetDevices().FirstOrDefault(d =>
            string.Equals(d.Serial, device.Serial, StringComparison.Ordinal));
        if (target is null && device.Key.StartsWith("adb:", StringComparison.Ordinal))
            throw new IOException("The selected ADB server target is no longer present.");
        return target;
    }

    /// <summary>Checks the current HDC server and propagates handshake errors.</summary>
    private HdcServerClient? ProbeHdcServer()
    {
        if (!DebugBridgeRoute.UseHdcServer()) return null;
        var client = new HdcServerClient();
        try { client.Connect(); return client; }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Reboots through the routed debug bridge session or the Fastboot transport.</summary>
    public async Task<bool> RebootAsync(DetectedDevice device, string mode, CancellationToken cancellationToken = default)
    {
        // 目标模式即 reboot:<mode> 服务中冒号之后的部分（不带 "reboot " 前缀）。
        // 若前缀传入，两种后端会拼成非法的 "reboot:reboot <mode>"，设备会回退为普通进系统重启。
        // "now" 表示普通重启进系统（与 FirmwareKit.Comm.ADB 的默认值一致）。
        // The target mode is the part after the colon of the reboot:<mode> service (no
        // "reboot " prefix). Passing the prefix yields the invalid "reboot:reboot <mode>"
        // which falls back to a plain system reboot. "now" means a plain reboot (matching
        // FirmwareKit.Comm.ADB's default).
        var adbTarget = mode switch
        {
            "bootloader" => "bootloader",
            "recovery" => "recovery",
            "edl" => "edl",
            "sideload" => "sideload",
            // fastboot 与 fastbootd 都表示用户空间 fastboot：adb reboot fastboot 进入 fastbootd。
            // Both fastboot and fastbootd mean userspace fastboot: adb reboot fastboot enters fastbootd.
            "fastboot" => "fastboot",
            "fastbootd" => "fastboot",
            _ => "now",
        };
        var fastbootTarget = mode switch
        {
            "bootloader" => "bootloader",
            "recovery" => "recovery",
            "edl" => "edl",
            // fastboot 协议下 reboot-fastboot 即重启到 fastbootd（用户空间 fastboot）。
            // Over the fastboot protocol, reboot-fastboot is the reboot into fastbootd (userspace fastboot).
            "fastboot" => "fastboot",
            "fastbootd" => "fastboot",
            _ => null,
        };

        if (device.Protocol == DeviceProtocolKind.Fastboot)
        {
            using var session = OpenFastbootSession(device);
            if (session is null)
            {
                return false;
            }

            session.Reboot(fastbootTarget);
            return true;
        }

        // Resolve ownership before sending a reboot; never retry on another transport.
        if (device.Protocol == DeviceProtocolKind.Adb)
        {
            using var session = OpenAdbSession(device);
            if (session is null) return false;
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            session.CreateDeviceServices().Reboot(adbTarget);
            return true;
        }

        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            using var hdc = OpenHdcSession(device);
            if (hdc is null)
            {
                return false;
            }

            try
            {
                hdc.Connect();
                hdc.CreateDeviceServices().Reboot(mode);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// <para>查询 ADB 设备状态（优先 adb server；不可达时通过直连握手确认状态）。</para>
    /// Queries the ADB device state (adb server preferred; direct handshake when the server connection is refused).
    /// </summary>
    public async Task<string> GetAdbStateAsync(string serial, CancellationToken cancellationToken = default)
    {
        var client = AdbServerClient;
        var data = client?.GetDevices().FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal));
        if (data is not null) return data.State.ToString().ToLowerInvariant();
        var targets = Scan().Where(d => d.Protocol == DeviceProtocolKind.Adb &&
            string.Equals(d.Serial, serial, StringComparison.Ordinal)).Take(2).ToArray();
        if (targets.Length != 1 || targets[0].Key.StartsWith("adb:", StringComparison.Ordinal))
            return "unknown";
        var target = targets[0];
        using var session = OpenAdbSession(target);
        if (session is null)
        {
            return "unknown";
        }
        await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return session.Connection.IsConnected ? "device" : "unknown";
    }

    /// <summary>
    /// Streams logcat through adb server when available, otherwise through direct USB.
    /// <para>优先通过 adb server 读取 logcat，否则使用 USB 直连；流结束或取消时完成。</para>
    /// </summary>
    public async Task StartLogcatStreamingAsync(DetectedDevice device, Action<AdbLogcatEntry> onEntry, CancellationToken cancellationToken)
    {
        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            var decoder = new AdbLogcatLineDecoder(line => onEntry(AdbLogcatEntry.Parse(line)));
            try
            {
                if (UseHdcServerFor(device))
                {
                    using var hdc = new HdcServerClient();
                    await hdc.ExecuteStreamingAsync(device.Serial, "shell hilog -v year",
                        bytes => decoder.Feed(bytes), cancellationToken).ConfigureAwait(false);
                }
                else
                    await Task.Run(() =>
                    {
                        using var session = HdcDirectSessionPool.Acquire(device);
                        session.CreateShellClient("hilog -v year").ExecuteStreaming(bytes => decoder.Feed(bytes), null, cancellationToken);
                    }, cancellationToken).ConfigureAwait(false);
            }
            finally { if (!cancellationToken.IsCancellationRequested) decoder.Feed([], flush: true); }
            return;
        }
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("Logcat requires an ADB device.", nameof(device));
        cancellationToken.ThrowIfCancellationRequested();
        var client = AdbServerClient;
        var serverDevice = ResolveAdbServerTarget(client, device);
        if (client is not null && serverDevice is not null)
        {
            var reader = new AdbLogcatReader(async (command, onLine, token) =>
            {
                var receiver = new AdvancedSharpAdbClient.Receivers.FunctionOutputReceiver(line =>
                {
                    if (token.IsCancellationRequested) return false;
                    onLine(line);
                    return true;
                });
                await client.ExecuteRemoteCommandAsync(command, serverDevice, receiver,
                    System.Text.Encoding.UTF8, token).ConfigureAwait(false);
            });
            await reader.RunAsync(onEntry, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var session = OpenAdbSession(device)
            ?? throw new InvalidOperationException("Unable to open an ADB session for the device.");
        await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var directReader = new AdbLogcatReader(async (command, onLine, token) =>
        {
            var stdout = new AdbLogcatLineDecoder(onLine);
            var stderr = new AdbLogcatLineDecoder(onLine);
            try
            {
                var shell = session.CreateShellClient(command);
                var exitCode = await Task.Run(() => shell.ExecuteStreaming(
                    buffer => stdout.Feed(buffer), buffer => stderr.Feed(buffer), token), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (exitCode != 0) throw new InvalidOperationException($"ADB shell exited with code {exitCode}.");
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    stdout.Feed([], flush: true);
                    stderr.Feed([], flush: true);
                }
            }
        });
        await directReader.RunAsync(onEntry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears all device logcat buffers, using adb server or direct USB.
    /// <para>通过 adb server 或 USB 直连清空设备端全部 logcat 缓冲区；失败时抛出异常。</para>
    /// </summary>
    public async Task ClearLogcatBuffersAsync(DetectedDevice device, CancellationToken cancellationToken = default)
    {
        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            await HdcDeviceService.FromSettings().ShellAsync(device, "hilog -r", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("Logcat requires an ADB device.", nameof(device));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        const string successMarker = "__UOTAN_LOGCAT_CLEARED__";
        // The legacy adb-server shell API does not expose the remote exit code.
        const string command = "logcat -b all -c && echo " + successMarker;
        var confirmed = false;
        var diagnostic = new System.Text.StringBuilder();
        void OnLine(string line)
        {
            if (line.Trim() == successMarker) confirmed = true;
            else if (diagnostic.Length < 4096)
                diagnostic.AppendLine(line[..Math.Min(line.Length, 4096 - diagnostic.Length)]);
        }

        try
        {
            token.ThrowIfCancellationRequested();
            var client = AdbServerClient;
            var serverDevice = ResolveAdbServerTarget(client, device);
            var exitCode = 0;
            if (client is not null && serverDevice is not null)
            {
                var receiver = new AdvancedSharpAdbClient.Receivers.FunctionOutputReceiver(line =>
                {
                    if (token.IsCancellationRequested) return false;
                    OnLine(line);
                    return true;
                });
                await client.ExecuteRemoteCommandAsync(command, serverDevice, receiver,
                    System.Text.Encoding.UTF8, token).ConfigureAwait(false);
            }
            else
            {
                using var session = OpenAdbSession(device)
                    ?? throw new InvalidOperationException("Unable to open an ADB session for the device.");
                await session.ConnectAsync(token).ConfigureAwait(false);
                var stdout = new AdbLogcatLineDecoder(OnLine);
                var stderr = new AdbLogcatLineDecoder(OnLine);
                var shell = session.CreateShellClient(command);
                exitCode = await Task.Run(() => shell.ExecuteStreaming(
                    buffer => stdout.Feed(buffer), buffer => stderr.Feed(buffer), token), token).ConfigureAwait(false);
                stdout.Feed([], flush: true);
                stderr.Feed([], flush: true);
            }
            token.ThrowIfCancellationRequested();
            if (!confirmed || exitCode != 0)
                throw new InvalidOperationException(diagnostic.Length > 0 ? diagnostic.ToString().Trim()
                    : $"Logcat buffer clear was not confirmed (exit code {exitCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Clearing logcat buffers timed out.");
        }
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
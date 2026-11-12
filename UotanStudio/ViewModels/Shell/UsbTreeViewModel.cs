using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using UotanStudio.Comm.Transports.Usb.Drivers;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Outcome of one USB-tree driver configuration change, which decides whether the window shows a restart
/// notice.
/// <para>USB 树一次驱动配置变更的结果，决定窗口是否弹出重启提示。</para>
/// </summary>
public enum UsbDriverChangeOutcome
{
    /// <summary>The change did not complete. <para>变更未完成。</para></summary>
    Failed,

    /// <summary>The change completed and no restart is needed. <para>变更已完成且无需重启。</para></summary>
    Completed,

    /// <summary>The change was applied but the device needs a restart or a replug.
    /// <para>变更已应用，但设备需要重启计算机或重新插拔。</para></summary>
    RestartRequired
}

/// <summary>
/// View model of the USB tree window: it reads the USB bus topology through the host device-monitor
/// service, renders node texts against the reporting backend's capabilities, and supports backend
/// selection, an Android-only filter, expand/collapse, text export and auto-refresh on device changes.
/// Reads always run on a background thread while UI state is updated on the UI thread only.
/// <para>USB 树窗口视图模型：经宿主设备监视服务读取 USB 总线拓扑，按后端能力生成节点文本，支持后端切换、
/// 只看 Android 相关、展开/折叠、文本导出与设备变更自动刷新。读取一律在后台线程执行，界面状态只在 UI 线程更新。</para>
/// </summary>
public sealed partial class UsbTreeViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// One entry of the backend selector; a <see cref="Backend"/> of <c>null</c> lets Comm prefer the
    /// platform-native backend automatically.
    /// <para>后端选择器的一项：<c>null</c> 表示由 Comm 自动优先平台原生后端。</para>
    /// </summary>
    public sealed partial class UsbTreeBackendOption : ObservableObject
    {
        private readonly Func<string> _displayFactory;
        private readonly Func<string> _descriptionFactory;

        /// <summary>The localized display text. <para>本地化的显示文本。</para></summary>
        [ObservableProperty] private string _display = string.Empty;

        /// <summary>The capability summary shown as the selector tooltip. <para>能力摘要，用作选择器提示。</para></summary>
        [ObservableProperty] private string _description = string.Empty;

        /// <summary>Creates a backend option.<para>创建后端选项。</para></summary>
        /// <param name="backend">The backend this option stands for; <see langword="null"/> means automatic selection.
        /// <para>该选项代表的后端；<see langword="null"/> 表示自动选择。</para></param>
        /// <param name="displayFactory">The display-text factory.<para>显示文本工厂。</para></param>
        /// <param name="descriptionFactory">The capability-summary factory.<para>能力摘要工厂。</para></param>
        public UsbTreeBackendOption(UsbTopologyBackendKind? backend, Func<string> displayFactory,
            Func<string> descriptionFactory)
        {
            Backend = backend;
            _displayFactory = displayFactory;
            _descriptionFactory = descriptionFactory;
            RefreshTexts();
        }

        /// <summary><para>该后端的标识；自动选择时为 <see langword="null"/>。</para>
        /// The backend identity; <see langword="null"/> for automatic selection.</summary>
        public UsbTopologyBackendKind? Backend { get; }

        /// <summary><para>语言切换后重新求值显示文本与能力摘要。</para>
        /// Re-evaluates the display text and capability summary after a language change.</summary>
        internal void RefreshTexts()
        {
            Display = _displayFactory();
            Description = _descriptionFactory();
        }
    }

    private const int DebounceMilliseconds = 250;
    private const int ExpansionMemoryLimit = 4096;
    private const string TextSeparator = " · ";

    private readonly Func<UsbTopologyBackendKind?, UsbTopologyResult?> _readTopology;
    private readonly Func<UsbTopologyBackendKind, UsbTopologyCapabilities> _capabilitiesOf;
    private readonly IReadOnlyList<UsbTopologyBackendKind> _backendKinds;
    private readonly IUsbDriverConfigService _driverConfig;
    private readonly Dictionary<string, bool> _expandedById = new(StringComparer.Ordinal);
    private readonly bool _monitorsDevices;
    private readonly Lock _sync = new();
    private Timer? _debounce;
    private Task? _refreshTask;
    private UsbTopologyResult? _lastResult;
    private int _visibleCount;
    private bool _rerunRequested;
    private bool _initialized;
    private volatile bool _disposed;

    /// <summary>The roots of the currently visible tree. <para>当前可见树的根节点。</para></summary>
    public ObservableCollection<UsbTreeNodeViewModel> Roots { get; } = [];

    /// <summary><para>后端选择器条目，首项始终是自动选择。</para>
    /// The backend selector entries; the first one is always automatic selection.</summary>
    public ObservableCollection<UsbTreeBackendOption> Backends { get; } = [];

    /// <summary>The backend option selected in the view model. <para>视图模型选中的后端选项。</para></summary>
    [ObservableProperty] private UsbTreeBackendOption? _selectedBackend;

    /// <summary>The "Android related only" switch. <para>「只看 Android 相关」开关。</para></summary>
    [ObservableProperty] private bool _onlyAndroid;

    /// <summary>The node shown in the detail panel. <para>详情面板当前节点。</para></summary>
    [ObservableProperty] private UsbTreeNodeViewModel? _selectedNode;

    /// <summary>The status-bar text. <para>状态栏文本。</para></summary>
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Whether a topology read is running. <para>是否正在读取拓扑。</para></summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Whether a driver takeover or its release is in flight; the tree toolbar stays disabled meanwhile.
    /// <para>是否正在执行驱动接管或解除接管；期间树工具栏保持禁用。</para>
    /// </summary>
    [ObservableProperty] private bool _isDriverOperationRunning;

    /// <summary>
    /// Constructs the view model with the host device-monitor service as the topology source.
    /// <para>使用宿主设备监视服务作为拓扑来源构造视图模型。</para>
    /// </summary>
    /// <param name="topologySource">
    /// The injectable topology source; <see langword="null"/> reads the real system topology through
    /// <c>App.DeviceMonitor</c>.
    /// <para>可注入的拓扑来源；为 <see langword="null"/> 时经 <c>App.DeviceMonitor</c> 读取真实系统拓扑。</para>
    /// </param>
    /// <param name="driverConfig">
    /// The WinUSB driver configuration service; <see langword="null"/> uses
    /// <see cref="UsbDriverConfigService.Default"/>.
    /// <para>WinUSB 驱动配置服务；为 <see langword="null"/> 时使用
    /// <see cref="UsbDriverConfigService.Default"/>。</para>
    /// </param>
    public UsbTreeViewModel(
        Func<UsbTopologyBackendKind?, UsbTopologyResult?>? topologySource = null,
        IUsbDriverConfigService? driverConfig = null)
        : this(monitorDeviceChanges: true, topologySource, driverConfig: driverConfig)
    {
    }

    /// <summary>
    /// Constructs the view model with every source and subscription switch.
    /// <para>构造视图模型的所有来源与订阅开关。</para>
    /// </summary>
    /// <param name="monitorDeviceChanges">
    /// Whether to subscribe to the host device monitor and start hotplug monitoring. Headless tests pass
    /// <see langword="false"/> so they never touch real USB.
    /// <para>是否订阅宿主设备监视器的变更事件并启动热插拔监听。无头测试置为 <see langword="false"/>，
    /// 从而绝不接触真实 USB。</para>
    /// </param>
    /// <param name="topologySource">
    /// The topology source; <see langword="null"/> uses the host service.
    /// <para>拓扑来源；为 <see langword="null"/> 时使用宿主服务。</para>
    /// </param>
    /// <param name="backends">
    /// The backend candidates; <see langword="null"/> uses the host-reported available list. When a list is
    /// supplied, <paramref name="capabilitiesSource"/> has to describe every backend in it, including
    /// backends that cannot run here.
    /// <para>后端候选列表；为 <see langword="null"/> 时使用宿主报告的可用列表。传入的列表若包含本机无法
    /// 运行的后端，<paramref name="capabilitiesSource"/> 必须仍能描述它。</para>
    /// </param>
    /// <param name="capabilitiesSource">
    /// The backend capability source.
    /// <para>后端能力来源。</para>
    /// </param>
    /// <param name="driverConfig">
    /// The WinUSB driver configuration service; <see langword="null"/> uses
    /// <see cref="UsbDriverConfigService.Default"/>.
    /// <para>WinUSB 驱动配置服务；为 <see langword="null"/> 时使用
    /// <see cref="UsbDriverConfigService.Default"/>。</para>
    /// </param>
    internal UsbTreeViewModel(bool monitorDeviceChanges,
        Func<UsbTopologyBackendKind?, UsbTopologyResult?>? topologySource = null,
        IReadOnlyList<UsbTopologyBackendKind>? backends = null,
        Func<UsbTopologyBackendKind, UsbTopologyCapabilities>? capabilitiesSource = null,
        IUsbDriverConfigService? driverConfig = null)
    {
        _monitorsDevices = monitorDeviceChanges;
        _backendKinds = backends ?? DeviceMonitorService.TopologyBackends;
        _readTopology = topologySource ?? (backend => DeviceMonitorService.IsUsbTopologySupported
            ? App.DeviceMonitor.ReadUsbTopology(backend)
            : null);
        _capabilitiesOf = capabilitiesSource ?? DeviceMonitorService.TopologyCapabilities;
        _driverConfig = driverConfig ?? UsbDriverConfigService.Default;

        BuildBackendOptions();
        StatusText = Localized(IsTopologySupported ? "UsbTree_Ready" : "UsbTree_NoBackend");
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
        if (_monitorsDevices)
        {
            App.DeviceMonitor.DevicesChanged += OnDevicesChanged;
            App.DeviceMonitor.Start();
        }

        _initialized = true;
    }

    /// <summary><para>是否存在可用的 USB 拓扑后端；为 <see langword="false"/> 时界面必须显示不支持而非空树。</para>
    /// Whether any USB topology backend is available; when <see langword="false"/> the UI must state the
    /// platform is unsupported instead of showing an empty tree.</summary>
    public bool IsTopologySupported => _backendKinds.Count > 0;

    /// <summary>The message shown when no backend is available. <para>不支持时的提示文本。</para></summary>
    public string UnsupportedMessage => Localized("UsbTree_NoBackend");

    /// <summary>Whether a node is selected. <para>是否已选中一个节点。</para></summary>
    public bool HasSelection => SelectedNode is not null;

    /// <summary>
    /// Reads the topology once. While a read is in flight the existing read is reused and a rerun request
    /// is recorded, so a plug storm cannot stack scans.
    /// <para>读取一次拓扑。已有读取在飞行中时复用该次读取并登记重跑请求，避免插拔风暴叠加扫描。</para>
    /// </summary>
    /// <returns>
    /// The task completing the read.
    /// <para>读取完成的任务。</para>
    /// </returns>
    public Task RefreshAsync()
    {
        if (_disposed || IsDriverOperationRunning)
        {
            return Task.CompletedTask;
        }

        if (_refreshTask is { IsCompleted: false })
        {
            _rerunRequested = true;
            return _refreshTask;
        }

        return _refreshTask = RefreshLoopAsync();
    }

    /// <summary>
    /// Exports the currently visible tree as text, including driver and interface diagnostics.
    /// <para>把当前可见树导出为文本，包含驱动与接口诊断信息。</para>
    /// </summary>
    /// <returns>
    /// The exported text, or an empty string when nothing has been read yet.
    /// <para>导出文本；尚未成功读取时为空字符串。</para>
    /// </returns>
    public string BuildTextExport()
    {
        if (_lastResult is not { } result)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine(JoinNonEmpty(Localized("UsbTree_Backend"),
            result.Capabilities.Backend.ToString(), BuildSummary()));
        AppendExportNodes(builder, Roots, 0);
        return builder.ToString();
    }

    /// <summary>
    /// Configures driver-side access for the node's device: on Linux an udev access rule plus the usb-serial
    /// module block make the device node usable directly, and on Windows a freshly generated self-signed WinUSB
    /// package replaces the current driver, which is the prerequisite for opening MediaTek preloader,
    /// download-mode and other vendor-mode devices through the WinUSB path. The node must offer the action and
    /// no operation may be running on it; the configuration runs in the background, the status bar reports the
    /// outcome and the tree refreshes on success.
    /// <para>为该节点的设备配置驱动侧访问：Linux 上通过 udev 访问规则加 usb-serial 模块屏蔽使设备节点可直接
    /// 使用；Windows 上用新生成的自签名 WinUSB 包替换当前驱动，这是通过 WinUSB 路径打开联发科预加载器、下载
    /// 模式及其他厂商模式设备的前提。节点必须提供该操作且当前无进行中的操作；配置在后台执行，期间树不读取、
    /// 工具栏控件禁用，状态栏报告结果，成功后刷新树。</para>
    /// </summary>
    /// <param name="node">The tree node whose device is configured.<para>要被配置设备的树节点。</para></param>
    /// <returns>
    /// <see cref="UsbDriverChangeOutcome.RestartRequired"/> when Windows needs a restart or a replug,
    /// <see cref="UsbDriverChangeOutcome.Completed"/> when the change is already in effect, and
    /// <see cref="UsbDriverChangeOutcome.Failed"/> otherwise.
    /// <para>Windows 需要重启或重新插拔时为 <see cref="UsbDriverChangeOutcome.RestartRequired"/>；变更已生效时为
    /// <see cref="UsbDriverChangeOutcome.Completed"/>；其余情况为 <see cref="UsbDriverChangeOutcome.Failed"/>。</para>
    /// </returns>
    public async Task<UsbDriverChangeOutcome> ConfigureDeviceAsync(UsbTreeNodeViewModel node)
    {
        if (node is null || !node.ShowDriverAction || node.IsDriverBusy || IsDriverOperationRunning || _disposed)
        {
            return UsbDriverChangeOutcome.Failed;
        }

        if (OperatingSystem.IsLinux())
        {
            return await ConfigureLinuxDeviceAccessAsync(node).ConfigureAwait(true);
        }

        if (!OperatingSystem.IsWindows() || node.Node.HardwareIds.Count == 0)
        {
            return UsbDriverChangeOutcome.Failed;
        }

        SetDriverOperationRunning(node, true);
        StatusText = Localized("UsbTree_Configuring", node.DisplayText);
        var outcome = UsbDriverChangeOutcome.Failed;
        try
        {
            var request = new WinUsbTakeoverRequest
            {
                InstanceId = node.Node.InstanceId,
                HardwareId = node.Node.HardwareIds[0],
                DeviceName = DescribeTakeoverDevice(node.Node)
            };
            var result = await Task.Run(() => _driverConfig.InstallTakeover(request)).ConfigureAwait(true);
            switch (result.Status)
            {
                case WinUsbDriverOperationStatus.Succeeded:
                    StatusText = Localized("UsbTree_Configured", node.DisplayText);
                    outcome = UsbDriverChangeOutcome.Completed;
                    break;
                case WinUsbDriverOperationStatus.RestartRequired:
                    outcome = ReportRestartRequired(node, "Device configuration", result.Message);
                    break;
                case WinUsbDriverOperationStatus.ElevationRequired:
                    StatusText = Localized("UsbTree_ConfigurationElevationRequired");
                    break;
                default:
                    var detail = result.Message is { Length: > 0 } message ? message : result.Status.ToString();
                    StatusText = Localized("UsbTree_ConfigurationFailed", detail);
                    LogService.Instance.Write(LogLevel.Error, "UsbTree",
                        $"Device configuration failed: {detail}");
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText = Localized("UsbTree_ConfigurationFailed", ex.Message);
            LogService.Instance.Write(LogLevel.Error, "UsbTree",
                $"Device configuration failed: {ex.Message}");
        }
        finally
        {
            SetDriverOperationRunning(node, false);
        }

        if (outcome is UsbDriverChangeOutcome.Completed or UsbDriverChangeOutcome.RestartRequired)
        {
            await RefreshAfterDriverChangeAsync().ConfigureAwait(true);
        }

        return outcome;
    }

    /// <summary>
    /// Configures kernel-side access for the node's device on Linux through the udev access configuration: the
    /// elevated child process installs the per-device rule and the usb-serial module block, unbinds the current
    /// claimant and re-applies the rules, so the device node becomes usable without a replug.
    /// <para>在 Linux 上经 udev 访问配置为该节点的设备配置内核侧访问：提权子进程安装按设备规则与 usb-serial
    /// 模块屏蔽、解绑当前占用者并重新应用规则，使设备节点无需重新插拔即可使用。</para>
    /// </summary>
    /// <param name="node">The tree node whose device is configured.<para>要被配置设备的树节点。</para></param>
    /// <returns><see cref="UsbDriverChangeOutcome.Completed"/> when the configuration was applied.
    /// <para>配置生效时为 <see cref="UsbDriverChangeOutcome.Completed"/>。</para></returns>
    private async Task<UsbDriverChangeOutcome> ConfigureLinuxDeviceAccessAsync(UsbTreeNodeViewModel node)
    {
        if (node.Node.VendorId is not { } vendorId || node.Node.ProductId is not { } productId)
        {
            return UsbDriverChangeOutcome.Failed;
        }

        SetDriverOperationRunning(node, true);
        StatusText = Localized("UsbTree_Configuring", node.DisplayText);
        var outcome = UsbDriverChangeOutcome.Failed;
        try
        {
            var result = await Task.Run(() => LinuxUdevDeviceAccess.Configure(vendorId, productId))
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case LinuxDeviceAccessStatus.Succeeded:
                    StatusText = Localized("UsbTree_Configured", node.DisplayText);
                    outcome = UsbDriverChangeOutcome.Completed;
                    break;
                case LinuxDeviceAccessStatus.ElevationRequired:
                    StatusText = Localized("UsbTree_ConfigurationElevationRequired");
                    break;
                default:
                    var detail = result.Message is { Length: > 0 } message ? message : result.Status.ToString();
                    StatusText = Localized("UsbTree_ConfigurationFailed", detail);
                    LogService.Instance.Write(LogLevel.Error, "UsbTree",
                        $"Device configuration failed: {detail}");
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText = Localized("UsbTree_ConfigurationFailed", ex.Message);
            LogService.Instance.Write(LogLevel.Error, "UsbTree",
                $"Device configuration failed: {ex.Message}");
        }
        finally
        {
            SetDriverOperationRunning(node, false);
        }

        if (outcome is UsbDriverChangeOutcome.Completed or UsbDriverChangeOutcome.RestartRequired)
        {
            await RefreshAfterDriverChangeAsync().ConfigureAwait(true);
        }

        return outcome;
    }

    /// <summary>
    /// Unconfigures the node's device again: on Linux the udev access rule and the usb-serial module block are
    /// removed and the kernel regains its original driver behaviour, and on Windows the self-signed package and
    /// its certificate are deleted so the device falls back to a system driver. The node must offer the action
    /// and no operation may be running on it; the removal runs in the background, the status bar reports the
    /// outcome and the tree refreshes on success.
    /// <para>取消该节点设备的配置：Linux 上移除 udev 访问规则与 usb-serial 模块屏蔽，内核恢复原有驱动行为；
    /// Windows 上删除自签名包与其证书，设备回退系统驱动。节点必须提供该操作且当前无进行中的操作；移除在后台
    /// 执行，期间树不读取、工具栏控件禁用，状态栏报告结果，成功后刷新树。</para>
    /// </summary>
    /// <param name="node">The tree node whose configuration ends.<para>要结束配置的树节点。</para></param>
    /// <returns>
    /// <see cref="UsbDriverChangeOutcome.RestartRequired"/> when the package was removed but the device still
    /// runs the takeover driver, <see cref="UsbDriverChangeOutcome.Completed"/> when the binding was observed
    /// to leave it, and <see cref="UsbDriverChangeOutcome.Failed"/> otherwise.
    /// <para>驱动包已移除但设备仍运行接管驱动时为 <see cref="UsbDriverChangeOutcome.RestartRequired"/>；观测到
    /// 绑定已离开接管驱动时为 <see cref="UsbDriverChangeOutcome.Completed"/>；其余情况为
    /// <see cref="UsbDriverChangeOutcome.Failed"/>。</para>
    /// </returns>
    public async Task<UsbDriverChangeOutcome> UnconfigureDeviceAsync(UsbTreeNodeViewModel node)
    {
        if (node is null || !node.ShowDriverAction || node.IsDriverBusy || IsDriverOperationRunning || _disposed)
        {
            return UsbDriverChangeOutcome.Failed;
        }

        if (OperatingSystem.IsLinux())
        {
            return await UnconfigureLinuxDeviceAccessAsync(node).ConfigureAwait(true);
        }

        if (!OperatingSystem.IsWindows())
        {
            return UsbDriverChangeOutcome.Failed;
        }

        SetDriverOperationRunning(node, true);
        StatusText = Localized("UsbTree_Unconfiguring", node.DisplayText);
        var outcome = UsbDriverChangeOutcome.Failed;
        try
        {
            var result = await Task.Run(() => _driverConfig.UninstallTakeover(node.Node.InstanceId))
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case WinUsbDriverOperationStatus.Succeeded:
                    StatusText = Localized("UsbTree_Unconfigured", node.DisplayText);
                    outcome = UsbDriverChangeOutcome.Completed;
                    break;
                case WinUsbDriverOperationStatus.RestartRequired:
                    outcome = ReportRestartRequired(node, "Device unconfiguration", result.Message);
                    break;
                case WinUsbDriverOperationStatus.ElevationRequired:
                    StatusText = Localized("UsbTree_ConfigurationElevationRequired");
                    break;
                default:
                    var detail = result.Message is { Length: > 0 } message ? message : result.Status.ToString();
                    StatusText = Localized("UsbTree_ConfigurationFailed", detail);
                    LogService.Instance.Write(LogLevel.Error, "UsbTree",
                        $"Device unconfiguration failed: {detail}");
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText = Localized("UsbTree_ConfigurationFailed", ex.Message);
            LogService.Instance.Write(LogLevel.Error, "UsbTree",
                $"Device unconfiguration failed: {ex.Message}");
        }
        finally
        {
            SetDriverOperationRunning(node, false);
        }

        if (outcome is UsbDriverChangeOutcome.Completed or UsbDriverChangeOutcome.RestartRequired)
        {
            await RefreshAfterDriverChangeAsync().ConfigureAwait(true);
        }

        return outcome;
    }

    /// <summary>
    /// Removes the udev access configuration from the node's device on Linux: the elevated child process deletes
    /// the per-device rule and the usb-serial module block and re-applies the rules, restoring the kernel's
    /// original driver behaviour.
    /// <para>在 Linux 上移除该节点设备的 udev 访问配置：提权子进程删除按设备规则与 usb-serial 模块屏蔽并重新
    /// 应用规则，恢复内核原有的驱动行为。</para>
    /// </summary>
    /// <param name="node">The tree node whose device is unconfigured.<para>要被取消配置设备的树节点。</para></param>
    /// <returns><see cref="UsbDriverChangeOutcome.Completed"/> when the configuration was removed.
    /// <para>配置被移除时为 <see cref="UsbDriverChangeOutcome.Completed"/>。</para></returns>
    private async Task<UsbDriverChangeOutcome> UnconfigureLinuxDeviceAccessAsync(UsbTreeNodeViewModel node)
    {
        if (node.Node.VendorId is not { } vendorId || node.Node.ProductId is not { } productId)
        {
            return UsbDriverChangeOutcome.Failed;
        }

        SetDriverOperationRunning(node, true);
        StatusText = Localized("UsbTree_Unconfiguring", node.DisplayText);
        var outcome = UsbDriverChangeOutcome.Failed;
        try
        {
            var result = await Task.Run(() => LinuxUdevDeviceAccess.Unconfigure(vendorId, productId))
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case LinuxDeviceAccessStatus.Succeeded:
                    StatusText = Localized("UsbTree_Unconfigured", node.DisplayText);
                    outcome = UsbDriverChangeOutcome.Completed;
                    break;
                case LinuxDeviceAccessStatus.ElevationRequired:
                    StatusText = Localized("UsbTree_ConfigurationElevationRequired");
                    break;
                default:
                    var detail = result.Message is { Length: > 0 } message ? message : result.Status.ToString();
                    StatusText = Localized("UsbTree_ConfigurationFailed", detail);
                    LogService.Instance.Write(LogLevel.Error, "UsbTree",
                        $"Device unconfiguration failed: {detail}");
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText = Localized("UsbTree_ConfigurationFailed", ex.Message);
            LogService.Instance.Write(LogLevel.Error, "UsbTree",
                $"Device unconfiguration failed: {ex.Message}");
        }
        finally
        {
            SetDriverOperationRunning(node, false);
        }

        if (outcome is UsbDriverChangeOutcome.Completed or UsbDriverChangeOutcome.RestartRequired)
        {
            await RefreshAfterDriverChangeAsync().ConfigureAwait(true);
        }

        return outcome;
    }

    /// <summary>
    /// Reports a driver change that still needs a restart or a replug: the status bar shows localized text
    /// and the backend detail goes to the unified log.
    /// <para>上报仍需重启或重新插拔的驱动变更：状态栏显示本地化文本，后端细节写入统一日志。</para>
    /// </summary>
    /// <param name="node">The node whose device changed. <para>发生变更的设备节点。</para></param>
    /// <param name="operation">The log operation name. <para>日志中的操作名。</para></param>
    /// <param name="detail">The backend detail. <para>后端返回的细节。</para></param>
    /// <returns><see cref="UsbDriverChangeOutcome.RestartRequired"/>.
    /// <para>返回 <see cref="UsbDriverChangeOutcome.RestartRequired"/>。</para></returns>
    private UsbDriverChangeOutcome ReportRestartRequired(UsbTreeNodeViewModel node, string operation,
        string? detail)
    {
        StatusText = Localized("UsbTree_RestartRequiredStatus", node.DisplayText);
        if (detail is { Length: > 0 })
        {
            LogService.Instance.Write(LogLevel.Warn, "UsbTree", $"{operation} needs a restart: {detail}");
        }

        return UsbDriverChangeOutcome.RestartRequired;
    }

    /// <summary>
    /// Drops the host USB enumeration inventory after a driver binding changed so device discovery re-reads
    /// the interface GUIDs instead of reusing the ones cached before the takeover, then refreshes the tree.
    /// <para>驱动绑定变化后丢弃宿主 USB 枚举清单，使设备发现重读接口 GUID、不再复用接管前缓存的结果，随后刷新树。</para>
    /// </summary>
    /// <returns>A task completing when the topology refresh finished. <para>拓扑刷新完成时结束的任务。</para></returns>
    private async Task RefreshAfterDriverChangeAsync()
    {
        if (_monitorsDevices)
        {
            await App.DeviceMonitor.ForceFullRefreshAsync().ConfigureAwait(true);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Installs the bundled vendor driver package that covers the node's device: the package is staged
    /// into the driver store and PnP is asked to install it on the driverless device, which is how a
    /// Rockchip maskrom, MediaTek preloader or other vendor-mode device becomes usable. The node must
    /// offer the installation action and no operation may be running on it; the install runs in the
    /// background, the status bar reports the outcome and the tree refreshes on success.
    /// <para>安装覆盖该节点设备的捆绑厂商驱动包：包被暂存到驱动仓库，并请求 PnP 在该无驱动设备上安装，
    /// 这是瑞芯微 maskrom、联发科预加载器等厂商模式设备可用的前提。节点必须提供安装操作且当前无进行中的
    /// 操作；安装后台执行，期间树不读取、工具栏控件禁用，状态栏报告结果，成功后刷新树。</para>
    /// </summary>
    /// <param name="node">The tree node whose device receives the driver.
    /// <para>要安装驱动的树节点。</para></param>
    public async Task InstallDriverAsync(UsbTreeNodeViewModel node)
    {
        if (node is null || !node.CanInstallDriver || node.IsDriverBusy || IsDriverOperationRunning ||
            _disposed || !OperatingSystem.IsWindows() || node.MatchedDriverPackageId is not { } packageId)
        {
            return;
        }

        SetDriverOperationRunning(node, true);
        var nameKey = DriverPackageCatalog.Find(packageId)?.DisplayNameKey ?? packageId;
        var packageName = Localized($"DriverPackage_{nameKey}_Name");
        StatusText = Localized("UsbTree_DriverInstalling", packageName, node.DisplayText);
        var refreshRequired = false;
        try
        {
            var result = await Task.Run(() => _driverConfig.InstallPackage(packageId)).ConfigureAwait(true);
            switch (result.Status)
            {
                case WinUsbDriverOperationStatus.Succeeded:
                    StatusText = Localized("UsbTree_DriverInstalled", packageName, node.DisplayText);
                    refreshRequired = true;
                    break;
                case WinUsbDriverOperationStatus.ElevationRequired:
                    StatusText = Localized("UsbTree_DriverElevationRequired");
                    break;
                default:
                    var detail = result.Message is { Length: > 0 } message ? message : result.Status.ToString();
                    StatusText = Localized("UsbTree_DriverInstallFailed", detail);
                    LogService.Instance.Write(LogLevel.Error, "UsbTree",
                        $"Vendor driver package install failed: {packageId}: {detail}");
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText = Localized("UsbTree_DriverInstallFailed", ex.Message);
            LogService.Instance.Write(LogLevel.Error, "UsbTree",
                $"Vendor driver package install failed: {packageId}: {ex.Message}");
        }
        finally
        {
            SetDriverOperationRunning(node, false);
        }

        if (refreshRequired)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Device-change entry point: invoked by the <c>DeviceMonitorService.DevicesChanged</c> subscription,
    /// debounced and then marshalled back to the UI thread to start a read. Tests drive this same entry
    /// point to verify the subscription lifetime.
    /// <para>设备变更通知入口：由 <c>DeviceMonitorService.DevicesChanged</c> 订阅调用，先去抖再回到 UI 线程
    /// 发起读取。测试可直接驱动同一入口以验证订阅生命周期。</para>
    /// </summary>
    internal void NotifyDeviceChanged()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _debounce ??= new Timer(_ => Dispatcher.UIThread.Post(QueuedRefresh), null,
                Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Releases resources: unsubscribes from device and language changes and stops the debounce timer, so
    /// later notifications never read the topology again.
    /// <para>释放资源：取消设备变更、语言变更订阅并停止去抖计时器，之后的任何通知都不再读取拓扑。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        if (_monitorsDevices)
        {
            App.DeviceMonitor.DevicesChanged -= OnDevicesChanged;
        }

        lock (_sync)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    /// <summary>The refresh command. <para>刷新按钮命令。</para></summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    /// <summary>Expands every visible node. <para>展开全部节点。</para></summary>
    [RelayCommand]
    private void ExpandAll() => SetExpanded(Roots, true);

    /// <summary>Collapses every visible node. <para>折叠全部节点。</para></summary>
    [RelayCommand]
    private void CollapseAll() => SetExpanded(Roots, false);

    partial void OnSelectedBackendChanged(UsbTreeBackendOption? value)
    {
        if (_initialized && !_disposed)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnOnlyAndroidChanged(bool value)
    {
        if (_initialized)
        {
            RebuildVisibleTree();
            StatusText = _lastResult is null
                ? Localized(IsTopologySupported ? "UsbTree_Ready" : "UsbTree_NoBackend")
                : BuildSummary();
        }
    }

    partial void OnSelectedNodeChanged(UsbTreeNodeViewModel? value)
        => OnPropertyChanged(nameof(HasSelection));

    private void QueuedRefresh()
    {

        if (!_disposed)
        {
            _ = RefreshAsync();
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => NotifyDeviceChanged();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(UnsupportedMessage));
        foreach (var option in Backends)
        {
            option.RefreshTexts();
        }

        foreach (var node in Roots)
        {
            node.RefreshTextsRecursively();
        }

        StatusText = _lastResult is not null
            ? BuildSummary()
            : Localized(IsTopologySupported ? "UsbTree_Ready" : "UsbTree_NoBackend");
    }

    private void BuildBackendOptions()
    {
        Backends.Add(new UsbTreeBackendOption(null,
            () => Localized("UsbTree_BackendAuto"),
            () => Localized("UsbTree_BackendAutoDescription")));
        foreach (var kind in _backendKinds)
        {
            var backend = kind;
            Backends.Add(new UsbTreeBackendOption(backend,
                backend.ToString,
                () => DescribeCapabilities(_capabilitiesOf(backend))));
        }

        SelectedBackend = Backends[0];
    }

    private async Task RefreshLoopAsync()
    {
        do
        {
            _rerunRequested = false;
            await ReadAsync().ConfigureAwait(true);
        }
        while (_rerunRequested && !_disposed);
    }

    private async Task ReadAsync()
    {
        if (!IsTopologySupported)
        {
            _lastResult = null;
            RebuildVisibleTree();
            StatusText = Localized("UsbTree_NoBackend");
            return;
        }

        IsBusy = true;
        StatusText = Localized("UsbTree_Reading");
        try
        {
            var backend = SelectedBackend?.Backend;
            var result = await Task.Run(() => _readTopology(backend)).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            Apply(result);
        }
        catch (Exception ex)
        {
            _lastResult = null;
            RebuildVisibleTree();
            StatusText = Localized("UsbTree_ReadFailed");
            LogService.Instance.Write(LogLevel.Error, "UsbTree",
                $"USB topology read failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(UsbTopologyResult? result)
    {
        if (result is null)
        {
            _lastResult = null;
            RebuildVisibleTree();
            StatusText = Localized("UsbTree_BackendUnavailable");
            return;
        }

        _lastResult = result;
        RebuildVisibleTree();
        StatusText = BuildSummary();
    }

    private void RebuildVisibleTree()
    {
        RememberExpansion(Roots);
        var selectedId = SelectedNode?.Node.InstanceId;
        Roots.Clear();
        if (_expandedById.Count > ExpansionMemoryLimit)
        {
            _expandedById.Clear();
        }

        if (_lastResult is { } result)
        {
            var branches = OnlyAndroid
                ? UsbTopologyTree.Retain(result.Roots, IsFilteredIn)
                : result.Roots;
            foreach (var branch in branches)
            {
                Roots.Add(BuildNode(branch, result.Capabilities, 0));
            }

            _visibleCount = UsbTopologyTree.Flatten(branches).Count;
        }
        else
        {
            _visibleCount = 0;
        }

        SelectedNode = FindById(Roots, selectedId);
        RefreshDriverStates();
    }

    private UsbTreeNodeViewModel BuildNode(UsbTopologyBranch branch,
        UsbTopologyCapabilities capabilities, int depth)
    {
        var children = new UsbTreeNodeViewModel[branch.Children.Count];
        for (var index = 0; index < branch.Children.Count; index++)
        {
            children[index] = BuildNode(branch.Children[index], capabilities, depth + 1);
        }

        var node = new UsbTreeNodeViewModel(branch.Node, capabilities, children, depth,
            candidate => ConfigureDeviceAsync(candidate),
            candidate => UnconfigureDeviceAsync(candidate),
            InstallDriverAsync);
        node.ShowDriverAction = _driverConfig.IsSupported &&
            branch.Node.Kind is UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Function &&
            (capabilities.Backend == UsbTopologyBackendKind.WindowsPnp
                ? branch.Node.HardwareIds.Count > 0
                : OperatingSystem.IsLinux() && branch.Node.VendorId is not null && branch.Node.ProductId is not null) &&
            IsFilteredIn(branch.Node);
        node.ShowDriverInstallAction = _driverConfig.IsSupported &&
            capabilities.Backend == UsbTopologyBackendKind.WindowsPnp &&
            branch.Node.Kind is UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Function &&
            branch.Node.HardwareIds.Count > 0;
        node.IsDriverInstalled = capabilities.SupportsDriverInfo &&
            (branch.Node.DriverService is { Length: > 0 } || branch.Node.DriverInfPath is { Length: > 0 });
        if (_expandedById.TryGetValue(branch.Node.InstanceId, out var expanded))
        {
            node.IsExpanded = expanded;
        }

        return node;
    }

    private void RefreshDriverStates()
    {
        if (!_driverConfig.IsSupported || _disposed)
        {
            return;
        }

        List<UsbTreeNodeViewModel> takeoverNodes = [];
        List<UsbTreeNodeViewModel> installNodes = [];
        CollectDriverActionNodes(Roots, takeoverNodes, installNodes);
        if (takeoverNodes.Count == 0 && installNodes.Count == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var driverNode in takeoverNodes)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    var configured = driverNode.Node.VendorId is { } vendorId && driverNode.Node.ProductId is { } productId &&
                        LinuxUdevDeviceAccess.IsConfigured(vendorId, productId);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_disposed && !driverNode.IsDriverBusy)
                        {
                            driverNode.IsConfigured = configured;
                        }
                    });
                }

                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            foreach (var driverNode in takeoverNodes)
            {
                if (_disposed)
                {
                    return;
                }

                var takenOver = false;
                try
                {
                    takenOver = _driverConfig.QueryState(driverNode.Node.InstanceId).IsTakeoverBinding;
                }
                catch (Exception ex)
                {
                    LogService.Instance.Write(LogLevel.Error, "UsbTree",
                        $"USB driver state query failed: {ex.Message}");
                    var failure = ex.Message;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_disposed)
                        {
                            StatusText = Localized("UsbTree_DriverStateRefreshFailed", failure);
                        }
                    });
                    continue;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && !driverNode.IsDriverBusy)
                    {
                        driverNode.IsConfigured = takenOver;
                    }
                });
            }

            foreach (var installNode in installNodes)
            {
                if (_disposed || installNode.IsDriverInstalled)
                {
                    continue;
                }

                List<string> deviceIds = [.. installNode.Node.HardwareIds, .. installNode.Node.CompatibleIds];
                var matchedPackage = DriverPackageCatalog.FindMatch(deviceIds);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && !installNode.IsDriverBusy)
                    {
                        installNode.MatchedDriverPackageId = matchedPackage?.Id;
                    }
                });
            }
        });
    }

    private static void CollectDriverActionNodes(IReadOnlyList<UsbTreeNodeViewModel> nodes,
        List<UsbTreeNodeViewModel> takeoverNodes, List<UsbTreeNodeViewModel> installNodes)
    {
        foreach (var candidate in nodes)
        {
            if (candidate.ShowDriverAction)
            {
                takeoverNodes.Add(candidate);
            }

            if (candidate.ShowDriverInstallAction)
            {
                installNodes.Add(candidate);
            }

            CollectDriverActionNodes(candidate.Children, takeoverNodes, installNodes);
        }
    }

    /// <summary>
    /// Toggles the "a driver operation is in flight" state and mirrors it onto every node that offers the
    /// takeover or driver-installation action, so all of their row buttons disable at once. Reads are
    /// skipped while the state is on, because a rebuild recreates the node view models and would drop the
    /// operation's own state.
    /// <para>切换「有驱动操作进行中」状态，并同步到每个提供接管或驱动安装操作的节点，使其行按钮同时禁用。
    /// 该状态开启时跳过读取，因为重建会重创节点视图模型并丢失操作自身的状态。</para>
    /// </summary>
    /// <param name="node">The node the operation runs on.<para>操作所在的节点。</para></param>
    /// <param name="running">Whether the operation is in flight.<para>操作是否在进行中。</para></param>
    private void SetDriverOperationRunning(UsbTreeNodeViewModel node, bool running)
    {
        IsDriverOperationRunning = running;
        var takeoverNodes = new List<UsbTreeNodeViewModel>();
        var installNodes = new List<UsbTreeNodeViewModel>();
        CollectDriverActionNodes(Roots, takeoverNodes, installNodes);
        foreach (var candidate in takeoverNodes)
        {
            candidate.IsDriverBusy = running;
        }

        foreach (var candidate in installNodes)
        {
            candidate.IsDriverBusy = running;
        }

        node.IsDriverBusy = running;
    }

    /// <summary>
    /// The criterion the "filter mode" switch applies: the backend recognized a protocol on the node.
    /// The takeover row action is offered for exactly the nodes this criterion accepts, so a row the
    /// filter would hide never carries the button.
    /// <para>「过滤显示」开关所用的判定：后端在该节点上识别出了协议。接管行操作只对这些节点提供，
    /// 因此会被过滤隐藏的行不会带该按钮。</para>
    /// </summary>
    /// <param name="node">The node to test.<para>要判定的节点。</para></param>
    /// <returns><see langword="true"/> when the node carries a recognized protocol.
    /// <para>节点带有已识别协议时为 <see langword="true"/>。</para></returns>
    private static bool IsFilteredIn(UsbTopologyNode node) => node.Protocol is { Length: > 0 };

    /// <summary>
    /// Builds the device description the takeover INF publishes, naming the device mode the backend
    /// recognized so Device Manager shows the mode instead of a generic WinUSB entry. Unrecognized modes
    /// get no description and fall back to the hardware id.
    /// <para>构建接管 INF 发布的设备描述，写明后端识别到的设备模式，使设备管理器显示该模式而不是笼统的
    /// WinUSB 条目。未识别出模式时不提供描述，回退为硬件 ID。</para>
    /// </summary>
    /// <param name="node">The node being taken over.<para>要被接管的节点。</para></param>
    /// <returns>The description, or <see langword="null"/> when the mode is unknown.
    /// <para>设备描述；模式未知时为 <see langword="null"/>。</para></returns>
    private static string? DescribeTakeoverDevice(UsbTopologyNode node) => node.Protocol switch
    {
        "rkdl" => "UotanStudio WinUSB (Rockchip RKDL)",
        "mtk" => "UotanStudio WinUSB (MediaTek BROM)",
        "edl" => "UotanStudio WinUSB (Qualcomm EDL)",
        "fastboot" => "UotanStudio WinUSB (Fastboot)",
        "adb" => "UotanStudio WinUSB (ADB)",
        "hdc" => "UotanStudio WinUSB (HDC)",
        _ => null
    };

    private void RememberExpansion(IReadOnlyList<UsbTreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            _expandedById[node.Node.InstanceId] = node.IsExpanded;
            RememberExpansion(node.Children);
        }
    }

    private static UsbTreeNodeViewModel? FindById(IReadOnlyList<UsbTreeNodeViewModel> nodes, string? instanceId)
    {
        if (instanceId is null)
        {
            return null;
        }

        foreach (var node in nodes)
        {
            if (string.Equals(node.Node.InstanceId, instanceId, StringComparison.Ordinal))
            {
                return node;
            }

            if (FindById(node.Children, instanceId) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static void SetExpanded(IReadOnlyList<UsbTreeNodeViewModel> nodes, bool expanded)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = expanded;
            SetExpanded(node.Children, expanded);
        }
    }

    private string BuildSummary()
    {
        var elapsed = (_lastResult?.Elapsed.TotalMilliseconds ?? 0)
            .ToString("F0", CultureInfo.CurrentCulture);
        return JoinNonEmpty(Localized("UsbTree_NodeCount", _visibleCount),
            Localized("UsbTree_Elapsed", elapsed));
    }

    private static string DescribeCapabilities(UsbTopologyCapabilities capabilities)
        => JoinNonEmpty(
            Segment(capabilities.SupportsDriverInfo, "UsbTree_CapDriver_Yes", "UsbTree_CapDriver_No"),
            Segment(capabilities.SupportsInterfaceNodes, "UsbTree_CapInterfaces_Yes",
                "UsbTree_CapInterfaces_No"),
            Segment(capabilities.SupportsHostControllerNodes, "UsbTree_CapHost_Yes", "UsbTree_CapHost_No"),
            Segment(capabilities.SupportsDeviceInterfaceGuids, "UsbTree_CapGuids_Yes",
                "UsbTree_CapGuids_No"));

    private static string Segment(bool supported, string yesKey, string noKey)
        => Localized(supported ? yesKey : noKey);

    private void AppendExportNodes(StringBuilder builder, IReadOnlyList<UsbTreeNodeViewModel> nodes, int depth)
    {
        var indent = new string(' ', depth * 4);
        foreach (var node in nodes)
        {
            builder.AppendLine(JoinNonEmpty(indent + node.DisplayText, node.MetaText, node.ProtocolText));
            builder.AppendLine(JoinNonEmpty(indent + "    " + node.DiagnosticsText, node.InstanceIdText));
            AppendExportNodes(builder, node.Children, depth + 1);
        }
    }

    private static string JoinNonEmpty(params string[] values)
        => string.Join(TextSeparator, values.Where(value => value.Length > 0));

    private static string Localized(string key) => LanguageService.Instance.GetString(key);

    private static string Localized(string key, params object[] args)
        => LanguageService.Instance.GetString(key, args);
}
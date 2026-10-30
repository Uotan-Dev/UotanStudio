using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Drivers;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Shell;

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
        if (_disposed)
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
    /// Takes the node's device over with a freshly generated self-signed WinUSB package, which is the
    /// prerequisite for opening MediaTek preloader, download-mode and other vendor-mode devices through
    /// the WinUSB path. The node must offer the takeover action and no operation may be running on it;
    /// the takeover runs in the background, the status bar reports the outcome and the tree refreshes on
    /// success.
    /// <para>用新生成的自签名 WinUSB 包接管该节点的设备，这是通过 WinUSB 路径打开联发科预加载器、下载模式
    /// 及其他厂商模式设备的前提。节点必须提供接管操作且当前无进行中的操作；接管在后台执行，状态栏报告
    /// 结果，成功后刷新树。</para>
    /// </summary>
    /// <param name="node">The tree node whose device is taken over.<para>要被接管设备的树节点。</para></param>
    public async Task EnableTakeoverAsync(UsbTreeNodeViewModel node)
    {
        if (node is null || !node.ShowDriverAction || node.IsDriverBusy || _disposed ||
            !OperatingSystem.IsWindows() || node.Node.HardwareIds.Count == 0)
        {
            return;
        }

        node.IsDriverBusy = true;
        StatusText = Localized("UsbTree_TakeoverEnabling", node.DisplayText);
        try
        {
            var request = new WinUsbTakeoverRequest
            {
                InstanceId = node.Node.InstanceId,
                HardwareId = node.Node.HardwareIds[0]
            };
            var result = await Task.Run(() => _driverConfig.InstallTakeover(request)).ConfigureAwait(true);
            switch (result.Status)
            {
                case WinUsbDriverOperationStatus.Succeeded:
                    StatusText = Localized("UsbTree_TakeoverEnabled", node.DisplayText);
                    _ = RefreshAsync();
                    break;
                case WinUsbDriverOperationStatus.ElevationRequired:
                    StatusText = Localized("UsbTree_TakeoverElevationRequired");
                    break;
                default:
                    var detail = result.Message is { Length: > 0 } message ? message : result.Status.ToString();
                    StatusText = Localized("UsbTree_TakeoverFailed", detail);
                    LogService.Instance.Write(LogLevel.Error, "UsbTree",
                        $"WinUSB takeover failed: {detail}");
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText = Localized("UsbTree_TakeoverFailed", ex.Message);
            LogService.Instance.Write(LogLevel.Error, "UsbTree",
                $"WinUSB takeover failed: {ex.Message}");
        }
        finally
        {
            node.IsDriverBusy = false;
        }
    }

    /// <summary>
    /// Ends the takeover of the node's device: the self-signed package and its certificate are deleted and
    /// the device falls back to a system driver. The node must offer the takeover action and no operation
    /// may be running on it; the removal runs in the background, the status bar reports the outcome and
    /// the tree refreshes on success.
    /// <para>结束该节点设备的接管：删除自签名包与其证书，设备回退系统驱动。节点必须提供接管操作且当前无
    /// 进行中的操作；移除在后台执行，状态栏报告结果，成功后刷新树。</para>
    /// </summary>
    /// <param name="node">The tree node whose takeover ends.<para>要结束接管的树节点。</para></param>
    public async Task CancelTakeoverAsync(UsbTreeNodeViewModel node)
    {
        if (node is null || !node.ShowDriverAction || node.IsDriverBusy || _disposed ||
            !OperatingSystem.IsWindows())
        {
            return;
        }

        node.IsDriverBusy = true;
        StatusText = Localized("UsbTree_TakeoverDisabling", node.DisplayText);
        try
        {
            var result = await Task.Run(() => _driverConfig.UninstallTakeover(node.Node.InstanceId))
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case WinUsbDriverOperationStatus.Succeeded:
                    StatusText = Localized("UsbTree_TakeoverDisabled", node.DisplayText);
                    _ = RefreshAsync();
                    break;
                case WinUsbDriverOperationStatus.ElevationRequired:
                    StatusText = Localized("UsbTree_TakeoverElevationRequired");
                    break;
                default:
                    var detail = result.Message is { Length: > 0 } message ? message : result.Status.ToString();
                    StatusText = Localized("UsbTree_TakeoverFailed", detail);
                    LogService.Instance.Write(LogLevel.Error, "UsbTree",
                        $"Ending the WinUSB takeover failed: {detail}");
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText = Localized("UsbTree_TakeoverFailed", ex.Message);
            LogService.Instance.Write(LogLevel.Error, "UsbTree",
                $"Ending the WinUSB takeover failed: {ex.Message}");
        }
        finally
        {
            node.IsDriverBusy = false;
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
                ? UsbTopologyTree.Retain(result.Roots, static node => node.Protocol is { Length: > 0 })
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
            EnableTakeoverAsync, CancelTakeoverAsync);
        node.ShowDriverAction = _driverConfig.IsSupported &&
            capabilities.Backend == UsbTopologyBackendKind.WindowsPnp &&
            branch.Node.Kind is UsbTopologyNodeKind.Device or UsbTopologyNodeKind.Function &&
            branch.Node.HardwareIds.Count > 0;
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

        List<UsbTreeNodeViewModel> driverNodes = [];
        CollectDriverActionNodes(Roots, driverNodes);
        if (driverNodes.Count == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            foreach (var driverNode in driverNodes)
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
                        driverNode.IsTakenOver = takenOver;
                    }
                });
            }
        });
    }

    private static void CollectDriverActionNodes(IReadOnlyList<UsbTreeNodeViewModel> nodes,
        List<UsbTreeNodeViewModel> collected)
    {
        foreach (var candidate in nodes)
        {
            if (candidate.ShowDriverAction)
            {
                collected.Add(candidate);
            }

            CollectDriverActionNodes(candidate.Children, collected);
        }
    }

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
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// <para>USB 树窗口视图模型：经宿主设备监视服务读取 USB 总线拓扑，按后端能力生成节点文本，支持后端切换、
/// 只看 Android 相关、展开/折叠、文本导出与设备变更自动刷新。读取一律在后台线程执行，界面状态只在 UI 线程更新。</para>
/// View model of the USB tree window: it reads the USB bus topology through the host device-monitor
/// service, renders node texts against the reporting backend's capabilities, and supports backend
/// selection, an Android-only filter, expand/collapse, text export and auto-refresh on device changes.
/// Reads always run on a background thread while UI state is updated on the UI thread only.
/// </summary>
public sealed partial class UsbTreeViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// <para>后端选择器的一项：<c>null</c> 表示由 Comm 自动优先平台原生后端。</para>
    /// One entry of the backend selector; a <see cref="Backend"/> of <c>null</c> lets Comm prefer the
    /// platform-native backend automatically.
    /// </summary>
    public sealed partial class UsbTreeBackendOption : ObservableObject
    {
        private readonly Func<string> _displayFactory;
        private readonly Func<string> _descriptionFactory;

        /// <summary><para>本地化的显示文本。</para>The localized display text.</summary>
        [ObservableProperty] private string _display = string.Empty;

        /// <summary><para>能力摘要，用作选择器提示。</para>The capability summary shown as the selector tooltip.</summary>
        [ObservableProperty] private string _description = string.Empty;

        /// <summary>
        /// <para>创建后端选项。</para>
        /// Creates a backend option.
        /// </summary>
        /// <param name="backend">
        /// <para>该选项代表的后端；<see langword="null"/> 表示自动选择。</para>
        /// The backend this option stands for; <see langword="null"/> means automatic selection.
        /// </param>
        /// <param name="displayFactory">
        /// <para>显示文本工厂。</para>
        /// The display-text factory.
        /// </param>
        /// <param name="descriptionFactory">
        /// <para>能力摘要工厂。</para>
        /// The capability-summary factory.
        /// </param>
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

    /// <summary><para>当前可见树的根节点。</para>The roots of the currently visible tree.</summary>
    public ObservableCollection<UsbTreeNodeViewModel> Roots { get; } = [];

    /// <summary><para>后端选择器条目，首项始终是自动选择。</para>
    /// The backend selector entries; the first one is always automatic selection.</summary>
    public ObservableCollection<UsbTreeBackendOption> Backends { get; } = [];

    /// <summary><para>视图模型选中的后端选项。</para>The backend option selected in the view model.</summary>
    [ObservableProperty] private UsbTreeBackendOption? _selectedBackend;

    /// <summary><para>「只看 Android 相关」开关。</para>The "Android related only" switch.</summary>
    [ObservableProperty] private bool _onlyAndroid;

    /// <summary><para>详情面板当前节点。</para>The node shown in the detail panel.</summary>
    [ObservableProperty] private UsbTreeNodeViewModel? _selectedNode;

    /// <summary><para>状态栏文本。</para>The status-bar text.</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary><para>是否正在读取拓扑。</para>Whether a topology read is running.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// <para>使用宿主设备监视服务作为拓扑来源构造视图模型。</para>
    /// Constructs the view model with the host device-monitor service as the topology source.
    /// </summary>
    /// <param name="topologySource">
    /// <para>可注入的拓扑来源；为 <see langword="null"/> 时经 <c>App.DeviceMonitor</c> 读取真实系统拓扑。</para>
    /// The injectable topology source; <see langword="null"/> reads the real system topology through
    /// <c>App.DeviceMonitor</c>.
    /// </param>
    public UsbTreeViewModel(
        Func<UsbTopologyBackendKind?, UsbTopologyResult?>? topologySource = null)
        : this(monitorDeviceChanges: true, topologySource)
    {
    }

    /// <summary>
    /// <para>构造视图模型的所有来源与订阅开关。</para>
    /// Constructs the view model with every source and subscription switch.
    /// </summary>
    /// <param name="monitorDeviceChanges">
    /// <para>是否订阅宿主设备监视器的变更事件并启动热插拔监听。无头测试置为 <see langword="false"/>，
    /// 从而绝不接触真实 USB。</para>
    /// Whether to subscribe to the host device monitor and start hotplug monitoring. Headless tests pass
    /// <see langword="false"/> so they never touch real USB.
    /// </param>
    /// <param name="topologySource">
    /// <para>拓扑来源；为 <see langword="null"/> 时使用宿主服务。</para>
    /// The topology source; <see langword="null"/> uses the host service.
    /// </param>
    /// <param name="backends">
    /// <para>后端候选列表；为 <see langword="null"/> 时使用宿主报告的可用列表。传入的列表若包含本机无法
    /// 运行的后端，<paramref name="capabilitiesSource"/> 必须仍能描述它。</para>
    /// The backend candidates; <see langword="null"/> uses the host-reported available list. When a list is
    /// supplied, <paramref name="capabilitiesSource"/> has to describe every backend in it, including
    /// backends that cannot run here.
    /// </param>
    /// <param name="capabilitiesSource">
    /// <para>后端能力来源。</para>
    /// The backend capability source.
    /// </param>
    internal UsbTreeViewModel(bool monitorDeviceChanges,
        Func<UsbTopologyBackendKind?, UsbTopologyResult?>? topologySource = null,
        IReadOnlyList<UsbTopologyBackendKind>? backends = null,
        Func<UsbTopologyBackendKind, UsbTopologyCapabilities>? capabilitiesSource = null)
    {
        _monitorsDevices = monitorDeviceChanges;
        _backendKinds = backends ?? DeviceMonitorService.TopologyBackends;
        _readTopology = topologySource ?? (backend => DeviceMonitorService.IsUsbTopologySupported
            ? App.DeviceMonitor.ReadUsbTopology(backend)
            : null);
        _capabilitiesOf = capabilitiesSource ?? DeviceMonitorService.TopologyCapabilities;

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

    /// <summary><para>不支持时的提示文本。</para>The message shown when no backend is available.</summary>
    public string UnsupportedMessage => Localized("UsbTree_NoBackend");

    /// <summary><para>是否已选中一个节点。</para>Whether a node is selected.</summary>
    public bool HasSelection => SelectedNode is not null;

    /// <summary>
    /// <para>读取一次拓扑。已有读取在飞行中时复用该次读取并登记重跑请求，避免插拔风暴叠加扫描。</para>
    /// Reads the topology once. While a read is in flight the existing read is reused and a rerun request
    /// is recorded, so a plug storm cannot stack scans.
    /// </summary>
    /// <returns>
    /// <para>读取完成的任务。</para>
    /// The task completing the read.
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
    /// <para>把当前可见树导出为文本，包含驱动与接口诊断信息。</para>
    /// Exports the currently visible tree as text, including driver and interface diagnostics.
    /// </summary>
    /// <returns>
    /// <para>导出文本；尚未成功读取时为空字符串。</para>
    /// The exported text, or an empty string when nothing has been read yet.
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
    /// <para>设备变更通知入口：由 <c>DeviceMonitorService.DevicesChanged</c> 订阅调用，先去抖再回到 UI 线程
    /// 发起读取。测试可直接驱动同一入口以验证订阅生命周期。</para>
    /// Device-change entry point: invoked by the <c>DeviceMonitorService.DevicesChanged</c> subscription,
    /// debounced and then marshalled back to the UI thread to start a read. Tests drive this same entry
    /// point to verify the subscription lifetime.
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
    /// <para>释放资源：取消设备变更、语言变更订阅并停止去抖计时器，之后的任何通知都不再读取拓扑。</para>
    /// Releases resources: unsubscribes from device and language changes and stops the debounce timer, so
    /// later notifications never read the topology again.
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

    /// <summary><para>刷新按钮命令。</para>The refresh command.</summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    /// <summary><para>展开全部节点。</para>Expands every visible node.</summary>
    [RelayCommand]
    private void ExpandAll() => SetExpanded(Roots, true);

    /// <summary><para>折叠全部节点。</para>Collapses every visible node.</summary>
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
                () => backend.ToString(),
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
    }

    private UsbTreeNodeViewModel BuildNode(UsbTopologyBranch branch,
        UsbTopologyCapabilities capabilities, int depth)
    {
        var children = new UsbTreeNodeViewModel[branch.Children.Count];
        for (var index = 0; index < branch.Children.Count; index++)
        {
            children[index] = BuildNode(branch.Children[index], capabilities, depth + 1);
        }

        var node = new UsbTreeNodeViewModel(branch.Node, capabilities, children, depth);
        if (_expandedById.TryGetValue(branch.Node.InstanceId, out var expanded))
        {
            node.IsExpanded = expanded;
        }

        return node;
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
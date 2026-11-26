using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Terminal;
using UotanStudio.Services.Ui.Workspace;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// Singleton registry of workspace dock panels: each tool panel is recorded with its host dock
/// so it can be shown, hidden and focused, and current visibility is exposed as observable
/// properties.
/// <para>工作区停靠项注册表（单例）：每个工具停靠项连同所属停靠区一起登记，可见性可显示、隐藏与聚焦；
/// 当前可见状态以可观察属性暴露。</para>
/// </summary>
public sealed partial class WorkspacePanelService : ObservableObject
{
    /// <summary>Project Explorer panel key.<para>项目资源管理器停靠项键。</para></summary>
    public const string ProjectExplorer = "ProjectExplorer";

    /// <summary>Packing settings panel key.<para>打包设置停靠项键。</para></summary>
    public const string ProjectSetting = "ProjectSetting";

    /// <summary>Plugins panel key. The persisted value preserves visibility in existing workspace snapshots.<para>插件停靠项键。持久化的取值可在既有工作区快照中保留可见状态。</para></summary>
    public const string PluginTool = "PatchTool";

    /// <summary>Image manager panel key.<para>镜像管理停靠项键。</para></summary>
    public const string ImagesView = "ImagesView";

    /// <summary>AI assistant panel key.<para>AI 助手停靠项键。</para></summary>
    public const string AiAssistant = "AiAssistant";

    /// <summary>Properties panel key.<para>属性停靠项键。</para></summary>
    public const string Properties = "Properties";

    /// <summary>Workspace log panel key.<para>工作区日志停靠项键。</para></summary>
    public const string WorkspaceLog = "WorkspaceLog";

    /// <summary>ADB log panel key.<para>ADB 日志停靠项键。</para></summary>
    public const string AdbLog = "AdbLog";

    /// <summary>Terminal panel key.<para>终端停靠项键。</para></summary>
    public const string Terminal = "Terminal";

    /// <summary>
    /// Locale resource key per panel key: the single source for every workspace dock title. Titles
    /// are applied on registration and re-applied whenever the application language changes.
    /// <para>每个停靠项键对应的语言资源键：所有工作区停靠项标题的唯一来源。注册时应用标题，应用语言切换时重新应用。</para>
    /// </summary>
    private static readonly Dictionary<string, string> PanelTitleKeys = new(StringComparer.Ordinal)
    {
        [ProjectExplorer] = "Dock_ProjectExplorer",
        [ProjectSetting] = "Dock_ProjectSetting",
        [PluginTool] = "Dock_Plugins",
        [ImagesView] = "Dock_ImagesView",
        [AiAssistant] = "Dock_AiAssistant",
        [Properties] = "Dock_Properties",
        [WorkspaceLog] = "Dock_Output",
        [AdbLog] = "Dock_AdbLog"
    };

    /// <summary>
    /// Singleton instance.
    /// <para>单例实例。</para>
    /// </summary>
    /// <value>The shared service instance. <para>共享的服务实例。</para></value>
    public static WorkspacePanelService Instance { get; } = new();

    private readonly Dictionary<string, PanelEntry> _panels = new(StringComparer.Ordinal);
    private readonly Dictionary<IDockable, string> _titledDockables = new(ReferenceEqualityComparer.Instance);
    private TerminalDockService? _terminals;
    private IRootDock? _mainRoot;

    private sealed class PanelEntry
    {
        public required string Key { get; init; }
        public required IDockable Dockable { get; init; }
        public required IDock RegisteredHost { get; init; }
        public IDock? Host { get; set; }
        public string? TitleKey { get; set; }
    }

    internal WorkspacePanelService() =>
        WeakReferenceMessenger.Default.Register<WorkspacePanelService, LanguageStateChangedMessage>(
            this, static (service, _) => service.RefreshLocalizedTitles());

    internal Dictionary<string, bool> CaptureVisibility()
    {
        var visibility = _panels.Keys.ToDictionary(key => key, IsPanelVisible);
        visibility[Terminal] = IsPanelVisible(Terminal);
        return visibility;
    }

    internal void RegisterMainRoot(IRootDock root) => _mainRoot = root;

    internal void RegisterTerminals(TerminalDockService terminals)
    {
        if (!ReferenceEquals(_terminals, terminals)) _terminals?.Dispose();
        _terminals = terminals;
        RefreshTerminalVisibility();
    }

    internal void RefreshTerminalVisibility() => TerminalVisible = _terminals?.IsVisible == true;

    internal void OpenDeviceShell(DetectedDevice device)
    {
        (_terminals ?? throw new InvalidOperationException("The terminal dock is unavailable."))
            .CreateDeviceShell(device);
    }

    /// <summary>
    /// Builds the toolbar profile that labels an in-process device shell; the shell runs on the
    /// host's device session, so the profile carries no executable to spawn.
    /// <para>构造用于标识进程内设备 shell 的工具栏配置；shell 运行在宿主的设备会话上，因此配置不带要
    /// 启动的可执行文件。</para>
    /// </summary>
    /// <param name="device">The device whose shell is labelled.<para>要标注 shell 的设备。</para></param>
    /// <returns>The label-only profile for the device shell.<para>设备 shell 的纯标签配置。</para></returns>
    internal static TerminalProfile CreateDeviceShellProfile(DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new TerminalProfile("Device shell", Environment.ProcessPath ?? string.Empty, [],
            "Terminal_DeviceShell");
    }

    internal void RestoreVisibility(IReadOnlyDictionary<string, bool> panels)
    {
        foreach (var (key, visible) in panels)
        {
            if (visible) Show(key, activate: false);
            else Hide(key);
        }
    }

    /// <summary>
    /// Registers a localized title for a dockable that is not a toggleable panel, such as the
    /// document pane; the title is applied immediately and again after every language change.
    /// <para>为非可切换停靠项（如文档区）登记本地化标题；标题立即应用，并在每次语言切换后重新应用。</para>
    /// </summary>
    /// <param name="dockable">The dockable whose title is localized. <para>标题需要本地化的停靠项。</para></param>
    /// <param name="titleKey">The locale resource key. <para>语言资源键。</para></param>
    public void RegisterLocalizedTitle(IDockable dockable, string titleKey)
    {
        ArgumentNullException.ThrowIfNull(dockable);
        ArgumentException.ThrowIfNullOrEmpty(titleKey);
        _titledDockables[dockable] = titleKey;
        dockable.Title = LanguageService.Instance.GetString(titleKey);
    }

    /// <summary>
    /// Re-applies every registered workspace dock title in the active language. Called after a
    /// language change so titles update immediately instead of only when the layout is rebuilt.
    /// <para>用当前语言重新应用所有已登记的工作区停靠项标题。语言切换后调用，使标题立即更新，而不是等到重建布局才更新。</para>
    /// </summary>
    internal void RefreshLocalizedTitles()
    {
        foreach (var entry in _panels.Values)
        {
            ApplyPanelTitle(entry);
        }

        foreach (var (dockable, titleKey) in _titledDockables)
        {
            dockable.Title = LanguageService.Instance.GetString(titleKey);
        }
    }

    private static void ApplyPanelTitle(PanelEntry entry)
    {
        if (entry.TitleKey is { Length: > 0 } titleKey)
        {
            entry.Dockable.Title = LanguageService.Instance.GetString(titleKey);
        }
    }

    /// <summary>
    /// Whether the Project Explorer panel is currently visible.
    /// <para>项目资源管理器停靠项当前是否可见。</para>
    /// </summary>
    [ObservableProperty]
    private bool _projectExplorerVisible = true;

    /// <summary>
    /// Whether the packing settings panel is currently visible (hidden on startup by default).
    /// <para>打包设置停靠项当前是否可见（启动时默认隐藏）。</para>
    /// </summary>
    [ObservableProperty]
    private bool _projectSettingVisible;

    /// <summary>Whether the plugins panel is visible.<para>插件停靠项当前是否可见。</para></summary>
    [ObservableProperty]
    private bool _pluginToolVisible = true;

    /// <summary>
    /// Whether the image manager panel is currently visible.
    /// <para>镜像管理停靠项当前是否可见。</para>
    /// </summary>
    [ObservableProperty]
    private bool _imagesViewVisible = true;

    /// <summary>Whether the AI assistant panel is visible.<para>AI 助手停靠项当前是否可见。</para></summary>
    [ObservableProperty]
    private bool _aiAssistantVisible;

    /// <summary>
    /// Whether the properties panel is currently visible.
    /// <para>属性停靠项当前是否可见。</para>
    /// </summary>
    [ObservableProperty]
    private bool _propertiesVisible = true;

    /// <summary>
    /// Whether the workspace log panel is currently visible.
    /// <para>工作区日志停靠项当前是否可见。</para>
    /// </summary>
    [ObservableProperty]
    private bool _workspaceLogVisible = true;

    /// <summary>
    /// Whether the ADB log panel is currently visible.
    /// <para>ADB 日志停靠项当前是否可见。</para>
    /// </summary>
    [ObservableProperty]
    private bool _adbLogVisible = true;

    /// <summary>Whether the terminal panel is visible.<para>终端停靠项当前是否可见。</para></summary>
    [ObservableProperty]
    private bool _terminalVisible = true;

    /// <summary>
    /// Registers a workspace panel with its host dock and syncs the initial visibility.
    /// Re-registering the same key overwrites the previous entry (idempotent, safe when the
    /// factory rebuilds the layout).
    /// <para>注册一个工作区停靠项及其所属停靠区，并同步初始可见状态；相同键重复注册时覆盖旧条目（幂等，工厂重建布局时安全）。</para>
    /// </summary>
    /// <param name="key">Panel key. <para>停靠项键。</para></param>
    /// <param name="dockable">The dockable reference. <para>停靠项引用。</para></param>
    /// <param name="host">The dock that owns the entry. <para>所属停靠区。</para></param>
    /// <param name="visible">Whether initially visible. <para>初始是否可见。</para></param>
    public void Register(string key, IDockable dockable, IDock host, bool visible)
    {
        if (key == AdbLog && GetEntry(key)?.Dockable is IDisposable previous &&
            !ReferenceEquals(previous, dockable))
            previous.Dispose();
        var entry = new PanelEntry
        {
            Key = key,
            Dockable = dockable,
            RegisteredHost = host,
            Host = host,
            TitleKey = PanelTitleKeys.GetValueOrDefault(key)
        };
        _panels[key] = entry;
        ApplyPanelTitle(entry);
        SetVisibleState(key, visible);
    }

    /// <summary>
    /// Shows a specified panel: re-adds it to its host dock's visible list and makes it active.
    /// <para>显示指定停靠项：加回所属停靠区的可见列表并设为激活项。</para>
    /// </summary>
    /// <param name="key">Panel key. <para>停靠项键。</para></param>
    public void Show(string key) => Show(key, activate: true);

    /// <summary>
    /// Shows a specified panel; when activate is false the entry is only restored without changing the
    /// active dockable, so restoring visibility keeps focus.
    /// <para>显示指定停靠项；activate 为 false 时仅恢复可见状态而不切换激活项，恢复可见状态不会切换焦点。</para>
    /// </summary>
    /// <param name="key">Panel key. <para>停靠项键。</para></param>
    /// <param name="activate">Whether to also make it the active panel. <para>是否同时设为激活项。</para></param>
    public void Show(string key, bool activate)
    {
        if (key == Terminal)
        {
            if (activate)
            {
                _terminals?.Show();
            }
            else
            {
                _terminals?.ShowWithoutActivate();
            }
            if (_panels.GetValueOrDefault(WorkspaceLog) is { } bottomEntry
                && bottomEntry.RegisteredHost.Factory is { } bottomFactory)
            {
                EnsureHostMounted(bottomFactory, bottomEntry.RegisteredHost);
            }
            RefreshTerminalVisibility();
            return;
        }

        var entry = GetEntry(key);
        if (entry is null)
        {
            return;
        }

        UpdateHost(entry);
        var factory = entry.Dockable.Factory ?? entry.RegisteredHost.Factory;
        if (factory is null)
        {
            return;
        }

        // A panel the dock is holding after a hide sits in the root's HiddenDockables and restoring it
        // returns it to the pane it came from. A panel that was never hidden (or was closed from a
        // floating window) has no such entry, so it goes back to its mounted host or registered tool dock.
        var mainRoot = FindRoot(factory, entry.RegisteredHost);
        var hidden = mainRoot?.HiddenDockables;
        if (hidden?.Contains(entry.Dockable) == true)
        {
            factory.RestoreDockable(entry.Dockable);
        }

        if (entry.Dockable.Owner is not IDock currentOwner
            || currentOwner.VisibleDockables?.Contains(entry.Dockable) != true
            || (mainRoot is not null && !ContainsInDockTree(mainRoot, currentOwner)))
        {
            var targetHost = entry.Host is { } lastHost
                && mainRoot is not null
                && ContainsInDockTree(mainRoot, lastHost)
                ? lastHost
                : entry.RegisteredHost;

            if (mainRoot is not null
                && !ContainsInDockTree(mainRoot, targetHost)
                && !IsInLiveFloatingWindow(mainRoot, targetHost))
            {
                targetHost.Owner = null;
            }

            if (entry.Dockable.Owner is IDock staleOwner && !ReferenceEquals(staleOwner, targetHost))
            {
                staleOwner.VisibleDockables?.Remove(entry.Dockable);
            }

            if (targetHost.VisibleDockables?.Contains(entry.Dockable) != true)
            {
                factory.AddDockable(targetHost, entry.Dockable);
            }
            else
            {
                entry.Dockable.Owner = targetHost;
                targetHost.IsEmpty = false;
            }
        }

        if (entry.Dockable.Owner is IDock activeHost)
        {
            if (mainRoot is not null)
            {
                EnsureHostMounted(factory, mainRoot, activeHost);
            }
            else
            {
                EnsureHostMounted(factory, activeHost);
            }
        }

        if (activate && entry.Dockable.Owner is IDock)
        {
            factory.SetActiveDockable(entry.Dockable);
            if ((mainRoot ?? factory.FindRoot(entry.Dockable, static _ => true)) is { } root)
            {
                factory.SetFocusedDockable(root, entry.Dockable);
            }
        }

        SetVisibleState(key, IsPanelVisible(key));
    }

    /// <summary>
    /// Hides a specified panel; the dock keeps it in <c>HiddenDockables</c> together with the pane it
    /// came from, so showing it again returns it to the same position.
    /// <para>隐藏指定停靠项；Dock 会把它连同来源停靠区一起记录在 <c>HiddenDockables</c> 中，
    /// 再次显示时会回到原位置。</para>
    /// </summary>
    /// <param name="key">Panel key. <para>停靠项键。</para></param>
    public void Hide(string key)
    {
        if (key == Terminal)
        {
            _terminals?.Hide();
            RefreshTerminalVisibility();
            return;
        }

        var entry = GetEntry(key);
        if (entry is null)
        {
            return;
        }

        UpdateHost(entry);
        var factory = entry.Dockable.Factory ?? entry.RegisteredHost.Factory;
        var sourceDock = entry.Dockable.Owner as IDock;
        var ownerRoot = factory?.FindRoot(entry.Dockable, static _ => true);
        factory?.HideDockable(entry.Dockable);

        if (factory is not null && ownerRoot?.Window is { } floatingWindow)
        {
            if (sourceDock is not null
                && sourceDock.VisibleDockables is null or { Count: 0 }
                && HasVisibleLeafDockables(floatingWindow.Layout))
            {
                factory.CollapseDock(sourceDock);
            }

            if (!HasVisibleLeafDockables(floatingWindow.Layout))
            {
                ownerRoot.HiddenDockables?.Remove(entry.Dockable);
                entry.Dockable.Owner = null;
                entry.Dockable.OriginalOwner = null;

                var mainRoot = FindRoot(factory, entry.RegisteredHost);
                CloseFloatingWindow(factory, mainRoot, floatingWindow);

                if (mainRoot is not null && !ContainsInDockTree(mainRoot, entry.RegisteredHost))
                {
                    entry.RegisteredHost.Owner = null;
                }
            }
        }

        SetVisibleState(key, false);
    }

    /// <summary>
    /// Toggles the specified panel's visible state.
    /// <para>切换指定停靠项的显示与隐藏状态。</para>
    /// </summary>
    /// <param name="key">Panel key. <para>停靠项键。</para></param>
    public void Toggle(string key)
    {
        if (IsPanelVisible(key))
        {
            Hide(key);
        }
        else
        {
            Show(key);
        }
    }

    /// <summary>
    /// Queries whether a specified panel is currently present in its host dock's visible list.
    /// <para>查询指定停靠项当前是否真实位于所属停靠区的可见列表中。</para>
    /// </summary>
    /// <param name="key">Panel key. <para>停靠项键。</para></param>
    /// <returns>True when it is present in the visible list. <para>停靠项位于可见列表时返回 true。</para></returns>
    public bool IsPanelVisible(string key)
    {
        if (key == Terminal) return _terminals?.IsVisible == true;
        var entry = GetEntry(key);
        if (entry is null) return false;
        return entry.Dockable.Owner is IDock owner && owner.VisibleDockables?.Contains(entry.Dockable) == true;
    }

    /// <summary>
    /// Resyncs the visibility observables when a host dock's visible contents change, such as a
    /// dockable removed through the dock's close button.
    /// <para>所属停靠区的可见内容变化时（例如停靠项经关闭按钮被移除），重新同步该区内各停靠项的可见性可观察属性。</para>
    /// </summary>
    /// <param name="dock">The dock whose contents changed. <para>内容变化的停靠区。</para></param>
    public void OnDockContentsChanged(IDock? dock = null)
    {
        foreach (var (key, entry) in _panels)
        {
            var isVisible = entry.Dockable.Owner is IDock owner && owner.VisibleDockables?.Contains(entry.Dockable) == true;
            SetVisibleState(key, isVisible);
        }
        RefreshTerminalVisibility();
    }

    private PanelEntry? GetEntry(string key) => _panels.TryGetValue(key, out var entry) ? entry : null;

    internal IToolDock? FindRegisteredToolHost(IDockable dockable)
    {
        foreach (var entry in _panels.Values)
        {
            if (ReferenceEquals(entry.Dockable, dockable) && entry.RegisteredHost is IToolDock toolDock)
            {
                return toolDock;
            }
        }

        return null;
    }

    internal IToolDock? FindBottomToolHost()
        => _panels.GetValueOrDefault(WorkspaceLog)?.RegisteredHost as IToolDock;

    internal void EnsureHostMounted(IFactory factory, IRootDock root, IDock host)
    {
        if (ContainsInDockTree(root, host))
        {
            return;
        }

        if (FindInDockTree<IProportionalDock>(root, static d => string.Equals(d.Id, "WorkspaceMainLayout", StringComparison.Ordinal))
            is not { } mainLayout)
        {
            return;
        }

        var leftPane = _panels.GetValueOrDefault(ProjectExplorer)?.RegisteredHost;
        if (ReferenceEquals(host, leftPane))
        {
            SyncLeftPane(leftPane, factory, mainLayout, isShowing: true);
            return;
        }

        var bottomPane = _panels.GetValueOrDefault(WorkspaceLog)?.RegisteredHost;
        if (ReferenceEquals(host, bottomPane))
        {
            if (FindInDockTree<IProportionalDock>(root, static d => string.Equals(d.Id, "WorkspaceCenterColumn", StringComparison.Ordinal))
                is { } centerCol)
            {
                SyncBottomPane(bottomPane, centerCol, factory, isShowing: true);
            }
            return;
        }

        var rightPaneTop = _panels.GetValueOrDefault(ImagesView)?.RegisteredHost;
        var rightPane = _panels.GetValueOrDefault(Properties)?.RegisteredHost;
        if (ReferenceEquals(host, rightPaneTop) || ReferenceEquals(host, rightPane))
        {
            var rightCol = FindInDockTree<IProportionalDock>(root, static d => string.Equals(d.Id, "WorkspaceRightColumn", StringComparison.Ordinal))
                ?? (rightPaneTop?.Owner is IProportionalDock { Id: "WorkspaceRightColumn" } topCol ? topCol : null)
                ?? (rightPane?.Owner is IProportionalDock { Id: "WorkspaceRightColumn" } bottomCol ? bottomCol : null)
                ?? new ProportionalDock
                {
                    Id = "WorkspaceRightColumn",
                    Orientation = Orientation.Vertical,
                    Proportion = 0.15,
                    CollapsedProportion = 0.15,
                    Owner = mainLayout,
                    Factory = factory,
                    VisibleDockables = factory.CreateList<IDockable>()
                };
            SyncRightColumn(root, rightCol, rightPaneTop, rightPane, factory, mainLayout, isShowing: true);
        }
    }

    private static void UpdateHost(PanelEntry? entry)
    {
        if (entry is null) return;
        if (entry.Dockable.Owner is IToolDock actual && actual.VisibleDockables?.Contains(entry.Dockable) == true)
        {
            var ownerRoot = actual.Factory?.FindRoot(actual, static _ => true);
            if (ownerRoot?.Window is null)
            {
                entry.Host = actual;
            }
        }
    }

    private void SetVisibleState(string key, bool value)
    {
        switch (key)
        {
            case ProjectExplorer:
                ProjectExplorerVisible = value;
                break;
            case ProjectSetting:
                ProjectSettingVisible = value;
                break;
            case PluginTool:
                PluginToolVisible = value;
                break;
            case ImagesView:
                ImagesViewVisible = value;
                break;
            case AiAssistant:
                AiAssistantVisible = value;
                break;
            case Properties:
                PropertiesVisible = value;
                break;
            case WorkspaceLog:
                WorkspaceLogVisible = value;
                break;
            case AdbLog:
                AdbLogVisible = value;
                break;
            case Terminal:
                TerminalVisible = value;
                break;
        }
    }

    private void EnsureHostMounted(IFactory factory, IDock host)
    {
        var root = FindRoot(factory, host);
        if (root is not null)
        {
            EnsureHostMounted(factory, root, host);
        }
    }

    private IRootDock? FindRoot(IFactory factory, IDock host)
    {
        if (_mainRoot is not null)
        {
            return _mainRoot;
        }

        if (factory.FindRoot(host, static _ => true) is { } hostRoot)
        {
            return UnwrapMainRoot(hostRoot);
        }

        foreach (var entry in _panels.Values)
        {
            if (factory.FindRoot(entry.RegisteredHost, static _ => true) is { } root)
            {
                return UnwrapMainRoot(root);
            }
        }

        return null;
    }

    private static IRootDock UnwrapMainRoot(IRootDock root)
    {
        var current = root;
        while (current.Window?.Owner is IRootDock parent && !ReferenceEquals(parent, current))
        {
            current = parent;
        }

        return current;
    }

    private static bool ContainsInDockTree(IDock root, IDockable target)
        => FindInDockTree<IDockable>(root, d => ReferenceEquals(d, target)) is not null;

    private static bool IsInLiveFloatingWindow(IRootDock root, IDockable target)
    {
        if (root.Windows is null)
        {
            return false;
        }

        foreach (var window in root.Windows)
        {
            if (window.Layout is not null && ContainsInDockTree(window.Layout, target))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasVisibleLeafDockables(IDockable? root)
    {
        if (root is null)
        {
            return false;
        }

        if (root is ITool or IDocument)
        {
            return true;
        }

        if (root is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (HasVisibleLeafDockables(child))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static void CloseFloatingWindow(IFactory factory, IRootDock? mainRoot, IDockWindow floatingWindow)
    {
        factory.CloseWindow(floatingWindow);
        if (mainRoot?.Windows?.Contains(floatingWindow) == true)
        {
            factory.RemoveWindow(floatingWindow);
        }
    }

    private static T? FindInDockTree<T>(IDockable? current, Func<T, bool>? predicate = null)
        where T : class, IDockable
    {
        if (current is null)
        {
            return null;
        }

        if (current is T matched && (predicate is null || predicate(matched)))
        {
            return matched;
        }

        if (current is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (FindInDockTree(child, predicate) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Syncs the project explorer host with the front of <paramref name="mainLayout"/>: a host holding no
    /// dockables is removed together with its trailing splitter, and a showing host is re-inserted at the front.
    /// <para>把项目资源管理器宿主同步到 <paramref name="mainLayout"/> 的开头：宿主无内容时连同其后的分隔条
    /// 一起移除，显示时重新插入到最前面。</para>
    /// </summary>
    /// <param name="leftPane">The project explorer host, or <see langword="null"/> when unregistered.
    /// <para>项目资源管理器宿主；未登记时为 <see langword="null"/>。</para></param>
    /// <param name="factory">The dock factory used for list creation.<para>用于创建列表的停靠工厂。</para></param>
    /// <param name="mainLayout">The root proportional layout.<para>根比例布局。</para></param>
    /// <param name="isShowing">Whether the sync happens while panels are being shown.
    /// <para>本次同步是否发生在面板显示时。</para></param>
    private void SyncLeftPane(IDock? leftPane, IFactory factory, IProportionalDock mainLayout, bool isShowing)
    {
        if (leftPane is not null)
        {
            var leftCount = leftPane.VisibleDockables?.Count ?? 0;
            var mainDockables = mainLayout.VisibleDockables ??= factory.CreateList<IDockable>();
            if (leftCount == 0)
            {
                var idx = mainDockables.IndexOf(leftPane);
                if (idx >= 0)
                {
                    mainDockables.RemoveAt(idx);
                    if (idx < mainDockables.Count && mainDockables[idx] is IProportionalDockSplitter)
                    {
                        mainDockables.RemoveAt(idx);
                    }
                }
            }
            else if (isShowing && !mainDockables.Contains(leftPane))
            {
                leftPane.Proportion = 0.15;
                leftPane.CollapsedProportion = 0.15;
                leftPane.IsEmpty = false;
                factory.InsertDockable(mainLayout, leftPane, 0);
                factory.InsertDockable(mainLayout, new ProportionalDockSplitter(), 1);
            }
        }
    }

    /// <summary>
    /// Syncs the right column with <paramref name="mainLayout"/>: the column collapses when neither host holds
    /// dockables, a showing column is appended as the last layout entry, and the top/bottom split is rebuilt from
    /// the hosts that still hold dockables.
    /// <para>把右侧列同步到 <paramref name="mainLayout"/>：两个宿主都无内容时收起该列，显示时作为布局最后一项
    /// 追加，并按仍有内容的宿主重建上/下切分。</para>
    /// </summary>
    /// <param name="root">The main root dock owning <paramref name="mainLayout"/>.<para>拥有 <paramref name="mainLayout"/> 的主根停靠区。</para></param>
    /// <param name="rightCol">The right column dock, or <see langword="null"/> when not found.
    /// <para>右侧列停靠区；未找到时为 <see langword="null"/>。</para></param>
    /// <param name="rightPaneTop">The top right host.<para>右上宿主。</para></param>
    /// <param name="rightPane">The bottom right host.<para>右下宿主。</para></param>
    /// <param name="factory">The dock factory used for list creation.<para>用于创建列表的停靠工厂。</para></param>
    /// <param name="mainLayout">The root proportional layout.<para>根比例布局。</para></param>
    /// <param name="isShowing">Whether the sync happens while panels are being shown.
    /// <para>本次同步是否发生在面板显示时。</para></param>
    private void SyncRightColumn(IRootDock root, IProportionalDock? rightCol, IDock? rightPaneTop, IDock? rightPane,
        IFactory factory, IProportionalDock mainLayout, bool isShowing)
    {
        if (rightCol is not null && rightPaneTop is not null && rightPane is not null)
        {
            var topVisible = rightPaneTop.VisibleDockables is { Count: > 0 }
                && !IsInLiveFloatingWindow(root, rightPaneTop);
            var bottomVisible = rightPane.VisibleDockables is { Count: > 0 }
                && !IsInLiveFloatingWindow(root, rightPane);
            var mainDockables = mainLayout.VisibleDockables ??= factory.CreateList<IDockable>();

            if (!topVisible && !bottomVisible)
            {
                var idx = mainDockables.IndexOf(rightCol);
                if (idx >= 0)
                {
                    mainDockables.RemoveAt(idx);
                    if (idx > 0 && mainDockables[idx - 1] is IProportionalDockSplitter)
                    {
                        mainDockables.RemoveAt(idx - 1);
                    }
                }

                rightCol.VisibleDockables?.Clear();
                rightCol.IsEmpty = true;
                return;
            }

            if (!mainDockables.Contains(rightCol) && isShowing)
            {
                rightCol.Orientation = Orientation.Vertical;
                rightCol.Proportion = 0.15;
                rightCol.CollapsedProportion = 0.15;
                rightCol.IsEmpty = false;

                // If Dock collapsed WorkspaceRightColumn earlier and promoted one of the right panes
                // directly into mainLayout, replace that promoted slot in place with rightCol.
                var promotedIdx = -1;
                for (var i = mainDockables.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(mainDockables[i], rightPaneTop) || ReferenceEquals(mainDockables[i], rightPane))
                    {
                        if (promotedIdx < 0)
                        {
                            promotedIdx = i;
                        }
                        else
                        {
                            mainDockables.RemoveAt(i);
                            if (i > 0 && mainDockables[i - 1] is IProportionalDockSplitter)
                            {
                                mainDockables.RemoveAt(i - 1);
                                i--;
                            }
                            promotedIdx--;
                        }
                    }
                }

                if (promotedIdx >= 0)
                {
                    mainDockables[promotedIdx] = rightCol;
                    rightCol.Owner = mainLayout;
                    rightCol.Factory = factory;
                }
                else
                {
                    if (mainDockables.Count > 0 && mainDockables[^1] is not IProportionalDockSplitter)
                    {
                        factory.AddDockable(mainLayout, new ProportionalDockSplitter());
                    }
                    factory.AddDockable(mainLayout, rightCol);
                }
            }

            if (mainDockables.Contains(rightCol))
            {
                rightCol.Orientation = Orientation.Vertical;
                rightCol.Owner = mainLayout;
                rightCol.Factory = factory;
                rightCol.IsEmpty = false;
                var rightDockables = rightCol.VisibleDockables ??= factory.CreateList<IDockable>();

                if (topVisible && bottomVisible)
                {
                    rightPaneTop.Proportion = double.NaN;
                    rightPaneTop.CollapsedProportion = double.NaN;
                    rightPaneTop.IsEmpty = false;
                    rightPane.Proportion = 0.35;
                    rightPane.CollapsedProportion = 0.35;
                    rightPane.IsEmpty = false;

                    if (rightDockables.Count == 3
                        && ReferenceEquals(rightDockables[0], rightPaneTop)
                        && rightDockables[1] is IProportionalDockSplitter
                        && ReferenceEquals(rightDockables[2], rightPane))
                    {
                        // Already in canonical [top, splitter, bottom] layout.
                    }
                    else if (rightDockables.Count == 1 && ReferenceEquals(rightDockables[0], rightPaneTop))
                    {
                        factory.AddDockable(rightCol, new ProportionalDockSplitter());
                        factory.AddDockable(rightCol, rightPane);
                    }
                    else if (rightDockables.Count == 1 && ReferenceEquals(rightDockables[0], rightPane))
                    {
                        factory.InsertDockable(rightCol, rightPaneTop, 0);
                        factory.InsertDockable(rightCol, new ProportionalDockSplitter(), 1);
                    }
                    else
                    {
                        rightDockables.Clear();
                        factory.AddDockable(rightCol, rightPaneTop);
                        factory.AddDockable(rightCol, new ProportionalDockSplitter());
                        factory.AddDockable(rightCol, rightPane);
                    }

                    rightCol.ActiveDockable ??= rightPaneTop;
                }
                else if (topVisible)
                {
                    rightPaneTop.Proportion = double.NaN;
                    rightPaneTop.CollapsedProportion = double.NaN;
                    rightPaneTop.IsEmpty = false;

                    for (var i = rightDockables.Count - 1; i >= 0; i--)
                    {
                        if (!ReferenceEquals(rightDockables[i], rightPaneTop))
                        {
                            rightDockables.RemoveAt(i);
                        }
                    }

                    if (!rightDockables.Contains(rightPaneTop))
                    {
                        factory.AddDockable(rightCol, rightPaneTop);
                    }

                    rightCol.ActiveDockable = rightPaneTop;
                }
                else
                {
                    rightPane.Proportion = double.NaN;
                    rightPane.CollapsedProportion = double.NaN;
                    rightPane.IsEmpty = false;

                    for (var i = rightDockables.Count - 1; i >= 0; i--)
                    {
                        if (!ReferenceEquals(rightDockables[i], rightPane))
                        {
                            rightDockables.RemoveAt(i);
                        }
                    }

                    if (!rightDockables.Contains(rightPane))
                    {
                        factory.AddDockable(rightCol, rightPane);
                    }

                    rightCol.ActiveDockable = rightPane;
                }

                factory.InitDockable(rightCol, mainLayout);
            }
        }
    }

    /// <summary>
    /// Syncs the workspace log host with the end of <paramref name="centerCol"/>: a host holding no dockables is
    /// removed together with its leading splitter, and a showing host is appended after a splitter.
    /// <para>把工作区日志宿主同步到 <paramref name="centerCol"/> 的末尾：宿主无内容时连同其前的分隔条一起移除，
    /// 显示时在分隔条之后追加。</para>
    /// </summary>
    /// <param name="bottomPane">The workspace log host, or <see langword="null"/> when unregistered.
    /// <para>工作区日志宿主；未登记时为 <see langword="null"/>。</para></param>
    /// <param name="centerCol">The center column dock.<para>中央列停靠区。</para></param>
    /// <param name="factory">The dock factory used for list creation.<para>用于创建列表的停靠工厂。</para></param>
    /// <param name="isShowing">Whether the sync happens while panels are being shown.
    /// <para>本次同步是否发生在面板显示时。</para></param>
    private void SyncBottomPane(IDock? bottomPane, IProportionalDock centerCol, IFactory factory, bool isShowing)
    {
        if (bottomPane is not null)
        {
            var bottomToolsCount = bottomPane.VisibleDockables?.Count ?? 0;
            var centerDockables = centerCol.VisibleDockables ??= factory.CreateList<IDockable>();
            if (bottomToolsCount == 0)
            {
                var idx = centerDockables.IndexOf(bottomPane);
                if (idx >= 0)
                {
                    centerDockables.RemoveAt(idx);
                    if (idx > 0 && centerDockables[idx - 1] is IProportionalDockSplitter)
                    {
                        centerDockables.RemoveAt(idx - 1);
                    }
                }
            }
            else if (isShowing && !centerDockables.Contains(bottomPane))
            {
                bottomPane.Proportion = 0.25;
                bottomPane.CollapsedProportion = 0.25;
                bottomPane.IsEmpty = false;
                if (centerDockables.Count > 0 && centerDockables[^1] is not IProportionalDockSplitter)
                {
                    factory.AddDockable(centerCol, new ProportionalDockSplitter());
                }
                factory.AddDockable(centerCol, bottomPane);
            }
        }
    }
}
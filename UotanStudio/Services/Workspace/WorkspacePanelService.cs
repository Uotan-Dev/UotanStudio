using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
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

    private sealed class PanelEntry
    {
        public required string Key { get; init; }
        public required IDockable Dockable { get; init; }
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
    /// Shows a specified panel; when activate is false the entry is only re-added to its host
    /// dock's visible list without changing the active one, so restoring visibility keeps focus.
    /// <para>显示指定停靠项；activate 为 false 时仅把它加回所属停靠区的可见列表而不改变激活项，
    /// 恢复可见状态不会切换焦点。</para>
    /// </summary>
    /// <param name="key">Panel key. <para>停靠项键。</para></param>
    /// <param name="activate">Whether to also make it the active panel. <para>是否同时设为激活项。</para></param>
    public void Show(string key, bool activate)
    {
        if (key == Terminal)
        {
            if (activate) _terminals?.Show();
            else _terminals?.ShowWithoutActivate();
            RefreshTerminalVisibility();
            return;
        }
        var entry = GetEntry(key);
        UpdateHost(entry);
        if (entry is null || entry.Host is not { } host)
        {
            return;
        }

        if (host.VisibleDockables is { } list && !list.Contains(entry.Dockable))
        {
            list.Add(entry.Dockable);
        }

        if (activate && !ReferenceEquals(host.ActiveDockable, entry.Dockable))
        {
            host.ActiveDockable = entry.Dockable;
        }

        SetVisibleState(key, true);
    }

    /// <summary>
    /// Hides a specified panel: removes it from its host dock's visible list; when it was the
    /// active panel, the first remaining panel becomes active instead.
    /// <para>隐藏指定停靠项：从所属停靠区的可见列表移除；若它当前为激活项，则改为激活余下的第一个。</para>
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
        UpdateHost(entry);
        if (entry is null || entry.Host is not { } host)
        {
            return;
        }

        if (host.VisibleDockables is { } list && list.Contains(entry.Dockable))
        {
            list.Remove(entry.Dockable);
            if (ReferenceEquals(host.ActiveDockable, entry.Dockable))
            {
                host.ActiveDockable = list.FirstOrDefault();
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
        UpdateHost(entry);
        if (entry?.Host is { } host)
        {
            return host.VisibleDockables?.Contains(entry.Dockable) == true;
        }

        return false;
    }

    /// <summary>
    /// Resyncs the visibility observables when a host dock's visible contents change, such as a
    /// dockable removed through the dock's close button.
    /// <para>所属停靠区的可见内容变化时（例如停靠项经关闭按钮被移除），重新同步该区内各停靠项的可见性可观察属性。</para>
    /// </summary>
    /// <param name="dock">The dock whose contents changed. <para>内容变化的停靠区。</para></param>
    public void OnDockContentsChanged(IDock dock)
    {
        foreach (var (key, entry) in _panels)
        {
            if (ReferenceEquals(entry.Host, dock))
            {
                SetVisibleState(key, dock.VisibleDockables?.Contains(entry.Dockable) == true);
            }
        }
        RefreshTerminalVisibility();
    }

    private PanelEntry? GetEntry(string key) => _panels.TryGetValue(key, out var entry) ? entry : null;

    internal IToolDock? FindRegisteredToolHost(IDockable dockable)
    {
        foreach (var entry in _panels.Values)
        {
            if (ReferenceEquals(entry.Dockable, dockable) && entry.Host is IToolDock toolDock)
            {
                return toolDock;
            }
        }

        return null;
    }

    private static void UpdateHost(PanelEntry? entry)
    {
        if (entry?.Dockable.Owner is IDock actual && actual.VisibleDockables?.Contains(entry.Dockable) == true)
            entry.Host = actual;
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
}
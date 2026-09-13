using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Core;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// <para>工作区停靠面板注册表（单例）：登记 <c>WorkspaceDockFactory</c> 创建的各工具面板及其宿主停靠区，
/// 供顶部「窗口」菜单执行显示/隐藏/聚焦。面板当前可见状态以可观察属性暴露，供菜单项 IsChecked 绑定。</para>
/// Workspace dock panel registry (singleton): registers the tool panels created by
/// <c>WorkspaceDockFactory</c> together with their host dock so the top-level "Window" menu can
/// show/hide/focus them. The current visibility of each panel is exposed as observable properties
/// for the menu items' IsChecked bindings.
/// </summary>
public sealed partial class WorkspacePanelService : ObservableObject
{
    /// <summary>项目资源管理器面板键。 Project Explorer panel key.</summary>
    public const string ProjectExplorer = "ProjectExplorer";

    /// <summary>打包设置面板键。 Packing settings panel key.</summary>
    public const string ProjectSetting = "ProjectSetting";

    /// <summary>镜像管理面板键。 Image manager panel key.</summary>
    public const string ImagesView = "ImagesView";

    /// <summary>AI 助手面板键。 AI assistant panel key.</summary>
    public const string AiAssistant = "AiAssistant";

    /// <summary>属性面板键。 Properties panel key.</summary>
    public const string Properties = "Properties";

    /// <summary>工作区日志面板键。 Workspace log panel key.</summary>
    public const string WorkspaceLog = "WorkspaceLog";

    /// <summary>ADB 日志面板键。 ADB log panel key.</summary>
    public const string AdbLog = "AdbLog";

    /// <summary>终端面板键。 Terminal panel key.</summary>
    public const string Terminal = "Terminal";

    /// <summary>
    /// <para>单例实例。</para>
    /// Singleton instance.
    /// </summary>
    public static WorkspacePanelService Instance { get; } = new();

    private readonly Dictionary<string, PanelEntry> _panels = new(StringComparer.Ordinal);
    private TerminalDockService? _terminals;

    private sealed class PanelEntry
    {
        public required string Key { get; init; }
        public required IDockable Dockable { get; init; }
        public IDock? Host { get; set; }
    }

    internal WorkspacePanelService() { }

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

    internal void RestoreVisibility(IReadOnlyDictionary<string, bool> panels)
    {
        foreach (var (key, visible) in panels)
        {
            // 恢复可见性不改变当前激活面板：有工程时以解决方案状态恢复，活动面板保持用户
            // 当前所处位置；仅加回可见列表/从可见列表移除。
            // Restoring visibility must not change the active panel (e.g. after opening a solution the
            // left pane should stay on the project explorer): only add back / remove from visible lists.
            if (visible) Show(key, activate: false);
            else Hide(key);
        }
    }

    /// <summary>
    /// <para>项目资源管理器面板当前是否可见。</para>
    /// Whether the Project Explorer panel is currently visible.
    /// </summary>
    [ObservableProperty]
    private bool _projectExplorerVisible = true;

    /// <summary>
    /// <para>打包设置面板当前是否可见（启动时默认隐藏）。</para>
    /// Whether the packing settings panel is currently visible (hidden on startup by default).
    /// </summary>
    [ObservableProperty]
    private bool _projectSettingVisible;

    /// <summary>
    /// <para>镜像管理面板当前是否可见。</para>
    /// Whether the image manager panel is currently visible.
    /// </summary>
    [ObservableProperty]
    private bool _imagesViewVisible = true;

    /// <summary>AI 助手面板当前是否可见。 Whether the AI assistant panel is visible.</summary>
    [ObservableProperty]
    private bool _aiAssistantVisible;

    /// <summary>
    /// <para>属性面板当前是否可见。</para>
    /// Whether the properties panel is currently visible.
    /// </summary>
    [ObservableProperty]
    private bool _propertiesVisible = true;

    /// <summary>
    /// <para>工作区日志面板当前是否可见。</para>
    /// Whether the workspace log panel is currently visible.
    /// </summary>
    [ObservableProperty]
    private bool _workspaceLogVisible = true;

    /// <summary>
    /// <para>ADB 日志面板当前是否可见。</para>
    /// Whether the ADB log panel is currently visible.
    /// </summary>
    [ObservableProperty]
    private bool _adbLogVisible = true;

    /// <summary>终端面板当前是否可见。 Whether the terminal panel is visible.</summary>
    [ObservableProperty]
    private bool _terminalVisible = true;

    /// <summary>
    /// <para>注册一个工作区面板及其宿主停靠区，并同步初始可见状态。
    /// 相同键重复注册时覆盖旧条目（幂等，工厂重建布局时安全）。</para>
    /// Registers a workspace panel with its host dock and syncs the initial visibility.
    /// Re-registering the same key overwrites the previous entry (idempotent, safe when the
    /// factory rebuilds the layout).
    /// </summary>
    /// <param name="key"><para>面板键。</para>The panel key.</param>
    /// <param name="dockable"><para>停靠面板引用。</para>The dock panel reference.</param>
    /// <param name="host"><para>宿主停靠区。</para>The host dock.</param>
    /// <param name="visible"><para>初始是否可见。</para>Whether initially visible.</param>
    public void Register(string key, IDockable dockable, IDock host, bool visible)
    {
        if (key == AdbLog && GetEntry(key)?.Dockable is IDisposable previous &&
            !ReferenceEquals(previous, dockable))
            previous.Dispose();
        _panels[key] = new PanelEntry { Key = key, Dockable = dockable, Host = host };
        SetVisibleState(key, visible);
    }

    /// <summary>
    /// <para>显示指定面板：加回宿主停靠区可见列表并设为激活面板。</para>
    /// Shows the specified panel: adds it back to the host dock's visible list and makes it active.
    /// </summary>
    /// <param name="key"><para>面板键。</para>The panel key.</param>
    public void Show(string key) => Show(key, activate: true);

    /// <summary>
    /// <para>显示指定面板；<paramref name="activate"/> 为 false 时仅加回宿主停靠区可见列表，
    /// 不改变当前激活面板（供会话恢复使用，避免把活动面板切到被恢复的面板上）。</para>
    /// Shows the specified panel; with <paramref name="activate"/> false it only re-adds the panel
    /// to the host dock's visible list without changing the active panel (used by session restore so
    /// restoring visibility never switches the active panel to a restored panel).
    /// </summary>
    /// <param name="key"><para>面板键。</para>The panel key.</param>
    /// <param name="activate"><para>是否同时设为激活面板。</para>Whether to also make it the active panel.</param>
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
    /// <para>隐藏指定面板：从宿主停靠区可见列表移除；若它当前为激活面板则激活余下第一个。</para>
    /// Hides the specified panel: removes it from the host dock's visible list; if it was the
    /// active panel, the first remaining panel becomes active instead.
    /// </summary>
    /// <param name="key"><para>面板键。</para>The panel key.</param>
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
    /// <para>切换指定面板的显示/隐藏状态。</para>
    /// Toggles the specified panel's visible state.
    /// </summary>
    /// <param name="key"><para>面板键。</para>The panel key.</param>
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
    /// <para>查询指定面板当前是否实际在宿主停靠区的可见列表中。</para>
    /// Queries whether the specified panel is currently present in the host dock's visible list.
    /// </summary>
    /// <param name="key"><para>面板键。</para>The panel key.</param>
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
    /// <para>宿主停靠区可见内容变化时（如用户经停靠区关闭按钮移除面板）由工厂回调，
    /// 重新同步该停靠区内各面板的可见性可观察属性。</para>
    /// Called by the factory when a host dock's visible contents change (e.g. a panel is removed
    /// through the dock's close button), resyncing the visibility observables for the panels of
    /// that dock.
    /// </summary>
    /// <param name="dock"><para>内容变化的停靠区。</para>The dock whose contents changed.</param>
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

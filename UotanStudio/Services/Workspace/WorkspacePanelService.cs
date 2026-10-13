using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Core;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Terminal;

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
    /// Singleton instance.
    /// <para>单例实例。</para>
    /// </summary>
    /// <value>The shared service instance. <para>共享的服务实例。</para></value>
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

    internal void OpenDeviceShell(DetectedDevice device)
    {
        var profile = CreateDeviceShellProfile(device);
        (_terminals ?? throw new InvalidOperationException("The terminal dock is unavailable."))
            .CreateTerminal(profile);
    }

    internal static TerminalProfile CreateDeviceShellProfile(DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var app = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current application executable is unavailable.");
        var assembly = Path.Combine(AppContext.BaseDirectory, "UotanStudio.Desktop.dll");
        var runtimeRoot = Directory.GetParent(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
            ?.Parent?.Parent?.FullName;
        var dotnet = runtimeRoot is null ? null : Path.Combine(runtimeRoot,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var useDotnet = File.Exists(assembly) && dotnet is not null && File.Exists(dotnet);
        var executable = useDotnet ? dotnet! : app;
        string[] arguments = useDotnet
            ? [assembly, "--managed-device-shell", device.Protocol.ToString(), device.Serial]
            : ["--managed-device-shell", device.Protocol.ToString(), device.Serial];
        return new TerminalProfile("Device shell", executable, arguments);
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
        _panels[key] = new PanelEntry { Key = key, Dockable = dockable, Host = host };
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
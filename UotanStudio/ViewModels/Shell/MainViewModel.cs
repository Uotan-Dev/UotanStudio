using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Composition;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.ViewModels.Shell;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ShellComponents _components;
    private bool _disposed;
    private readonly Services.Ui.Workspace.WorkspaceAutoSaveService _autoSave;
    private readonly Services.Workspace.WorkspaceModificationTracker _modifications;
    internal Task InitializeAutoSaveAsync() => _autoSave.InitializeAsync();
    internal void AttachMainWindow(Window window) => _autoSave.AttachMainWindow(window);
    internal void ResumeAutoSave() => _autoSave.Resume();
    internal async Task<bool> FlushAutoSaveAsync()
    {
        SettingsVM.FlushPendingSave();
        return await _autoSave.SaveOnCloseAsync();
    }
    public MenuViewModel MenuVM { get; }
    public WorkspaceViewModel WorkspaceVM { get; }
    public NodeEditorViewModel NodeEditorVM { get; }
    public SettingsViewModel SettingsVM { get; }

    /// <summary>
    /// Device panel singleton: shared by the top-bar device selector and the device
    /// panel; first access creates it and starts the monitor service (full USB scan
    /// plus hotplug monitoring).
    /// <para>设备面板单例：顶栏设备选择器与设备面板共享同一实例，
    /// 首次访问时创建并启动设备监视服务（USB 全量扫描 + 热插拔监听）。</para>
    /// </summary>
    public Devices.DeviceViewModel DeviceVM => Devices.DeviceViewModel.EnsureInstance();

    /// <summary>
    /// Window title (including the current solution name), updated when a solution is opened or closed.
    /// <para>窗口标题（含当前解决方案名），随解决方案打开/关闭更新。</para>
    /// </summary>
    [ObservableProperty]
    private string _windowTitle = "UotanStudio";

    /// <summary>
    /// Constructs the main view model, binding the node editor reference to the menu and subscribing to
    /// solution-context changes to keep the window title in sync.
    /// <para>构造主视图模型，绑定节点编辑器引用到菜单，并订阅解决方案上下文变更以同步窗口标题。</para>
    /// </summary>
    public MainViewModel() : this(ShellComposition.Create()) { }

    internal MainViewModel(ShellComponents components)
    {
        _components = components;
        MenuVM = components.Menu;
        NodeEditorVM = components.Nodes;
        SettingsVM = components.Settings;
        WorkspaceVM = components.Workspace;
        _autoSave = components.AutoSave;
        _modifications = components.Modifications;
        _modifications.Changed += OnModificationsChanged;
        WorkspaceVM.PropertyChanged += OnWorkspacePropertyChanged;
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);
        WeakReferenceMessenger.Default.Register<LanguageStateChangedMessage>(this, OnLanguageStateChanged);
        UpdateWindowTitle();
    }

    private void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message) => UpdateWindowTitle();

    private void OnLanguageStateChanged(object recipient, LanguageStateChangedMessage message) => UpdateWindowTitle();

    private void OnModificationsChanged(object? sender, EventArgs e) => UpdateWindowTitle();

    private void OnWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceVM.Layout)) _modifications.Refresh();
    }

    private void UpdateWindowTitle()
    {
        var context = ProjectContextService.Instance;
        var applicationTitle = LanguageService.Instance.GetString("MainWindow_Title");
        var title = context.IsProjectOpen && context.SolutionName is { Length: > 0 } name
            ? $"{name} - {applicationTitle}"
            : applicationTitle;
        WindowTitle = _modifications.HasUnsavedChanges ? "\u25cf " + title : title;
    }

    /// <summary>Stops workspace persistence and releases subscriptions.<para>停止工作区持久化并释放订阅。</para></summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WorkspaceVM.PropertyChanged -= OnWorkspacePropertyChanged;
        _modifications.Changed -= OnModificationsChanged;
        _components.Dispose();
        // Each open view runs its own close logic (mirror links stop, editors release files) before the process ends.
        // 每个已打开视图在进程结束前运行自身的关闭逻辑（投屏链路停止、编辑器释放文件）。
        WorkspaceVM.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
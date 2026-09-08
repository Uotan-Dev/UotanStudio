using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Workspace;

namespace UotanStudio.ViewModels.Shell;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Services.Workspace.WorkspaceAutoSaveService _autoSave;
    private readonly Services.Workspace.WorkspaceModificationTracker _modifications;
    internal Task InitializeAutoSaveAsync() => _autoSave.InitializeAsync();
    internal void ResumeAutoSave() => _autoSave.Resume();
    internal async Task<bool> FlushAutoSaveAsync()
    {
        SettingsVM.FlushPendingSave();
        return await _autoSave.SaveOnCloseAsync();
    }
    public MenuViewModel MenuVM { get; } = new();
    public WorkspaceViewModel WorkspaceVM { get; }
    public NodeEditorViewModel NodeEditorVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();

    /// <summary>
    /// <para>设备面板单例：顶栏设备选择器与设备面板共享同一实例，
    /// 首次访问时创建并启动设备监视服务（USB 全量扫描 + 热插拔监听）。</para>
    /// Device panel singleton: shared by the top-bar device selector and the device
    /// panel; first access creates it and starts the monitor service (full USB scan
    /// plus hotplug monitoring).
    /// </summary>
    public ViewModels.Devices.DeviceViewModel DeviceVM => ViewModels.Devices.DeviceViewModel.EnsureInstance();

    /// <summary>
    /// <para>窗口标题（含当前解决方案名），随解决方案打开/关闭更新。</para>
    /// Window title (including the current solution name), updated when a solution is opened or closed.
    /// </summary>
    [ObservableProperty]
    private string _windowTitle = "UotanStudio";

    /// <summary>
    /// <para>构造主视图模型，绑定节点编辑器引用到菜单，并订阅解决方案上下文变更以同步窗口标题。</para>
    /// Constructs the main view model, binding the node editor reference to the menu and subscribing to
    /// solution-context changes to keep the window title in sync.
    /// </summary>
    public MainViewModel()
    {
        WorkspaceVM = new WorkspaceViewModel(new WorkspaceDockFactory(SettingsVM.Ai));
        _modifications = new(() => WorkspaceVM.Layout);
        _modifications.Changed += OnModificationsChanged;
        WorkspaceVM.PropertyChanged += OnWorkspacePropertyChanged;
        MenuVM.NodeEditorVM = NodeEditorVM;
        MenuVM.WorkspaceVM = WorkspaceVM;
        _autoSave = new(WorkspaceVM, SettingsVM, ProjectOperationsService.Instance);
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);
        UpdateWindowTitle();
    }

    private void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message) => UpdateWindowTitle();

    private void OnModificationsChanged(object? sender, EventArgs e) => UpdateWindowTitle();

    private void OnWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceVM.Layout)) _modifications.Refresh();
    }

    private void UpdateWindowTitle()
    {
        var context = ProjectContextService.Instance;
        var title = context.IsProjectOpen && context.SolutionName is { Length: > 0 } name
            ? $"{name} - UotanStudio"
            : "UotanStudio";
        WindowTitle = _modifications.HasUnsavedChanges ? "\u25cf " + title : title;
    }

    /// <summary>Stops workspace persistence and releases subscriptions.<para>停止工作区持久化并释放订阅。</para></summary>
    public void Dispose()
    {
        _autoSave.Dispose();
        WorkspaceVM.PropertyChanged -= OnWorkspacePropertyChanged;
        _modifications.Changed -= OnModificationsChanged;
        _modifications.Dispose();
        SettingsVM.Dispose();
        WorkspaceVM.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.ViewModels.Shell;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Services.Workspace.WorkspaceAutoSaveService _autoSave;
    internal Task InitializeAutoSaveAsync() => _autoSave.InitializeAsync();
    internal async Task FlushAutoSaveAsync()
    {
        SettingsVM.FlushPendingSave();
        await _autoSave.FlushAsync();
    }
    public MenuViewModel MenuVM { get; } = new();
    public WorkspaceViewModel WorkspaceVM { get; } = new();
    public NodeEditorViewModel NodeEditorVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();

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
        MenuVM.NodeEditorVM = NodeEditorVM;
        MenuVM.WorkspaceVM = WorkspaceVM;
        _autoSave = new(WorkspaceVM, SettingsVM, ProjectOperationsService.Instance);
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);
        UpdateWindowTitle();
    }

    private void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message) => UpdateWindowTitle();

    private void UpdateWindowTitle()
    {
        var context = ProjectContextService.Instance;
        WindowTitle = context.IsProjectOpen && context.SolutionName is { Length: > 0 } name
            ? $"{name} - UotanStudio"
            : "UotanStudio";
    }

    /// <summary>Stops workspace persistence and releases subscriptions.<para>停止工作区持久化并释放订阅。</para></summary>
    public void Dispose()
    {
        _autoSave.Dispose();
        SettingsVM.Dispose();
        WorkspaceVM.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}

using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Projects;

/// <summary>Desktop compatibility facade over the independent project lifecycle library.
/// <para>包装独立项目生命周期库的桌面兼容门面。</para></summary>
public sealed class ProjectOperationsService
{
    private static readonly Lazy<ProjectOperationsService> LazyInstance = new(() => new());
    public static ProjectOperationsService Instance => LazyInstance.Value;
    private readonly IProjectWorkspace _workspace;
    private readonly ProjectSession _session;
    private object? _workspaceOwner;
    internal bool IsWorkspaceAttached => _workspaceOwner is not null;

    /// <summary>Attaches the one desktop shell supported by the global firmware metadata context.
    /// <para>附着全局固件元数据上下文所支持的唯一桌面外壳。</para></summary>
    /// <param name="nodes">The node editor binding to attach.<para>要附着的节点编辑绑定。</para></param>
    /// <param name="workspace">The workspace binding to attach.<para>要附着的工作区绑定。</para></param>
    /// <returns>A token that detaches the shell when disposed.<para>释放时解除外壳附着的令牌。</para></returns>
    internal IDisposable AttachWorkspace(NodeEditorViewModel nodes, WorkspaceViewModel workspace)
    {
        if (_workspaceOwner is not null) throw new InvalidOperationException("A desktop shell already owns this project service.");
        var owner = new object();
        _workspaceOwner = owner;
        NodeEditorVM = nodes;
        WorkspaceVM = workspace;
        return new WorkspaceBinding(this, owner, nodes, workspace);
    }

    private sealed class WorkspaceBinding(ProjectOperationsService service, object owner,
        NodeEditorViewModel nodes, WorkspaceViewModel workspace) : IDisposable
    {
        public void Dispose()
        {
            if (!ReferenceEquals(service._workspaceOwner, owner)) return;
            if (ReferenceEquals(service.NodeEditorVM, nodes)) service.NodeEditorVM = null;
            if (ReferenceEquals(service.WorkspaceVM, workspace)) service.WorkspaceVM = null;
            service._workspaceOwner = null;
        }
    }

    static ProjectOperationsService()
    {
        ProjectDiagnostics.Message += (level, source, message) => LogService.Instance.Write(level switch
        {
            ProjectLogLevel.Warn => LogLevel.Warn,
            ProjectLogLevel.Error => LogLevel.Error,
            _ => LogLevel.Info
        }, source, message);
    }

    internal ProjectOperationsService() : this(new ProjectWorkspaceCoordinator()) { }
    internal ProjectOperationsService(IProjectWorkspace workspace)
    {
        _workspace = workspace;
        _session = new(workspace, ProjectContextService.Instance.Core, OperationHistoryService.Instance.Core);
        _session.CaptureState = CaptureSolutionState;
        _session.Changed += OnSessionChanged;
        _session.SolutionReloaded += OnSolutionReloaded;
    }

    private const string PanelStateKey = "panels";
    /// <summary>Delegate slot that captures the delivery plan element before solution saves.
    /// <para>在解决方案保存前捕获交付计划元素的委托槽位。</para></summary>
    /// <value>The capture delegate, or <see langword="null"/> when unset.<para>捕获委托；未设置时为 <see langword="null"/>。</para></value>
    internal Func<JsonElement?>? CaptureDeliveryState { get; set; }
    /// <summary>Delivery plan element carried by the most recently opened/saved solution; null when absent.
    /// <para>最近打开或保存的解决方案所携带的交付计划元素；缺失时为 <see langword="null"/>。</para></summary>
    /// <value>The pending delivery plan element, or <see langword="null"/> when absent.<para>待处理的交付计划元素；缺失时为 <see langword="null"/>。</para></value>
    internal JsonElement? PendingDeliveryState => _session.SolutionState is { } state
        && state.TryGetValue(DeliveryPlanStore.StateKey, out var delivery) ? delivery : null;
    /// <summary>Persists the delivery plan and panel visibility by rewriting only the solution file.
    /// <para>仅重写解决方案文件即可持久化交付计划与工作区可见性。</para></summary>
    public Task PersistSolutionStateAsync() => _session.PersistSolutionStateAsync();

    /// <summary>Holds the project session gate while a patch graph reads and publishes project files.
    /// <para>在补丁图读取并发布项目文件期间持有项目会话门。</para></summary>
    /// <param name="operation">The gated asynchronous operation to run.<para>要受门控执行的异步操作。</para></param>
    /// <param name="cancellationToken">Token that cancels the wait and the operation.<para>用于取消等待与操作的令牌。</para></param>
    internal Task ExecuteFileOperationAsync(Func<Task> operation, CancellationToken cancellationToken)
        => _session.ExecuteFileOperationAsync(operation, cancellationToken);

    private Dictionary<string, JsonElement>? CaptureSolutionState()
    {
        Dictionary<string, JsonElement> state = [];
        if (CaptureDeliveryState?.Invoke() is { ValueKind: JsonValueKind.Object } delivery)
            state[DeliveryPlanStore.StateKey] = delivery;
        else if (_session.SolutionState is { } previous && previous.TryGetValue(DeliveryPlanStore.StateKey, out var kept))
            state[DeliveryPlanStore.StateKey] = kept;
        var panels = WorkspacePanelService.Instance.CaptureVisibility();
        state[PanelStateKey] = JsonSerializer.SerializeToElement(panels, AppSettingsJsonContext.Default.DictionaryStringBoolean);
        return state;
    }

    private Dictionary<string, JsonElement>? _restoredState;
    private int _solutionReloadGeneration;
    internal int SolutionReloadGeneration => _solutionReloadGeneration;
    private void OnSolutionReloaded() => _solutionReloadGeneration++;

    private void OnSessionChanged()
    {
        if (!ReferenceEquals(_restoredState, _session.SolutionState))
        {
            _restoredState = _session.SolutionState;
            if (_session.SolutionState is { } state && state.TryGetValue(PanelStateKey, out var panels)
                && panels.Deserialize(AppSettingsJsonContext.Default.DictionaryStringBoolean) is { Count: > 0 } visibility)
                WorkspacePanelService.Instance.RestoreVisibility(visibility);
        }
        WeakReferenceMessenger.Default.Send(new ProjectContextChangedMessage());
    }

    private ProjectWorkspaceCoordinator DesktopWorkspace => _workspace as ProjectWorkspaceCoordinator
        ?? throw new InvalidOperationException("This project service uses an explicitly supplied workspace adapter.");
    public NodeEditorViewModel? NodeEditorVM { get => DesktopWorkspace.NodeEditor; set => DesktopWorkspace.NodeEditor = value; }
    public WorkspaceViewModel? WorkspaceVM { get => DesktopWorkspace.Workspace; set => DesktopWorkspace.Workspace = value; }
    public string? CurrentProjectPath { get => _session.CurrentProjectPath; set => _session.CurrentProjectPath = value; }
    internal bool IsBusy => _session.IsBusy;
    internal bool IsImageOperationRunning => DesktopWorkspace.IsImageOperationRunning;
    internal Func<Task<bool>>? BeforeProjectChangeAsync { get => _session.BeforeProjectChangeAsync; set => _session.BeforeProjectChangeAsync = value; }
    internal Task WaitForIdleAsync() => _session.WaitForIdleAsync();
    internal async Task AutoSaveAsync(bool persistProject = true, bool persistPageState = true)
    {
        if (IsBusy)
        {
            return;
        }
        await _session.AutoSaveAsync(persistProject, persistPageState);
        AttachedNodeEditor?.MarkAllSaved();
    }
    public Task CreateNewSolutionAsync(string path, CancellationToken cancellationToken = default)
        => _session.CreateNewSolutionAsync(path, cancellationToken);
    public Task<bool> OpenProjectFromPathAsync(string path) => _session.OpenProjectFromPathAsync(path);
    internal ProjectOpenFailure OpenFailure => _session.OpenFailure;
    internal int? OpenFailureStoredVersion => _session.OpenFailureStoredVersion;
    internal string? OpenFailurePath => _session.OpenFailurePath;
    public async Task<bool> SaveAsync(bool saveActiveDocument = true)
    {
        var saved = await _session.SaveAsync(saveActiveDocument);
        if (saved)
        {
            AttachedNodeEditor?.MarkAllSaved();
        }
        return saved;
    }

    private NodeEditorViewModel? AttachedNodeEditor
        => _workspace is ProjectWorkspaceCoordinator coordinator ? coordinator.NodeEditor : null;
    /// <summary>Saves the project under a new path and clears every node-editor dirty flag after the save succeeds.
    /// <para>把工程另存为新路径，并在保存成功后清除节点编辑器的全部脏标记。</para></summary>
    /// <param name="path">Destination solution file path; the session switches to it only after publication succeeds.
    /// <para>目标解决方案文件路径；仅在发布成功后才会切换到该路径。</para></param>
    /// <returns>A task that completes after the copy is published and the session root switched.
    /// <para>副本发布并切换会话根目录后完成的任务。</para></returns>
    public async Task SaveAsAsync(string path)
    {
        await _session.SaveAsAsync(path);
        AttachedNodeEditor?.MarkAllSaved();
    }
    public Task ImportFilesAsync(string[] paths) => _session.ImportFilesAsync(paths);
    public Task AddSubProjectAsync(string fwPath) => _session.AddSubProjectAsync(fwPath);
    public Task ImportSubProjectAsync(string dirPath) => _session.ImportSubProjectAsync(dirPath);
    public Task SaveActiveDocumentAsync() => _session.SaveActiveDocumentAsync();
    public Task PersistToAsync(string path) => _session.PersistToAsync(path);
    internal Task PersistToAsync(string path, bool persistPageState) => _session.PersistToAsync(path, persistPageState);
    public Task<bool> SwitchSubProjectAsync(string name) => _session.SwitchSubProjectAsync(name, persistCurrent: false);
    public Task<bool> CloseAsync() => _session.CloseAsync();
    internal Task<bool> CloseAfterSaveAsync() => _session.CloseAsync(confirmChange: false);
    public ProjectSessionState State => _session.State;
    public Task<bool> RenameSubProjectAsync(string name, string newName, CancellationToken cancellationToken = default)
        => _session.RenameSubProjectAsync(name, newName, cancellationToken);
    public Task<bool> RemoveSubProjectAsync(string name, CancellationToken cancellationToken = default)
        => _session.RemoveSubProjectAsync(name, cancellationToken);
    public Task<bool> CreateNodeProjectAsync(string name, CancellationToken cancellationToken = default)
        => _session.CreateNodeProjectAsync(name, cancellationToken);
    public Task<bool> OpenNodeProjectAsync(string name, CancellationToken cancellationToken = default)
        => _session.OpenNodeProjectAsync(name, cancellationToken);
    public Task<bool> CloseNodeProjectAsync(string name, CancellationToken cancellationToken = default)
        => _session.CloseNodeProjectAsync(name, cancellationToken);
    public Task<bool> RemoveNodeProjectAsync(string name, CancellationToken cancellationToken = default)
        => _session.RemoveNodeProjectAsync(name, cancellationToken);
    public Task<bool> SetActiveNodeProjectAsync(string name, CancellationToken cancellationToken = default)
        => _session.SetActiveNodeProjectAsync(name, cancellationToken);
    public Task ExportPackageAsync(string output, CancellationToken cancellationToken = default)
        => _session.ExportPackageAsync(output, cancellationToken);
    public Task<bool> ImportPackageAsync(string archive, string destination, CancellationToken cancellationToken = default)
        => _session.ImportPackageAsync(archive, destination, cancellationToken);
    public Task<bool> UndoAsync(CancellationToken cancellationToken = default) => _session.UndoAsync(cancellationToken);
    public Task<bool> RedoAsync(CancellationToken cancellationToken = default) => _session.RedoAsync(cancellationToken);
    internal bool CanUndo => _session.History.CanUndo;
    internal bool CanRedo => _session.History.CanRedo;
    public Task ExportAsync(string output, CancellationToken cancellationToken = default) => _session.ExportAsync(output, cancellationToken);
}
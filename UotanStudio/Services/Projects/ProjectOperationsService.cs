using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Projects;

/// <summary>Desktop compatibility facade over the independent project lifecycle library.</summary>
public sealed class ProjectOperationsService
{
    private static readonly Lazy<ProjectOperationsService> LazyInstance = new(() => new());
    public static ProjectOperationsService Instance => LazyInstance.Value;
    private readonly IProjectWorkspace _workspace;
    private readonly ProjectSession _session;
    private object? _workspaceOwner;
    internal bool IsWorkspaceAttached => _workspaceOwner is not null;

    /// <summary>Attaches the one desktop shell supported by the global firmware metadata context.</summary>
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
        // 交付计划与面板可见性随解决方案文件持久化：保存前捕获，打开后恢复。
        // Delivery plan and panel visibility persist inside the solution file: capture before saves, restore after opens.
        _session.CaptureState = CaptureSolutionState;
        _session.Changed += OnSessionChanged;
        _session.SolutionReloaded += OnSolutionReloaded;
    }

    private const string PanelStateKey = "panels";
    /// <summary>Host hook (delivery page) that captures the delivery plan element before solution saves.</summary>
    internal Func<JsonElement?>? CaptureDeliveryState { get; set; }
    /// <summary>Delivery plan element carried by the most recently opened/saved solution; null when absent.</summary>
    internal JsonElement? PendingDeliveryState => _session.SolutionState is { } state
        && state.TryGetValue(DeliveryPlanStore.StateKey, out var delivery) ? delivery : null;
    /// <summary>Persists the delivery plan and panel visibility by rewriting only the solution file.</summary>
    public Task PersistSolutionStateAsync() => _session.PersistSolutionStateAsync();

    /// <summary>Holds the project session gate while a patch graph reads and publishes project files.</summary>
    internal Task ExecuteFileOperationAsync(Func<Task> operation, CancellationToken cancellationToken)
        => _session.ExecuteFileOperationAsync(operation, cancellationToken);

    private Dictionary<string, JsonElement>? CaptureSolutionState()
    {
        Dictionary<string, JsonElement> state = [];
        if (CaptureDeliveryState?.Invoke() is { ValueKind: JsonValueKind.Object } delivery)
            state[DeliveryPlanStore.StateKey] = delivery;
        // 交付页尚未加载时保留解决方案文件中原有的交付计划，避免被空捕获覆盖。
        // Keep the delivery plan already stored in the solution file until the delivery page has loaded and captures.
        else if (_session.SolutionState is { } previous && previous.TryGetValue(DeliveryPlanStore.StateKey, out var kept))
            state[DeliveryPlanStore.StateKey] = kept;
        var panels = WorkspacePanelService.Instance.CaptureVisibility();
        state[PanelStateKey] = JsonSerializer.SerializeToElement(panels, AppSettingsJsonContext.Default.DictionaryStringBoolean);
        return state;
    }

    private Dictionary<string, JsonElement>? _restoredState;
    // 解决方案完整重载计数器：每次打开/添加/删除子项目的完整重载递增。订阅方在每次刷新时比较它，
    // 因此即使面板当时不在可视树中、错过了 ProjectContextChangedMessage，也会在重新进入时强制重载。
    // Full-reload counter, incremented on every open/add/remove-sub-project reload. Subscribers compare it on
    // every refresh, so a panel that was detached and missed ProjectContextChangedMessage still reloads later.
    private int _solutionReloadGeneration;
    internal int SolutionReloadGeneration => _solutionReloadGeneration;
    private void OnSolutionReloaded() => _solutionReloadGeneration++;

    private void OnSessionChanged()
    {
        // 仅在解决方案状态更换（打开/另存）时恢复面板可见性，不覆盖用户随后的手动开关。
        // Restore panel visibility only when the solution state is replaced (open/save-as), never over later manual toggles.
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
    internal Task AutoSaveAsync(bool persistProject = true, bool persistPageState = true)
        => _session.AutoSaveAsync(persistProject, persistPageState);
    public Task CreateNewSolutionAsync(string path, CancellationToken cancellationToken = default)
        => _session.CreateNewSolutionAsync(path, cancellationToken);
    public Task<bool> OpenProjectFromPathAsync(string path) => _session.OpenProjectFromPathAsync(path);
    internal ProjectOpenFailure OpenFailure => _session.OpenFailure;
    internal int? OpenFailureStoredVersion => _session.OpenFailureStoredVersion;
    internal string? OpenFailurePath => _session.OpenFailurePath;
    public Task<bool> SaveAsync(bool saveActiveDocument = true) => _session.SaveAsync(saveActiveDocument);
    public Task SaveAsAsync(string path) => _session.SaveAsAsync(path);
    public Task ImportFilesAsync(string[] paths) => _session.ImportFilesAsync(paths);
    public Task AddSubProjectAsync(string fwPath) => _session.AddSubProjectAsync(fwPath);
    public Task ImportSubProjectAsync(string dirPath) => _session.ImportSubProjectAsync(dirPath);
    public Task SaveActiveDocumentAsync() => _session.SaveActiveDocumentAsync();
    public Task PersistToAsync(string path) => _session.PersistToAsync(path);
    internal Task PersistToAsync(string path, bool persistPageState) => _session.PersistToAsync(path, persistPageState);
    // BeforeProjectChangeAsync owns the desktop AutoSave policy; do not force a save when it is disabled.
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
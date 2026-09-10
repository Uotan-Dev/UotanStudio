using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
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
        _session.Changed += () => WeakReferenceMessenger.Default.Send(new ProjectContextChangedMessage());
    }

    private ProjectWorkspaceCoordinator DesktopWorkspace => _workspace as ProjectWorkspaceCoordinator
        ?? throw new InvalidOperationException("This project service uses an explicitly supplied workspace adapter.");
    public NodeEditorViewModel? NodeEditorVM { get => DesktopWorkspace.NodeEditor; set => DesktopWorkspace.NodeEditor = value; }
    public WorkspaceViewModel? WorkspaceVM { get => DesktopWorkspace.Workspace; set => DesktopWorkspace.Workspace = value; }
    public string? CurrentProjectPath { get => _session.CurrentProjectPath; set => _session.CurrentProjectPath = value; }
    internal bool IsBusy => _session.IsBusy;
    internal Func<Task<bool>>? BeforeProjectChangeAsync { get => _session.BeforeProjectChangeAsync; set => _session.BeforeProjectChangeAsync = value; }
    internal Task WaitForIdleAsync() => _session.WaitForIdleAsync();
    internal Task AutoSaveAsync(bool persistProject = true, bool persistPageState = true)
        => _session.AutoSaveAsync(persistProject, persistPageState);
    public Task CreateNewSolutionAsync(string path) => _session.CreateNewSolutionAsync(path);
    public Task<bool> OpenProjectFromPathAsync(string path) => _session.OpenProjectFromPathAsync(path);
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
    public Task ExportPackageAsync(string output, CancellationToken cancellationToken = default)
        => _session.ExportPackageAsync(output, cancellationToken);
    public Task<bool> ImportPackageAsync(string archive, string destination, CancellationToken cancellationToken = default)
        => _session.ImportPackageAsync(archive, destination, cancellationToken);
    public Task<bool> UndoAsync(CancellationToken cancellationToken = default) => _session.UndoAsync(cancellationToken);
    public Task<bool> RedoAsync(CancellationToken cancellationToken = default) => _session.RedoAsync(cancellationToken);
    public Task ExportAsync(string output, CancellationToken cancellationToken = default) => _session.ExportAsync(output, cancellationToken);
}
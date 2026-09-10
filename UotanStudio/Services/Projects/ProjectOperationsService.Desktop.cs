using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Projects;

/// <summary>Legacy desktop bindings; operation logic uses IProjectWorkspace instead.</summary>
public sealed partial class ProjectOperationsService
{
    private static readonly Lazy<ProjectOperationsService> LazyInstance = new(() => new ProjectOperationsService());

    /// <summary>Shared desktop project operations, also used by the shell composition root.</summary>
    public static ProjectOperationsService Instance => LazyInstance.Value;

    internal ProjectOperationsService() : this(new ProjectWorkspaceCoordinator()) { }

    private ProjectWorkspaceCoordinator DesktopWorkspace => _workspace as ProjectWorkspaceCoordinator
        ?? throw new InvalidOperationException("This project service uses an explicitly supplied workspace adapter.");

    /// <summary>Compatibility binding for the current node editor.</summary>
    public NodeEditorViewModel? NodeEditorVM
    {
        get => DesktopWorkspace.NodeEditor;
        set => DesktopWorkspace.NodeEditor = value;
    }

    /// <summary>Compatibility binding for the current document workspace.</summary>
    public WorkspaceViewModel? WorkspaceVM
    {
        get => DesktopWorkspace.Workspace;
        set => DesktopWorkspace.Workspace = value;
    }
}
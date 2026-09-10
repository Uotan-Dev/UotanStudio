using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Workspace;

namespace UotanStudio.Composition;

/// <summary>Explicitly assembles one shell, reusing the existing application project service.</summary>
internal static class ShellComposition
{
    internal static ShellComponents Create()
    {
        var projects = ProjectOperationsService.Instance;
        if (projects.IsWorkspaceAttached) throw new InvalidOperationException("Only one desktop project shell may be active.");
        var menu = new MenuViewModel(projects);
        var nodes = new NodeEditorViewModel();
        var settings = new SettingsViewModel();
        var workspace = new WorkspaceViewModel(new WorkspaceDockFactory(settings.Ai));
        return new ShellComponents(projects, menu, nodes, settings, workspace);
    }
}

/// <summary>Owns shell objects; shared application services remain alive when the shell is released.</summary>
internal sealed class ShellComponents : IDisposable
{
    private bool _disposed;
    private readonly IDisposable _projectBinding;
    internal ProjectOperationsService Projects { get; }
    internal MenuViewModel Menu { get; }
    internal NodeEditorViewModel Nodes { get; }
    internal SettingsViewModel Settings { get; }
    internal WorkspaceViewModel Workspace { get; }
    internal WorkspaceAutoSaveService AutoSave { get; }
    internal WorkspaceModificationTracker Modifications { get; }

    internal ShellComponents(ProjectOperationsService projects, MenuViewModel menu,
        NodeEditorViewModel nodes, SettingsViewModel settings, WorkspaceViewModel workspace)
    {
        Projects = projects;
        Menu = menu;
        Nodes = nodes;
        Settings = settings;
        Workspace = workspace;
        Modifications = new(() => workspace.Layout);
        _projectBinding = projects.AttachWorkspace(nodes, workspace);
        AutoSave = new(workspace, settings, projects);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AutoSave.Dispose();
        Modifications.Dispose();
        Menu.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(Nodes);
        _projectBinding.Dispose();
        try { Settings.Dispose(); }
        finally { Workspace.Dispose(); }
    }
}
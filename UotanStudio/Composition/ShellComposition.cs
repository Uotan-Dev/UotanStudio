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
    /// <summary>
    /// <para>兼容入口：使用 <see cref="ProjectOperationsService.Instance"/> 单例组装默认 Shell。</para>
    /// Compatibility entry: assembles the default shell around the
    /// <see cref="ProjectOperationsService.Instance"/> singleton.
    /// </summary>
    internal static ShellComponents Create() => Create(ProjectOperationsService.Instance);

    /// <summary>
    /// <para>显式装配：接收工程操作服务（桌面单例身份与释放归属不变，仍由应用进程持有），
    /// Shell 内对象由返回的 <see cref="ShellComponents"/> 持有并在 Dispose 时按
    /// AutoSave → Modifications → Menu/Nodes → projectBinding → Settings → Workspace 顺序释放。</para>
    /// Explicit assembly: receives the project operations service (its desktop singleton identity
    /// and release ownership are unchanged — still held by the application process); shell-scoped
    /// objects are owned by the returned <see cref="ShellComponents"/> and released on Dispose in
    /// the order AutoSave → Modifications → Menu/Nodes → projectBinding → Settings → Workspace.
    /// </summary>
    internal static ShellComponents Create(ProjectOperationsService projects)
    {
        if (projects is null) throw new ArgumentNullException(nameof(projects));
        if (projects.IsWorkspaceAttached) throw new InvalidOperationException("Only one desktop project shell may be active.");
        var menu = new MenuViewModel(projects);
        var nodes = new NodeEditorViewModel();
        var settings = new SettingsViewModel();
        var workspace = new WorkspaceViewModel(new WorkspaceDockFactory(settings.Ai));
        return new ShellComponents(projects, menu, nodes, settings, workspace);
    }
}

/// <summary>
/// <para>持有 Shell 作用域对象；工程操作服务与应用级服务（设备监视、日志、语言）的生命周期长于
/// Shell，不在此释放。装配来源见 <see cref="ShellComposition.Create(ProjectOperationsService)"/>。</para>
/// Owns shell-scoped objects; the project operations service and application-scoped services
/// (device monitoring, logging, language) outlive the shell and are not released here. Assembly
/// source: <see cref="ShellComposition.Create(ProjectOperationsService)"/>.
/// </summary>
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
        Nodes.Dispose();
        _projectBinding.Dispose();
        try { Settings.Dispose(); }
        finally { Workspace.Dispose(); }
    }
}
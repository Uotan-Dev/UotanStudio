using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Workspace;

namespace UotanStudio.Composition;

/// <summary>
/// Assembles one shell explicitly around a supplied project operations service.
/// <para>围绕给定的工程操作服务显式装配一个 Shell。</para>
/// </summary>
internal static class ShellComposition
{
    /// <summary>
    /// Compatibility entry: assembles the default shell around the shared project operations service.
    /// <para>兼容入口：围绕共享的工程操作服务组装默认 Shell。</para>
    /// </summary>
    /// <returns>The assembled shell components. <para>装配得到的 Shell 组件。</para></returns>
    internal static ShellComponents Create() => Create(ProjectOperationsService.Instance);

    /// <summary>
    /// Builds the shell components in stages, yielding through the supplied progress callback between
    /// them and disposing every partially built component when construction fails.
    /// <para>分阶段构建 Shell 组件，阶段之间经给定的进度回调让出，构建失败时释放已部分构建的全部组件。</para>
    /// </summary>
    /// <param name="report">Publishes each construction stage. <para>上报每个构建阶段。</para></param>
    /// <param name="appliedCultureName">The language preference already applied at startup, or null for independent construction. <para>启动时已应用的语言偏好；独立构建时为 null。</para></param>
    /// <returns>The assembled shell components. <para>装配得到的 Shell 组件。</para></returns>
    /// <exception cref="InvalidOperationException">A workspace is already attached to the shared service. <para>共享服务上已经附加了工作区。</para></exception>
    internal static async Task<ShellComponents> CreateAsync(Func<SplashPhase, Task> report,
        string? appliedCultureName = null)
    {
        var projects = ProjectOperationsService.Instance;
        if (projects.IsWorkspaceAttached)
        {
            throw new InvalidOperationException("Only one desktop project shell may be active.");
        }
        MenuViewModel? menu = null;
        SettingsViewModel? settings = null;
        NodeEditorViewModel? nodes = null;
        WorkspaceViewModel? workspace = null;
        try
        {
            await report(SplashPhase.Services);
            menu = new MenuViewModel(projects);
            await report(SplashPhase.Nodes);
            nodes = new NodeEditorViewModel();
            await report(SplashPhase.Workspace);
            var appSettings = await Task.Run(SettingsPersistenceService.Instance.LoadSettings);
            settings = new SettingsViewModel(appSettings,
                applyLanguage: appliedCultureName is null
                    || !string.Equals(appSettings.CultureName, appliedCultureName, StringComparison.Ordinal));
            workspace = new WorkspaceViewModel(new WorkspaceDockFactory(settings.Ai));
            return new ShellComponents(projects, menu, nodes, settings, workspace);
        }
        catch
        {
            menu?.Dispose();
            nodes?.Dispose();
            settings?.Dispose();
            workspace?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Explicit assembly: builds one shell around the given project operations service.
    /// <para>显式装配：围绕给定的工程操作服务构建一个 Shell。</para>
    /// </summary>
    /// <param name="projects">The project operations service the shell binds to. <para>Shell 所绑定的工程操作服务。</para></param>
    /// <returns>The assembled shell components owning the shell-scoped objects. <para>持有 Shell 作用域对象的装配结果。</para></returns>
    /// <exception cref="ArgumentNullException">The supplied service is null. <para>给定的服务为 null。</para></exception>
    /// <exception cref="InvalidOperationException">The service already has an attached workspace. <para>该服务已经附加了工作区。</para></exception>
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
/// Owns the shell-scoped objects; the project operations service and the application-scoped
/// services outlive the shell and are not released here.
/// <para>持有 Shell 作用域对象；工程操作服务与应用级服务的生命周期长于 Shell，不在此释放。</para>
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
        Nodes.Documents = new WorkspaceWorkflowDocuments(workspace);
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
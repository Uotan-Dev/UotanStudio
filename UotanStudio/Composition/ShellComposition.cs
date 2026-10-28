using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using UotanStudio.Services;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.Services.Workspace;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views;
using UotanStudio.Views.Workspace;

namespace UotanStudio.Composition;

/// <summary>
/// Assembles one shell explicitly around a supplied project operations service, preferring the
/// composition-root singletons when the container is initialized.
/// <para>围绕给定的工程操作服务显式装配一个 Shell；容器已初始化时优先使用组合根单例。</para>
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
    /// Builds the menu view model over the supplied services; the composition root and the independent shell
    /// assembly share this definition so the menu never learns a second construction pattern.
    /// <para>围绕给定服务构建菜单视图模型；组合根与独立装配共用这一处定义，菜单不会出现第二种构造方式。</para>
    /// </summary>
    /// <param name="projects">The project operations service the menu drives. <para>菜单驱动的工程操作服务。</para></param>
    /// <param name="windows">Window service used by file pickers and dialogs. <para>文件选择与对话框使用的窗口服务。</para></param>
    /// <param name="localizer">Localized string lookup. <para>本地化字符串查询。</para></param>
    /// <param name="log">Unified log sink. <para>统一日志接收端。</para></param>
    /// <returns>The menu view model. <para>菜单视图模型。</para></returns>
    internal static MenuViewModel CreateMenu(ProjectOperationsService projects, IWindowService windows,
        ILocalizer localizer, IUnifiedLog log) => new(projects, windows, localizer, log);

    /// <summary>Builds the node editor view model; every shell assembly shares this definition.
    /// <para>构建节点编辑器视图模型；每种 shell 装配共用这一处定义。</para></summary>
    /// <returns>The node editor view model. <para>节点编辑器视图模型。</para></returns>
    internal static NodeEditorViewModel CreateNodes() => new();

    /// <summary>Builds the settings view model over the supplied settings snapshot.
    /// <para>围绕给定的设置快照构建设置视图模型。</para></summary>
    /// <param name="settings">The persisted settings to edit. <para>要编辑的持久化设置。</para></param>
    /// <param name="applyLanguage">Whether the model applies the stored culture on construction.
    /// <para>构造时是否应用已存储的区域性。</para></param>
    /// <returns>The settings view model. <para>设置视图模型。</para></returns>
    internal static SettingsViewModel CreateSettings(AppSettings settings, bool applyLanguage)
        => new(settings, applyLanguage);

    /// <summary>Builds the workspace view model whose dock factory follows the AI setting.
    /// <para>构建工作区视图模型，其停靠工厂跟随 AI 设置。</para></summary>
    /// <param name="settings">The settings view model carrying the AI preference. <para>携带 AI 偏好的设置视图模型。</para></param>
    /// <returns>The workspace view model. <para>工作区视图模型。</para></returns>
    internal static WorkspaceViewModel CreateWorkspace(SettingsViewModel settings)
        => new(new WorkspaceDockFactory(settings.Ai));

    /// <summary>
    /// Builds the shell components in stages, yielding through the supplied progress callback between
    /// them. View models come from the composition root when <see cref="App.Services"/> is initialized,
    /// otherwise they are constructed explicitly; every locally built component is disposed when
    /// construction fails, while container-owned singletons stay with the container.
    /// <para>分阶段构建 Shell 组件，阶段之间经给定的进度回调让出。容器（<see cref="App.Services"/>）已初始化时
    /// 视图模型取自组合根，否则显式构造；构建失败时只释放本地构建的组件，容器持有的单例仍归容器所有。</para>
    /// </summary>
    /// <param name="report">Publishes each construction stage. <para>上报每个构建阶段。</para></param>
    /// <param name="appliedCultureName">The language preference already applied at startup, or null for independent construction. <para>启动时已应用的语言偏好；独立构建时为 null。</para></param>
    /// <returns>The assembled shell components. <para>装配得到的 Shell 组件。</para></returns>
    /// <exception cref="InvalidOperationException">A workspace is already attached to the shared service. <para>共享服务上已经附加了工作区。</para></exception>
    internal static async Task<ShellComponents> CreateAsync(Func<SplashPhase, Task> report,
        string? appliedCultureName = null)
    {
        var services = App.Services;
        var projects = services?.GetService<ProjectOperationsService>() ?? ProjectOperationsService.Instance;
        if (projects.IsWorkspaceAttached)
        {
            throw new InvalidOperationException("Only one desktop project shell may be active.");
        }
        MenuViewModel? menu = null;
        SettingsViewModel? settings = null;
        NodeEditorViewModel? nodes = null;
        WorkspaceViewModel? workspace = null;
        var ownsMenu = false;
        var ownsNodes = false;
        var ownsSettings = false;
        var ownsWorkspace = false;
        try
        {
            await report(SplashPhase.Services);
            var windows = services?.GetService<IWindowService>() ?? UiWindowService.Shared;
            DeviceViewModel.Windows = windows;
            menu = services?.GetService<MenuViewModel>();
            if (menu is null)
            {
                menu = CreateMenu(projects, windows, LanguageService.Instance, LogService.Instance);
                ownsMenu = true;
            }
            await report(SplashPhase.Nodes);
            nodes = services?.GetService<NodeEditorViewModel>();
            if (nodes is null)
            {
                nodes = CreateNodes();
                ownsNodes = true;
            }
            await report(SplashPhase.Workspace);
            settings = services?.GetService<SettingsViewModel>();
            if (settings is null)
            {
                var appSettings = await Task.Run(SettingsPersistenceService.Instance.LoadSettings);
                settings = CreateSettings(appSettings,
                    applyLanguage: appliedCultureName is null
                        || !string.Equals(appSettings.CultureName, appliedCultureName, StringComparison.Ordinal));
                ownsSettings = true;
            }
            workspace = services?.GetService<WorkspaceViewModel>();
            if (workspace is null)
            {
                workspace = CreateWorkspace(settings);
                ownsWorkspace = true;
            }
            return new ShellComponents(projects, menu, nodes, settings, workspace);
        }
        catch
        {
            if (ownsMenu)
            {
                menu?.Dispose();
            }
            if (ownsNodes)
            {
                nodes?.Dispose();
            }
            if (ownsSettings)
            {
                settings?.Dispose();
            }
            if (ownsWorkspace)
            {
                workspace?.Dispose();
            }
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
        var windows = UiWindowService.Shared;
        DeviceViewModel.Windows = windows;
        var menu = CreateMenu(projects, windows, LanguageService.Instance, LogService.Instance);
        var nodes = CreateNodes();
        var settings = CreateSettings(new AppSettings(), applyLanguage: false);
        var workspace = CreateWorkspace(settings);
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
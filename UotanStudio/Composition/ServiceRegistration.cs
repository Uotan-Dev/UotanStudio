using Microsoft.Extensions.DependencyInjection;
using UotanStudio.Services;
using UotanStudio.Services.Compare;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Mcp;
using UotanStudio.Services.Network;
using UotanStudio.Services.PatchPackages;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Scrcpy;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Services.Workspace;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Flash;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Composition;

/// <summary>
/// Composition-root registrations for the desktop host: application services and the shell view
/// models are added through explicit factories, and the catalog names every application-scoped
/// singleton so <see cref="ServiceHub"/> resolves all of them instead of only a subset. The container
/// never scans assemblies, never activates constructors by reflection and never uses
/// <see cref="ActivatorUtilities"/>; type-mapped registration stays out because the host view models
/// expose internal constructors next to public parameterless ones, so a mapping would silently pick the
/// parameterless overload, and because open generic registrations fail at run time under NativeAOT
/// without any build diagnostic.
/// <para>桌面宿主的组合根注册：应用服务与 Shell 视图模型全部通过显式工厂加入，目录覆盖每一个应用级
/// 单例，使 <see cref="ServiceHub"/> 能解析全部单例而不只是其中一部分。容器不扫描程序集、不反射激活
/// 构造函数、也不使用 <see cref="ActivatorUtilities"/>；类型映射注册保持禁用，因为宿主视图模型的内部
/// 构造函数与公共无参构造函数并存，映射会静默选中无参重载，而开放泛型注册在 NativeAOT 下运行时才失败
/// 且没有任何编译诊断。</para>
/// </summary>
internal static class ServiceRegistration
{
    /// <summary>
    /// Adds the application and UI services to a service collection using explicit factories; the
    /// supplied monitor and window service are the instances every resolution returns. Static facade
    /// registrations use their non-resolving defaults, so resolving one from the container never asks
    /// that facade to resolve itself through <see cref="ServiceHub"/> again.
    /// <para>使用显式工厂把应用服务与 UI 服务加入服务集合；传入的监视器与窗口服务就是每次解析返回的实例。
    /// 静态门面注册使用不解析容器的默认实例，避免容器解析门面时门面又经 <see cref="ServiceHub"/> 解析自身。</para>
    /// </summary>
    /// <param name="services">The collection receiving the registrations. <para>接收注册的服务集合。</para></param>
    /// <param name="monitor">The device monitor owned by the composition root. <para>由组合根持有的设备监视器。</para></param>
    /// <param name="windowService">The window service supplied to view models. <para>提供给视图模型的窗口服务。</para></param>
    /// <returns>The same collection, for chaining. <para>同一个服务集合，便于链式调用。</para></returns>
    /// <exception cref="ArgumentNullException">Any argument is null. <para>任一参数为 null。</para></exception>
    internal static IServiceCollection Register(IServiceCollection services,
        DeviceMonitorService? monitor, IWindowService windowService)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(windowService);

        services.AddTransient<AvbToolViewModel>(_ => new AvbToolViewModel());
        services.AddTransient<ChecksumViewModel>(_ => new ChecksumViewModel());
        services.AddTransient<ApkEditorViewModel>(_ => new ApkEditorViewModel());
        services.AddTransient<KeyManagerViewModel>(_ => new KeyManagerViewModel());
        services.AddTransient<SchemeEditorViewModel>(_ => new SchemeEditorViewModel());
        services.AddTransient<UsbTreeViewModel>(_ => new UsbTreeViewModel());
        services.AddTransient<VisualPluginManagerViewModel>(_ => new VisualPluginManagerViewModel());
        services.AddTransient<RegexGeneratorViewModel>(_ => new RegexGeneratorViewModel());
        services.AddSingleton<Services.Flash.IDiskFlashService>(_ => Services.Flash.DiskFlashService.Default);
        services.AddSingleton<UiAbstractions.IDiskFlashDialogs>(_ =>
            new Services.Ui.DiskFlashDialogs(() => windowService.OwnerWindow as Avalonia.Controls.Window));
        services.AddTransient<DiskFlashViewModel>(sp => new DiskFlashViewModel(
            sp.GetService<Services.Flash.IDiskFlashService>(),
            sp.GetService<UiAbstractions.IDiskFlashDialogs>()));
        services.AddSingleton<ILocalizer>(_ => LanguageService.Default);
        services.AddSingleton<LanguageService>(_ => LanguageService.Default);
        services.AddSingleton<IUnifiedLog>(_ => LogService.Default);
        services.AddSingleton<LogService>(_ => LogService.Default);
        if (monitor is not null)
        {
            services.AddSingleton<DeviceMonitorService>(_ => monitor);
        }
        services.AddSingleton<IWindowService>(_ => windowService);
        services.AddSingleton<HttpClient>(_ => SharedHttpClient.Shared);
        services.AddSingleton<ScrcpyServerService>(_ => ScrcpyServerService.Shared);
        services.AddSingleton<RootPatchPackageService>(_ => RootPatchPackageService.Shared);
        services.AddSingleton<McpToolCallLog>(_ => McpToolCallLog.Instance);
        services.AddSingleton<McpExposureService>(_ => McpExposureService.Instance);
        services.AddSingleton<McpClientConnectionService>(_ => McpClientConnectionService.Instance);
        services.AddSingleton<IFlashService>(_ => FlashService.Default);
        services.AddSingleton<ProjectOperationsService>(_ => ProjectOperationsService.Default);
        services.AddSingleton<SettingsPersistenceService>(_ => SettingsPersistenceService.Default);
        services.AddSingleton<BinaryDiffService>(_ => BinaryDiffService.Instance);
        services.AddSingleton<FolderCompareService>(_ => FolderCompareService.Instance);
        services.AddSingleton<TextDiffService>(_ => TextDiffService.Instance);
        services.AddSingleton<DeviceFileSystemService>(_ => DeviceFileSystemService.Instance);
        services.AddSingleton<DexService>(_ => DexService.Instance);
        services.AddSingleton<FirmwareService>(_ => FirmwareService.Instance);
        services.AddSingleton<OperationHistoryService>(_ => OperationHistoryService.Instance);
        services.AddSingleton<ProjectContextService>(_ => ProjectContextService.Instance);
        services.AddSingleton<VisualPluginManager>(_ => VisualPluginManager.Instance);
        services.AddSingleton<WorkspacePanelService>(_ => WorkspacePanelService.Instance);

        services.AddSingleton<FlashViewModel>(sp => new FlashViewModel(
            sp.GetRequiredService<IFlashService>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<IUnifiedLog>()));
        services.AddSingleton<MenuViewModel>(sp => ShellComposition.CreateMenu(
            sp.GetRequiredService<ProjectOperationsService>(), sp.GetRequiredService<IWindowService>(),
            sp.GetRequiredService<ILocalizer>(), sp.GetRequiredService<IUnifiedLog>()));
        services.AddSingleton<NodeEditorViewModel>(_ => ShellComposition.CreateNodes());
        services.AddSingleton<SettingsViewModel>(sp => ShellComposition.CreateSettings(
            sp.GetRequiredService<SettingsPersistenceService>().LoadSettings(), applyLanguage: false));
        services.AddSingleton<WorkspaceViewModel>(sp => ShellComposition.CreateWorkspace(
            sp.GetRequiredService<SettingsViewModel>()));
        return services;
    }

    /// <summary>
    /// Builds the provider without eager activation so startup still constructs nothing until a service
    /// is first resolved; Debug builds additionally validate the descriptor graph at build time, which
    /// covers descriptor-level type mappings only, while the dependency completeness of factory
    /// registrations is covered by tests.
    /// <para>构建提供程序时不急切激活服务，启动期仍不构造任何服务；Debug 构建额外在构建期校验描述符图，
    /// 该校验只覆盖描述符级类型映射，工厂注册的依赖完整性由测试覆盖。</para>
    /// </summary>
    /// <param name="services">The populated service collection. <para>已注册的服务集合。</para></param>
    /// <returns>The composed provider. <para>组合得到的提供程序。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null. <para><paramref name="services"/> 为 null。</para></exception>
    internal static ServiceProvider BuildProvider(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = ValidateRegisteredGraph,
            ValidateScopes = false
        });
    }

#if DEBUG
    /// <summary>Whether the provider validates its descriptor graph while it is built.
    /// <para>提供程序在构建时是否校验描述符图。</para></summary>
    private const bool ValidateRegisteredGraph = true;
#else
    /// <summary>Whether the provider validates its descriptor graph while it is built.
    /// <para>提供程序在构建时是否校验描述符图。</para></summary>
    private const bool ValidateRegisteredGraph = false;
#endif
}
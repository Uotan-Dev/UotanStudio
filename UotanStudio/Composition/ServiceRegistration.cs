using Microsoft.Extensions.DependencyInjection;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Flash;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Workspace;

namespace UotanStudio.Composition;

/// <summary>
/// Composition-root registrations for the desktop host: application services and the shell view
/// models are added through explicit factories. The container never scans assemblies, never
/// activates constructors by reflection and never uses <see cref="ActivatorUtilities"/>; that keeps
/// registration compatible with trimming and NativeAOT as a contract, without claiming a publish
/// verification here.
/// <para>桌面宿主的组合根注册：应用服务与 Shell 视图模型全部通过显式工厂加入。容器不扫描程序集、
/// 不反射激活构造函数、也不使用 <see cref="ActivatorUtilities"/>；这是裁剪与 NativeAOT 兼容的注册契约，
/// 不表示此处已完成发布验证。</para>
/// </summary>
internal static class ServiceRegistration
{
    /// <summary>
    /// Adds the application and UI services to a service collection using explicit factories; the
    /// supplied monitor and window service are the instances every resolution returns.
    /// <para>使用显式工厂把应用服务与 UI 服务加入服务集合；传入的监视器与窗口服务就是每次解析返回的实例。</para>
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

        // Every Add* call below passes an explicit factory. Do not replace them with the parameterless
        // AddSingleton<T>() overload, ActivatorUtilities or assembly scanning: those paths need
        // constructor metadata that trimming and NativeAOT remove.
        // 下面每次 Add* 都传入显式工厂。不要改成无工厂的 AddSingleton<T>()、ActivatorUtilities 或程序集扫描：
        // 那些路径依赖构造函数元数据，裁剪与 NativeAOT 会把它移除。
        // The facade registrations delegate to the static Instance getters so the container singleton
        // and the pre-container test default resolve to the same lazy instance.
        // 门面注册转发到静态 Instance，使容器单例与容器建立前的测试默认实例解析到同一个惰性实例。
        services.AddSingleton<ILocalizer>(_ => LanguageService.Instance);
        services.AddSingleton<LanguageService>(_ => LanguageService.Instance);
        services.AddSingleton<IUnifiedLog>(_ => LogService.Instance);
        services.AddSingleton<LogService>(_ => LogService.Instance);
        if (monitor is not null)
        {
            services.AddSingleton<DeviceMonitorService>(_ => monitor);
        }
        services.AddSingleton<IWindowService>(_ => windowService);
        services.AddSingleton<IFlashService>(_ => FlashService.Default);
        services.AddSingleton<ProjectOperationsService>(_ => ProjectOperationsService.Instance);
        services.AddSingleton<SettingsPersistenceService>(_ => SettingsPersistenceService.Instance);

        services.AddSingleton<FlashViewModel>(sp => new FlashViewModel(sp.GetRequiredService<IFlashService>()));
        services.AddSingleton<MenuViewModel>(sp => new MenuViewModel(
            sp.GetRequiredService<ProjectOperationsService>(), sp.GetRequiredService<IWindowService>()));
        services.AddSingleton<NodeEditorViewModel>(_ => new NodeEditorViewModel());
        services.AddSingleton<SettingsViewModel>(sp => new SettingsViewModel(
            sp.GetRequiredService<SettingsPersistenceService>().LoadSettings(), applyLanguage: false));
        services.AddSingleton<WorkspaceViewModel>(sp => new WorkspaceViewModel(
            new WorkspaceDockFactory(sp.GetRequiredService<SettingsViewModel>().Ai)));
        return services;
    }

    /// <summary>
    /// Builds the provider without reflection-based validation so startup never activates a service
    /// eagerly and every registration stays an explicit factory.
    /// <para>构建提供程序时不启用基于反射的校验，使启动不会急切激活任何服务，并保持全部注册为显式工厂。</para>
    /// </summary>
    /// <param name="services">The populated service collection. <para>已注册的服务集合。</para></param>
    /// <returns>The composed provider. <para>组合得到的提供程序。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null. <para><paramref name="services"/> 为 null。</para></exception>
    internal static ServiceProvider BuildProvider(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = false
        });
    }
}
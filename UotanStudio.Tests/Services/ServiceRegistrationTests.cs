using Microsoft.Extensions.DependencyInjection;
using UotanStudio.Composition;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void Register_ExplicitFactories_ResolveSharedSingletons()
    {
        ServiceHub.Reset();
        var services = new ServiceCollection();
        ServiceRegistration.Register(services, monitor: null, NullWindowService.Instance);
        using var provider = ServiceRegistration.BuildProvider(services);

        var localizer = provider.GetRequiredService<ILocalizer>();
        Assert.Same(localizer, provider.GetRequiredService<LanguageService>());
        Assert.Same(LanguageService.Instance, localizer);

        var log = provider.GetRequiredService<LogService>();
        Assert.Same(log, provider.GetRequiredService<IUnifiedLog>());
        Assert.Same(LogService.Instance, log);

        Assert.Same(FlashService.Default, provider.GetRequiredService<IFlashService>());
        Assert.Same(ProjectOperationsService.Instance, provider.GetRequiredService<ProjectOperationsService>());
        Assert.Same(SettingsPersistenceService.Instance, provider.GetRequiredService<SettingsPersistenceService>());
        Assert.Same(NullWindowService.Instance, provider.GetRequiredService<IWindowService>());

        var flash = provider.GetRequiredService<FlashViewModel>();
        Assert.Same(flash, provider.GetRequiredService<FlashViewModel>());
    }

    [Fact]
    public void Register_EveryRegistration_Resolves()
    {
        ServiceHub.Reset();
        var services = new ServiceCollection();
        ServiceRegistration.Register(services, monitor: null, NullWindowService.Instance);
        using var provider = ServiceRegistration.BuildProvider(services);

        var failures = new List<string>();
        foreach (var descriptor in services)
        {
            try
            {
                _ = provider.GetRequiredService(descriptor.ServiceType);
            }
            catch (Exception ex)
            {
                failures.Add($"{descriptor.ServiceType.FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            "Every registered service must resolve:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ServiceHub_Uninitialized_TryGetReturnsNullAndFacadesFallBack()
    {
        ServiceHub.Reset();

        Assert.Null(ServiceHub.TryGet<ILocalizer>());
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = ServiceHub.GetRequired<ILocalizer>();
        });

        var language = LanguageService.Instance;
        var log = LogService.Instance;
        var projects = ProjectOperationsService.Instance;
        var settings = SettingsPersistenceService.Instance;
        Assert.Same(language, LanguageService.Instance);
        Assert.Same(log, LogService.Instance);
        Assert.Same(projects, ProjectOperationsService.Instance);
        Assert.Same(settings, SettingsPersistenceService.Instance);
    }

    [Fact]
    public void ServiceHub_Initialized_TryGetRoutesToContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<LogService>(_ => LogService.Instance);
        using var provider = services.BuildServiceProvider();
        try
        {
            ServiceHub.Initialize(provider);
            Assert.Same(LogService.Instance, ServiceHub.TryGet<LogService>());
            Assert.Same(provider.GetRequiredService<LogService>(), ServiceHub.GetRequired<LogService>());
        }
        finally
        {
            ServiceHub.Reset();
        }
    }
}
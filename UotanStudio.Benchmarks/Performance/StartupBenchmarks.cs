using BenchmarkDotNet.Attributes;
using UotanStudio.Benchmarks.Infrastructure;
using UotanStudio.Composition;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Services;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;
using UotanStudio.Views.Shell;
using UotanStudio.Views.Workspace;

namespace UotanStudio.Benchmarks.Performance;

/// <summary>Steady-state cost of building shell/VM components on the Avalonia headless UI dispatcher.
/// <para>在 Avalonia 无头 UI 调度器上构建外壳/VM 组件的稳态开销。</para></summary>
[MemoryDiagnoser]
public class StartupBenchmarks
{
    private NodeEditorUiFixture _fixture = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = new NodeEditorUiFixture();
        Run(() =>
        {
            LanguageService.Instance.Initialize();
            LanguageService.Instance.ApplyLanguage("en-US");
            FirmwareFormatRegistry.RegisterAll();
        });
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _fixture.DisposeAsync();

    [Benchmark]
    public Task<int> Menu() => RunAsync(() =>
    {
        using var menu = new MenuViewModel();
        return Task.FromResult(menu.GetType().GetHashCode());
    });

    [Benchmark]
    public Task<int> Nodes() => RunAsync(() =>
    {
        using var nodes = new NodeEditorViewModel();
        return Task.FromResult(nodes.ToolboxItems.Count);
    });

    [Benchmark]
    public Task<int> Settings() => RunAsync(() =>
    {
        using var settings = new SettingsViewModel();
        return Task.FromResult(settings.Ai.GetType().GetHashCode());
    });

    [Benchmark]
    public Task<int> StartupSettings() => RunAsync(() =>
    {
        using var settings = new SettingsViewModel(SettingsPersistenceService.Instance.LoadSettings(), applyLanguage: false);
        return Task.FromResult(settings.Ai.GetType().GetHashCode());
    });

    [Benchmark]
    public Task<int> Workspace() => RunAsync(() =>
    {
        using var settings = new SettingsViewModel();
        using var workspace = new WorkspaceViewModel(new WorkspaceDockFactory(settings.Ai));
        return Task.FromResult(workspace.Layout is null ? 0 : 1);
    });

    [Benchmark]
    public Task<object> MainView() => RunAsync(() => Task.FromResult<object>(new MainView()));

    [Benchmark]
    public Task<string> LocaleReapply() => RunAsync(() =>
    {
        LanguageService.Instance.ApplyLanguage(LanguageService.Instance.CurrentLanguage);
        return Task.FromResult(LanguageService.Instance.CurrentLanguage);
    });

    [Benchmark]
    public Task<int> WorkspaceComponents() => RunAsync(() =>
    {
        ProjectSettingViewModel.EnsureInstance();
        using var plugins = new PluginToolViewModel();
        using var explorer = new ProjectExplorerViewModel();
        DeviceViewModel.EnsureInstance();
        ImageViewModel.EnsureInstance();
        var properties = new PropertiesViewModel();
        using var log = new WorkspaceLogViewModel();
        using var adbLog = new AdbLogViewModel();
        var icon = new AppIcon { Kind = AppIconKind.File };
        using var workspace = new WorkspaceViewModel(new UotanStudio.Views.Workspace.WorkspaceDockFactory());
        return Task.FromResult((workspace.Layout is null ? 0 : 1) + (properties is null ? 0 : 1) + (icon is null ? 0 : 1));
    });

    [Benchmark]
    public Task<Type> ComposeShell() => RunAsync(async () =>
    {
        using var components = await ShellComposition.CreateAsync(
            _ => Task.CompletedTask, SettingsPersistenceService.Instance.LoadSettings().CultureName);
        return components.GetType();
    });

    private void Run(Action action) => Run(() =>
    {
        action();
        return 0;
    });

    private T Run<T>(Func<T> factory)
    {
        T result = default!;
        _fixture.DispatchAsync(() =>
        {
            result = factory();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> factory)
    {
        T result = default!;
        await _fixture.DispatchAsync(async () =>
        {
            result = await factory();
        }, CancellationToken.None);
        return result;
    }
}
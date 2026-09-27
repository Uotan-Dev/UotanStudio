using System.Diagnostics;
using Material.Icons;
using Material.Icons.Avalonia;
using UotanStudio.Composition;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Services;
using UotanStudio.Tests.Views;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Shell;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Performance;

[Collection("Node editor UI")]
public sealed class StartupMeasurements(NodeEditorUiFixture fixture, ITestOutputHelper output)
{
    public static bool Enabled => Environment.GetEnvironmentVariable("UOTAN_STARTUP_PERF") == "1";

    [Fact(Skip = "Set UOTAN_STARTUP_PERF=1 and run this method alone in Release; it initializes native device monitoring.", SkipUnless = nameof(Enabled))]
    public async Task StartupComponents_ColdAndWarm_ReportTimeAndAllocations()
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                output.WriteLine($"Runtime={Environment.Version}; OS={Environment.OSVersion}; CPUs={Environment.ProcessorCount}; renderer=headless; workspace includes device monitor initialization");
                LanguageService.Instance.Initialize();
                Measure("locale-first", () => LanguageService.Instance.ApplyLanguage("en-US"));
                Measure("formats-first", FirmwareFormatRegistry.RegisterAll);
                for (var iteration = 0; iteration < 4; iteration++)
                {
                    var prefix = iteration == 0 ? "cold" : $"warm-{iteration}";
                    using var menu = Measure(prefix + "-menu", () => new MenuViewModel());
                    using var nodes = Measure(prefix + "-nodes", () => new NodeEditorViewModel());
                    using var settings = Measure(prefix + "-settings", () => new SettingsViewModel());
                    using var startupSettings = Measure(prefix + "-startup-settings",
                        () => new SettingsViewModel(SettingsPersistenceService.Instance.LoadSettings(), applyLanguage: false));
                    using var workspace = Measure(prefix + "-workspace",
                        () => new WorkspaceViewModel(new WorkspaceDockFactory(settings.Ai)));
                    Measure(prefix + "-main-view", () => new MainView());
                    Measure(prefix + "-locale-reapply", () =>
                        LanguageService.Instance.ApplyLanguage(LanguageService.Instance.CurrentLanguage));
                    Assert.NotEmpty(nodes.ToolboxCategories);
                    Assert.NotNull(workspace.Layout);
                }

                var phase = SplashPhase.Initializing;
                var clock = Stopwatch.StartNew();
                using var components = await ShellComposition.CreateAsync(next =>
                {
                    output.WriteLine($"composition-{phase}: ms={clock.Elapsed.TotalMilliseconds:F3}");
                    phase = next;
                    clock.Restart();
                    return Task.CompletedTask;
                }, SettingsPersistenceService.Instance.LoadSettings().CultureName);
                output.WriteLine($"composition-{phase}: ms={clock.Elapsed.TotalMilliseconds:F3}");
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
    }

    [Fact(Skip = "Set UOTAN_STARTUP_PERF=1 and run this method alone in Release; it initializes native device monitoring.", SkipUnless = nameof(Enabled))]
    public async Task WorkspaceComponents_FirstUse_ReportTimeAndAllocations()
    {
        await fixture.Session.Dispatch(() =>
        {
            LanguageService.Instance.Initialize();
            LanguageService.Instance.ApplyLanguage("en-US");
            Measure("project-settings", ProjectSettingViewModel.EnsureInstance);
            using var plugins = Measure("plugins", () => new PluginToolViewModel());
            using var explorer = Measure("explorer", () => new ProjectExplorerViewModel());
            using var devices = Measure("device-panel", DeviceViewModel.EnsureInstance);
            Measure("images", ImageViewModel.EnsureInstance);
            Measure("properties", () => new PropertiesViewModel());
            using var log = Measure("log", () => new WorkspaceLogViewModel());
            using var adbLog = Measure("adb-log", () => new AdbLogViewModel());
            Measure("material-icon", () => new MaterialIcon { Kind = MaterialIconKind.FileDocumentOutline });
            using var workspace = Measure("remaining-layout", () => new WorkspaceViewModel());
            App.DeviceMonitor.Dispose();
        }, TestContext.Current.CancellationToken);
    }

    private T Measure<T>(string name, Func<T> action)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        var value = action();
        output.WriteLine($"{name}: ms={clock.Elapsed.TotalMilliseconds:F3}; allocated={GC.GetAllocatedBytesForCurrentThread() - allocated}");
        return value;
    }

    private void Measure(string name, Action action) => Measure(name, () =>
    {
        action();
        return true;
    });
}
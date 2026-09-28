using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.Services.Mcp;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class SettingsMcpExposureHeadlessTests(NodeEditorUiFixture fixture)
{
    private static readonly string[] McpExposureResourceKeys =
    [
        "Settings_McpExposeRoot",
        "Settings_McpExposeRootPlaceholder",
        "Settings_McpExposeRootPick",
        "Settings_McpExposeRootHint",
        "Settings_McpExposeFlash",
        "Settings_McpExposeFlashWarning"
    ];

    [Fact]
    public async Task Constructor_PersistedMcpExposureOptions_LoadsIntoViewModel()
    {
        await fixture.Session.Dispatch(() =>
        {
            var input = new AppSettings
            {
                McpExposeEnabled = true,
                McpExposePort = 19002,
                McpExposeRootDirectory = "uotan-mcp-root-" + Guid.NewGuid().ToString("N"),
                McpExposeAllowFlash = true
            };

            using var settings = new SettingsViewModel(input, applyLanguage: false);

            Assert.Equal(input.McpExposeRootDirectory, settings.McpExposeRootDirectory);
            Assert.True(settings.McpExposeAllowFlash);
            Assert.True(settings.McpExposeEnabled);
            Assert.Equal(19002, settings.McpExposePort);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Constructor_MissingMcpExposureOptions_DefaultsToUnrestrictedAndNoFlash()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var settings = new SettingsViewModel(new AppSettings(), applyLanguage: false);

            Assert.Equal(string.Empty, settings.McpExposeRootDirectory);
            Assert.False(settings.McpExposeAllowFlash);
            Assert.False(settings.McpExposeEnabled);
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task SettingsView_McpExposureSection_ResolvesLocalizedTextInBothCultures(string culture)
    {
        await fixture.Session.Dispatch(() =>
        {
            var language = LanguageService.Instance;
            var previous = language.CurrentLanguage;
            try
            {
                language.Initialize();
                language.ApplyLanguage(culture);

                foreach (var key in McpExposureResourceKeys)
                {
                    var text = language.GetString(key);
                    Assert.False(string.IsNullOrWhiteSpace(text),
                        $"Resource key '{key}' resolved to no text while '{culture}' was applied.");
                    Assert.NotEqual(key, text);
                }
            }
            finally
            {
                language.ApplyLanguage(previous);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SettingsView_McpExposureSection_BindsRootAndFlashControls()
    {
        var rootValue = "uotan-mcp-root-" + Guid.NewGuid().ToString("N");
        var changedRoot = "uotan-mcp-root-" + Guid.NewGuid().ToString("N");

        await fixture.Session.Dispatch(() =>
        {
            var language = LanguageService.Instance;
            var previous = language.CurrentLanguage;
            Window? window = null;
            try
            {
                language.Initialize();
                language.ApplyLanguage("en-US");

                using var settings = new SettingsViewModel(new AppSettings
                {
                    McpExposeRootDirectory = rootValue,
                    McpExposeAllowFlash = true
                }, applyLanguage: false);

                var view = new SettingsView { DataContext = settings };
                window = new Window { Content = view, Width = 1000, Height = 3000 };
                Application.Current!.Resources["AssetImageConverter"] =
                    new UotanStudio.Converters.AssetImageConverter();
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var rootBox = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
                    box => ReferenceEquals(box.DataContext, settings) && box.Text == rootValue);
                Assert.Equal(rootValue, rootBox.Text);

                settings.McpExposeRootDirectory = changedRoot;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(changedRoot, rootBox.Text);

                var viewModelToggles = view.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Where(toggle => ReferenceEquals(toggle.DataContext, settings)).ToList();
                Assert.NotEmpty(viewModelToggles);

                settings.McpExposeAllowFlash = false;
                Dispatcher.UIThread.RunJobs();
                var onWhenFlashOff = viewModelToggles.Where(toggle => toggle.IsChecked == true).ToList();

                settings.McpExposeAllowFlash = true;
                Dispatcher.UIThread.RunJobs();
                var onWhenFlashOn = viewModelToggles.Where(toggle => toggle.IsChecked == true).ToList();

                var flashToggle = Assert.Single(onWhenFlashOn.Except(onWhenFlashOff));
                Assert.True(flashToggle.IsChecked == true);

                var warning = language.GetString("Settings_McpExposeFlashWarning");
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    block => block.Text == warning);
            }
            finally
            {
                window?.Close();
                Application.Current!.Resources.Remove("AssetImageConverter");
                language.ApplyLanguage(previous);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Apply_McpExposureSettings_ChangesLiveServerOptions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = McpExposureService.Instance;
        var (port1, port2) = ReserveTwoLoopbackPorts();
        var rootA = Path.Combine(Path.GetTempPath(), "uotan-mcp-expose-" + Guid.NewGuid().ToString("N"));
        var rootB = Path.Combine(Path.GetTempPath(), "uotan-mcp-expose-" + Guid.NewGuid().ToString("N"));

        try
        {
            service.Apply(enabled: true, port: port1, rootDirectory: rootA, allowFlash: false);
            await WaitForAsync(() => service.IsRunning && service.Port == port1 && service.LastError is null, cancellationToken);
            Assert.Equal(port1, service.Port);
            Assert.Null(service.LastError);

            service.Apply(enabled: true, port: port2, rootDirectory: rootB, allowFlash: false);
            await WaitForAsync(() => service.IsRunning && service.Port == port2 && service.LastError is null, cancellationToken);
            Assert.True(service.IsRunning);
            Assert.Equal(port2, service.Port);

            service.Apply(enabled: false, port: port2, rootDirectory: rootB, allowFlash: false);
            await WaitForAsync(() => !service.IsRunning, cancellationToken);

            using var rejected = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(
                async () => await rejected.ConnectAsync(IPAddress.Loopback, port2, cancellationToken));
        }
        finally
        {
            service.Stop();
        }
    }

    private static (int First, int Second) ReserveTwoLoopbackPorts()
    {
        var first = new TcpListener(IPAddress.Loopback, 0);
        var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        second.Start();
        var firstPort = ((IPEndPoint)first.LocalEndpoint).Port;
        var secondPort = ((IPEndPoint)second.LocalEndpoint).Port;
        first.Stop();
        second.Stop();
        return (firstPort, secondPort);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(25, cancellationToken);
        }

        Assert.True(condition(), "The MCP exposure server state did not change in time.");
    }
}
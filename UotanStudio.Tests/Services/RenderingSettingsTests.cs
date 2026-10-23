using System.Text.Json;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class RenderingSettingsTests
{
    [Fact]
    public void AppSettings_Default_UsesAutomaticRenderingChoices()
    {
        var settings = new AppSettings();

        Assert.Equal(RenderingBackend.Auto, settings.RenderingBackend);
        Assert.Equal(RenderingCompositionMode.Auto, settings.RenderingCompositionMode);
    }

    [Fact]
    public void AppSettings_MissingRenderingKeys_KeepsAutomaticChoices()
    {
        var settings = JsonSerializer.Deserialize("{}", AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Equal(RenderingBackend.Auto, settings.RenderingBackend);
        Assert.Equal(RenderingCompositionMode.Auto, settings.RenderingCompositionMode);
    }

    [Fact]
    public void AppSettings_RoundTrip_PersistsRenderingChoicesAsNames()
    {
        var settings = new AppSettings
        {
            RenderingBackend = RenderingBackend.AngleEgl,
            RenderingCompositionMode = RenderingCompositionMode.DirectComposition,
        };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"renderingBackend\": \"AngleEgl\"", json, StringComparison.Ordinal);
        Assert.Contains("\"renderingCompositionMode\": \"DirectComposition\"", json, StringComparison.Ordinal);

        var reloaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(RenderingBackend.AngleEgl, reloaded.RenderingBackend);
        Assert.Equal(RenderingCompositionMode.DirectComposition, reloaded.RenderingCompositionMode);
    }

    [Fact]
    public void SupportedBackends_Windows_OffersAngleEglFirstAfterAutomatic()
    {
        RenderingBackend[] expected =
        [
            RenderingBackend.Auto,
            RenderingBackend.AngleEgl,
            RenderingBackend.Software,
            RenderingBackend.Wgl,
            RenderingBackend.Vulkan,
        ];

        Assert.Equal(expected, RenderingPreferenceCatalog.SupportedBackends(RenderingPlatform.Windows));
    }

    [Fact]
    public void SupportedBackends_LinuxX11_OffersGlxAndEgl()
    {
        RenderingBackend[] expected =
        [
            RenderingBackend.Auto,
            RenderingBackend.Glx,
            RenderingBackend.Egl,
            RenderingBackend.Software,
            RenderingBackend.Vulkan,
        ];

        Assert.Equal(expected, RenderingPreferenceCatalog.SupportedBackends(RenderingPlatform.LinuxX11));
    }

    [Fact]
    public void SupportedBackends_MacOS_OffersMetalAndOpenGl()
    {
        RenderingBackend[] expected =
        [
            RenderingBackend.Auto,
            RenderingBackend.Metal,
            RenderingBackend.OpenGl,
            RenderingBackend.Software,
        ];

        Assert.Equal(expected, RenderingPreferenceCatalog.SupportedBackends(RenderingPlatform.MacOS));
    }

    [Fact]
    public void SupportedBackends_Wayland_OffersOnlyAutomatic()
    {
        RenderingBackend[] expected = [RenderingBackend.Auto];

        Assert.Equal(expected, RenderingPreferenceCatalog.SupportedBackends(RenderingPlatform.LinuxWayland));
        Assert.Empty(RenderingPreferenceCatalog.SupportedCompositionModes(RenderingPlatform.LinuxWayland));
        Assert.Equal(RenderingBackend.Auto,
            RenderingPreferenceCatalog.Normalize(RenderingPlatform.LinuxWayland, RenderingBackend.Glx));
    }

    [Fact]
    public void SupportedBackends_UnsupportedPlatform_OffersOnlyAutomatic()
    {
        RenderingBackend[] expected = [RenderingBackend.Auto];

        Assert.Equal(expected, RenderingPreferenceCatalog.SupportedBackends(RenderingPlatform.Unsupported));
    }

    [Fact]
    public void SupportedCompositionModes_OnlyWindows_OffersChoices()
    {
        RenderingCompositionMode[] expected =
        [
            RenderingCompositionMode.Auto,
            RenderingCompositionMode.WinUIComposition,
            RenderingCompositionMode.DirectComposition,
            RenderingCompositionMode.LowLatencyDxgiSwapChain,
            RenderingCompositionMode.RedirectionSurface,
        ];

        Assert.Equal(expected, RenderingPreferenceCatalog.SupportedCompositionModes(RenderingPlatform.Windows));
        Assert.True(RenderingPreferenceCatalog.SupportsCompositionMode(RenderingPlatform.Windows));
        Assert.Empty(RenderingPreferenceCatalog.SupportedCompositionModes(RenderingPlatform.LinuxX11));
        Assert.Empty(RenderingPreferenceCatalog.SupportedCompositionModes(RenderingPlatform.MacOS));
        Assert.False(RenderingPreferenceCatalog.SupportsCompositionMode(RenderingPlatform.LinuxX11));
        Assert.False(RenderingPreferenceCatalog.SupportsCompositionMode(RenderingPlatform.MacOS));
    }

    [Fact]
    public void Normalize_BackendUnknownToPlatform_FallsBackToAutomatic()
    {
        Assert.Equal(RenderingBackend.Auto,
            RenderingPreferenceCatalog.Normalize(RenderingPlatform.MacOS, RenderingBackend.AngleEgl));
        Assert.Equal(RenderingBackend.Auto,
            RenderingPreferenceCatalog.Normalize(RenderingPlatform.LinuxX11, RenderingBackend.Metal));
        Assert.Equal(RenderingBackend.AngleEgl,
            RenderingPreferenceCatalog.Normalize(RenderingPlatform.Windows, RenderingBackend.AngleEgl));
        Assert.Equal(RenderingBackend.Software,
            RenderingPreferenceCatalog.Normalize(RenderingPlatform.LinuxX11, RenderingBackend.Software));
    }

    [Fact]
    public void NormalizeComposition_NonWindowsPlatform_FallsBackToAutomatic()
    {
        Assert.Equal(RenderingCompositionMode.Auto,
            RenderingPreferenceCatalog.NormalizeComposition(
                RenderingPlatform.LinuxX11, RenderingCompositionMode.WinUIComposition));
        Assert.Equal(RenderingCompositionMode.Auto,
            RenderingPreferenceCatalog.NormalizeComposition(
                RenderingPlatform.MacOS, RenderingCompositionMode.RedirectionSurface));
        Assert.Equal(RenderingCompositionMode.WinUIComposition,
            RenderingPreferenceCatalog.NormalizeComposition(
                RenderingPlatform.Windows, RenderingCompositionMode.WinUIComposition));
    }

    [Fact]
    public void DetectWindowingPlatform_WithoutApplication_FallsBackToTheOsFamily()
        => Assert.Equal(RenderingPreferenceCatalog.CurrentPlatform,
            RenderingRuntimeProbe.DetectWindowingPlatform());

    [Fact]
    public void MapGraphicsLabelKey_KnownImplementations_ReportTheirBackend()
    {
        Assert.Equal("Settings_RenderingBackendAngleEgl",
            RenderingRuntimeProbe.MapGraphicsLabelKey(
                "Avalonia.Win32.OpenGl.Angle.D3D11AngleWin32PlatformGraphics"));
        Assert.Equal("Settings_RenderingBackendAngleEglD3D9",
            RenderingRuntimeProbe.MapGraphicsLabelKey(
                "Avalonia.Win32.OpenGl.Angle.D3D9AngleWin32PlatformGraphics"));
        Assert.Equal("Settings_RenderingBackendWgl",
            RenderingRuntimeProbe.MapGraphicsLabelKey("Avalonia.Win32.OpenGl.WglPlatformOpenGlInterface"));
        Assert.Equal("Settings_RenderingBackendVulkan",
            RenderingRuntimeProbe.MapGraphicsLabelKey("Avalonia.Vulkan.VulkanPlatformGraphics"));
        Assert.Equal("Settings_RenderingBackendGlx",
            RenderingRuntimeProbe.MapGraphicsLabelKey("Avalonia.X11.Glx.GlxPlatformGraphics"));
        Assert.Equal("Settings_RenderingBackendEgl",
            RenderingRuntimeProbe.MapGraphicsLabelKey("Avalonia.OpenGL.Egl.EglPlatformGraphics"));
        Assert.Equal("Settings_RenderingBackendEgl",
            RenderingRuntimeProbe.MapGraphicsLabelKey("Avalonia.Wayland.Server.WaylandPlatformGraphics"));
        Assert.Equal("Settings_RenderingBackendMetal",
            RenderingRuntimeProbe.MapGraphicsLabelKey("Avalonia.Native.MetalPlatformGraphics"));
        Assert.Equal("Settings_RenderingBackendOpenGl",
            RenderingRuntimeProbe.MapGraphicsLabelKey("Avalonia.Native.AvaloniaNativeGlPlatformGraphics"));
        Assert.Equal("Settings_RenderingBackendSoftware", RenderingRuntimeProbe.MapGraphicsLabelKey(null));
        Assert.Null(RenderingRuntimeProbe.MapGraphicsLabelKey("Some.Unknown.PlatformGraphics"));
    }

    [Fact]
    public void MapCompositionLabelKey_KnownSurfaces_ReportTheirCompositionMode()
    {
        Assert.Equal("Settings_CompositionModeWinUI",
            RenderingRuntimeProbe.MapCompositionLabelKey(
                "Avalonia.Win32.WinRT.Composition.WinUiCompositedWindowSurface"));
        Assert.Equal("Settings_CompositionModeDirect",
            RenderingRuntimeProbe.MapCompositionLabelKey(
                "Avalonia.Win32.DComposition.DirectCompositedWindowSurface"));
        Assert.Null(RenderingRuntimeProbe.MapCompositionLabelKey("Some.Unknown.Surface"));
    }

    [Fact]
    public void SettingsViewModel_AutomaticBackendLabel_ReportsLiveStateWithoutFallbackChain()
    {
        using var viewModel = new SettingsViewModel(new AppSettings(), applyLanguage: false);

        var label = viewModel.RenderingBackendOptions[0].DisplayName;

        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.DoesNotContain("→", label, StringComparison.Ordinal);
        Assert.NotEqual("平台默认", label);
        Assert.NotEqual("platform default", label, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsViewModel_UnsupportedPersistedBackend_ShowsAutomaticChoice()
    {
        var platform = RenderingPreferenceCatalog.CurrentPlatform;
        var unsupported = platform == RenderingPlatform.Windows
            ? RenderingBackend.Metal
            : RenderingBackend.AngleEgl;
        var settings = new AppSettings
        {
            RenderingBackend = unsupported,
            RenderingCompositionMode = RenderingCompositionMode.WinUIComposition,
        };

        using var viewModel = new SettingsViewModel(settings, applyLanguage: false);

        Assert.InRange(viewModel.RenderingBackendIndex, 0, viewModel.RenderingBackendOptions.Count - 1);
        Assert.Equal(RenderingBackend.Auto,
            RenderingPreferenceCatalog.SupportedBackends(platform)[viewModel.RenderingBackendIndex]);
        Assert.Equal(platform == RenderingPlatform.Windows, viewModel.HasCompositionModeOptions);
    }
}
using System.Text.Json;
using Avalonia.Controls;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WindowPlacementSettingsTests
{
    [Fact]
    public void AppSettings_Default_HasNoMainWindowPlacement()
    {
        Assert.Null(new AppSettings().MainWindowPlacement);
    }

    [Fact]
    public void AppSettings_RoundTrip_PersistsMainWindowPlacementAndStateName()
    {
        var settings = new AppSettings
        {
            MainWindowPlacement = new()
            {
                Width = 1440.5,
                Height = 900,
                X = -32,
                Y = 64,
                State = WindowState.Maximized
            }
        };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"mainWindowPlacement\"", json, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"Maximized\"", json, StringComparison.Ordinal);

        var reloaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        var placement = Assert.IsType<MainWindowPlacement>(reloaded.MainWindowPlacement);
        Assert.Equal(1440.5, placement.Width);
        Assert.Equal(900d, placement.Height);
        Assert.Equal(-32, placement.X);
        Assert.Equal(64, placement.Y);
        Assert.Equal(WindowState.Maximized, placement.State);
    }

    [Fact]
    public void AppSettings_PlacementWithoutGeometry_LeavesValuesUnset()
    {
        var settings = JsonSerializer.Deserialize("""{"mainWindowPlacement":{"state":"Normal"}}""",
            AppSettingsJsonContext.Default.AppSettings)!;

        var placement = Assert.IsType<MainWindowPlacement>(settings.MainWindowPlacement);
        Assert.Null(placement.Width);
        Assert.Null(placement.Height);
        Assert.Null(placement.X);
        Assert.Null(placement.Y);
        Assert.Equal(WindowState.Normal, placement.State);
    }

    [Fact]
    public void MainWindowPlacement_Matches_ComparesEveryStoredValue()
    {
        var placement = new MainWindowPlacement { Width = 1, Height = 2, X = 3, Y = 4, State = WindowState.Normal };

        Assert.True(placement.Matches(new() { Width = 1, Height = 2, X = 3, Y = 4 }));
        Assert.False(placement.Matches(new() { Width = 1, Height = 2, X = 3, Y = 5 }));
        Assert.False(placement.Matches(new() { Width = 1, Height = 2, X = 3, Y = 4, State = WindowState.FullScreen }));
        Assert.False(placement.Matches(null));
    }
}
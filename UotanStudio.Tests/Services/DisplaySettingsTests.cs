using System.Text.Json;
using UotanStudio.Models;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class DisplaySettingsTests
{
    [Fact]
    public void AppSettings_Default_DefinesReadbackListAndAllProtocols()
    {
        var settings = new AppSettings();

        Assert.Equal(FlashProtocolCatalog.DefaultReadbackSkipPartitions, settings.FlashReadbackSkipPartitions);
        Assert.Collection(
            settings.FlashProtocolDisplay,
            item => AssertPreference(item, FlashProtocolCatalog.Edl, visible: true),
            item => AssertPreference(item, FlashProtocolCatalog.Mtk, visible: true),
            item => AssertPreference(item, FlashProtocolCatalog.Rockchip, visible: true),
            item => AssertPreference(item, FlashProtocolCatalog.Sunxi, visible: true));
    }

    [Fact]
    public void AppSettings_RoundTrip_PreservesReadbackListAndProtocolOrder()
    {
        var settings = new AppSettings
        {
            FlashReadbackSkipPartitions = "SYSTEM,VENDOR",
            FlashProtocolDisplay =
            [
                new DisplayPreference { Key = FlashProtocolCatalog.Sunxi, Visible = false },
                new DisplayPreference { Key = FlashProtocolCatalog.Edl, Visible = true },
                new DisplayPreference { Key = FlashProtocolCatalog.Rockchip, Visible = false },
                new DisplayPreference { Key = FlashProtocolCatalog.Mtk, Visible = true }
            ]
        };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var reloaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Equal("SYSTEM,VENDOR", reloaded.FlashReadbackSkipPartitions);
        Assert.Collection(
            reloaded.FlashProtocolDisplay,
            item => AssertPreference(item, FlashProtocolCatalog.Sunxi, visible: false),
            item => AssertPreference(item, FlashProtocolCatalog.Edl, visible: true),
            item => AssertPreference(item, FlashProtocolCatalog.Rockchip, visible: false),
            item => AssertPreference(item, FlashProtocolCatalog.Mtk, visible: true));
    }

    [Fact]
    public void AppSettings_PartialProtocolList_NormalizesDuplicatesAndUnknownKeys()
    {
        const string json = """
            {
              "flashProtocolDisplay": [
                { "key": "MTK", "visible": false },
                { "key": "unknown", "visible": true },
                { "key": "mtk", "visible": true }
              ]
            }
            """;

        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Equal(FlashProtocolCatalog.DefaultReadbackSkipPartitions, settings.FlashReadbackSkipPartitions);
        Assert.Collection(
            settings.FlashProtocolDisplay,
            item => AssertPreference(item, FlashProtocolCatalog.Mtk, visible: false),
            item => AssertPreference(item, FlashProtocolCatalog.Edl, visible: true),
            item => AssertPreference(item, FlashProtocolCatalog.Rockchip, visible: true),
            item => AssertPreference(item, FlashProtocolCatalog.Sunxi, visible: true));
    }

    [Fact]
    public void AppSettings_Default_DefinesAllBottomBarItemsVisible()
    {
        var settings = new AppSettings();

        Assert.Collection(
            settings.BottomBarDisplay,
            item => Assert.Equal(BottomBarCatalog.Workspace, item.Key),
            item => Assert.Equal(BottomBarCatalog.Flash, item.Key),
            item => Assert.Equal(BottomBarCatalog.Node, item.Key),
            item => Assert.Equal(BottomBarCatalog.Delivery, item.Key),
            item => Assert.Equal(BottomBarCatalog.Settings, item.Key));
        Assert.All(settings.BottomBarDisplay, item => Assert.True(item.Visible));
    }

    [Fact]
    public void AppSettings_BottomBarNormalization_PreservesOrderAndVisibility()
    {
        var display = BottomBarCatalog.Normalize(
        [
            new DisplayPreference { Key = "SETTINGS", Visible = false },
            new DisplayPreference { Key = "unknown", Visible = true },
            new DisplayPreference { Key = "settings", Visible = true },
            new DisplayPreference { Key = BottomBarCatalog.Node, Visible = false },
            new DisplayPreference { Key = BottomBarCatalog.Flash, Visible = false },
            new DisplayPreference { Key = BottomBarCatalog.Delivery, Visible = false },
            new DisplayPreference { Key = BottomBarCatalog.Workspace, Visible = false }
        ]);

        Assert.Collection(
            display,
            item => Assert.Equal(BottomBarCatalog.Settings, item.Key),
            item => Assert.Equal(BottomBarCatalog.Node, item.Key),
            item => Assert.Equal(BottomBarCatalog.Flash, item.Key),
            item => Assert.Equal(BottomBarCatalog.Delivery, item.Key),
            item => Assert.Equal(BottomBarCatalog.Workspace, item.Key));
        Assert.All(display, item => Assert.False(item.Visible));
    }

    [Fact]
    public void AppSettings_RoundTrip_PreservesBottomBarOrderAndVisibility()
    {
        var settings = new AppSettings
        {
            BottomBarDisplay =
            [
                new DisplayPreference { Key = BottomBarCatalog.Flash, Visible = false },
                new DisplayPreference { Key = BottomBarCatalog.Workspace, Visible = true },
                new DisplayPreference { Key = BottomBarCatalog.Delivery, Visible = false },
                new DisplayPreference { Key = BottomBarCatalog.Node, Visible = true },
                new DisplayPreference { Key = BottomBarCatalog.Settings, Visible = true }
            ]
        };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var reloaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Collection(
            reloaded.BottomBarDisplay,
            item => Assert.Equal(BottomBarCatalog.Flash, item.Key),
            item => Assert.Equal(BottomBarCatalog.Workspace, item.Key),
            item => Assert.Equal(BottomBarCatalog.Delivery, item.Key),
            item => Assert.Equal(BottomBarCatalog.Node, item.Key),
            item => Assert.Equal(BottomBarCatalog.Settings, item.Key));
        Assert.False(reloaded.BottomBarDisplay[0].Visible);
        Assert.True(reloaded.BottomBarDisplay[1].Visible);
        Assert.False(reloaded.BottomBarDisplay[2].Visible);
    }

    private static void AssertPreference(DisplayPreference preference, string key, bool visible)
    {
        Assert.Equal(key, preference.Key);
        Assert.Equal(visible, preference.Visible);
    }
}
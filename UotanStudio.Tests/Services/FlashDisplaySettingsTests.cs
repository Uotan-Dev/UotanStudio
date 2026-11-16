using System.Text.Json;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashDisplaySettingsTests
{
    [Fact]
    public void AppSettings_Default_DefinesReadbackListAndAllProtocols()
    {
        var settings = new AppSettings();

        Assert.Equal(FlashProtocolCatalog.DefaultReadbackSkipPartitions, settings.FlashReadbackSkipPartitions);
        Assert.Collection(
            settings.FlashProtocolDisplay,
            item => AssertProtocol(item, FlashProtocolCatalog.Edl, visible: true),
            item => AssertProtocol(item, FlashProtocolCatalog.Mtk, visible: true),
            item => AssertProtocol(item, FlashProtocolCatalog.Rockchip, visible: true),
            item => AssertProtocol(item, FlashProtocolCatalog.Sunxi, visible: true));
    }

    [Fact]
    public void AppSettings_RoundTrip_PreservesReadbackListAndProtocolOrder()
    {
        var settings = new AppSettings
        {
            FlashReadbackSkipPartitions = "SYSTEM,VENDOR",
            FlashProtocolDisplay =
            [
                new FlashProtocolDisplayPreference { Key = FlashProtocolCatalog.Sunxi, Visible = false },
                new FlashProtocolDisplayPreference { Key = FlashProtocolCatalog.Edl, Visible = true },
                new FlashProtocolDisplayPreference { Key = FlashProtocolCatalog.Rockchip, Visible = false },
                new FlashProtocolDisplayPreference { Key = FlashProtocolCatalog.Mtk, Visible = true }
            ]
        };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var reloaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Equal("SYSTEM,VENDOR", reloaded.FlashReadbackSkipPartitions);
        Assert.Collection(
            reloaded.FlashProtocolDisplay,
            item => AssertProtocol(item, FlashProtocolCatalog.Sunxi, visible: false),
            item => AssertProtocol(item, FlashProtocolCatalog.Edl, visible: true),
            item => AssertProtocol(item, FlashProtocolCatalog.Rockchip, visible: false),
            item => AssertProtocol(item, FlashProtocolCatalog.Mtk, visible: true));
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
            item => AssertProtocol(item, FlashProtocolCatalog.Mtk, visible: false),
            item => AssertProtocol(item, FlashProtocolCatalog.Edl, visible: true),
            item => AssertProtocol(item, FlashProtocolCatalog.Rockchip, visible: true),
            item => AssertProtocol(item, FlashProtocolCatalog.Sunxi, visible: true));
    }

    private static void AssertProtocol(FlashProtocolDisplayPreference preference, string key, bool visible)
    {
        Assert.Equal(key, preference.Key);
        Assert.Equal(visible, preference.Visible);
    }
}
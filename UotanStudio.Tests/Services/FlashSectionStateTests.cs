using System.Text.Json;
using UotanStudio.Services;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashSectionStateTests
{
    [Fact]
    public void AppSettings_Default_HasNoExpandedFlashSections()
    {
        Assert.Empty(new AppSettings().ExpandedFlashSections);
    }

    [Fact]
    public void AppSettings_RoundTrip_PersistsExpandedFlashSections()
    {
        var settings = new AppSettings { ExpandedFlashSections = [FlashSectionState.Edl, FlashSectionState.Image] };

        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"expandedFlashSections\"", json, StringComparison.Ordinal);
        Assert.Contains("\"edl\"", json, StringComparison.Ordinal);

        var reloaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(["edl", "image"], reloaded.ExpandedFlashSections);
    }

    [Fact]
    public void AppSettings_Copy_IsolatesExpandedFlashSections()
    {
        var settings = new AppSettings { ExpandedFlashSections = [FlashSectionState.Mtk] };

        var copy = settings.Copy();
        copy.ExpandedFlashSections.Add(FlashSectionState.Rockchip);

        Assert.Equal([FlashSectionState.Mtk], settings.ExpandedFlashSections);
        Assert.Equal([FlashSectionState.Mtk, FlashSectionState.Rockchip], copy.ExpandedFlashSections);
    }

    [Fact]
    public void Capture_OnlyExpandedSections_AreListedInPresentationOrder()
    {
        Assert.Equal(["mtk", "image"], FlashSectionState.Capture(edl: false, mtk: true, rockchip: false, image: true));
        Assert.Empty(FlashSectionState.Capture(edl: false, mtk: false, rockchip: false, image: false));
        Assert.Equal(["edl", "mtk", "rockchip", "image"],
            FlashSectionState.Capture(edl: true, mtk: true, rockchip: true, image: true));
    }

    [Fact]
    public void IsExpanded_IgnoresCaseAndUnknownIdentifiers()
    {
        Assert.True(FlashSectionState.IsExpanded(["EDL"], FlashSectionState.Edl));
        Assert.False(FlashSectionState.IsExpanded(["unknown"], FlashSectionState.Edl));
        Assert.False(FlashSectionState.IsExpanded([], FlashSectionState.Edl));
        Assert.False(FlashSectionState.IsExpanded(null, FlashSectionState.Edl));
    }
}
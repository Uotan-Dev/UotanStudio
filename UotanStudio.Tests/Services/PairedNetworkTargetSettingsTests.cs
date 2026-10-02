using System.Text.Json;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PairedNetworkTargetSettingsTests
{
    [Fact]
    public void PairedNetworkTargets_LegacyJsonWithoutField_DeserializesToEmptyList()
    {
        var settings = JsonSerializer.Deserialize(
            """{"recentProjects":["a.uotan"]}""", AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Empty(settings.PairedNetworkTargets);
        Assert.Equal(["a.uotan"], settings.RecentProjects);
    }

    [Fact]
    public void PairedNetworkTargets_RoundTripThroughSourceGeneratedContext_PreservesAddressAndTlsFlag()
    {
        var settings = new AppSettings();
        settings.PairedNetworkTargets.Add(new PairedNetworkTarget("192.168.1.20:37811", true));
        settings.PairedNetworkTargets.Add(new PairedNetworkTarget("192.168.1.21:5555", false));

        var restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings),
            AppSettingsJsonContext.Default.AppSettings)!;

        Assert.Equal(2, restored.PairedNetworkTargets.Count);
        Assert.Equal("192.168.1.20:37811", restored.PairedNetworkTargets[0].Address);
        Assert.True(restored.PairedNetworkTargets[0].RequiresTls);
        Assert.False(restored.PairedNetworkTargets[1].RequiresTls);
    }

    [Fact]
    public void Copy_PairedNetworkTargetsMutated_LeavesSourceListUntouched()
    {
        var settings = new AppSettings();
        settings.PairedNetworkTargets.Add(new PairedNetworkTarget("192.168.1.20:37811", true));

        var copy = settings.Copy();
        copy.PairedNetworkTargets.Add(new PairedNetworkTarget("192.168.1.22:41000", true));

        Assert.Single(settings.PairedNetworkTargets);
        Assert.Equal(2, copy.PairedNetworkTargets.Count);
    }
}
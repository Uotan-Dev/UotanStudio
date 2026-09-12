using System.Text.Json;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

public class FirmwareParallelismSettingsTests
{
    [Theory]
    [InlineData("{}", 4)]
    [InlineData("{\"firmwareParallelism\":1}", 1)]
    [InlineData("{\"firmwareParallelism\":8}", 8)]
    [InlineData("{\"firmwareParallelism\":0}", 4)]
    [InlineData("{\"firmwareParallelism\":99}", 4)]
    public void Settings_DefaultValidateCopyAndRoundTrip(string json, int expected)
    {
        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(expected, settings.FirmwareParallelism);
        var copy = settings.Copy();
        var saved = JsonSerializer.Serialize(copy, AppSettingsJsonContext.Default.AppSettings);
        Assert.Equal(expected, JsonSerializer.Deserialize(saved, AppSettingsJsonContext.Default.AppSettings)!.FirmwareParallelism);
    }
}
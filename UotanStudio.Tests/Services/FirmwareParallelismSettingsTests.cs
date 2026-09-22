using System.Text.Json;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

public class FirmwareParallelismSettingsTests
{
    [Theory]
    [InlineData("{}", -1)]
    [InlineData("{\"firmwareParallelism\":1}", 1)]
    [InlineData("{\"firmwareParallelism\":64}", 64)]
    [InlineData("{\"firmwareParallelism\":0}", -1)]
    [InlineData("{\"firmwareParallelism\":65}", -1)]
    public void Settings_DefaultValidateCopyAndRoundTrip(string json, int expected)
    {
        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        expected = expected < 0 ? FirmwareParallelism.DefaultWorkers : expected;
        Assert.Equal(expected, settings.FirmwareParallelism);
        var copy = settings.Copy();
        var saved = JsonSerializer.Serialize(copy, AppSettingsJsonContext.Default.AppSettings);
        Assert.Equal(expected, JsonSerializer.Deserialize(saved, AppSettingsJsonContext.Default.AppSettings)!.FirmwareParallelism);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(64, 64)]
    [InlineData(65, 64)]
    public void NormalizeWorkerCount_OutsideSupportedRange_ClampsToOneThroughSixtyFour(int input, int expected)
        => Assert.Equal(expected, FirmwareParallelism.NormalizeWorkerCount(input));

    [Fact]
    public void DetectPhysicalCoreCount_CurrentMachine_ReturnsPositiveCountAndBoundedDefault()
    {
        var physicalCores = FirmwareParallelism.DetectPhysicalCoreCount();
        Assert.True(physicalCores > 0);
        Assert.Equal(FirmwareParallelism.NormalizeWorkerCount(physicalCores), FirmwareParallelism.DefaultWorkers);
        Assert.InRange(FirmwareParallelism.DefaultWorkers, 1, FirmwareParallelism.MaximumWorkers);
    }
}
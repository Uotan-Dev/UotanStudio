using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FastbootStepServiceTests
{
    [Fact]
    public void ParseCowPartitions_OnlyIncludesDeviceReportedLogicalCowNames()
    {
        var variables = new Dictionary<string, string>
        {
            ["is-logical:system_a-cow"] = "yes",
            ["is-logical:vendor-cow"] = "no",
            ["partition-size:boot-cow"] = "0x1000",
            ["is-logical:../data-cow"] = "yes",
            ["is-logical:system_a"] = "yes",
            ["is-logical:product-cow"] = "yes"
        };

        var names = FastbootStepService.ParseCowPartitions(variables);

        Assert.Equal(["product-cow", "system_a-cow"], names);
    }
}
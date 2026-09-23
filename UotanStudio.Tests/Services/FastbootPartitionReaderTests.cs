using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FastbootPartitionReaderTests
{
    [Fact]
    public void Parse_ValidSizes_ReturnsOnlySafeNamedPartitions()
    {
        var variables = new Dictionary<string, string>
        {
            ["partition-size:boot_a"] = "0x100000",
            ["partition-size:system"] = "0x200000",
            ["partition-size:bad-name"] = "0x3000",
            ["partition-size:overflow"] = "0xFFFFFFFFFFFFFFFF",
            ["partition-size:empty"] = "0x0",
            ["partition-type:vendor"] = "raw"
        };

        var result = FastbootPartitionReader.Parse(variables);

        Assert.Equal(["boot_a", "system"], result.Select(partition => partition.Name));
        Assert.Equal([1048576L, 2097152L], result.Select(partition => partition.Size));
    }

    [Fact]
    public void Parse_FastbootdLogicalView_FiltersPhysicalAndUnconfirmedPartitions()
    {
        var variables = new Dictionary<string, string>
        {
            ["is-userspace"] = "yes",
            ["partition-size:boot_a"] = "0x100000",
            ["partition-size:system_a"] = "0x200000",
            ["partition-size:vendor_a"] = "0x300000",
            ["is-logical:system_a"] = "yes",
            ["is-logical:vendor_a"] = "no",
            ["is-logical:missing"] = "yes"
        };

        var logical = FastbootPartitionReader.Parse(variables, logicalOnly: true);
        var all = FastbootPartitionReader.Parse(variables);

        Assert.Equal("system_a", Assert.Single(logical).Name);
        Assert.Equal(["boot_a", "system_a", "vendor_a"], all.Select(partition => partition.Name));
    }
}
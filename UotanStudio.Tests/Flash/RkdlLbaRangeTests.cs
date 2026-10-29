using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class RkdlLbaRangeTests
{
    private static readonly RockchipPartition[] Partitions =
    [
        new("uboot", sizeSectors: 0x2000, offsetSectors: 0x4000),
        new("boot", sizeSectors: 0x8000, offsetSectors: 0x8000)
    ];

    [Fact]
    public void IsRkRangeDeclared_RangeInsideOnePartition_ReturnsTrue()
    {
        Assert.True(FlashViewModel.IsRkRangeDeclared(Partitions, 0x8100, 0x100));
    }

    [Fact]
    public void IsRkRangeDeclared_RangeEqualToWholePartition_ReturnsTrue()
    {
        Assert.True(FlashViewModel.IsRkRangeDeclared(Partitions, 0x8000, 0x8000));
    }

    [Fact]
    public void IsRkRangeDeclared_RangeCrossesPartitionBoundary_ReturnsFalse()
    {
        Assert.False(FlashViewModel.IsRkRangeDeclared(Partitions, 0xFE00, 0x400));
    }

    [Fact]
    public void IsRkRangeDeclared_RangeBeforeFirstPartition_ReturnsFalse()
    {
        Assert.False(FlashViewModel.IsRkRangeDeclared(Partitions, 0x0, 0x10));
    }

    [Fact]
    public void IsRkRangeDeclared_RangeEndsExactlyAtPartitionEnd_ReturnsTrue()
    {
        Assert.True(FlashViewModel.IsRkRangeDeclared(Partitions, 0x5F00, 0x100));
    }

    [Fact]
    public void IsRkRangeDeclared_ZeroSectors_ReturnsFalse()
    {
        Assert.False(FlashViewModel.IsRkRangeDeclared(Partitions, 0x8000, 0));
    }

    [Fact]
    public void IsRkRangeDeclared_EmptyTable_ReturnsFalse()
    {
        Assert.False(FlashViewModel.IsRkRangeDeclared([], 0, 1));
    }
}
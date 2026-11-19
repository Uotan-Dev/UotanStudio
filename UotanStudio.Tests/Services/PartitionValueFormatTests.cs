using UotanStudio.Firmware.BlockImage;
using UotanStudio.Models;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PartitionValueFormatTests
{
    [Theory]
    [InlineData("512", 512UL)]
    [InlineData("0", 0UL)]
    [InlineData("1K", 1024UL)]
    [InlineData("1KiB", 1024UL)]
    [InlineData("1.5M", 1572864UL)]
    [InlineData("8 MB", 8388608UL)]
    [InlineData("2G", 2147483648UL)]
    [InlineData("1T", 1099511627776UL)]
    [InlineData("0x200", 512UL)]
    [InlineData("0x400K", 1048576UL)]
    public void TryParseBytes_UnitSuffixesAndBareNumbers_AreBytes(string text, ulong expected)
    {
        Assert.True(PartitionTableFormat.TryParseBytes(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1X")]
    [InlineData("-4")]
    public void TryParseBytes_InvalidText_Fails(string text)
    {
        Assert.False(PartitionTableFormat.TryParseBytes(text, out _));
    }

    [Fact]
    public void FormatBytes_RoundTripsThroughTryParseBytes()
    {
        ulong[] values = [0, 512, 1024, 1536, 1024UL * 1024 * 1024, 64UL * 1024 * 1024 * 1024];
        foreach (var value in values)
        {
            var text = PartitionTableFormat.FormatBytes(value);
            Assert.True(PartitionTableFormat.TryParseBytes(text, out var parsed), text);
            Assert.Equal(value, parsed);
        }
    }

    [Theory]
    [InlineData(512UL, "512 B")]
    [InlineData(1024UL, "1 KB")]
    [InlineData(1572864UL, "1.5 MB")]
    [InlineData(1099511627776UL, "1 TB")]
    public void FormatBytes_UsesShortUnitsWithoutBinarySuffix(ulong bytes, string expected)
    {
        Assert.Equal(expected, PartitionTableFormat.FormatBytes(bytes));
        Assert.DoesNotContain("iB", PartitionTableFormat.FormatBytes(bytes));
    }

    [Fact]
    public void BytesToSectors_RoundsUp()
    {
        Assert.Equal(1UL, PartitionTableFormat.BytesToSectors(1));
        Assert.Equal(1UL, PartitionTableFormat.BytesToSectors(512));
        Assert.Equal(2UL, PartitionTableFormat.BytesToSectors(513));
    }

    [Fact]
    public void PartitionItem_SizeBytesAndSize_StayInSync()
    {
        var item = new PartitionItemViewModel(
            new PartitionEntry { Name = "boot", StartSector = 2048, SizeSectors = 4096, Type = "linear" }, 0, null);

        item.SizeBytes = "8M";
        Assert.Equal(PartitionTableFormat.FormatSectors(16384), item.Size);

        item.Size = "0x800";
        Assert.Equal(PartitionTableFormat.FormatBytes(1024UL * 1024), item.SizeBytes);

        item.StartBytes = "1048576";
        Assert.Equal(PartitionTableFormat.FormatSectors(2048), item.Start);

        item.Start = "0x400";
        Assert.Equal(PartitionTableFormat.FormatBytes(512UL * 1024), item.StartBytes);
    }

    [Fact]
    public void PartitionItem_InvalidLinkedInput_KeepsTheOtherField()
    {
        var item = new PartitionItemViewModel(
            new PartitionEntry { Name = "boot", StartSector = 2048, SizeSectors = 4096, Type = "linear" }, 0, null);
        var sizeBefore = item.Size;
        var startBefore = item.Start;

        item.SizeBytes = "not-a-size";
        item.StartBytes = "not-a-start";

        Assert.Equal(sizeBefore, item.Size);
        Assert.Equal(startBefore, item.Start);
    }
}

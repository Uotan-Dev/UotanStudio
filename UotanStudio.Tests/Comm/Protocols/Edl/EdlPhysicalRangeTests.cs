using UotanStudio.Comm.Protocols.Edl;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlPhysicalRangeTests
{
    [Theory]
    [InlineData(-1, 4, 0, 512, false)]
    [InlineData(0, 0, 0, 512, false)]
    [InlineData(0, -3, 0, 512, false)]
    [InlineData(0, 4, -1, 512, false)]
    [InlineData(0, 4, 0, 0, false)]
    [InlineData(0, 4, 0, -1, false)]
    [InlineData(0, 4, 0, 65537, false)]
    [InlineData(0, 4, 0, 512, true)]
    public void Constructor_ViolatingBounds_Throws(long start, long blocks, int partition, long sectorSize, bool relative)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EdlPhysicalRange(start, blocks, partition, sectorSize, relative));

    [Fact]
    public void Parse_DiskRelativeExpression_MarksRelativeAndKeepsManifestText()
    {
        var range = EdlPhysicalRange.Parse("NUM_DISK_SECTORS-5.", 4, 1, 4096, storageSlot: 2);

        Assert.True(range.RelativeToEnd);
        Assert.Equal(5, range.StartLba);
        Assert.Equal(4, range.BlockCount);
        Assert.Equal(1, range.PhysicalPartitionNumber);
        Assert.Equal(4096, range.SectorSize);
        Assert.Equal(2, range.StorageSlot);
        Assert.Equal("NUM_DISK_SECTORS-5.", range.StartSectorExpression);
    }

    [Fact]
    public void Parse_HexConstantExpression_IsAbsoluteAndKeepsOriginalText()
    {
        var range = EdlPhysicalRange.Parse("0x8000", 8, 0, 4096);

        Assert.False(range.RelativeToEnd);
        Assert.Equal(32768, range.StartLba);
        Assert.Equal("0x8000", range.StartSectorExpression);
        Assert.Null(range.StorageSlot);
    }

    [Fact]
    public void Resolve_AbsoluteRangeWithinCapacity_ReturnsStartAndBlocks()
    {
        var range = new EdlPhysicalRange(100, 50, 0, 512);
        var geometry = new FirehoseStorageGeometry(0, 512, 1000);

        Assert.Equal((100L, 50L), range.Resolve(geometry));
    }

    [Fact]
    public void Resolve_DiskRelativeRange_EvaluatesAgainstReportedSectorCount()
    {
        var range = new EdlPhysicalRange(100, 50, 0, 512, RelativeToEnd: true);
        var geometry = new FirehoseStorageGeometry(0, 512, 1000);

        Assert.Equal((900L, 50L), range.Resolve(geometry));
    }

    [Fact]
    public void Resolve_RelativeRangeExtendingPastStorageStart_Throws()
    {
        var range = new EdlPhysicalRange(40, 50, 0, 512, RelativeToEnd: true);
        var geometry = new FirehoseStorageGeometry(0, 512, 1000);

        Assert.Throws<ArgumentOutOfRangeException>(() => range.Resolve(geometry));
    }

    [Theory]
    [InlineData(980, 30, false)]
    [InlineData(1000, 1, false)]
    [InlineData(1001, 1, false)]
    public void Resolve_BeyondReportedCapacity_ThrowsBeforeAnyCommand(long start, long blocks, bool relative)
    {
        var range = new EdlPhysicalRange(start, blocks, 0, 512, relative);
        var geometry = new FirehoseStorageGeometry(0, 512, 1000);

        Assert.Throws<ArgumentOutOfRangeException>(() => range.Resolve(geometry));
    }

    [Fact]
    public void ValidateAgainst_ChangedSectorSize_RejectsStalePlan()
    {
        var range = new EdlPhysicalRange(10, 10, 0, 512);
        var resized = new FirehoseStorageGeometry(0, 4096, 1000);

        var error = Assert.Throws<InvalidDataException>(() => range.ValidateAgainst(resized));
        Assert.Contains("sector size", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAgainst_DifferentPhysicalPartition_RejectsGeometry()
    {
        var range = new EdlPhysicalRange(10, 10, 1, 512);
        var otherPartition = new FirehoseStorageGeometry(0, 512, 1000);

        var error = Assert.Throws<InvalidDataException>(() => range.ValidateAgainst(otherPartition));
        Assert.Contains("partition", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAgainst_ShrunkCapacity_RejectsOldPlan()
    {
        var range = new EdlPhysicalRange(900, 100, 0, 512);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            range.ValidateAgainst(new FirehoseStorageGeometry(0, 512, 950)));
    }

    [Fact]
    public void RecordValueSemantics_CopyWithPreservesBindingsAndEquality()
    {
        var original = EdlPhysicalRange.Parse("NUM_DISK_SECTORS-5.", 4, 1, 4096, storageSlot: 3);
        var copy = original with { };

        Assert.Equal(original, copy);
        Assert.NotEqual(original, original with { BlockCount = 5 });
        Assert.Contains("NUM_DISK_SECTORS-", original.ToString(), StringComparison.Ordinal);
        Assert.Contains("pp=1", original.ToString(), StringComparison.Ordinal);
        Assert.Contains("slot=3", original.ToString(), StringComparison.Ordinal);
    }
}
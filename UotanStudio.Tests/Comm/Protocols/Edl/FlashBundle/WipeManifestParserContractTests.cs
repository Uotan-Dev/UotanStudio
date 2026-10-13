using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public sealed class WipeManifestParserContractTests
{
    [Fact]
    public void Parse_RangedWipeElement_ProducesOneEntryWithEveryDeclaredValue()
    {
        var wipes = WipeManifestParser.Parse(Xml("""
            <data>
              <wipe SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="16" physical_partition_number="1"
                    start_sector="NUM_DISK_SECTORS-8." label="tail" zeros_filename="Zeros4KB.bin" slot="2" />
            </data>
            """));

        var entry = Assert.Single(wipes);
        Assert.Equal(4096L, entry.SectorSizeInBytes);
        Assert.Equal(16L, entry.NumPartitionSectors);
        Assert.Equal(1, entry.PhysicalPartitionNumber);
        Assert.Equal("NUM_DISK_SECTORS-8.", entry.StartSector);
        Assert.Equal("tail", entry.Label);
        Assert.Equal("Zeros4KB.bin", entry.ZeroFilename);
        Assert.Equal(2, entry.Slot);
        Assert.False(entry.EntireStorage);

        var range = entry.ToPlanRange(4096);
        Assert.NotNull(range);
        Assert.True(range!.RelativeToEnd);
        Assert.Equal(8L, range.StartLba);
        Assert.Equal(16L, range.BlockCount);
        Assert.Equal(1, range.PhysicalPartitionNumber);
        Assert.Equal(4096L, range.SectorSize);
    }

    [Fact]
    public void Parse_BareWipeElement_AppliesDeclaredDefaultsAndRefusesPlanningWithoutACount()
    {
        var entry = Assert.Single(WipeManifestParser.Parse(Xml("<data><wipe /></data>")));

        Assert.Equal(0L, entry.SectorSizeInBytes);
        Assert.Equal(0L, entry.NumPartitionSectors);
        Assert.Equal(0, entry.PhysicalPartitionNumber);
        Assert.Equal("0", entry.StartSector);
        Assert.Empty(entry.Label);
        Assert.Equal(WipeManifestEntry.DefaultZeroFilename, entry.ZeroFilename);
        Assert.Null(entry.Slot);
        Assert.False(entry.EntireStorage);
        Assert.Throws<ArgumentOutOfRangeException>(() => entry.ToPlanRange(4096));
    }

    [Theory]
    [InlineData("<wipe num_partition_sectors=\"many\" start_sector=\"0\" />")]
    [InlineData("<wipe SECTOR_SIZE_IN_BYTES=\"4k\" num_partition_sectors=\"16\" />")]
    [InlineData("<wipe physical_partition_number=\"0x1\" num_partition_sectors=\"16\" />")]
    [InlineData("<wipe slot=\"a\" num_partition_sectors=\"16\" />")]
    [InlineData("<wipe physical_partition_number=\"1.5\" num_partition_sectors=\"16\" />")]
    public void Parse_NonNumericAttribute_RefusesTheWholeDocument(string element)
    {
        var error = Assert.Throws<EdlException>(
            () => WipeManifestParser.Parse(Xml("<data>" + element + "</data>")));

        Assert.Equal(EdlErrorCode.XmlParseError, error.ErrorCode);
    }

    [Fact]
    public void Parse_WhitespacePaddedAttributes_ResolveToTheDeclaredRegion()
    {
        var entry = Assert.Single(WipeManifestParser.Parse(Xml("""
            <data>
              <wipe SECTOR_SIZE_IN_BYTES=" 4096 " num_partition_sectors=" 16 "
                    physical_partition_number=" 1 " start_sector=" 32768 " slot=" 2 " />
            </data>
            """)));

        Assert.Equal(4096L, entry.SectorSizeInBytes);
        Assert.Equal(16L, entry.NumPartitionSectors);
        Assert.Equal(1, entry.PhysicalPartitionNumber);
        Assert.Equal(2, entry.Slot);

        var range = entry.ToPlanRange(4096);
        Assert.NotNull(range);
        Assert.Equal(32768L, range!.StartLba);
        Assert.Equal(16L, range.BlockCount);
        Assert.False(range.RelativeToEnd);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    public void Parse_AllFlagAttribute_ReadsOnlyTrueAndOneAsEntireStorage(string value, bool expected)
    {
        var entry = Assert.Single(WipeManifestParser.Parse(
            Xml($"<data><wipe all=\"{value}\" num_partition_sectors=\"16\" /></data>")));

        Assert.Equal(expected, entry.EntireStorage);
        if (expected)
        {
            Assert.Null(entry.ToPlanRange(4096));
        }
    }

    [Fact]
    public void Parse_WipeElementsAmongProgramAndErase_KeepsDocumentOrderAndIgnoresOtherElements()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("rawprogram0.xml", """
            <data>
              <program SECTOR_SIZE_IN_BYTES="4096" filename="boot.img" physical_partition_number="0"
                       start_sector="6" num_partition_sectors="2" label="boot" />
              <wipe label="first" num_partition_sectors="4" start_sector="0" />
              <erase num_partition_sectors="8" physical_partition_number="2" start_sector="40" label="wipe_me" />
              <wipe label="second" num_partition_sectors="8" physical_partition_number="2"
                    start_sector="NUM_DISK_SECTORS-8." />
            </data>
            """);

        var wipes = WipeManifestParser.Parse(path);

        Assert.Equal(2, wipes.Count);
        Assert.Equal("first", wipes[0].Label);
        Assert.Equal("0", wipes[0].StartSector);
        Assert.Equal("second", wipes[1].Label);
        Assert.Equal(2, wipes[1].PhysicalPartitionNumber);
        Assert.Equal(8L, wipes[1].NumPartitionSectors);
        Assert.Equal("NUM_DISK_SECTORS-8.", wipes[1].StartSector);
        Assert.Equal(2, RawProgramParser.Parse(path).Count);
    }

    [Fact]
    public void Parse_WipeElementNestedInsideAContainer_RefusesTheDocument()
    {
        var error = Assert.Throws<EdlException>(() => WipeManifestParser.Parse(Xml(
            "<data><list><wipe num_partition_sectors=\"16\" start_sector=\"0\" /></list></data>")));

        Assert.Equal(EdlErrorCode.XmlParseError, error.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankFilePath_RefusesBeforeReadingAnyFile(string filePath)
    {
        var error = Assert.Throws<EdlException>(() => WipeManifestParser.Parse(filePath));

        Assert.Equal(EdlErrorCode.InvalidParameter, error.ErrorCode);
    }

    [Fact]
    public void Parse_NullStream_RefusesWithInvalidParameter()
    {
        var error = Assert.Throws<EdlException>(() => WipeManifestParser.Parse((Stream)null!));

        Assert.Equal(EdlErrorCode.InvalidParameter, error.ErrorCode);
    }

    [Fact]
    public void Parse_DocumentWithoutWipeElements_YieldsNoRequests()
    {
        var wipes = WipeManifestParser.Parse(Xml("""
            <data>
              <program SECTOR_SIZE_IN_BYTES="4096" filename="boot.img" physical_partition_number="0"
                       start_sector="6" num_partition_sectors="2" />
              <wipe2 num_partition_sectors="4" start_sector="0" />
            </data>
            """));

        Assert.Empty(wipes);
    }

    private static MemoryStream Xml(string document) => new(Encoding.UTF8.GetBytes(document));
}
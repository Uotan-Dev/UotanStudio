using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class BundleEraseReadManifestParserTests
{
    [Fact]
    public void RawProgramParser_EraseElement_ProducesEraseShapedEntry()
    {
        const string xml = """
            <data>
              <program SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="2" filename="a.img" physical_partition_number="1" start_sector="6" label="keep"/>
              <erase SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="9" physical_partition_number="2" start_sector="40" label="wipe_me"/>
            </data>
            """;

        var entries = RawProgramParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        Assert.Equal(2, entries.Count);
        var erase = entries[1];
        Assert.Empty(erase.Filename);
        Assert.Equal("wipe_me", erase.Label);
        Assert.Equal(9, erase.NumPartitionSectors);
        Assert.Equal(2, erase.PhysicalPartitionNumber);
        Assert.Equal("40", erase.StartSector);
        Assert.Equal(4096, erase.SectorSizeInBytes);
    }

    [Fact]
    public void RawProgramParser_ProgramOnly_DoesNotInventEraseEntries()
    {
        const string xml = """
            <data>
              <program SECTOR_SIZE_IN_BYTES="512" num_partition_sectors="3" filename="b.img" physical_partition_number="0" start_sector="1"/>
            </data>
            """;

        var entries = RawProgramParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        Assert.Single(entries);
        Assert.Equal("b.img", entries[0].Filename);
    }

    [Fact]
    public void ReadManifestParser_ReadElement_CapturesRangeAttributes()
    {
        const string xml = """
            <data>
              <read SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="16" filename="dump.bin" physical_partition_number="3" start_sector="NUM_DISK_SECTORS-16." label="tail"/>
            </data>
            """;

        var entries = ReadManifestParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var entry = Assert.Single(entries);
        Assert.Equal("dump.bin", entry.Filename);
        Assert.Equal(16, entry.NumPartitionSectors);
        Assert.Equal(3, entry.PhysicalPartitionNumber);
        Assert.Equal("NUM_DISK_SECTORS-16.", entry.StartSector);
    }

    [Fact]
    public void ReadManifestParser_ProgramElementIsIgnored()
    {
        const string xml = """
            <data>
              <program SECTOR_SIZE_IN_BYTES="512" filename="x.img" start_sector="2"/>
            </data>
            """;

        var entries = ReadManifestParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        Assert.Empty(entries);
    }

    [Fact]
    public void ReadManifestEntry_ResolveStartSector_NonPositiveCount_Rejects()
    {
        var geometry = new FirehoseStorageGeometry(0, 512, 100);
        var entry = new ReadManifestEntry { StartSector = "10", NumPartitionSectors = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => entry.ResolveStartSector(geometry));
    }

    [Fact]
    public void ReadManifestEntry_ResolveStartSector_RelativeWithinCapacity_Resolves()
    {
        var geometry = new FirehoseStorageGeometry(0, 512, 100);
        var entry = new ReadManifestEntry { StartSector = "NUM_DISK_SECTORS-16.", NumPartitionSectors = 16 };

        Assert.Equal(84, entry.ResolveStartSector(geometry));
    }
}
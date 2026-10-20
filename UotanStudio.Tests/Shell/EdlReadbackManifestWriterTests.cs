using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Shell;

public sealed class EdlReadbackManifestWriterTests
{
    private static readonly EdlReadbackManifestEntry BootEntry = new()
    {
        PartitionName = "boot",
        StartSector = 2048,
        SectorCount = 1024,
        PhysicalPartitionNumber = 0,
        FileName = "boot.img"
    };

    private static readonly EdlReadbackManifestEntry UserDataEntry = new()
    {
        PartitionName = "userdata",
        StartSector = 999,
        SectorCount = 1,
        PhysicalPartitionNumber = 0,
        FileName = "userdata.img"
    };

    [Fact]
    public async Task WriteAsync_StandardMode_KeepsExactStartSectors()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "rawprogram.xml");

        await EdlReadbackManifestWriter.WriteAsync(
            path, [BootEntry], sectorSizeInBytes: 4096, numDiskSectors: 0,
            generalized: false, TestContext.Current.CancellationToken);

        var document = XDocument.Load(path);
        var program = Assert.Single(document.Descendants("program"));
        Assert.Equal("boot", (string?)program.Attribute("label"));
        Assert.Equal("2048", (string?)program.Attribute("start_sector"));
        Assert.Equal("1024", (string?)program.Attribute("num_partition_sectors"));
        Assert.Equal("4096", (string?)program.Attribute("SECTOR_SIZE_IN_BYTES"));
        Assert.Equal("boot.img", (string?)program.Attribute("filename"));
    }

    [Fact]
    public void Generate_GeneratedManifest_RoundTripsThroughRawProgramParser()
    {
        var content = EdlReadbackManifestWriter.Generate(
            [BootEntry, UserDataEntry], sectorSizeInBytes: 4096, numDiskSectors: 0, generalized: false);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var entries = RawProgramParser.Parse(stream);

        Assert.Equal(2, entries.Count);
        Assert.Equal("boot.img", entries[0].Filename);
        Assert.Equal("boot", entries[0].Label);
        Assert.Equal("2048", entries[0].StartSector);
        Assert.Equal(1024, entries[0].NumPartitionSectors);
        Assert.Equal(4096, entries[0].SectorSizeInBytes);
        Assert.Equal(0, entries[0].PhysicalPartitionNumber);
        Assert.Equal("userdata.img", entries[1].Filename);
    }

    [Fact]
    public void Parse_LegacyFileAttribute_FallsBackToFilename()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <data>
              <program SECTOR_SIZE_IN_BYTES="4096" file="boot.img" label="boot" start_sector="2048" num_partition_sectors="1024" physical_partition_number="0" />
            </data>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        var entry = Assert.Single(RawProgramParser.Parse(stream));

        Assert.Equal("boot.img", entry.Filename);
        Assert.Equal("boot", entry.Label);
    }

    [Fact]
    public async Task WriteAsync_GeneralizedMode_RewritesOnlyDiskEndEntries()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "rawprogram.xml");
        var diskEnd = BootEntry with { StartSector = 8192 - BootEntry.SectorCount };

        await EdlReadbackManifestWriter.WriteAsync(
            path, [diskEnd, UserDataEntry], sectorSizeInBytes: 4096, numDiskSectors: 8192,
            generalized: true, TestContext.Current.CancellationToken);

        var document = XDocument.Load(path);
        var programs = document.Descendants("program").ToArray();
        Assert.Equal("NUM_DISK_SECTORS-1024.", (string?)programs[0].Attribute("start_sector"));
        Assert.Equal("999", (string?)programs[1].Attribute("start_sector"));
    }

    [Fact]
    public async Task WriteAsync_GeneralizedWithoutDiskSize_KeepsStartSectors()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "rawprogram.xml");

        await EdlReadbackManifestWriter.WriteAsync(
            path, [BootEntry], sectorSizeInBytes: 4096, numDiskSectors: 0,
            generalized: true, TestContext.Current.CancellationToken);

        var document = XDocument.Load(path);
        var program = Assert.Single(document.Descendants("program"));
        Assert.Equal("2048", (string?)program.Attribute("start_sector"));
    }

    [Fact]
    public async Task WriteAsync_ExistingFile_ReplacedAtomicallyOnSuccess()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "rawprogram.xml");
        await File.WriteAllTextAsync(path, "stale", TestContext.Current.CancellationToken);

        await EdlReadbackManifestWriter.WriteAsync(
            path, [BootEntry], sectorSizeInBytes: 4096, numDiskSectors: 0,
            generalized: false, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("stale", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void Generate_NonPositiveSectorSize_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            EdlReadbackManifestWriter.Generate([BootEntry], sectorSizeInBytes: 0, numDiskSectors: 0, generalized: false));
    }

    [Fact]
    public void Generate_EmptyEntries_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            EdlReadbackManifestWriter.Generate([], sectorSizeInBytes: 4096, numDiskSectors: 0, generalized: false));
    }

    [Fact]
    public void Generate_NegativeStartSector_Throws()
    {
        var entry = BootEntry with { StartSector = -1 };
        Assert.Throws<ArgumentException>(() =>
            EdlReadbackManifestWriter.Generate([entry], sectorSizeInBytes: 4096, numDiskSectors: 0, generalized: false));
    }
}
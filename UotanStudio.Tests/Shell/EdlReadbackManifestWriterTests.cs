using System.Xml.Linq;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
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
        Assert.Equal("boot.img", (string?)program.Attribute("file"));
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
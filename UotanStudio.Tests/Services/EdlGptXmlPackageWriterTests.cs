using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class EdlGptXmlPackageWriterTests
{
    private const int SectorSize = 4096;
    private const long DiskSectors = 61_844_480L;

    private static readonly Guid LinuxDataGuid = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    private static EdlGptXmlLayout Layout(params GptPartition[] partitions) => new(
        Lun: 0,
        SectorSize: SectorSize,
        DiskSectors: DiskSectors,
        LastUsableLba: 61_844_474UL,
        PartitionEntryCount: 64,
        PartitionEntrySize: 128,
        PrimaryArrayLba: 2,
        BackupArrayLba: 61_844_475,
        Partitions: partitions);

    private static GptPartition Partition(int index, string name, ulong first, ulong last) => new(
        index, LinuxDataGuid, Guid.NewGuid(), first, last, 0, name,
        checked((long)first * SectorSize), checked((long)(last - first + 1) * SectorSize));

    [Fact]
    public async Task BuildRawProgram_GeneratedLayout_ParsesBackAndKeepsGptRowsAtTheDiskEdge()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, EdlGptXmlPackageWriter.RawProgramFileName);
        await File.WriteAllTextAsync(path, EdlGptXmlPackageWriter.BuildRawProgram(
            Layout(Partition(0, "boot_a", 2048, 4095), Partition(1, "userdata", 4096, 100_000))),
            TestContext.Current.CancellationToken);

        var entries = RawProgramParser.ParseAll([path], directory.Path);

        Assert.Equal(4, entries.Count);
        Assert.Equal("boot_a", entries[0].Label);
        Assert.Equal("boot_a.img", entries[0].Filename);
        Assert.Equal("2048", entries[0].StartSector);
        Assert.Equal(2048, entries[0].NumPartitionSectors);
        Assert.Equal(SectorSize, entries[0].SectorSizeInBytes);
        Assert.Equal("userdata", entries[1].Label);
        Assert.Equal(95_905, entries[1].NumPartitionSectors);
        Assert.Equal("PrimaryGPT", entries[2].Label);
        Assert.Equal(EdlGptXmlPackageWriter.PrimaryGptFileName, entries[2].Filename);
        Assert.Equal("0", entries[2].StartSector);
        Assert.Equal(4, entries[2].NumPartitionSectors);
        Assert.Equal("BackupGPT", entries[3].Label);
        Assert.Equal(EdlGptXmlPackageWriter.BackupGptFileName, entries[3].Filename);
        Assert.Equal("NUM_DISK_SECTORS-5.", entries[3].StartSector);
        Assert.Equal(5, entries[3].NumPartitionSectors);
    }

    [Fact]
    public void BuildPatch_GeneratedLayout_TargetsDiskWithDiskRelativeGptFields()
    {
        var content = EdlGptXmlPackageWriter.BuildPatch(Layout(Partition(0, "boot_a", 2048, 4095)));

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var patches = PatchParser.Parse(stream);

        Assert.Equal(11, patches.Count);
        Assert.All(patches, patch => Assert.Equal("DISK", patch.Filename));
        Assert.All(patches, patch => Assert.Equal(0, patch.PhysicalPartitionNumber));
        Assert.Equal(SectorSize, patches[0].SectorSizeInBytes);
        var lastUsable = Assert.Single(patches,
            patch => patch.ByteOffset == 48 && patch.Label == "PrimaryGPT");
        Assert.Equal("NUM_DISK_SECTORS-6.", lastUsable.Value);
        Assert.Equal("1", lastUsable.StartSector);
        var primaryArrayCrc = Assert.Single(patches,
            patch => patch.ByteOffset == 88 && patch.Label == "PrimaryGPT");
        Assert.Equal("CRC32(2,8192)", primaryArrayCrc.Value);
        var backupArray = Assert.Single(patches,
            patch => patch.ByteOffset == 72 && patch.Label == "BackupGPT");
        Assert.Equal("NUM_DISK_SECTORS-5.", backupArray.Value);
        var backupArrayCrc = Assert.Single(patches,
            patch => patch.ByteOffset == 88 && patch.Label == "BackupGPT");
        Assert.Equal("CRC32(NUM_DISK_SECTORS-5.,8192)", backupArrayCrc.Value);
        var backupHeaderCrc = Assert.Single(patches,
            patch => patch.ByteOffset == 16 && patch.StartSector == "NUM_DISK_SECTORS-1." &&
                patch.Value.StartsWith("CRC32(", StringComparison.Ordinal));
        Assert.Equal("CRC32(NUM_DISK_SECTORS-1.,92)", backupHeaderCrc.Value);
    }

    [Fact]
    public void BuildPartition_GeneratedLayout_CarriesLabelTypeAndSectorDerivedSize()
    {
        var content = EdlGptXmlPackageWriter.BuildPartition(Layout(Partition(0, "boot_a", 2048, 4095)));

        var document = XDocument.Parse(content);
        var partition = Assert.Single(document.Descendants("partition"));
        Assert.Equal("boot_a", (string?)partition.Attribute("label"));
        Assert.Equal("8192", (string?)partition.Attribute("size_in_kb"));
        Assert.Equal(LinuxDataGuid.ToString().ToUpperInvariant(), (string?)partition.Attribute("type"));
        Assert.Equal("boot_a.img", (string?)partition.Attribute("filename"));
    }

    [Fact]
    public void FileLabel_HostileOrReservedLabels_ProducePortableStems()
    {
        var hostile = Partition(3, "../../etc/passwd", 6, 7);
        var reserved = Partition(4, "con", 8, 9);
        var empty = Partition(5, "   ", 10, 11);

        Assert.Equal(".._.._etc_passwd", EdlGptXmlPackageWriter.FileLabel(hostile));
        Assert.Equal("_con", EdlGptXmlPackageWriter.FileLabel(reserved));
        Assert.Equal("partition5", EdlGptXmlPackageWriter.FileLabel(empty));
    }

    [Fact]
    public void PackageRelativeFileName_InsideAndOutsideThePackage_ReturnsRelativeAndAbsolute()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pkg");

        Assert.Equal(Path.Combine("images", "boot.img"),
            EdlGptXmlPackageWriter.PackageRelativeFileName(folder, Path.Combine(folder, "images", "boot.img")));
        var outside = Path.Combine(Path.GetTempPath(), "other", "boot.img");
        Assert.Equal(Path.GetFullPath(outside), EdlGptXmlPackageWriter.PackageRelativeFileName(folder, outside));
    }

    [Fact]
    public async Task UpdateRawProgramFileNameAsync_SingleMatchingRow_RewritesOnlyThatRow()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, EdlGptXmlPackageWriter.RawProgramFileName);
        await File.WriteAllTextAsync(path, EdlGptXmlPackageWriter.BuildRawProgram(
            Layout(Partition(0, "boot_a", 2048, 4095), Partition(1, "userdata", 4096, 100_000))),
            TestContext.Current.CancellationToken);

        var updated = await EdlGptXmlPackageWriter.UpdateRawProgramFileNameAsync(
            path, "boot_a", 0, "chosen.img", TestContext.Current.CancellationToken);

        Assert.True(updated);
        var programs = XDocument.Load(path).Descendants("program").ToArray();
        Assert.Equal("chosen.img", (string?)programs[0].Attribute("filename"));
        Assert.Equal("userdata.img", (string?)programs[1].Attribute("filename"));
        Assert.False(await EdlGptXmlPackageWriter.UpdateRawProgramFileNameAsync(
            path, "absent", 0, "chosen.img", TestContext.Current.CancellationToken));
    }
}
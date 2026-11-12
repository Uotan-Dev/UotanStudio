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
        PrimaryBlobSectors: 6,
        BackupBlobSectors: 5,
        LastUsableOffset: 6,
        EntryCount: 64,
        EntrySize: 128,
        PrimaryArrayLba: 2,
        BackupArrayLba: 61_844_475,
        Partitions: partitions);

    private static GptPartition Partition(int index, string name, ulong first, ulong last, ulong attributes = 0,
        Guid? typeId = null) => new(
        index, typeId ?? LinuxDataGuid, Guid.NewGuid(), first, last, attributes, name,
        checked((long)first * SectorSize), checked((long)((last >= first ? last - first + 1 : 0) * SectorSize)));

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
        Assert.Equal(string.Empty, entries[0].Filename);
        Assert.Equal("2048", entries[0].StartSector);
        Assert.Equal(2048, entries[0].NumPartitionSectors);
        Assert.Equal(SectorSize, entries[0].SectorSizeInBytes);
        Assert.Equal("userdata", entries[1].Label);
        Assert.Equal(95_905, entries[1].NumPartitionSectors);
        Assert.Equal("PrimaryGPT", entries[2].Label);
        Assert.Equal(EdlGptXmlPackageWriter.PrimaryGptFileName, entries[2].Filename);
        Assert.Equal("0", entries[2].StartSector);
        Assert.Equal(6, entries[2].NumPartitionSectors);
        Assert.Equal("BackupGPT", entries[3].Label);
        Assert.Equal(EdlGptXmlPackageWriter.BackupGptFileName, entries[3].Filename);
        Assert.Equal("NUM_DISK_SECTORS-5.", entries[3].StartSector);
        Assert.Equal(5, entries[3].NumPartitionSectors);
    }

    [Fact]
    public void BuildPatch_GeneratedLayout_FollowsPtoolRowsForBlobsAndDisk()
    {
        var content = EdlGptXmlPackageWriter.BuildPatch(Layout(Partition(0, "boot_a", 2048, 4095)));

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var patches = PatchParser.Parse(stream);

        Assert.Equal(26, patches.Count);
        Assert.All(patches, patch => Assert.Equal(0, patch.PhysicalPartitionNumber));
        Assert.All(patches, patch => Assert.Equal(SectorSize, patch.SectorSizeInBytes));
        Assert.Equal(6, patches.Count(patch => patch.Filename == EdlGptXmlPackageWriter.PrimaryGptFileName));
        Assert.Equal(7, patches.Count(patch => patch.Filename == EdlGptXmlPackageWriter.BackupGptFileName));
        Assert.Equal(13, patches.Count(patch => patch.Filename == "DISK"));

        var lastPartition = patches
            .Where(patch => patch.What!.Contains("last partition", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, lastPartition.Length);
        Assert.All(lastPartition, patch => Assert.Equal(40L, patch.ByteOffset));
        Assert.All(lastPartition, patch => Assert.Equal("NUM_DISK_SECTORS-6.", patch.Value));

        var lastUsable = patches
            .Where(patch => patch.What == "Update Primary Header with LastUseableLBA.")
            .ToArray();
        Assert.Equal(2, lastUsable.Length);
        Assert.All(lastUsable, patch => Assert.Equal("NUM_DISK_SECTORS-6.", patch.Value));
        Assert.Equal(2, patches.Count(patch => patch.What == "Update Backup Header with LastUseableLBA."));
        Assert.Equal(2, patches.Count(patch => patch.What == "Update Primary Header with BackupGPT Header Location."));
        Assert.Equal(2, patches.Count(patch => patch.What == "Update Backup Header with CurrentLBA."));
        var arrayLocation = patches
            .Where(patch => patch.What == "Update Backup Header with Partition Array Location.")
            .ToArray();
        Assert.Equal(2, arrayLocation.Length);
        Assert.All(arrayLocation, patch => Assert.Equal("NUM_DISK_SECTORS-5.", patch.Value));
        Assert.Equal(1, patches.Count(patch =>
            patch.What == "Update Primary Header with CRC of Partition Array." &&
            patch.Filename == EdlGptXmlPackageWriter.PrimaryGptFileName));
        Assert.Equal("CRC32(2,8192)", patches[14].Value);
        Assert.Equal(1, patches.Count(patch =>
            patch.What == "Update Backup Header with CRC of Partition Array." &&
            patch.Filename == EdlGptXmlPackageWriter.BackupGptFileName));
        var backupArrayDiskCrc = Assert.Single(patches, patch =>
            patch.What == "Update Backup Header with CRC of Partition Array." && patch.Filename == "DISK");
        Assert.Equal("CRC32(NUM_DISK_SECTORS-5.,8192)", backupArrayDiskCrc.Value);
        Assert.Equal(1, patches.Count(patch => patch.What == "Zero Out Header CRC in Primary Header." &&
            patch.Filename == EdlGptXmlPackageWriter.PrimaryGptFileName));
        Assert.Equal(1, patches.Count(patch => patch.What == "Update Primary Header with CRC of Primary Header." &&
            patch.Filename == EdlGptXmlPackageWriter.PrimaryGptFileName));
        Assert.Equal(1, patches.Count(patch => patch.What == "Zero Out Header CRC in Backup Header." &&
            patch.Filename == EdlGptXmlPackageWriter.BackupGptFileName));
        Assert.Equal(1, patches.Count(patch => patch.What == "Update Backup Header with CRC of Backup Header." &&
            patch.Filename == EdlGptXmlPackageWriter.BackupGptFileName));
        var backupHeaderDiskCrc = Assert.Single(patches, patch =>
            patch.What == "Update Backup Header with CRC of Backup Header." && patch.Filename == "DISK");
        Assert.Equal("CRC32(NUM_DISK_SECTORS-1.,92)", backupHeaderDiskCrc.Value);
    }

    [Fact]
    public void BuildRawProgram_BoundImageAndNamedBlobs_WritesTheGivenNames()
    {
        var content = EdlGptXmlPackageWriter.BuildRawProgram(
            Layout(Partition(0, "boot_a", 2048, 4095), Partition(1, "system_b", 4096, 8191)),
            partition => partition.Name == "system_b" ? "system.bin" : null,
            "gpt_main2.bin", "gpt_backup2.bin");

        var programs = XDocument.Parse(content).Descendants("program").ToArray();
        Assert.Equal(string.Empty, (string?)programs[0].Attribute("filename"));
        Assert.Equal("system.bin", (string?)programs[1].Attribute("filename"));
        Assert.Equal("gpt_main2.bin", (string?)programs[2].Attribute("filename"));
        Assert.Equal("gpt_backup2.bin", (string?)programs[3].Attribute("filename"));
    }

    [Fact]
    public void BuildRawProgram_ZeroLengthGrowPlaceholder_EmitsZeroSectorsAndSize()
    {
        var content = EdlGptXmlPackageWriter.BuildRawProgram(Layout(
            Partition(0, "boot_a", 2048, 4095),
            Partition(1, "last_parti", 4096, 4095, typeId: Guid.Empty)));

        var programs = XDocument.Parse(content).Descendants("program").ToArray();
        Assert.Equal("last_parti", (string?)programs[1].Attribute("label"));
        Assert.Equal("0", (string?)programs[1].Attribute("num_partition_sectors"));
        Assert.Equal("0", (string?)programs[1].Attribute("size_in_KB"));
        Assert.Equal("4096", (string?)programs[1].Attribute("start_sector"));
        Assert.Equal("0x1000000", (string?)programs[1].Attribute("start_byte_hex"));
    }

    [Fact]
    public void BuildPatch_LunNamedBlobs_TargetThoseFileNames()
    {
        var content = EdlGptXmlPackageWriter.BuildPatch(Layout(Partition(0, "boot_a", 2048, 4095)),
            "gpt_main4.bin", "gpt_backup4.bin");

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var patches = PatchParser.Parse(stream);

        Assert.Equal(6, patches.Count(patch => patch.Filename == "gpt_main4.bin"));
        Assert.Equal(7, patches.Count(patch => patch.Filename == "gpt_backup4.bin"));
        Assert.DoesNotContain(patches, patch => patch.Filename == EdlGptXmlPackageWriter.PrimaryGptFileName);
    }

    [Fact]
    public void BuildPartition_GeneratedLayout_CarriesLabelTypeAndSectorDerivedSize()
    {
        var content = EdlGptXmlPackageWriter.BuildPartition(Layout(
            Partition(0, "boot_a", 2048, 4095),
            Partition(1, "last_parti", 4096, 4095, typeId: Guid.Empty)));

        var document = XDocument.Parse(content);
        Assert.Equal("configuration", document.Root?.Name.LocalName);
        Assert.Contains("SECTOR_SIZE_IN_BYTES = 4096", (string?)document.Root!.Element("parser_instructions"),
            StringComparison.Ordinal);
        Assert.Contains("GROW_LAST_PARTITION_TO_FILL_DISK=true", (string?)document.Root.Element("parser_instructions"),
            StringComparison.Ordinal);
        var physical = Assert.Single(document.Descendants("physical_partition"));
        var partition = Assert.Single(physical.Elements("partition"));
        Assert.Equal("boot_a", (string?)partition.Attribute("label"));
        Assert.Equal("8192", (string?)partition.Attribute("size_in_kb"));
        Assert.Equal(LinuxDataGuid.ToString().ToUpperInvariant(), (string?)partition.Attribute("type"));
        Assert.Equal("false", (string?)partition.Attribute("readonly"));
        Assert.Equal(string.Empty, (string?)partition.Attribute("filename"));
    }

    [Fact]
    public void BuildPartition_ReadOnlyAndBootableAttributeBits_MapToTheFlags()
    {
        var content = EdlGptXmlPackageWriter.BuildPartition(Layout(
            Partition(0, "boot_a", 2048, 4095, attributes: (1UL << 60) | (1UL << 2))));

        var partition = Assert.Single(XDocument.Parse(content).Descendants("partition"));
        Assert.Equal("true", (string?)partition.Attribute("readonly"));
        Assert.Equal("true", (string?)partition.Attribute("bootable"));
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
        Assert.Equal(string.Empty, (string?)programs[1].Attribute("filename"));
        Assert.False(await EdlGptXmlPackageWriter.UpdateRawProgramFileNameAsync(
            path, "absent", 0, "chosen.img", TestContext.Current.CancellationToken));
    }
}
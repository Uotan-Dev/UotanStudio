using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class GptWriterTests
{
    [Fact]
    public void WriteNewImage_UnmodifiedReadTable_PreservesHeaderAndEntryBytes()
    {
        using var temp = new TempDirectory();
        var disk = GptReaderTests.CreateDisk(512);
        var source = temp.WriteBytes("disk.img", disk);
        GptTable table;
        using (var read = File.OpenRead(source))
        {
            table = GptReader.Read(read, 512, 128, TestContext.Current.CancellationToken);
        }
        var target = Path.Combine(temp.Path, "rebuilt.img");

        GptTableWriter.WriteNewImage(target, table, TestContext.Current.CancellationToken);

        var rebuilt = File.ReadAllBytes(target);
        Assert.Equal(disk.Length, rebuilt.Length);
        Assert.Equal(disk.AsSpan(512, 512).ToArray(), rebuilt.AsSpan(512, 512).ToArray());
        Assert.Equal(disk.AsSpan(127 * 512, 512).ToArray(), rebuilt.AsSpan(127 * 512, 512).ToArray());
        Assert.Equal(disk.AsSpan(1024, 16384).ToArray(), rebuilt.AsSpan(1024, 16384).ToArray());
        Assert.Equal(disk.AsSpan(95 * 512, 16384).ToArray(), rebuilt.AsSpan(95 * 512, 16384).ToArray());
    }

    [Fact]
    public void ReadBack_WrittenImage_YieldsEquivalentTable()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("disk.img", GptReaderTests.CreateDisk(512));
        GptTable table;
        using (var read = File.OpenRead(source))
        {
            table = GptReader.Read(read, 512, 128, TestContext.Current.CancellationToken);
        }
        var target = Path.Combine(temp.Path, "rebuilt.img");
        GptTableWriter.WriteNewImage(target, table, TestContext.Current.CancellationToken);

        GptTable reread;
        using (var read = File.OpenRead(target))
        {
            reread = GptReader.Read(read, 512, 128, TestContext.Current.CancellationToken);
        }

        Assert.Equal(table.DiskId, reread.DiskId);
        Assert.Equal(table.MetadataSha256, reread.MetadataSha256);
        Assert.Equal(table.Partitions, reread.Partitions);
        Assert.Equal(table.HeaderSize, reread.HeaderSize);
        Assert.Equal(table.FirstUsableLba, reread.FirstUsableLba);
        Assert.Equal(table.LastUsableLba, reread.LastUsableLba);
        Assert.Equal(table.PartitionEntryCount, reread.PartitionEntryCount);
        Assert.Equal(table.PartitionEntrySize, reread.PartitionEntrySize);
        Assert.Equal(table.PrimaryEntryArrayLba, reread.PrimaryEntryArrayLba);
        Assert.Equal(table.BackupEntryArrayLba, reread.BackupEntryArrayLba);
    }

    [Fact]
    public void Create_MutatedPartitionSet_OnEightMebDisk_ReadsBack()
    {
        using var temp = new TempDirectory();
        var diskId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var table = GptTableWriter.Create(diskId, 512, 16384,
        [
            new GptPartition(0, new Guid("a2a0d0eb-e5b9-3344-87c0-68b6b72699c7"), Guid.NewGuid(), 34, 137, 0, "system", 0, 0),
            new GptPartition(1, new Guid("a2a0d0eb-e5b9-3344-87c0-68b6b72699c7"), Guid.NewGuid(), 138, 169, 0, "vendor", 0, 0)
        ]);
        Assert.Equal(34UL, table.FirstUsableLba);
        Assert.Equal(16350UL, table.LastUsableLba);
        Assert.Equal(2UL, table.PrimaryEntryArrayLba);
        var target = Path.Combine(temp.Path, "new.img");

        GptTableWriter.WriteNewImage(target, table, TestContext.Current.CancellationToken);

        GptTable reread;
        using (var read = File.OpenRead(target))
        {
            reread = GptReader.Read(read, 512, 16384, TestContext.Current.CancellationToken);
        }
        Assert.Equal(diskId, reread.DiskId);
        Assert.Equal(2, reread.Partitions.Count);
        Assert.Equal("system", reread.FindPartition("system").Name);
        Assert.Equal(34 * 512, reread.FindPartition("system").ByteOffset);
        Assert.Equal(104 * 512, reread.FindPartition("system").ByteLength);
        Assert.Equal(138UL, reread.FindPartition("vendor").FirstLba);
        Assert.Equal(table.MetadataSha256, reread.MetadataSha256);
        var mbr = File.ReadAllBytes(target);
        Assert.Equal(0xEE, (int)mbr[450]);
    }

    [Fact]
    public void Repair_CorruptedPrimaryHeader_RebuildsFromBackup()
    {
        using var temp = new TempDirectory();
        var disk = GptReaderTests.CreateDisk(512);
        disk.AsSpan(512, 512).Clear();
        var source = temp.WriteBytes("broken.img", disk);
        var target = Path.Combine(temp.Path, "repaired.img");
        var before = GptRepair.Inspect(source, 512, TestContext.Current.CancellationToken);
        Assert.False(before.PrimaryValid);
        Assert.True(before.BackupValid);
        Assert.True(before.Repairable);
        Assert.Contains(before.Issues, issue => issue.Code == GptValidationIssueCode.PrimarySignatureMissing);

        var report = GptRepair.Repair(source, target, 512, TestContext.Current.CancellationToken);

        Assert.Equal(GptRepairAction.RebuiltFromBackup, report.Action);
        GptTable repairedTable;
        using (var read = File.OpenRead(target))
        {
            repairedTable = GptReader.Read(read, 512, 128, TestContext.Current.CancellationToken);
        }
        Assert.Equal("boot", Assert.Single(repairedTable.Partitions).Name);
        Assert.Equal(0xEE, (int)File.ReadAllBytes(target)[450]);
    }

    [Fact]
    public void Repair_CorruptedPrimaryArray_RebuildsArrayAtLbaTwo()
    {
        using var temp = new TempDirectory();
        var disk = GptReaderTests.CreateDisk(512);
        disk.AsSpan(512, 512).Clear();
        disk.AsSpan(1024, 128).Clear();
        var source = temp.WriteBytes("broken.img", disk);
        var target = Path.Combine(temp.Path, "repaired.img");

        var report = GptRepair.Repair(source, target, 512, TestContext.Current.CancellationToken);

        Assert.Equal(GptRepairAction.RebuiltFromBackup, report.Action);
        GptTable repairedTable;
        using (var read = File.OpenRead(target))
        {
            repairedTable = GptReader.Read(read, 512, 128, TestContext.Current.CancellationToken);
        }
        Assert.Equal("boot", Assert.Single(repairedTable.Partitions).Name);
    }

    [Fact]
    public void Repair_MissingBackup_WritesNothingAndReports()
    {
        using var temp = new TempDirectory();
        var disk = GptReaderTests.CreateDisk(512);
        disk.AsSpan(512, 512).Clear();
        disk.AsSpan(127 * 512, 512).Clear();
        var source = temp.WriteBytes("broken.img", disk);
        var target = Path.Combine(temp.Path, "repaired.img");

        var report = GptRepair.Repair(source, target, 512, TestContext.Current.CancellationToken);

        Assert.Equal(GptRepairAction.None, report.Action);
        Assert.False(report.Repairable);
        Assert.Contains(report.Issues, issue => issue.Code == GptValidationIssueCode.BackupMissing);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Repair_HealthyImage_PublishesWithoutMutation()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("disk.img", GptReaderTests.CreateDisk(512));
        var target = Path.Combine(temp.Path, "repaired.img");

        var report = GptRepair.Repair(source, target, 512, TestContext.Current.CancellationToken);

        Assert.True(report.PrimaryValid);
        Assert.Equal(GptRepairAction.None, report.Action);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task GptFormatHandler_DiskImage_ReportsManifestAndMetadata()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("disk.img", GptReaderTests.CreateDisk(512));
        var raw = temp.WriteBytes("plain.img", new byte[1024 * 1024]);
        var handler = new GptFormatHandler();
        Assert.True(handler.CanHandle(source));
        Assert.False(handler.CanHandle(raw));

        var metadata = await handler.ReadMetadataAsync(source, TestContext.Current.CancellationToken);
        Assert.True(metadata.Success);
        Assert.Equal("1", metadata.Metadata["PartitionCount"]);
        Assert.Equal("128", metadata.Metadata["PartitionEntryCount"]);

        var output = Path.Combine(temp.Path, "out");
        var unpack = await handler.UnpackAsync(source, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpack.Success);
        var manifest = Path.Combine(output, "gpt-partitions.tsv");
        Assert.Equal([manifest], unpack.Entries);
        var lines = await File.ReadAllLinesAsync(manifest, TestContext.Current.CancellationToken);
        Assert.Equal(2, lines.Length);
        Assert.Contains("boot", lines[1]);
    }
}
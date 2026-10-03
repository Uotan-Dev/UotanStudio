using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class GptDeviceImageTests
{
    private static GptTable ReadCanonicalTable()
    {
        var disk = GptReaderTests.CreateDisk(512);
        using var stream = new MemoryStream(disk);
        return GptReader.Read(stream, 512, 128, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void BuildExtents_ReadTable_ReproducesTheImageWriterBytesExactly()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("disk.img", GptReaderTests.CreateDisk(512));
        GptTable table;
        using (var read = File.OpenRead(source))
        {
            table = GptReader.Read(read, 512, 128, TestContext.Current.CancellationToken);
        }
        var rebuilt = Path.Combine(temp.Path, "rebuilt.img");
        GptTableWriter.WriteNewImage(rebuilt, table, TestContext.Current.CancellationToken);
        var image = File.ReadAllBytes(rebuilt);
        var arrayBlocks = table.PartitionEntryCount * table.PartitionEntrySize / (uint)table.BlockSize;

        var extents = GptDeviceImage.BuildExtents(table);

        Assert.Equal(2, extents.Count);
        var primary = extents[0];
        var backup = extents[1];
        Assert.Equal(0UL, primary.StartLba);
        Assert.Equal((uint)(table.PrimaryEntryArrayLba + arrayBlocks), primary.SectorCount);
        Assert.Equal(
            image.AsSpan(0, (int)primary.SectorCount * table.BlockSize).ToArray(),
            primary.Bytes);
        Assert.Equal(table.BackupEntryArrayLba, backup.StartLba);
        Assert.Equal(
            image.AsSpan((int)table.BackupEntryArrayLba * table.BlockSize).ToArray(),
            backup.Bytes);
    }

    [Fact]
    public void BuildExtents_CanonicalLayout_StaysOutsideTheUsablePartitionArea()
    {
        var table = ReadCanonicalTable();

        var extents = GptDeviceImage.BuildExtents(table);

        Assert.True(extents[0].StartLba + extents[0].SectorCount <= table.FirstUsableLba,
            $"the primary extent ends at {extents[0].StartLba + extents[0].SectorCount} while usable data starts at {table.FirstUsableLba}");
        Assert.True(extents[1].StartLba > table.LastUsableLba,
            $"the backup extent starts at {extents[1].StartLba} while usable data ends at {table.LastUsableLba}");
        Assert.Equal(extents[0].SectorCount * (ulong)table.BlockSize, (ulong)extents[0].Bytes.Length);
        Assert.Equal(extents[1].SectorCount * (ulong)table.BlockSize, (ulong)extents[1].Bytes.Length);
    }

    [Fact]
    public void BuildExtents_PrimaryAndBackupCarryProtectiveMbrAndBothHeaders()
    {
        var table = ReadCanonicalTable();

        var extents = GptDeviceImage.BuildExtents(table);

        var mbr = extents[0].Bytes;
        Assert.Equal(0xEE, mbr[450]);
        Assert.Equal(0x55, mbr[510]);
        Assert.Equal(0xAA, mbr[511]);
        Assert.Equal("EFI PART"u8.ToArray(), mbr.AsSpan(512, 8).ToArray());
        Assert.Equal(1UL, BitConverter.ToUInt64(mbr, 512 + 24));
        Assert.Equal(table.BlockCount - 1, BitConverter.ToUInt64(mbr, 512 + 32));
        var backup = extents[1].Bytes;
        Assert.Equal(extents[1].StartLba + extents[1].SectorCount, table.BlockCount);
        Assert.Equal((table.BlockCount - table.BackupEntryArrayLba) * (ulong)table.BlockSize, (ulong)backup.Length);
        var backupHeader = backup.AsSpan(
            checked((int)(table.BlockCount - 1 - extents[1].StartLba)) * table.BlockSize);
        Assert.Equal("EFI PART"u8.ToArray(), backupHeader[..8].ToArray());
        Assert.Equal(table.BlockCount - 1, BitConverter.ToUInt64(backupHeader.Slice(24, 8)));
        Assert.Equal(1UL, BitConverter.ToUInt64(backupHeader.Slice(32, 8)));
        Assert.Equal(table.DiskId.ToByteArray(), backupHeader.Slice(56, 16).ToArray());
    }

    [Fact]
    public void BuildExtents_SelectionReturnsOnlyTheRequestedCopy()
    {
        var table = ReadCanonicalTable();
        var both = GptDeviceImage.BuildExtents(table);

        var primary = GptDeviceImage.BuildExtents(table, GptCopySelection.Primary);
        var backup = GptDeviceImage.BuildExtents(table, GptCopySelection.Backup);

        Assert.Single(primary);
        Assert.Single(backup);
        Assert.Equal(both[0].Bytes, primary[0].Bytes);
        Assert.Equal(both[1].Bytes, backup[0].Bytes);
        Assert.Equal(0UL, primary[0].StartLba);
        Assert.Equal(table.BackupEntryArrayLba, backup[0].StartLba);
    }

    [Fact]
    public void BuildExtents_UndefinedSelection_IsRejectedBeforeReadingTheTable()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GptDeviceImage.BuildExtents(ReadCanonicalTable(), (GptCopySelection)7));
    }

    [Fact]
    public void BuildExtents_NullTable_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GptDeviceImage.BuildExtents(null!));
        Assert.Throws<ArgumentNullException>(
            () => GptDeviceImage.BuildExtents(null!, GptCopySelection.Primary));
    }

    [Fact]
    public void BuildExtents_OverlappingEntryArrays_AreRejected()
    {
        var table = new GptTable(Guid.Parse("11111111-2222-3333-4444-555555555555"), 512, 128, [],
            "0000000000000000000000000000000000000000000000000000000000000000", 92, 6, 94, 128, 128, 2, 3);

        Assert.Throws<InvalidDataException>(() => GptDeviceImage.BuildExtents(table));
    }

    [Fact]
    public void BuildExtents_TablesLargerThanOneSectorPerEntry_PadEveryArrayToWholeBlocks()
    {
        var table = ReadCanonicalTable();

        var extents = GptDeviceImage.BuildExtents(table);

        Assert.Equal(0, extents[0].Bytes.Length % table.BlockSize);
        Assert.Equal(0, extents[1].Bytes.Length % table.BlockSize);
        Assert.Equal(extents[0].Bytes[1024..1536].ToArray(), extents[1].Bytes[..512].ToArray());
    }
}
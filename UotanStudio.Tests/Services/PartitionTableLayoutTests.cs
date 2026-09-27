using System.Buffers.Binary;
using UotanStudio.Models;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PartitionTableLayoutTests
{
    private const int SectorSize = 512;
    private const int PrimaryHeaderSector = 8;
    private const int PrimaryEntriesSector = 16;
    private const int BackupEntriesSector = 48;
    private const int BackupHeaderSector = 80;
    private const int LastSector = 87;
    private const int DeclaredSlots = 32;
    private const int ArrayBytes = DeclaredSlots * GptTable.EntrySize;

    [Fact]
    public async Task LoadAsync_CombinedEdlGptBlob_ParsesEntriesFromCrcLocatedArrays()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "gpt_both0.bin");
            File.WriteAllBytes(path, CreateCombinedBlob([
                Entry(6, 7, "ssd"),
                Entry(8, 8199, "persist"),
                Entry(1585672, 1585671, "userdata")
            ]));
            var editor = new PartitionEditorViewModel();

            await editor.LoadAsync(path);

            Assert.Empty(editor.CompileError);
            Assert.Equal(["ssd", "persist", "userdata"], editor.Partitions.Select(p => p.Name));
            Assert.Equal("0x0", editor.Partitions.Single(p => p.Name == "userdata").Size);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_CombinedEdlGptBlob_PatchesBothCopiesAndKeepsUnmodelledBytes()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "gpt_both0.bin");
            var original = CreateCombinedBlob([
                Entry(6, 7, "ssd"),
                Entry(8, 8199, "persist")
            ]);
            File.WriteAllBytes(path, original);
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);
            editor.Partitions.Single(p => p.Name == "persist").Size = "0x4000";

            await editor.SaveAsync();

            Assert.Empty(editor.CompileError);
            var saved = File.ReadAllBytes(path);
            Assert.Equal(original.Length, saved.Length);
            Assert.Equal(Sector(original, 0), Sector(saved, 0));
            Assert.Equal(GapRegion(original), GapRegion(saved));
            Assert.Equal(Sector(saved, PrimaryEntriesSector, ArrayBytes), Sector(saved, BackupEntriesSector, ArrayBytes));
            foreach (var sector in new[] { PrimaryHeaderSector, BackupHeaderSector })
            {
                Assert.Equal(DeclaredSlots, (int)BinaryPrimitives.ReadUInt32LittleEndian(
                    saved.AsSpan(sector * SectorSize + 80)));
            }

            var reloaded = GptTable.Parse(saved);
            Assert.Equal(2, reloaded.Entries.Count);
            Assert.Equal(8UL, reloaded.Entries[1].FirstLba);
            Assert.Equal(8UL + 0x4000UL - 1UL, reloaded.Entries[1].LastLba);
            Assert.Equal(GptTable.Parse(original).Entries[0].UniqueGuid, reloaded.Entries[0].UniqueGuid);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_CombinedEdlGptBlobWithAddedPartition_FillsFreeSlotWithoutGrowingArray()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "gpt_both0.bin");
            var original = CreateCombinedBlob([Entry(6, 7, "ssd")]);
            File.WriteAllBytes(path, original);
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);

            editor.AddPartitionCommand.Execute(null);
            var added = Assert.Single(editor.Partitions, p => p.Name == "partition_2");
            added.Start = "0x10000";
            added.Size = "0x800";
            await editor.SaveAsync();

            Assert.Empty(editor.CompileError);
            var saved = File.ReadAllBytes(path);
            Assert.Equal(original.Length, saved.Length);
            var reloaded = GptTable.Parse(saved);
            Assert.Equal(["ssd", "partition_2"], reloaded.Entries.Select(e => e.Name));
            Assert.Equal(GptTable.DefaultTypeGuid, reloaded.Entries[1].TypeGuid);
            Assert.Equal(DeclaredSlots, (int)BinaryPrimitives.ReadUInt32LittleEndian(
                saved.AsSpan(PrimaryHeaderSector * SectorSize + 80)));
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ProtectiveMbrTable_RewritesOnlyTheMbrSector()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "mbr.img");
            File.WriteAllBytes(path, MbrTable.Build(0x12345678, [
                new PartitionEntry { Name = "protective", StartSector = 1, SizeSectors = uint.MaxValue, Type = "0xEE" }
            ]));
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);

            await editor.SaveAsync();

            Assert.Empty(editor.CompileError);
            Assert.Equal(SectorSize, new FileInfo(path).Length);
            var reloaded = MbrTable.Parse(File.ReadAllBytes(path));
            var entry = Assert.Single(reloaded.Partitions);
            Assert.Equal(1UL, entry.StartLba);
            Assert.Equal((ulong)uint.MaxValue, entry.SizeSectors);
            Assert.Equal(0xEE, entry.Type);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Build_StartBeyondThirtyTwoBitLba_ThrowsForEntries()
    {
        var exception = Assert.Throws<ArgumentException>(() => MbrTable.Build(0, [
            new PartitionEntry { Name = "far", StartSector = 1UL << 32, SizeSectors = 2048, Type = "0x83" }
        ]));

        Assert.Equal("entries", exception.ParamName);
        Assert.Contains("32-bit LBA limit", exception.Message, StringComparison.Ordinal);
    }

    private static GptEntry Entry(ulong first, ulong last, string name) => new()
    {
        TypeGuid = GptTable.DefaultTypeGuid,
        UniqueGuid = new Guid((int)first, (short)last, (short)name.Length, [1, 2, 3, 4, 5, 6, 7, 8]),
        FirstLba = first,
        LastLba = last,
        Attributes = 0,
        Name = name
    };

    private static byte[] CreateCombinedBlob(IReadOnlyList<GptEntry> entries)
    {
        var standard = GptTable.Build(2UL * 1024 * 1024 * 1024, Guid.NewGuid(), entries);
        var image = new byte[(LastSector + 1) * SectorSize];
        standard.AsSpan(0, SectorSize).CopyTo(image);

        var array = new byte[ArrayBytes];
        standard.AsSpan((int)GptTable.EntriesStartLba * SectorSize, entries.Count * GptTable.EntrySize).CopyTo(array);
        var arrayCrc = GptTable.Crc32(array);
        array.CopyTo(image, PrimaryEntriesSector * SectorSize);
        array.CopyTo(image, BackupEntriesSector * SectorSize);

        WriteHeader(image, standard, PrimaryHeaderSector, 1, 0, 2, arrayCrc);
        WriteHeader(image, standard, BackupHeaderSector, 0, 1, 0, arrayCrc);
        FillUnmodelledSectors(image);
        return image;
    }

    private static void WriteHeader(byte[] image, byte[] standard, int sector, ulong selfLba, ulong alternateLba, ulong entriesLba, uint arrayCrc)
    {
        standard.AsSpan(GptTable.HeaderOffset, GptTable.HeaderSize).CopyTo(image.AsSpan(sector * SectorSize));
        var header = image.AsSpan(sector * SectorSize, GptTable.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], selfLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], DeclaredSlots);
        BinaryPrimitives.WriteUInt32LittleEndian(header[GptTable.EntriesCrcOffset..], arrayCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[GptTable.HeaderCrcOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[GptTable.HeaderCrcOffset..], GptTable.Crc32(header));
    }

    private static void FillUnmodelledSectors(byte[] image)
    {
        for (var sector = 1; sector <= LastSector; sector++)
        {
            if (sector == PrimaryHeaderSector || sector == BackupHeaderSector
                || sector is >= PrimaryEntriesSector and < PrimaryEntriesSector + ArrayBytes / SectorSize
                || sector is >= BackupEntriesSector and < BackupEntriesSector + ArrayBytes / SectorSize)
            {
                continue;
            }

            var target = image.AsSpan(sector * SectorSize, SectorSize);
            for (var i = 0; i < target.Length; i++)
            {
                target[i] = (byte)((sector * SectorSize + i) * 31 + 7);
            }
        }
    }

    private static byte[] Sector(byte[] image, int sector) => Sector(image, sector, SectorSize);

    private static byte[] Sector(byte[] image, int sector, int length) =>
        image.AsSpan(sector * SectorSize, length).ToArray();

    private static byte[] GapRegion(byte[] image) =>
        image.AsSpan(56 * SectorSize, (BackupHeaderSector - 56) * SectorSize).ToArray();

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
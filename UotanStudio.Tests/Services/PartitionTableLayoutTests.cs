using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.Mbr;
using UotanStudio.Tests.TestInfrastructure;
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
    private const int ArrayBytes = DeclaredSlots * GptLayout.EntrySize;

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

            Assert.True(GptImageEditor.TryRead(saved, out var reloaded));
            Assert.Equal(2, reloaded.Partitions.Count);
            Assert.Equal(8UL, reloaded.Partitions[1].FirstLba);
            Assert.Equal(8UL + 0x4000UL - 1UL, reloaded.Partitions[1].LastLba);
            Assert.True(GptImageEditor.TryRead(original, out var originalTable));
            Assert.Equal(originalTable.Partitions[0].Id, reloaded.Partitions[0].Id);
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
            Assert.True(GptImageEditor.TryRead(saved, out var reloaded));
            Assert.Equal(["ssd", "partition_2"], reloaded.Partitions.Select(e => e.Name));
            Assert.Equal(GptLayout.DefaultTypeGuid, reloaded.Partitions[1].TypeId);
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

    private static GptPartition Entry(ulong first, ulong last, string name) => new(
        0,
        GptLayout.DefaultTypeGuid,
        new Guid((int)first, (short)last, (short)name.Length, [1, 2, 3, 4, 5, 6, 7, 8]),
        first,
        last,
        0,
        name,
        checked((long)(first * (ulong)GptLayout.SectorSize)),
        last >= first ? checked((long)((last - first + 1) * (ulong)GptLayout.SectorSize)) : 0);

    private static byte[] CreateCombinedBlob(IReadOnlyList<GptPartition> entries)
    {
        var standard = BuildStandardTableRegions(entries);
        var image = new byte[(LastSector + 1) * SectorSize];
        standard.AsSpan(0, SectorSize).CopyTo(image);

        var array = new byte[ArrayBytes];
        standard.AsSpan((int)GptLayout.EntriesStartLba * SectorSize, entries.Count * GptLayout.EntrySize).CopyTo(array);
        var arrayCrc = System.IO.Hashing.Crc32.HashToUInt32(array);
        array.CopyTo(image, PrimaryEntriesSector * SectorSize);
        array.CopyTo(image, BackupEntriesSector * SectorSize);

        WriteHeader(image, standard, PrimaryHeaderSector, 1, 0, 2, arrayCrc);
        WriteHeader(image, standard, BackupHeaderSector, 0, 1, 0, arrayCrc);
        FillUnmodelledSectors(image);
        return image;
    }

    private static byte[] BuildStandardTableRegions(IReadOnlyList<GptPartition> entries)
    {
        var fixture = new List<GptFixtureEntry>(entries.Count);
        foreach (var entry in entries)
        {
            fixture.Add(new GptFixtureEntry(entry.TypeId, entry.Id, entry.FirstLba, entry.LastLba, entry.Name));
        }

        return GptFixtureImage.Build(2UL * 1024 * 1024 * 1024, Guid.NewGuid(), fixture);
    }

    private static void WriteHeader(byte[] image, byte[] standard, int sector, ulong selfLba, ulong alternateLba, ulong entriesLba, uint arrayCrc)
    {
        standard.AsSpan(GptLayout.HeaderOffset, GptLayout.HeaderSize).CopyTo(image.AsSpan(sector * SectorSize));
        var header = image.AsSpan(sector * SectorSize, GptLayout.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], selfLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], DeclaredSlots);
        BinaryPrimitives.WriteUInt32LittleEndian(header[GptLayout.EntriesCrcOffset..], arrayCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[GptLayout.HeaderCrcOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[GptLayout.HeaderCrcOffset..], System.IO.Hashing.Crc32.HashToUInt32(header));
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
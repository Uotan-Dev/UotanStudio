using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Build;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Writing;
using UotanStudio.Models;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PartitionEditorBandTests
{
    [Fact]
    public async Task LoadAsync_GptImage_ComputesCapacityGapsAndRatios()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "disk.img");
            File.WriteAllBytes(path, GptTable.Build(1024UL * 1024, Guid.NewGuid(),
            [
                Partition(0x22, 1024, "first"),
                Partition(1058, 990, "second")
            ]));
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);

            Assert.Equal(3, editor.Segments.Count);
            Assert.True(editor.Segments[0].BandIsGap);
            Assert.False(editor.Segments[1].BandIsGap);
            Assert.Equal(34.0 / 2048.0, editor.Segments[0].BandWidthRatio, precision: 10);
            Assert.Equal(1024.0 / 2048.0, editor.Segments[1].BandWidthRatio, precision: 10);
            Assert.Equal(990.0 / 2048.0, editor.Segments[2].BandWidthRatio, precision: 10);
            Assert.NotEmpty(editor.CapacityText);
            editor.Cleanup();
            Assert.Empty(editor.Segments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Size_EditedWithValidValue_RefreshesSegmentRatioWithoutSave()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "disk.img");
            File.WriteAllBytes(path, GptTable.Build(1024UL * 1024, Guid.NewGuid(),
            [
                Partition(0x22, 1024, "first"),
                Partition(1058, 990, "second")
            ]));
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);
            var item = editor.Partitions.Single(p => p.Name == "second");

            item.Size = "0x100";

            var segment = Assert.Single(editor.Segments, s => ReferenceEquals(s.Item, item));
            Assert.Equal(256.0 / 2048.0, segment.BandWidthRatio, precision: 10);
            Assert.True(editor.IsDirty);

            item.Size = "not-a-size";

            segment = Assert.Single(editor.Segments, s => ReferenceEquals(s.Item, item));
            Assert.Equal(990.0 / 2048.0, segment.BandWidthRatio, precision: 10);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AddPartitionCommand_FreshDocument_DoesNotExecute()
    {
        var editor = new PartitionEditorViewModel();
        Assert.False(editor.AddPartitionCommand.CanExecute(null));
        Assert.False(editor.RemovePartitionCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddPartition_Gpt_SaveReloadsExtendedTable()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "disk.img");
            File.WriteAllBytes(path, GptTable.Build(2UL * 1024 * 1024, Guid.NewGuid(),
            [
                Partition(0x22, 1024, "first")
            ]));
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);

            Assert.True(editor.AddPartitionCommand.CanExecute(null));
            editor.AddPartitionCommand.Execute(null);

            Assert.Equal(2, editor.PartitionCount);
            Assert.NotNull(editor.SelectedPartition);
            Assert.Equal("partition_2", editor.SelectedPartition!.Name);
            await editor.SaveAsync();
            Assert.Empty(editor.CompileError);
            Assert.False(editor.IsDirty);

            var reloaded = GptTable.Parse(File.ReadAllBytes(path));
            Assert.Equal(2, reloaded.Entries.Count);
            Assert.Equal("partition_2", reloaded.Entries[1].Name);
            Assert.Equal(1064UL, reloaded.Entries[1].FirstLba);
            Assert.Equal(4096UL - 1064UL, reloaded.Entries[1].LastLba - reloaded.Entries[1].FirstLba + 1);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RemovePartition_Gpt_SaveReloadsShortenedTableWithRemainingGuid()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "disk.img");
            var remainingGuid = Guid.NewGuid();
            File.WriteAllBytes(path, GptTable.Build(1024UL * 1024, Guid.NewGuid(),
            [
                new GptEntry { TypeGuid = GptTable.DefaultTypeGuid, UniqueGuid = remainingGuid, FirstLba = 34, LastLba = 1057, Name = "keep" },
                new GptEntry { TypeGuid = GptTable.DefaultTypeGuid, UniqueGuid = Guid.NewGuid(), FirstLba = 1058, LastLba = 1500, Name = "drop" }
            ]));
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);
            editor.SelectedPartition = editor.Partitions.Single(p => p.Name == "drop");

            Assert.True(editor.RemovePartitionCommand.CanExecute(null));
            editor.RemovePartitionCommand.Execute(null);

            Assert.Single(editor.Partitions);
            Assert.Null(editor.SelectedPartition);
            await editor.SaveAsync();
            Assert.Empty(editor.CompileError);

            var reloaded = GptTable.Parse(File.ReadAllBytes(path));
            var entry = Assert.Single(reloaded.Entries);
            Assert.Equal("keep", entry.Name);
            Assert.Equal(remainingGuid, entry.UniqueGuid);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_OverlappingPartitions_ReportsErrorAndKeepsFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "disk.img");
            File.WriteAllBytes(path, GptTable.Build(1024UL * 1024, Guid.NewGuid(),
            [
                Partition(0x22, 1024, "first"),
                Partition(1058, 990, "second")
            ]));
            var before = File.ReadAllBytes(path);
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);
            editor.Partitions.Single(p => p.Name == "second").Start = "0x22";

            await editor.SaveAsync();

            Assert.NotEmpty(editor.CompileError);
            Assert.Equal(before, File.ReadAllBytes(path));
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AddPartition_Lp_SaveReloadsWithNewExtent()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "super.img");
            var builder = MetadataBuilder.New(8UL * 1024 * 1024, 65536, 2);
            builder.AddPartition("system", "default", 1024 * 1024);
            builder.AddPartition("vendor", "default", 1024 * 1024);
            MetadataWriter.WriteToImageFile(path, builder.Build());
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);
            Assert.Equal(2, editor.PartitionCount);

            editor.AddPartitionCommand.Execute(null);
            await editor.SaveAsync();

            Assert.Empty(editor.CompileError);
            var reloaded = MetadataReader.ReadFromImageFile(path);
            Assert.Equal(3, reloaded.Partitions.Count);
            Assert.Equal("partition_3", reloaded.Partitions[2].NameString);
            var extent = reloaded.Extents[(int)reloaded.Partitions[2].FirstExtentIndex];
            Assert.Equal(4632UL, extent.TargetData);
            Assert.Equal(16384UL - 4632UL, extent.NumSectors);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RemovePartition_Lp_SaveReindexesRemainingExtents()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "super.img");
            var builder = MetadataBuilder.New(8UL * 1024 * 1024, 65536, 2);
            builder.AddPartition("system", "default", 1024 * 1024);
            builder.AddPartition("vendor", "default", 1024 * 1024);
            MetadataWriter.WriteToImageFile(path, builder.Build());
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);
            editor.SelectedPartition = editor.Partitions.Single(p => p.Name == "vendor");

            editor.RemovePartitionCommand.Execute(null);
            await editor.SaveAsync();

            Assert.Empty(editor.CompileError);
            var reloaded = MetadataReader.ReadFromImageFile(path);
            var partition = Assert.Single(reloaded.Partitions);
            Assert.Equal("system", partition.NameString);
            Assert.Equal(0u, partition.FirstExtentIndex);
            Assert.Equal(2048UL, Assert.Single(reloaded.Extents).NumSectors);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AddPartition_Rockchip_SaveRewritesCmdlineAndDropsRemovedEntry()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "parameter");
            File.WriteAllText(path, """
                FIRMWARE_VER: 8.1
                MACHINE_MODEL: RK3288
                CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot),0x00002000@0x00006000(misc),0x00010000@0x00008000(recovery)

                """
                .ReplaceLineEndings(Environment.NewLine));
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);
            Assert.Equal(3, editor.PartitionCount);
            Assert.Equal(3, editor.Segments.Count(s => !s.BandIsGap));
            Assert.Contains(editor.Segments, s => s.BandIsGap);

            editor.AddPartitionCommand.Execute(null);
            editor.SelectedPartition = editor.Partitions.Single(p => p.Name == "misc");
            editor.RemovePartitionCommand.Execute(null);
            await editor.SaveAsync();

            Assert.Empty(editor.CompileError);
            var reloaded = RockchipParameterParser.Parse(path);
            Assert.Equal(["uboot", "recovery", "partition_4"], reloaded.Select(p => p.Name));
            Assert.Equal(0x18000L, reloaded[2].OffsetSectors);
            Assert.Equal(0x800L, reloaded[2].SizeSectors);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RemovePartition_Mbr_SaveKeepsBootableFlagsOfRemainingEntry()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "mbr.img");
            File.WriteAllBytes(path, MbrTable.Build(0x12345678,
            [
                Partition(2048, 1024, "p1"),
                Partition(4096, 1024, "p2")
            ], [true, false]));
            var editor = new PartitionEditorViewModel();
            await editor.LoadAsync(path);

            editor.SelectedPartition = editor.Partitions[1];
            editor.RemovePartitionCommand.Execute(null);
            await editor.SaveAsync();

            Assert.Empty(editor.CompileError);
            var reloaded = MbrTable.Parse(File.ReadAllBytes(path));
            var entry = Assert.Single(reloaded.Partitions);
            Assert.Equal(2048UL, entry.StartLba);
            Assert.True(entry.Bootable);
            Assert.Equal(0x12345678u, reloaded.DiskSignature);
            editor.Cleanup();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PartitionEntry Partition(ulong start, ulong size, string name) => new()
    {
        Name = name,
        StartSector = start,
        SizeSectors = size,
        Type = GptTable.DefaultTypeGuid.ToString("D")
    };

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-part-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class GptOffsetTableTests
{
    private const long ImageLength = 0x2_8800;
    private const long TableOffset = 0x2_0000;
    private const long SecondaryOffset = 0x2_4400;

    private static readonly Guid DiskId = Guid.Parse("55555555-6666-7777-8888-999999999999");

    private static IReadOnlyList<GptFixtureEntry> Entries =>
    [
        new(GptLayout.DefaultTypeGuid, Guid.Parse("00000000-0000-0000-0000-000000000001"), 34, 259, "bl2_a"),
        new(GptLayout.DefaultTypeGuid, Guid.Parse("00000000-0000-0000-0000-000000000002"), 260, 323, "fw_dtb_a"),
        new(GptLayout.DefaultTypeGuid, Guid.Parse("00000000-0000-0000-0000-000000000003"), 324, 835, "teeos_a")
    ];

    [Fact]
    public void TryRead_PopulatedTableBehindEmptyPrimaryCopy_ReportsPopulatedTable()
    {
        var image = GptFixtureImage.BuildAtOffset(ImageLength, TableOffset, DiskId, Entries, 128, 0);
        GptFixtureImage.WriteAtOffset(image, 0, DiskId, [], 128, 0);

        Assert.True(GptImageEditor.TryRead(image, out var table));

        Assert.Equal(["bl2_a", "fw_dtb_a", "teeos_a"], table.Partitions.Select(partition => partition.Name));
        Assert.Equal([34UL, 260UL, 324UL], table.Partitions.Select(partition => partition.FirstLba));
        Assert.Equal([226UL, 64UL, 512UL], table.Partitions.Select(partition => partition.LastLba - partition.FirstLba + 1));
        Assert.Equal(836UL, table.BlockCount);
        Assert.Equal(DiskId, table.DiskId);
    }

    [Fact]
    public void TryRead_SeveralTables_ReadsRequestedCopyAndReportsSource()
    {
        var image = GptFixtureImage.BuildAtOffset(ImageLength, TableOffset, DiskId, Entries, 128, 0);
        GptFixtureImage.WriteAtOffset(image, SecondaryOffset, DiskId, [Entries[0]], 128, 0);
        GptFixtureImage.WriteAtOffset(image, 0, DiskId, [], 128, 0);

        Assert.True(GptImageEditor.TryRead(image, 257, out var richest, out var richestSource));
        Assert.Equal(3, richest.Partitions.Count);
        Assert.Equal(new GptTableCopy(257, 128, 3), richestSource);

        Assert.True(GptImageEditor.TryRead(image, 291, out var single, out var singleSource));
        Assert.Equal(["bl2_a"], single.Partitions.Select(partition => partition.Name));
        Assert.Equal(291UL, singleSource.HeaderLba);

        Assert.True(GptImageEditor.TryRead(image, 1, out var empty, out _));
        Assert.Empty(empty.Partitions);

        Assert.False(GptImageEditor.TryRead(image, 700, out _, out _));
    }

    [Fact]
    public void TryRead_ContainerWithSeveralTables_SelectsRichestTableAndReportsEveryCopy()
    {
        var image = GptFixtureImage.BuildAtOffset(ImageLength, TableOffset, DiskId, Entries, 128, 0);
        GptFixtureImage.WriteAtOffset(image, SecondaryOffset, DiskId, [Entries[0]], 128, 0);
        GptFixtureImage.WriteAtOffset(image, 0, DiskId, [], 128, 0);

        Assert.True(GptImageEditor.TryRead(image, out var table));

        Assert.Equal(["bl2_a", "fw_dtb_a", "teeos_a"], table.Partitions.Select(partition => partition.Name));
        var copies = GptImageEditor.DescribeCopies(image);
        Assert.Equal([1UL, 257UL, 291UL], copies.Select(copy => copy.HeaderLba));
        Assert.Equal([0, 3, 1], copies.Select(copy => copy.PartitionCount));
        Assert.Equal([128U, 128U, 128U], copies.Select(copy => copy.EntryCount));
    }

    [Fact]
    public void Patch_ContainerWithSeveralTables_KeepsTheOtherPopulatedTable()
    {
        var image = GptFixtureImage.BuildAtOffset(ImageLength, TableOffset, DiskId, Entries, 128, 0);
        GptFixtureImage.WriteAtOffset(image, SecondaryOffset, DiskId, [Entries[0]], 128, 0);
        GptFixtureImage.WriteAtOffset(image, 0, DiskId, [], 128, 0);
        Assert.True(GptImageEditor.TryRead(image, out var table));
        var renamed = table.Partitions
            .Select(partition => partition with { Name = "renamed_" + partition.Name })
            .ToArray();

        var patched = GptImageEditor.Patch(image, renamed);

        Assert.Equal(
            image.AsSpan((int)SecondaryOffset + 2 * GptLayout.SectorSize, 128 * GptLayout.EntrySize).ToArray(),
            patched.AsSpan((int)SecondaryOffset + 2 * GptLayout.SectorSize, 128 * GptLayout.EntrySize).ToArray());
        Assert.True(GptImageEditor.TryRead(patched, out var reloaded));
        Assert.Equal(["renamed_bl2_a", "renamed_fw_dtb_a", "renamed_teeos_a"],
            reloaded.Partitions.Select(partition => partition.Name));
    }

    [Fact]
    public void TryRead_TableOnlyImageWithEmptyEntryArray_KeepsEmptyTable()
    {
        var image = GptFixtureImage.BuildAtOffset(0x4400, 0, DiskId, [], 128, 0);

        Assert.True(GptImageEditor.TryRead(image, out var table));
        Assert.Empty(table.Partitions);
        Assert.Equal(1UL, table.BlockCount);
    }

    [Fact]
    public void Patch_PopulatedTableBehindEmptyPrimaryCopy_RewritesBothEntryArrays()
    {
        var image = GptFixtureImage.BuildAtOffset(ImageLength, TableOffset, DiskId, Entries, 128, 0);
        GptFixtureImage.WriteAtOffset(image, 0, DiskId, [], 128, 0);
        Assert.True(GptImageEditor.TryRead(image, out var table));
        var renamed = table.Partitions
            .Select(partition => partition.Index == 0 ? partition with { Name = "boot_ctrl" } : partition)
            .ToArray();

        var patched = GptImageEditor.Patch(image, renamed);

        Assert.Equal(image.Length, patched.Length);
        Assert.True(GptImageEditor.TryRead(patched, out var reloaded));
        Assert.Equal(["boot_ctrl", "fw_dtb_a", "teeos_a"], reloaded.Partitions.Select(partition => partition.Name));
        Assert.Equal(
            patched.AsSpan(2 * GptLayout.SectorSize, 128 * GptLayout.EntrySize).ToArray(),
            patched.AsSpan((int)TableOffset + 2 * GptLayout.SectorSize, 128 * GptLayout.EntrySize).ToArray());
    }
}
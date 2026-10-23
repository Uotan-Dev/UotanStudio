using UotanStudio.Firmware.BlockImage;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.Mbr;
using UotanStudio.Firmware.VendorFirmware.Amlogic;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PartitionTableEditingTests
{
    [Fact]
    public void Patch_GptImageUnchangedPartitions_ProducesIdenticalBytes()
    {
        var image = CreateGptImage();
        Assert.True(GptImageEditor.TryRead(image, out var table));

        var patched = GptImageEditor.Patch(image, table.Partitions);

        Assert.Equal(image, patched);
    }

    [Fact]
    public void Patch_GptImageRenamedPartition_RoundTripsAndKeepsUnmodelledBytes()
    {
        var image = CreateGptImage();
        Assert.True(GptImageEditor.TryRead(image, out var table));
        var partitions = table.Partitions
            .Select(partition => partition.Index == 1
                ? partition with { Name = "renamed", LastLba = partition.FirstLba + 63 }
                : partition)
            .ToArray();

        var patched = GptImageEditor.Patch(image, partitions);

        Assert.Equal(image.Length, patched.Length);
        Assert.Equal(image.AsSpan(0, GptLayout.SectorSize).ToArray(), patched.AsSpan(0, GptLayout.SectorSize).ToArray());
        Assert.True(GptImageEditor.TryRead(patched, out var reloaded));
        Assert.Equal(["first", "renamed"], reloaded.Partitions.Select(partition => partition.Name));
        Assert.Equal(64UL, reloaded.Partitions[1].LastLba - reloaded.Partitions[1].FirstLba + 1);
        Assert.Equal(table.DiskId, reloaded.DiskId);
    }

    [Fact]
    public void Build_MbrTable_RoundTripsPrimaryAndLogicalPartitions()
    {
        var entries = new List<PartitionEntry>
        {
            new() { Name = "p1", StartSector = 64, SizeSectors = 64, Type = "0x83" },
            new() { Name = "p2", StartSector = 256, SizeSectors = 64, Type = "0x0C" },
            new() { Name = "p3", StartSector = 512, SizeSectors = 64, Type = "0x83" },
            new() { Name = "p4", StartSector = 1024, SizeSectors = 64, Type = "0x83" },
            new() { Name = "p5", StartSector = 2048, SizeSectors = 64, Type = "0x83" }
        };

        var image = MbrTable.Build(0x12345678, entries, [true, false, false, false, false]);
        var info = MbrTable.Parse(image);

        Assert.Equal(0x12345678u, info.DiskSignature);
        Assert.Equal([64UL, 256UL, 512UL, 1024UL, 2048UL], info.Partitions.Select(partition => partition.StartLba));
        Assert.Equal([64UL, 64UL, 64UL, 64UL, 64UL], info.Partitions.Select(partition => partition.SizeSectors));
        Assert.Equal([(byte)0x83, (byte)0x0C, (byte)0x83, (byte)0x83, (byte)0x83], info.Partitions.Select(partition => partition.Type));
        Assert.True(info.Partitions[0].Bootable);
        Assert.DoesNotContain(info.Partitions.Skip(1), partition => partition.Bootable);
    }

    [Fact]
    public void Build_AmlogicTable_RoundTripsNamesGeometryAndBootableFlags()
    {
        var entries = new List<PartitionEntry>
        {
            new() { Name = "boot", StartSector = 64, SizeSectors = 64, Type = "0x83" },
            new() { Name = "system", StartSector = 256, SizeSectors = 128, Type = "0x83" },
            new() { Name = "vendor", StartSector = 512, SizeSectors = 64, Type = "0x83" },
            new() { Name = "userdata", StartSector = 1024, SizeSectors = 64, Type = "0x83" },
            new() { Name = "cache", StartSector = 2048, SizeSectors = 64, Type = "0x83" }
        };

        var image = AmlogicPartitionTable.Build(0x87654321, entries, [true, false, false, true, false]);

        Assert.True(AmlogicPartitionTable.HasMagic(image));
        var info = AmlogicPartitionTable.Parse(image);
        Assert.Equal(0x87654321u, info.DiskSignature);
        Assert.Equal(["boot", "system", "vendor", "userdata", "cache"], info.Partitions.Select(partition => partition.Name));
        Assert.Equal([64UL, 256UL, 512UL, 1024UL, 2048UL], info.Partitions.Select(partition => partition.StartLba));
        Assert.Equal([64UL, 128UL, 64UL, 64UL, 64UL], info.Partitions.Select(partition => partition.SizeSectors));
        Assert.True(info.Partitions[0].Bootable);
        Assert.True(info.Partitions[3].Bootable);
        Assert.False(info.Partitions[4].Bootable);
    }

    [Fact]
    public void TryDetect_KnownLayouts_ReturnsMatchingKinds()
    {
        var gpt = CreateGptImage();
        var mbr = MbrTable.Build(1, [new PartitionEntry { Name = "p1", StartSector = 8, SizeSectors = 16, Type = "0x83" }]);
        var amlogic = AmlogicPartitionTable.Build(1, [new PartitionEntry { Name = "boot", StartSector = 8, SizeSectors = 16, Type = "0x83" }]);

        Assert.True(PartitionTableProbe.TryDetect(gpt, out var gptKind));
        Assert.Equal(PartitionTableKind.Gpt, gptKind);
        Assert.True(PartitionTableProbe.TryDetect(mbr, out var mbrKind));
        Assert.Equal(PartitionTableKind.Mbr, mbrKind);
        Assert.True(PartitionTableProbe.TryDetect(amlogic, out var amlogicKind));
        Assert.Equal(PartitionTableKind.Amlogic, amlogicKind);
        Assert.False(PartitionTableProbe.TryDetect(new byte[GptLayout.SectorSize], out var unknownKind));
        Assert.Equal(PartitionTableKind.Unknown, unknownKind);
    }

    [Fact]
    public void TryParse_KnownImages_ReturnsMatchingKinds()
    {
        Assert.True(PartitionTableProbe.TryParse(CreateGptImage(), out var gptKind));
        Assert.Equal(PartitionTableKind.Gpt, gptKind);

        var mbr = MbrTable.Build(1, [new PartitionEntry { Name = "p1", StartSector = 8, SizeSectors = 16, Type = "0x83" }]);
        Assert.True(PartitionTableProbe.TryParse(mbr, out var mbrKind));
        Assert.Equal(PartitionTableKind.Mbr, mbrKind);

        var amlogic = AmlogicPartitionTable.Build(1, [new PartitionEntry { Name = "boot", StartSector = 8, SizeSectors = 16, Type = "0x83" }]);
        Assert.True(PartitionTableProbe.TryParse(amlogic, out var amlogicKind));
        Assert.Equal(PartitionTableKind.Amlogic, amlogicKind);

        Assert.False(PartitionTableProbe.TryParse(new byte[GptLayout.SectorSize * 2], out var unknownKind));
        Assert.Equal(PartitionTableKind.Unknown, unknownKind);
    }

    private static byte[] CreateGptImage()
    {
        var entries = new List<GptFixtureEntry>
        {
            new(GptLayout.DefaultTypeGuid, Guid.NewGuid(), 34, 1057, "first"),
            new(GptLayout.DefaultTypeGuid, Guid.NewGuid(), 1058, 1500, "second")
        };

        return GptFixtureImage.Build(1024UL * 1024, Guid.Parse("11111111-2222-3333-4444-555555555555"), entries);
    }
}
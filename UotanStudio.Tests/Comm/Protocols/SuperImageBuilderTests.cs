using UotanStudio.Comm.Protocols.Fastboot.Utils;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

/// <summary>
/// Super image builder tests: LP geometry parsing / checksum validation, Build parameter
/// validation, metadata read-back for every slot, backup-geometry detection and the
/// path-based partition enumeration helpers.
/// <para>Super 镜像构建测试：LP geometry 解析/校验和验证、Build 参数校验、各槽位元数据
/// 回读、备份 geometry 检测以及基于路径的分区枚举辅助方法。</para>
/// </summary>
public class SuperImageBuilderTests
{
    private static readonly SuperImageBuilder Builder = new();

    private const uint MetadataMaxSize = 64 * 1024;
    private const uint MetadataSlotCount = 2;

    private static byte[] BuildImage(
        IReadOnlyList<SuperBuildPartition> partitions,
        uint deviceSize = 8 * 1024 * 1024,
        uint metadataMaxSize = MetadataMaxSize,
        uint metadataSlotCount = MetadataSlotCount,
        uint blockSize = 4096)
        => Builder.Build(partitions, deviceSize, metadataMaxSize, metadataSlotCount, blockSize);

    // ============== LpGeometry ==============

    [Fact]
    public void LpGeometry_TooShortOrBadMagic_ReturnsNull()
    {
        Assert.Null(LpGeometry.TryParse(new byte[10], 0));
        Assert.Null(LpGeometry.TryParse(null!, 0));
        Assert.Null(LpGeometry.TryParse(new byte[64], -1));

        var garbage = new byte[128];
        Assert.Null(LpGeometry.TryParse(garbage, 0));
    }

    [Fact]
    public void LpGeometry_ParsedFromBuiltImage_HasValidChecksum()
    {
        var image = BuildImage([new SuperBuildPartition("system", "default", 0x20000)]);

        var geometry = LpGeometry.TryParse(image, 0);
        Assert.NotNull(geometry);
        Assert.Equal(52u, geometry.StructSize);
        Assert.Equal(MetadataMaxSize, geometry.MetadataMaxSize);
        Assert.Equal(MetadataSlotCount, geometry.MetadataSlotCount);
        Assert.Equal(4096u, geometry.LogicalBlockSize);
        Assert.True(geometry.IsChecksumValid);
    }

    [Fact]
    public void LpGeometry_ChecksumCorruption_IsDetected()
    {
        var image = BuildImage([new SuperBuildPartition("system", "default", 0x20000)]);
        // Corrupt one byte inside the 32-byte checksum field (struct layout: magic(4) +
        // struct_size(4) + checksum(32) ...), keeping struct_size == 52 so the checksum
        // is actually recomputed and found inconsistent.
        // 破坏 32 字节校验和字段中的一字节（结构布局：magic(4) + struct_size(4) + checksum(32)…），
        // 保持 struct_size == 52，使校验和确实被重算并判定不一致。
        image[10] ^= 0xFF;

        var geometry = LpGeometry.TryParse(image, 0);
        Assert.NotNull(geometry);
        Assert.False(geometry.IsChecksumValid);
    }

    // ============== Build validation ==============

    [Fact]
    public void Build_ZeroDeviceSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildImage([], deviceSize: 0));
    }

    [Fact]
    public void Build_ZeroMetadataSlotCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildImage([], metadataSlotCount: 0));
    }

    [Fact]
    public void Build_MetadataSlotTooSmall_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildImage([], metadataMaxSize: 128));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1025u)]
    [InlineData(1536u)]
    [InlineData(4095u)]
    public void Build_InvalidBlockSize_Throws(uint blockSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildImage([new SuperBuildPartition("system", "default", 4096)], blockSize: blockSize));
    }

    [Fact]
    public void Build_EmptyPartitionName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            BuildImage([new SuperBuildPartition("", "default", 4096)]));
    }

    [Fact]
    public void Build_OverlongPartitionName_Throws()
    {
        var tooLong = new string('x', 37);
        Assert.Throws<ArgumentException>(() =>
            BuildImage([new SuperBuildPartition(tooLong, "default", 4096)]));
    }

    [Fact]
    public void Build_MaxLengthPartitionName_IsAccepted()
    {
        var name = new string('x', 36);
        var image = BuildImage([new SuperBuildPartition(name, "default", 4096)]);
        Assert.Equal(name, Assert.Single(Builder.GetLogicalPartitions(image)).Name);
    }

    [Fact]
    public void Build_NegativePartitionSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildImage([new SuperBuildPartition("system", "default", -1)]));
    }

    [Fact]
    public void Build_DeviceTooSmall_Throws()
    {
        // 1 MiB of partitions on a 256 KiB device cannot fit metadata + data.
        // 256 KiB 设备无法容纳 1 MiB 分区数据 + 元数据。
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildImage([new SuperBuildPartition("system", "default", 0x100000)], deviceSize: 256 * 1024));
    }

    // ============== Metadata read-back ==============

    [Fact]
    public void ReadMetadata_InvalidInputs_ReturnNull()
    {
        Assert.Null(Builder.ReadMetadata(null!));
        Assert.Null(Builder.ReadMetadata(Array.Empty<byte>()));
        Assert.Null(Builder.ReadMetadata(new byte[4096]));
        Assert.Null(Builder.ReadMetadata(new byte[16384], slot: -1));
    }

    [Fact]
    public void ReadMetadata_SlotOutOfRange_ReturnsNull()
    {
        var image = BuildImage([new SuperBuildPartition("system", "default", 0x10000)]);
        Assert.Null(Builder.ReadMetadata(image, 2));
    }

    [Fact]
    public void ReadMetadata_CorruptedPrimaryMetadata_ReturnsNull()
    {
        var image = BuildImage([new SuperBuildPartition("system", "default", 0x10000)]);
        // Zero the LP metadata header magic (0x414C5030) at the start of slot 0.
        // 将槽位 0 起始处的 LP 元数据头魔数（0x414C5030）清零。
        Array.Clear(image, 2 * SuperImageBuilder.GeometryBlockSize, 4);

        Assert.Null(Builder.ReadMetadata(image, 0));
    }

    [Fact]
    public void ReadMetadata_EverySlotReadsBackIdentically()
    {
        var image = BuildImage(
        [
            new SuperBuildPartition("system", "default", 0x20000),
            new SuperBuildPartition("vendor", "default", 0x10000)
        ]);

        for (uint slot = 0; slot < MetadataSlotCount; slot++)
        {
            var metadata = Builder.ReadMetadata(image, (int)slot);
            Assert.NotNull(metadata);
            Assert.Equal((int)slot, metadata.Slot);
            Assert.Equal(10u, metadata.MajorVersion);
            Assert.Equal(2, metadata.Partitions.Count);
            Assert.Equal(2, metadata.Extents.Count);
            Assert.Equal(1, metadata.BlockDevices.Count);
            Assert.Equal("system", metadata.Partitions[0].Name);
            Assert.Equal("vendor", metadata.Partitions[1].Name);
        }
    }

    [Fact]
    public void ReadMetadata_MultipleGroups_ResolveGroupNames()
    {
        var image = BuildImage(
        [
            new SuperBuildPartition("system", "default", 0x20000),
            new SuperBuildPartition("vendor", "vendor_data", 0x10000)
        ]);

        var metadata = Builder.ReadMetadata(image);
        Assert.NotNull(metadata);
        Assert.Equal(["default", "vendor_data"], metadata.GroupNames);
        Assert.Equal("default", metadata.Partitions[0].GroupName);
        Assert.Equal("vendor_data", metadata.Partitions[1].GroupName);
    }

    [Fact]
    public void ReadMetadata_ZeroSizedPartition_IsPreserved()
    {
        var image = BuildImage([new SuperBuildPartition("scratch", "default", 0)]);

        var metadata = Builder.ReadMetadata(image);
        Assert.NotNull(metadata);
        var partition = Assert.Single(metadata.Partitions);
        Assert.Equal("scratch", partition.Name);
        Assert.Equal(0L, partition.SizeInBytes);
        Assert.Equal(0ul, metadata.Extents[0].NumSectors);
    }

    // ============== IsSuperImage / GetLogicalPartitions ==============

    [Fact]
    public void IsSuperImage_RejectsGarbage()
    {
        Assert.False(Builder.IsSuperImage(new byte[8192]));
        Assert.False(Builder.IsSuperImage(Array.Empty<byte>()));
        Assert.False(Builder.IsSuperImage(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void IsSuperImage_RecognizesBackupGeometryOnly()
    {
        var image = BuildImage([new SuperBuildPartition("system", "default", 0x10000)]);
        // Wipe the primary geometry block; the backup at offset 4096 must still qualify.
        // 清除主 geometry 块；偏移 4096 处的备份副本必须仍然识别为 super 镜像。
        Array.Clear(image, 0, SuperImageBuilder.GeometryBlockSize);

        Assert.True(Builder.IsSuperImage(image));

        var metadata = Builder.ReadMetadata(image);
        Assert.NotNull(metadata);
        Assert.Equal("system", Assert.Single(metadata.Partitions).Name);
    }

    [Fact]
    public void IsSuperImage_FileVariants()
    {
        using var dir = new TempDirectory();
        var imagePath = dir.WriteBytes("super.img", BuildImage([new SuperBuildPartition("system", "default", 0x10000)]));

        Assert.True(Builder.IsSuperImage(imagePath));
        Assert.False(Builder.IsSuperImage(dir.Write("junk.bin", "not a super image")));
        Assert.False(Builder.IsSuperImage(Path.Combine(dir.Path, "missing.img")));
    }

    [Fact]
    public void GetLogicalPartitions_PathValidation()
    {
        Assert.Throws<ArgumentException>(() => Builder.GetLogicalPartitions("  "));
        Assert.Throws<FileNotFoundException>(() =>
            Builder.GetLogicalPartitions(Path.Combine(Path.GetTempPath(), $"uotan-missing-super-{Guid.NewGuid():N}.img")));
    }

    [Fact]
    public void GetLogicalPartitions_BytesAndPath_Agree()
    {
        using var dir = new TempDirectory();
        var image = BuildImage(
        [
            new SuperBuildPartition("system", "default", 0x20000, attributes: 0x2),
            new SuperBuildPartition("vendor", "default", 0x10000)
        ]);
        var imagePath = dir.WriteBytes("super.img", image);

        var fromBytes = Builder.GetLogicalPartitions(image);
        var fromPath = Builder.GetLogicalPartitions(imagePath);

        Assert.Equal(fromBytes.Count, fromPath.Count);
        for (var i = 0; i < fromBytes.Count; i++)
        {
            Assert.Equal(fromBytes[i].Name, fromPath[i].Name);
            Assert.Equal(fromBytes[i].SizeInBytes, fromPath[i].SizeInBytes);
            Assert.Equal(fromBytes[i].Attributes, fromPath[i].Attributes);
        }

        Assert.Equal("system", fromBytes[0].Name);
        Assert.Equal(0x2u, fromBytes[0].Attributes);
    }
}

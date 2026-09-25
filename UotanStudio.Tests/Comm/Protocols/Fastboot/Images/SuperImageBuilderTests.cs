using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Fastboot.Images;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Fastboot.Images;

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
        Assert.Null(LpGeometry.TryParse(new byte[64], int.MaxValue));

        var garbage = new byte[128];
        Assert.Null(LpGeometry.TryParse(garbage, 0));
    }

    [Fact]
    public void LpGeometry_ParsedFromBuiltImage_HasValidChecksum()
    {
        var image = BuildImage([new SuperBuildPartition("system", "default", 0x20000)]);

        var geometry = LpGeometry.TryParse(image, SuperImageBuilder.GeometryBlockSize);
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
        // Firmware 布局下 geometry 在 offset 4096，破坏该处的校验和字段。
        image[SuperImageBuilder.GeometryBlockSize + 10] ^= 0xFF;

        var geometry = LpGeometry.TryParse(image, SuperImageBuilder.GeometryBlockSize);
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
    public void ReadMetadata_CorruptedPrimaryMetadata_UsesBackup()
    {
        var image = BuildImage([new SuperBuildPartition("system", "default", 0x10000)]);
        // Zero the LP metadata header magic (0x414C5030) at the start of slot 0.
        // Firmware 布局下 metadata 区域起始偏移 = LP_PARTITION_RESERVED_BYTES + 2 * GeometryBlockSize = 12288。
        var metadataOffset = 4096 + 2 * SuperImageBuilder.GeometryBlockSize;
        Array.Clear(image, metadataOffset, 4);

        Assert.Equal("system", Assert.Single(Assert.IsType<SuperImageMetadata>(Builder.ReadMetadata(image, 0)).Partitions).Name);
        Array.Clear(image, metadataOffset + (int)(MetadataMaxSize * MetadataSlotCount), 4);
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
            Assert.Single(metadata.BlockDevices);
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
        Assert.Equal(0u, partition.NumExtents);
        Assert.Empty(metadata.Extents);
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
        // Primary geometry is at 4096; the backup is at 8192 after the reserved block.
        Array.Clear(image, 4096, SuperImageBuilder.GeometryBlockSize);

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

    [Theory]
    [InlineData(512u, 512u, 1u)]
    [InlineData(1024u, 4096u, 3u)]
    [InlineData(4096u, 65536u, 2u)]
    [InlineData(8192u, 8192u, 3u)]
    public void Build_UsesStandardAbsoluteLayoutAndRequestedGeometry(uint blockSize, uint metadataSize, uint slots)
    {
        var image = BuildImage([new SuperBuildPartition("system", "default", blockSize)], metadataMaxSize: metadataSize,
            metadataSlotCount: slots, blockSize: blockSize);
        Assert.Equal(8 * 1024 * 1024, image.Length);
        Assert.All(image[..4096], value => Assert.Equal(0, value));
        Assert.Equal(image[4096..8192], image[8192..12288]);
        Assert.Equal(metadataSize, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(4096 + 40)));
        Assert.Equal(slots, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(4096 + 44)));
        Assert.Equal(blockSize, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(4096 + 48)));

        var dataStart = ((12288UL + 2UL * metadataSize * slots + blockSize - 1) / blockSize) * blockSize;
        for (var slot = 0; slot < slots; slot++)
        {
            var metadata = Assert.IsType<SuperImageMetadata>(Builder.ReadMetadata(image, slot));
            Assert.Equal(dataStart / 512, Assert.Single(metadata.BlockDevices).FirstLogicalSector);
            Assert.Equal(dataStart / 512, Assert.Single(metadata.Extents).TargetData);
            var primary = checked(12288 + slot * (int)metadataSize);
            var backup = checked(primary + (int)(metadataSize * slots));
            Assert.Equal(image.AsSpan(primary, (int)metadata.MetadataSize).ToArray(), image.AsSpan(backup, (int)metadata.MetadataSize).ToArray());
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(primary + 8));
            Assert.Equal(128u, headerSize);
            var header = image.AsSpan(primary, (int)headerSize).ToArray();
            header.AsSpan(12, 32).Clear();
            Assert.Equal(SHA256.HashData(header), image.AsSpan(primary + 12, 32).ToArray());
        }
    }

    [Fact]
    public void Build_ExactCapacityFitsAndOneMoreBlockFailsBeforeOutputChanges()
    {
        const uint firstDataByte = 12288 + 4 * 65536;
        using var output = new MemoryStream();
        Builder.Build(output, [new SuperBuildPartition("system", "default", 4096)], firstDataByte + 4096, 65536, 2);
        var original = output.ToArray();
        Assert.Equal(firstDataByte + 4096, output.Length);
        Assert.ThrowsAny<ArgumentException>(() => Builder.Build(output,
            [new SuperBuildPartition("system", "default", 8192)], firstDataByte + 4096, 65536, 2));
        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void Build_StreamSupportsLargeDeviceWithoutBufferingImage()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "large-super.img");
        using (var output = File.Create(path))
        {
            Builder.Build(output, [new SuperBuildPartition("system", "default", 4096)], 8UL * 1024 * 1024 * 1024, 65536, 2);
            Assert.Equal(8L * 1024 * 1024 * 1024, output.Length);
        }
        Assert.True(Builder.IsSuperImage(path));
        Assert.Equal("system", Assert.Single(Builder.GetLogicalPartitions(path)).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildImage([], deviceSize: 512 * 1024 * 1024));
    }

    [Fact]
    public void ReadMetadata_RejectsInvalidGeometryChecksumsInBothCopies()
    {
        var image = BuildImage([]);
        image[4096 + 8] ^= 1;
        Assert.NotNull(Builder.ReadMetadata(image));
        image[8192 + 8] ^= 1;
        Assert.Null(Builder.ReadMetadata(image));
        Assert.False(Builder.IsSuperImage(image));
    }
}
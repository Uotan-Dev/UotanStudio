using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks LP disk layouts independently of the metadata writer. <para>独立于元数据写入器检查 LP 磁盘布局。</para></summary>
public class LpFormatConformanceTests
{
    private static LpMetadataGeometry Geometry => new() { MetadataMaxSize = 65536, MetadataSlotCount = 2, LogicalBlockSize = 4096 };

    [Theory]
    [InlineData(0, 128)]
    [InlineData(1, 128)]
    [InlineData(2, 256)]
    public void ReaderAcceptsVersionedHeaders(ushort minor, int size)
    {
        var header = CreateHeader(minor, size);
        using var stream = new MemoryStream(header);
        var metadata = MetadataReader.ParseMetadata(Geometry, stream);
        Assert.Equal(minor, metadata.Header.MinorVersion);
        Assert.Equal(size + 64, stream.Position);
        metadata.Header = new LpMetadataHeader { MinorVersion = minor };
        var serialized = MetadataWriter.SerializeMetadata(metadata);
        Assert.Equal(size + 64, serialized.Length);
        Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32LittleEndian(serialized.AsSpan(8)));
    }

    [Fact]
    public void BackupIsAfterPrimarySlotsAndRecoversCorruptPrimary()
    {
        var bytes = new byte[1024 * 1024];
        using var image = new MemoryStream(bytes);
        var metadata = MetadataBuilder.New((ulong)bytes.Length, Geometry.MetadataMaxSize, Geometry.MetadataSlotCount).Build();
        MetadataWriter.WriteToImageStream(image, metadata);
        Assert.Equal(143360, MetadataReader.GetBackupMetadataOffset(Geometry, (ulong)image.Length, 0));
        bytes[12288] = 0;
        var result = MetadataReader.ReadFromImageStream(image);
        Assert.Equal((ushort)10, result.Header.MajorVersion);
        Assert.Equal((byte)0, bytes[image.Length - 1]);
    }

    [Theory]
    [InlineData(0, 256, 0u, 0u)]
    [InlineData(2, 128, 0u, 0u)]
    [InlineData(0, 128, uint.MaxValue, 0u)]
    [InlineData(0, 128, 0u, uint.MaxValue)]
    public void ReaderRejectsInvalidHeaderAndTableBounds(ushort minor, int size, uint tablesSize, uint entryCount)
    {
        var bytes = CreateHeader(minor, size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), tablesSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), entryCount);
        UpdateHash(bytes);
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => MetadataReader.ParseMetadata(Geometry, stream));
    }

    [Fact]
    public void BuilderPreservesGeometryAndAllocatesAbsoluteAlignedSectors()
    {
        var builder = MetadataBuilder.New(32768, 512, 3, 8192);
        builder.AddPartition("first", "default", 1);
        builder.AddPartition("second", "default", 4097);
        builder.AddPartition("empty", "default", 0);
        Assert.Throws<ArgumentException>(() => builder.AddPartition("overflow", "default", 1));
        var metadata = builder.Build();
        Assert.Equal(512u, metadata.Geometry.MetadataMaxSize);
        Assert.Equal(3u, metadata.Geometry.MetadataSlotCount);
        Assert.Equal(8192u, metadata.Geometry.LogicalBlockSize);
        Assert.Equal(32UL, metadata.BlockDevices[0].FirstLogicalSector);
        Assert.Equal(new ulong[] { 32, 48 }, metadata.Extents.Select(e => e.TargetData));
        Assert.All(metadata.Extents, e => Assert.Equal(16UL, e.NumSectors));
        Assert.Equal(0u, metadata.Partitions[2].NumExtents);
        using var image = new MemoryStream(new byte[32768]);
        MetadataWriter.WriteToImageStream(image, metadata);
        var parsed = MetadataReader.ReadFromImageStream(image);
        Assert.Equal(3, parsed.Partitions.Count);
        Assert.Equal(48UL, parsed.Extents[1].TargetData);
    }

    [Fact]
    public void BuilderRejectsInsufficientDeviceAndOverflowWithoutAdvancingAllocation()
    {
        Assert.Throws<ArgumentException>(() => MetadataBuilder.New(274432, 65536, 2));
        Assert.Throws<ArgumentException>(() => MetadataBuilder.New(1048577, 65536, 2));
        var builder = MetadataBuilder.New(1048576, 65536, 2);
        Assert.Throws<OverflowException>(() => builder.AddPartition("huge", "default", ulong.MaxValue));
        builder.AddPartition("valid", "default", 1);
        var metadata = builder.Build();
        Assert.Equal(536UL, Assert.Single(metadata.Extents).TargetData);
    }

    [Fact]
    public void BuilderChecksGroupCapacityAfterLogicalBlockRounding()
    {
        var builder = MetadataBuilder.New(1048576, 65536, 2);
        builder.AddGroup("limited", 4096);
        builder.AddPartition("first", "limited", 1);
        builder.AddPartition("second", "limited", 1);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    private static byte[] CreateHeader(ushort minor, int size)
    {
        var header = new byte[size + 64];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x414C5030);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), minor);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88), 52);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(100), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(112), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(124), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(120), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(size), 536);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(size + 8), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(size + 16), 1048576);
        "super"u8.CopyTo(header.AsSpan(size + 24));
        SHA256.HashData(header.AsSpan(size)).CopyTo(header, 48);
        UpdateHash(header);
        return header;
    }

    private static void UpdateHash(byte[] header)
    {
        header.AsSpan(12, 32).Clear();
        var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
        SHA256.HashData(header.AsSpan(0, size)).CopyTo(header, 12);
    }
}
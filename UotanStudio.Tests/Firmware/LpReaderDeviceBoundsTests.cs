using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class LpReaderDeviceBoundsTests
{
    private static LpMetadataGeometry Geometry => new() { MetadataMaxSize = 65536, MetadataSlotCount = 2, LogicalBlockSize = 4096 };

    [Fact]
    public void ReaderRejectsMissingSuperDevice()
    {
        using var stream = new MemoryStream(CreateMetadata(null));
        var error = Assert.Throws<InvalidDataException>(() => MetadataReader.ParseMetadata(Geometry, stream));
        Assert.Contains("super device", error.Message);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(8UL)]
    [InlineData(535UL)]
    [InlineData(ulong.MaxValue)]
    public void ReaderRejectsSuperDataAreaOutsideUsableRegion(ulong firstSector)
    {
        using var stream = new MemoryStream(CreateMetadata(firstSector));
        var error = Assert.Throws<InvalidDataException>(() => MetadataReader.ParseMetadata(Geometry, stream));
        Assert.Contains("data area", error.Message);
    }

    [Fact]
    public void ReaderAcceptsExactBoundaryAndSecondaryDeviceStartingAtZero()
    {
        using var stream = new MemoryStream(CreateMetadata(536, secondaryDevice: true));
        var metadata = MetadataReader.ParseMetadata(Geometry, stream);
        Assert.Equal(536UL, metadata.BlockDevices[0].FirstLogicalSector);
        Assert.Equal(0UL, metadata.BlockDevices[1].FirstLogicalSector);
    }

    [Theory]
    [InlineData(0, 4u, false)]
    [InlineData(0, 8u, false)]
    [InlineData(0, 3u, true)]
    [InlineData(1, 4u, true)]
    [InlineData(1, 8u, true)]
    [InlineData(1, 16u, false)]
    [InlineData(2, 15u, true)]
    [InlineData(2, 0x80000000u, false)]
    public void ReaderChecksPartitionAttributeVersion(ushort minor, uint attributes, bool accepted)
    {
        using var stream = new MemoryStream(CreateMetadata(536, attributes: attributes, minor: minor));
        if (accepted)
            Assert.Equal(attributes, Assert.Single(MetadataReader.ParseMetadata(Geometry, stream).Partitions).Attributes);
        else
        {
            var error = Assert.Throws<InvalidDataException>(() => MetadataReader.ParseMetadata(Geometry, stream));
            Assert.Contains("attributes", error.Message);
        }
    }

    // Independent wire fixtures retain valid SHA256 checksums for rejected semantics.
    private static byte[] CreateMetadata(ulong? firstSector, bool secondaryDevice = false, uint? attributes = null, ushort minor = 0)
    {
        var headerSize = minor >= 2 ? 256 : 128;
        var partitionBytes = attributes.HasValue ? 52 : 0;
        var groupBytes = attributes.HasValue ? 48 : 0;
        var deviceCount = firstSector.HasValue ? (secondaryDevice ? 2 : 1) : 0;
        var tables = new byte[partitionBytes + groupBytes + deviceCount * 64];
        if (attributes.HasValue)
        {
            "system"u8.CopyTo(tables);
            BinaryPrimitives.WriteUInt32LittleEndian(tables.AsSpan(36), attributes.Value);
            "default"u8.CopyTo(tables.AsSpan(partitionBytes));
        }
        for (var index = 0; index < deviceCount; index++)
        {
            var device = tables.AsSpan(partitionBytes + groupBytes + index * 64, 64);
            BinaryPrimitives.WriteUInt64LittleEndian(device, index == 0 ? firstSector!.Value : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(device[8..], 4096);
            BinaryPrimitives.WriteUInt64LittleEndian(device[16..], 1048576);
            (index == 0 ? "super"u8 : "extra"u8).CopyTo(device[24..]);
        }
        var header = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x414C5030);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), minor);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), (uint)tables.Length);
        SHA256.HashData(tables).CopyTo(header, 48);
        Descriptor(header.AsSpan(80), 0, attributes.HasValue ? 1u : 0u, 52);
        Descriptor(header.AsSpan(92), (uint)partitionBytes, 0, 24);
        Descriptor(header.AsSpan(104), (uint)partitionBytes, attributes.HasValue ? 1u : 0u, 48);
        Descriptor(header.AsSpan(116), (uint)(partitionBytes + groupBytes), (uint)deviceCount, 64);
        SHA256.HashData(header).CopyTo(header, 12);
        return header.Concat(tables).ToArray();
    }

    private static void Descriptor(Span<byte> descriptor, uint offset, uint count, uint size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], size);
    }
}
using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage.Lp;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class LpWriterConformanceTests
{
    [Theory]
    [InlineData(0u, 0)]
    [InlineData(3u, 0)]
    [InlineData(4u, 1)]
    [InlineData(8u, 1)]
    [InlineData(15u, 1)]
    public void WriterSelectsVersionRequiredByPartitionAttributes(uint attributes, ushort expectedMinor)
    {
        var builder = MetadataBuilder.New(1024 * 1024, 65536, 2);
        builder.AddPartition("system", "default", 4096, attributes);
        var metadata = builder.Build();
        var bytes = MetadataWriter.SerializeMetadata(metadata);
        Assert.Equal(expectedMinor, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        using var stream = new MemoryStream(bytes);
        var parsed = MetadataReader.ParseMetadata(metadata.Geometry, stream);
        Assert.Equal(attributes, Assert.Single(parsed.Partitions).Attributes);
    }

    [Fact]
    public void WriterRejectsUnknownAttributes()
    {
        var builder = MetadataBuilder.New(1024 * 1024, 65536, 2);
        builder.AddPartition("system", "default", 4096, 16);
        Assert.Throws<InvalidDataException>(() => MetadataWriter.SerializeMetadata(builder.Build()));
    }

    [Fact]
    public void InvalidTablesDoNotTruncateExistingImage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-lp-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "super.img");
            var original = Enumerable.Repeat((byte)0xa7, 1024 * 1024).ToArray();
            File.WriteAllBytes(path, original);
            var builder = MetadataBuilder.New(32768, 512, 2);
            for (var i = 0; i < 10; i++) builder.AddPartition("empty" + i, "default", 0);
            Assert.Throws<InvalidDataException>(() => MetadataWriter.WriteToImageFile(path, builder.Build()));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MetadataUpdatePreservesPartitionPayload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-lp-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "super.img");
            var builder = MetadataBuilder.New(1024 * 1024, 65536, 2);
            builder.AddPartition("system", "default", 4096);
            var metadata = builder.Build();
            MetadataWriter.WriteToImageFile(path, metadata);
            var position = (long)metadata.Extents[0].TargetData * 512;
            using (var image = File.OpenWrite(path))
            {
                image.Position = position;
                image.Write("partition-content"u8);
            }
            metadata.Header = new LpMetadataHeader { Flags = 1 };
            MetadataWriter.WriteToImageFile(path, metadata);
            Assert.Equal((ushort)2, MetadataReader.ReadFromImageFile(path).Header.MinorVersion);
            using var result = File.OpenRead(path);
            result.Position = position;
            var actual = new byte[17];
            result.ReadExactly(actual);
            Assert.Equal("partition-content"u8.ToArray(), actual);
            Assert.Equal(1024 * 1024, result.Length);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
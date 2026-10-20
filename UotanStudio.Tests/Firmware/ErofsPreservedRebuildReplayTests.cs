using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsPreservedRebuildReplayTests
{
    [Fact]
    public async Task PreservedRebuild_OfACompressedSource_KeepsTheCompressedStorageStrategy()
    {
        using var temp = new TempDirectory();
        var payload = new byte[40 * 1024];
        Array.Fill(payload, (byte)0x5A);
        temp.WriteBytes("heavy.bin", payload);

        var imagePath = Path.Combine(temp.Path, "source.img");
        var source = new ErofsBuilder().Build(temp.Path, SourceOptions());
        await File.WriteAllBytesAsync(imagePath, source, TestContext.Current.CancellationToken);
        var sourceLayout = LayoutOf(source);

        var extracted = Path.Combine(temp.Path, "extracted");
        var rebuiltPath = Path.Combine(temp.Path, "rebuilt.img");
        var handler = new ErofsFormatHandler();
        var unpacked = await handler.UnpackAsync(imagePath, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);
        var packed = await handler.PackAsync(extracted, rebuiltPath, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);

        var rebuilt = await File.ReadAllBytesAsync(rebuiltPath, TestContext.Current.CancellationToken);
        var rebuiltLayout = LayoutOf(rebuilt);

        Assert.Equal(3, sourceLayout.DataLayout);
        Assert.Equal(1u, sourceLayout.Blocks);
        Assert.Equal(3, rebuiltLayout.DataLayout);
        Assert.True(rebuilt.Length <= source.Length,
            $"the preserved rebuild of a compressed source grew from {source.Length} to {rebuilt.Length} bytes " +
            $"(source layout {sourceLayout.DataLayout}/{sourceLayout.Blocks}, rebuilt {rebuiltLayout.DataLayout}/{rebuiltLayout.Blocks}).");
    }

    [Fact]
    public async Task PreservedRebuild_OfLegacyLayoutSource_KeepsTheCompressedStorageStrategy()
    {
        using var temp = new TempDirectory();
        var payload = new byte[40 * 1024];
        Array.Fill(payload, (byte)0x5A);
        temp.WriteBytes("heavy.bin", payload);

        var imagePath = Path.Combine(temp.Path, "source.img");
        var options = SourceOptions();
        options.LegacyCompatibility = true;
        var source = new ErofsBuilder().Build(temp.Path, options);
        await File.WriteAllBytesAsync(imagePath, source, TestContext.Current.CancellationToken);
        var sourceLayout = LayoutOf(source);

        var extracted = Path.Combine(temp.Path, "extracted");
        var rebuiltPath = Path.Combine(temp.Path, "rebuilt.img");
        var handler = new ErofsFormatHandler();
        var unpacked = await handler.UnpackAsync(imagePath, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);
        var packed = await handler.PackAsync(extracted, rebuiltPath, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);

        var rebuilt = await File.ReadAllBytesAsync(rebuiltPath, TestContext.Current.CancellationToken);
        var rebuiltLayout = LayoutOf(rebuilt);

        Assert.NotEqual(0, sourceLayout.DataLayout);
        Assert.Equal(sourceLayout, rebuiltLayout);
        Assert.Equal(source.Length, rebuilt.Length);
    }

    [Fact]
    public void PreservedRebuild_IncompressibleContent_KeepsTheRecordedCompressedStrategy()
    {
        using var temp = new TempDirectory();
        var payload = new byte[40 * 1024];
        new Random(20260815).NextBytes(payload);
        temp.WriteBytes("heavy.bin", payload);

        var options = SourceOptions();
        options.UseCompactedIndexes = true;
        var record = new ErofsInode
        {
            Layer = ErofsInodeLayer.Source,
            DataLayout = ErofsDataLayout.CompactIndexes,
            Idata = new ErofsCompressedIdata { CompactedIndexes = true, LogicalClusterSize = 4096 },
            SourcePath = "heavy.bin",
            SourceCompressed = true,
            SourceAlgorithm = ErofsCompressionAlgorithm.Lz4,
        };
        options.SourceInodes = new Dictionary<ulong, ErofsInode> { [ulong.MaxValue] = record };
        options.SourceInodesByPath = new Dictionary<string, ErofsInode>(StringComparer.Ordinal)
        {
            ["heavy.bin"] = record,
        };

        var image = new ErofsBuilder().Build(temp.Path, options);

        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single(file => file.Path == "heavy.bin");
        Assert.Equal(payload, parser.ReadFileBytes("heavy.bin"));
        var offset = checked((int)((long)parser.Superblock.MetaBlkAddr * parser.BlockSize + (long)entry.Nid * 32));
        var advise = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset));
        var layout = (advise >> 1) & 7;
        Assert.True(layout is 1 or 3,
            $"the rebuild must keep the recorded compressed strategy, but the layout is {layout}.");
    }

    [Fact]
    public void PreservedRebuild_WholeFileShrinkWithoutSourceRecord_StillDropsCompression()
    {
        using var temp = new TempDirectory();
        var payload = new byte[40 * 1024];
        new Random(20260816).NextBytes(payload);
        temp.WriteBytes("heavy.bin", payload);

        var options = SourceOptions();
        Assert.Null(options.SourceInodes);

        var image = new ErofsBuilder().Build(temp.Path, options);

        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single(file => file.Path == "heavy.bin");
        var offset = checked((int)((long)parser.Superblock.MetaBlkAddr * parser.BlockSize + (long)entry.Nid * 32));
        var advise = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset));
        var layout = (advise >> 1) & 7;
        Assert.Equal(0, layout);
    }

    [Fact]
    public void PreservedRebuild_WithoutSourceRecords_StillBuilds()
    {
        using var temp = new TempDirectory();
        var payload = new byte[40 * 1024];
        Array.Fill(payload, (byte)0x5A);
        temp.WriteBytes("heavy.bin", payload);

        var options = SourceOptions();
        Assert.Null(options.SourceInodes);

        var image = new ErofsBuilder().Build(temp.Path, options);

        using var parser = new ErofsParser(image);
        Assert.Contains(parser.EnumerateFiles(), file => file.Path == "heavy.bin");
    }

    private static ErofsOptions SourceOptions() => new()
    {
        Compress = true,
        Algorithm = ErofsCompressionAlgorithm.Lz4,
        MaxPclusterSize = 4096,
        VolumeName = "vendor",
        FsConfig = new Dictionary<string, FsConfigEntry>(StringComparer.Ordinal)
        {
            ["."] = new FsConfigEntry(0, 0, 0x41ED),
            ["heavy.bin"] = new FsConfigEntry(0, 0, 0x89ED),
        },
    };

    private static (int DataLayout, uint Blocks) LayoutOf(byte[] image)
    {
        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single(file => file.Path == "heavy.bin");
        var offset = checked((int)((long)parser.Superblock.MetaBlkAddr * parser.BlockSize + (long)entry.Nid * 32));
        var advise = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset));
        return ((advise >> 1) & 7,
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 16)));
    }
}
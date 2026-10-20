using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsPreservedRebuildDensityTests
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
        Assert.Equal(3, rebuiltLayout.DataLayout);
        Assert.True(rebuilt.Length <= source.Length,
            $"the preserved rebuild of a compressed source grew from {source.Length} to {rebuilt.Length} bytes " +
            $"(source layout {sourceLayout.DataLayout}/{sourceLayout.Blocks}, rebuilt {rebuiltLayout.DataLayout}/{rebuiltLayout.Blocks}).");
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

        var extracted = Path.Combine(temp.Path, "extracted");
        var rebuiltPath = Path.Combine(temp.Path, "rebuilt.img");
        var handler = new ErofsFormatHandler();
        var unpacked = await handler.UnpackAsync(imagePath, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpacked.Success, unpacked.Summary);
        var packed = await handler.PackAsync(extracted, rebuiltPath, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);

        var rebuilt = await File.ReadAllBytesAsync(rebuiltPath, TestContext.Current.CancellationToken);
        Assert.Equal(LayoutOf(source), LayoutOf(rebuilt));
        Assert.Equal(source.Length, rebuilt.Length);
    }

    private static (int DataLayout, uint Blocks) LayoutOf(byte[] image)
    {
        using var parser = new ErofsParser(image);
        var entry = parser.EnumerateFiles().Single(file => file.Path == "heavy.bin");
        var offset = checked((int)((long)parser.Superblock.MetaBlkAddr * parser.BlockSize + (long)entry.Nid * 32));
        var advise = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset));
        return ((advise >> 1) & 7,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 16)));
    }
}
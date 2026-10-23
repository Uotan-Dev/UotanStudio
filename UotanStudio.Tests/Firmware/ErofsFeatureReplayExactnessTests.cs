using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsFeatureReplayExactnessTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void PreservedRebuild_CompressedTailSource_ReplaysTheZtailpackingWord()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 20 * 1024).ToArray());
        var image = Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            CompressedTailPacking = true,
        });

        var (rebuilt, restored) = Replay(image, source.Path);
        var original = ErofsSuperblock.FromImage(image);
        var replay = ErofsSuperblock.FromImage(rebuilt);

        Assert.Equal(original.FeatureCompat, replay.FeatureCompat);
        Assert.Equal(original.FeatureIncompat, replay.FeatureIncompat);
        Assert.Equal(original.AvailableComprAlgs, replay.AvailableComprAlgs);
        Assert.Equal(0u, restored.DroppedIncompatBits);
        Assert.Equal(0u, restored.DroppedCompatBits);
    }

    [Fact]
    public void PreservedRebuild_DedupeOnlySource_ReplaysTheSharedFragmentBit()
    {
        using var source = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x41, 32 * 1024).ToArray();
        source.WriteBytes("a.bin", payload);
        source.WriteBytes("b.bin", payload);
        var image = Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            Dedup = true,
        });

        var (rebuilt, restored) = Replay(image, source.Path);
        var original = ErofsSuperblock.FromImage(image);
        var replay = ErofsSuperblock.FromImage(rebuilt);

        Assert.Equal(original.FeatureCompat, replay.FeatureCompat);
        Assert.Equal(original.FeatureIncompat, replay.FeatureIncompat);
        Assert.Equal(original.AvailableComprAlgs, replay.AvailableComprAlgs);
        Assert.Equal(0u, restored.DroppedIncompatBits);
        Assert.Equal(0u, restored.DroppedCompatBits);
    }

    private static byte[] Build(string source, ErofsOptions options)
        => new ErofsBuilder().Build(source, options);

    private static (byte[] Image, ErofsOptions Restored) Replay(byte[] image, string root)
    {
        using var parser = new ErofsParser(image);
        var options = parser.ReadRebuildOptions();
        var extracted = Path.Combine(root, "extracted");
        Directory.CreateDirectory(extracted);
        parser.ExtractToDirectory(extracted);
        return (new ErofsBuilder().Build(extracted, options), options);
    }
}
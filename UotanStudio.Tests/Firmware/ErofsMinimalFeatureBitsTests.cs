using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsMinimalFeatureBitsTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void Build_DefaultLz4_DerivesOnlyZeroPaddingWithoutConfigurationRecord()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 32 * 1024).ToArray());
        var options = new ErofsOptions { BlockSize = BlockSize, Algorithm = ErofsCompressionAlgorithm.Lz4 };

        var image = new ErofsBuilder().Build(source.Path, options);
        var superblock = ErofsSuperblock.FromImage(image);

        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, superblock.FeatureIncompat);
        Assert.Equal(ErofsCompression.Lz4DistanceMax, superblock.AvailableComprAlgs);
        Assert.Equal(BlockSize, options.MaxPclusterSize);
        Assert.False(options.UseCompactedIndexes);
        var configurationOffset = 1024 + 128 + superblock.SbExtSlots * 16;
        Assert.False(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(configurationOffset)) is 14 or 6);
    }

    [Fact]
    public void Build_NonLz4_DerivesConfigurationBitWithoutPaddingBit()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 32 * 1024).ToArray());

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Zstd,
            CompressionLevel = 3,
        });
        var superblock = ErofsSuperblock.FromImage(image);

        Assert.Equal(0u, superblock.FeatureIncompat & ErofsSuperblock.IncompatLz4_0Padding);
        Assert.NotEqual(0u, superblock.FeatureIncompat & ErofsSuperblock.IncompatCompressCfgs);
        Assert.Equal(1 << (int)ErofsCompressionAlgorithm.Zstd, superblock.AvailableComprAlgs);
    }

    [Fact]
    public void Build_MultiBlockPcluster_DerivesBothBits()
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", Enumerable.Repeat((byte)0x5A, 64 * 1024).ToArray());

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 4 * BlockSize,
        });
        var superblock = ErofsSuperblock.FromImage(image);

        Assert.Equal(
            ErofsSuperblock.IncompatLz4_0Padding | ErofsSuperblock.IncompatBigPcluster,
            superblock.FeatureIncompat);
    }

    [Fact]
    public void Build_OneBlockLz4_PacksFramesDensely()
    {
        using var source = new TempDirectory();
        var payload = new byte[512 * 1024];
        for (var offset = 0; offset < payload.Length; offset += 8192)
        {
            Array.Fill(payload, (byte)0x41, offset, 4096);
            Array.Fill(payload, (byte)0x42, offset + 4096, 4096);
        }

        source.WriteBytes("data.bin", payload);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
        });
        using var parser = new ErofsParser(image);

        Assert.True(image.Length < payload.Length / 2,
            $"a one-block LZ4 build must compress this payload; image={image.Length} source={payload.Length}.");
        Assert.True(parser.MaxCompressedPclusterBytes() <= BlockSize);
        Assert.Equal(0, parser.MaxPclusterKernelCoverageDeficit());
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(16384)]
    public void Build_DeflateIncompressiblePayload_SucceedsAndRoundTrips(int length)
    {
        using var source = new TempDirectory();
        var payload = new byte[length];
        new Random(20261023 + length).NextBytes(payload);
        source.WriteBytes("random.bin", payload);

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Algorithm = ErofsCompressionAlgorithm.Deflate,
        });
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("random.bin"));
    }
}
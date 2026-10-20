using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards EROFS big-pcluster output: the modern (post-5.13) layout stores a compressed pcluster whose
/// stream spans several physical blocks, encoded in the index head via the CBLKCNT marker. A single
/// compressed block per pcluster would still mount, so these tests assert a genuine multi-block
/// pcluster is emitted, survives the managed parser, and decodes on a big-pcluster-capable kernel.
/// The builder default caps a pcluster at one filesystem block, the word a bare <c>mkfs.erofs -zlz4</c>
/// publishes, so a multi-block frame is only reachable when the build asks for a larger pcluster or
/// declares the shared incompat bit.
/// <para>守护 EROFS big-pcluster 输出：现代（post-5.13）布局把单个压缩 pcluster 的流跨多个物理块，
/// 通过索引 head 的 CBLKCNT 标记编码。仅每 pcluster 一个压缩块也能挂载，因此本测试断言确实产出了
/// 多块 pcluster、能在托管解析器中存活，并可在支持 big-pcluster 的内核上解码。打包器默认把 pcluster
/// 上限钉死为一个文件系统块，与裸 <c>mkfs.erofs -zlz4</c> 发布的特性字相同，因此只有显式要求更大的
/// pcluster 或声明该共享 incompat 位时才会产出多块帧。</para>
/// </summary>
public sealed class ErofsBigPclusterTests
{
    private const int BlockSize = 4096;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_ModernLz4_EmitsMultiBlockPclusterAndRoundTrips(bool compacted)
    {
        var payload = BigPclusterPayload();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);

        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 1 << 20,
            UseCompactedIndexes = compacted,
        });

        var superblock = ErofsSuperblock.FromImage(image);
        Assert.Equal(0x00000003u, superblock.FeatureIncompat);
        Assert.Equal(1 << (int)ErofsCompressionAlgorithm.Lz4, superblock.AvailableComprAlgs);
        using var parser = new ErofsParser(image);
        var maxBytes = parser.MaxCompressedPclusterBytes();
        Assert.True(maxBytes > BlockSize,
            $"expected a pcluster spanning multiple {BlockSize}-byte blocks (compacted={compacted}); maxBytes={maxBytes} clusterOfs={parser.MaxCompressedClusterOfs()} len={image.Length}.");
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_DeclaredBigPclusterFeature_WritesMultiBlockPclusterAndSharedIncompatBit()
    {
        var payload = BigPclusterPayload();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = 1 << 20,
            Features = ErofsFeatureSet.FromWords(ErofsSuperblock.CompatSbChecksum, ErofsSuperblock.IncompatLz4_0Padding)
                .With(ErofsFeature.CompressionConfigs),
        };

        var image = new ErofsBuilder().Build(source.Path, options);
        var superblock = ErofsSuperblock.FromImage(image);

        Assert.Equal(0x00000003u, superblock.FeatureIncompat);
        Assert.True(options.FeaturesAuthoritative);
        Assert.Equal(0u, options.AddedIncompatBits);
        Assert.True(options.Features.Contains(ErofsFeature.BigPcluster));
        using var parser = new ErofsParser(image);
        Assert.True(parser.MaxCompressedPclusterBytes() > BlockSize,
            $"the declared feature must still license a multi-block pcluster; maxBytes={parser.MaxCompressedPclusterBytes()} len={image.Length}.");
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void Build_UndeclaredBigPclusterWithPinnedPcluster_KeepsEveryFrameInOneBlock()
    {
        var payload = BigPclusterPayload();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            MaxPclusterSize = BlockSize,
            UseCompactedIndexes = false,
            Features = ErofsFeatureSet.FromWords(ErofsSuperblock.CompatSbChecksum, ErofsSuperblock.IncompatLz4_0Padding)
                .With(ErofsFeature.Lz4ZeroPadding),
        };

        var image = new ErofsBuilder().Build(source.Path, options);
        var superblock = ErofsSuperblock.FromImage(image);

        Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding, superblock.FeatureIncompat);
        Assert.Equal(0u, superblock.FeatureIncompat & ErofsSuperblock.IncompatBigPcluster);
        Assert.Equal(ErofsCompression.Lz4DistanceMax, superblock.AvailableComprAlgs);
        using var parser = new ErofsParser(image);
        Assert.True(parser.MaxCompressedPclusterBytes() <= BlockSize,
            $"an undeclared big_pcluster must keep every frame inside one block; maxBytes={parser.MaxCompressedPclusterBytes()}.");
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    private static byte[] BigPclusterPayload()
    {
        var tile = new byte[32 * 1024];
        new Random(20260919).NextBytes(tile);
        var bytes = new byte[512 * 1024];
        for (var offset = 0; offset < bytes.Length; offset += tile.Length)
        {
            tile.CopyTo(bytes.AsSpan(offset, tile.Length));
        }

        return bytes;
    }
}
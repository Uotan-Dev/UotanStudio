using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards EROFS big-pcluster output: the modern (post-5.13) layout stores a compressed pcluster whose
/// stream spans several physical blocks, encoded in the index head via the CBLKCNT marker. A single
/// compressed block per pcluster would still mount, so these tests assert a genuine multi-block
/// pcluster is emitted, survives the managed parser, and decodes on a big-pcluster-capable kernel.
/// <para>守护 EROFS big-pcluster 输出：现代（post-5.13）布局把单个压缩 pcluster 的流跨多个物理块，
/// 通过索引 head 的 CBLKCNT 标记编码。仅每 pcluster 一个压缩块也能挂载，因此本测试断言确实产出了
/// 多块 pcluster、能在托管解析器中存活，并可在支持 big-pcluster 的内核上解码。</para>
/// </summary>
public sealed class ErofsBigPclusterTests
{
    // 512 KiB built by tiling a random 32 KiB block sixteen times. The repeat distance (32 KiB) is
    // within LZ4's 64 KiB match window, so each 256 KiB logical pcluster compresses to ~32 KiB plus
    // match tokens — far above one 4 KiB block and below the 1 MiB cap: a genuine multi-block big
    // pcluster. (A 128 KiB tile would exceed the match window and store as Plain.)
    // 512 KiB，由 32 KiB 随机块平铺十六次得到。重复间距（32 KiB）落在 LZ4 的 64 KiB 匹配窗口内，
    // 因此每个 256 KiB 逻辑 pcluster 压缩到约 32 KiB 加匹配标记——远高于单个 4 KiB 块、且低于 1 MiB
    // 上限：真正的多块 big pcluster。（128 KiB 的 tile 会超出匹配窗口而按 Plain 存储。）
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
            UseCompactedIndexes = compacted,
        });

        using var parser = new ErofsParser(image);
        var maxBytes = parser.MaxCompressedPclusterBytes();
        Assert.True(maxBytes > BlockSize,
            $"expected a pcluster spanning multiple {BlockSize}-byte blocks (compacted={compacted}); maxBytes={maxBytes} clusterOfs={parser.MaxCompressedClusterOfs()} len={image.Length}.");
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_ModernLz4_BigPclusterMountsReadOnlyOnKernel(bool compacted)
    {
        var capability = KernelMountVerifier.Probe("erofs", "CONFIG_EROFS_FS_ZIP");
        if (!capability.Available) Assert.Skip(capability.Detail);

        using var source = new TempDirectory();
        source.WriteBytes("data.bin", BigPclusterPayload());
        using var output = new TempDirectory();
        var image = Path.Combine(output.Path, "bigpcl.img");
        new ErofsBuilder().Build(source.Path, image, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            UseCompactedIndexes = compacted,
        });

        var result = KernelMountVerifier.Verify(image, source.Path, "erofs");
        Assert.True(result.Ok, $"compacted={compacted}: {result.Output}");
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
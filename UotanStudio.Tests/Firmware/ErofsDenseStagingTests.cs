using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the byte-granular (dense) EROFS planner's staging hand-off: a pcluster accepted by the binary
/// search must reach the image as the very frame the planner encoded, beginning at the block address the
/// index points to, a plain fallback cluster must stay unstaged so the image carries the source bytes
/// verbatim, and the whole build must repeat byte-for-byte once the superblock timestamp and UUID are
/// pinned. All branches are pinned by exact (SourceOffset, SourceLength, CompressedLength) triples read
/// back from the image index, not by a parse that merely succeeds.
/// <para>守护按字节粒度（dense）规划器与暂存文件的交接：二分查找接受的 pcluster 必须以规划器编码出的
/// 同一帧写入镜像，并且从索引指向的块地址开始；原样回退簇不得进入暂存，镜像里原样存放源字节；固定超级块
/// 时间戳与 UUID 后，整个构建必须逐字节可复现。各分支都按镜像索引反推出的精确三元组（源偏移、源长度、
/// 压缩长度）锁定，而不满足于镜像能被解析。</para>
/// </summary>
public sealed class ErofsDenseStagingTests
{
    private const byte PlainType = 0;
    private const byte NonHeadType = 2;

    [Fact]
    public void Build_Lz4Optimized_StagedPayload_MatchesRecompressedOracle()
    {
        var payload = TextLikePayload(64 * 1024 + 1234);
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var options = DenseLz4Options();
        var image = new ErofsBuilder().Build(source.Path, options);
        using var parser = new ErofsParser(image);
        Assert.True(parser.MaxCompressedClusterOfs() > 0,
            "the fixture produced no byte-granular dense pcluster, so the staged hand-off went untested.");
        Assert.Empty(parser.PclusterKernelCoverageDeficits());
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));

        var (heads, plainHeads) = VerifyPlannedPclusters(image, payload, options, parser, "data.bin");
        Assert.True(heads > 2, $"expected several dense pclusters, found {heads}.");
        Assert.True(plainHeads < heads,
            $"every one of the {heads} planned clusters took the plain branch, so no staged frame was pinned.");
    }

    [Fact]
    public void Build_SameDirectoryTwice_WithFixedUuidAndTimestamp_ProducesIdenticalBytes()
    {
        var text = TextLikePayload(48 * 1024 + 2049);
        var noise = RandomBytes(24 * 1024 + 777);
        using var source = new TempDirectory();
        source.WriteBytes("text.bin", text);
        source.WriteBytes("noise.bin", noise);
        var options = DenseLz4Options();
        options.BuildTime = 1_735_689_600UL;
        options.Uuid = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE];

        var first = new ErofsBuilder().Build(source.Path, options);
        using (var parser = new ErofsParser(first))
        {
            Assert.True(parser.MaxCompressedClusterOfs() > 0,
                "the fixture produced no dense pcluster, so the staging hand-off stayed outside the determinism gate.");
        }

        var second = new ErofsBuilder().Build(source.Path, options);
        Assert.Equal(first.Length, second.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Build_Lz4Optimized_PlainClustersKeepNegativeStagedOffset()
    {
        var payload = PlainFallbackPayload();
        using var source = new TempDirectory();
        source.WriteBytes("mixed.bin", payload);
        var options = DenseLz4Options();
        var image = new ErofsBuilder().Build(source.Path, options);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("mixed.bin"));
        Assert.Empty(parser.PclusterKernelCoverageDeficits());

        var (heads, plainHeads) = VerifyPlannedPclusters(image, payload, options, parser, "mixed.bin");
        Assert.True(plainHeads > 0,
            "the fixture produced no plain fallback cluster, so the unstaged branch went untested.");
        Assert.True(plainHeads < heads,
            $"the fixture produced only plain clusters out of {heads}, so the staged branch of the same plan went untested.");
    }

    private static (int Heads, int PlainHeads) VerifyPlannedPclusters(
        byte[] image, byte[] payload, ErofsOptions options, ErofsParser parser, string path)
    {
        var blockSize = parser.BlockSize;
        var heads = DenseHeads(parser, path);
        var oracle = new byte[ErofsCompression.MaximumOutputSize(payload.Length)];
        var plainHeads = 0;
        for (var i = 0; i < heads.Count; i++)
        {
            var head = heads[i];
            var (sourceOffset, sourceEnd) = SourceSpan(heads, i, payload.LongLength, blockSize);
            var length = checked((int)(sourceEnd - sourceOffset));
            Assert.True(length > 0, $"the pcluster head at logical cluster {head.Lcn} covers no source bytes.");
            var start = checked(head.Pblk * blockSize);
            Assert.True(start + blockSize <= image.Length,
                $"the pcluster head at block {head.Pblk} points past the image bounds.");
            if (head.Type == PlainType)
            {
                plainHeads++;
                Assert.Equal(payload.AsSpan((int)sourceOffset, length), image.AsSpan((int)start, length));
                AssertZeroFilled(image, start + length, blockSize - length);
                continue;
            }

            var frameLength = ErofsCompression.AlgorithmCompress(
                options, payload, (int)sourceOffset, length, oracle, 0, oracle.Length);
            Assert.True(frameLength > 0,
                $"the oracle refused the {length}-byte pcluster at source offset {sourceOffset}.");
            var blockBytes = ((frameLength + blockSize - 1) / blockSize) * blockSize;
            Assert.True(start + blockBytes <= image.Length,
                $"the {frameLength}-byte oracle frame does not fit the blocks starting at {start}.");
            AssertZeroFilled(image, start, blockBytes - frameLength);
            Assert.Equal(oracle.AsSpan(0, frameLength),
                image.AsSpan((int)start + blockBytes - frameLength, frameLength));
            if (i + 1 < heads.Count)
            {
                Assert.Equal(head.Pblk + blockBytes / blockSize, heads[i + 1].Pblk);
            }
        }

        return (heads.Count, plainHeads);
    }

    private static List<(long Lcn, byte Type, int ClusterOfs, long Pblk, uint Delta0, uint Delta1)> DenseHeads(
        ErofsParser parser, string path)
        => [.. parser.DumpIndexClusters(path).Where(row => row.Type != NonHeadType && row.Pblk > 0)];

    private static (long SourceOffset, long SourceEnd) SourceSpan(
        List<(long Lcn, byte Type, int ClusterOfs, long Pblk, uint Delta0, uint Delta1)> heads,
        int index, long fileSize, int clusterSize)
    {
        var head = heads[index];
        var sourceOffset = head.Lcn * clusterSize + head.ClusterOfs;
        var sourceEnd = index + 1 < heads.Count
            ? heads[index + 1].Lcn * clusterSize + heads[index + 1].ClusterOfs
            : fileSize;
        return (sourceOffset, sourceEnd);
    }

    private static ErofsOptions DenseLz4Options() => new()
    {
        Algorithm = ErofsCompressionAlgorithm.Lz4,
        LegacyCompatibility = true,
        OptimizeCompression = true,
    };

    private static void AssertZeroFilled(byte[] image, long offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Assert.True(image.AsSpan((int)offset, count).IndexOfAnyExcept(byte.MinValue) < 0,
            $"expected {count} zero padding bytes at image offset {offset}.");
    }

    private static byte[] TextLikePayload(int length)
    {
        string[] words = ["erofs", "pcluster", "clusterofs", "lcluster", "staging", "kernel", "decoder", "superblock"];
        var random = new Random(20261008);
        var builder = new StringBuilder(length + 32);
        while (builder.Length < length)
        {
            builder.Append(words[random.Next(words.Length)]).Append(random.Next(1000)).Append(' ');
        }

        return Encoding.ASCII.GetBytes(builder.ToString(0, length));
    }

    private static byte[] PlainFallbackPayload()
    {
        var random = new Random(20261008);
        var bytes = new byte[16384 + 8192 + 2377];
        random.NextBytes(bytes.AsSpan(0, 16384));
        Array.Fill(bytes, (byte)0x41, 16384, 8192);
        random.NextBytes(bytes.AsSpan(24576, 2377));
        return bytes;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(20261008).NextBytes(bytes);
        return bytes;
    }
}
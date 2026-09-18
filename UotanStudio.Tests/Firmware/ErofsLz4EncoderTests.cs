using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks the managed LZ4 block encoder selection used for EROFS pclusters.
/// <para>检查 EROFS pcluster 所使用的托管 LZ4 块编码器选择。</para></summary>
public sealed class ErofsLz4EncoderTests
{
    [Fact]
    public void AlgorithmCompress_Lz4hc_EncodesSmallerBlocksThanFastLz4()
    {
        var payload = TextLikePayload();
        var target = new byte[ErofsCompression.MaximumOutputSize(payload.Length)];
        var fast = ErofsCompression.AlgorithmCompress(ErofsAlgorithm.Lz4,
            payload, 0, payload.Length, target, 0, target.Length, 9, highCompression: false, 0, 0);
        Assert.True(fast > 0);
        Assert.Equal(payload, Decode(target, fast, payload.Length));

        var highCompression = ErofsCompression.AlgorithmCompress(ErofsAlgorithm.Lz4,
            payload, 0, payload.Length, target, 0, target.Length, 9, highCompression: true, 0, 0);
        Assert.Equal(payload, Decode(target, highCompression, payload.Length));
        Assert.True(highCompression < fast,
            $"LZ4HC encoded {highCompression} bytes, which is not smaller than the fast LZ4 {fast}.");
    }

    [Fact]
    public void AlgorithmCompress_FastLz4_HigherLevel_EncodesDenserBlocks()
    {
        // The fast matcher takes "acceleration" as its skip distance, so the panel level is mapped to
        // 10 - level: level 9 keeps the library default and searches the most, level 1 searches the
        // least. Both stay valid LZ4 streams; only density and speed differ. The payload interleaves
        // long random literal runs with far-distance repeats because the acceleration skip only
        // engages after several consecutive failed match attempts.
        // 快速匹配器把 acceleration 当作跳过距离，因此面板等级按 10 - level 映射：等级 9 保持库默认、
        // 搜索最充分，等级 1 搜索最少。两者都是合法 LZ4 流，仅密度与速度不同。语料用长随机字面量
        // 夹远距离重复块，因为加速度跳步只在连续多次匹配失败后才生效。
        var payload = SparseMatchPayload();
        var target = new byte[ErofsCompression.MaximumOutputSize(payload.Length)];
        var levelOne = ErofsCompression.AlgorithmCompress(ErofsAlgorithm.Lz4,
            payload, 0, payload.Length, target, 0, target.Length, 1, highCompression: false, 0, 0);
        var levelNine = ErofsCompression.AlgorithmCompress(ErofsAlgorithm.Lz4,
            payload, 0, payload.Length, target, 0, target.Length, 9, highCompression: false, 0, 0);
        Assert.NotEqual(levelOne, levelNine);
        Assert.True(levelNine < levelOne, $"Expected denser output at level 9 ({levelNine}) than level 1 ({levelOne}).");
    }

    [Fact]
    public void AlgorithmCompress_Lz4hc_RepeatedCalls_ProduceIdenticalBlocks()
    {
        // The builder compresses once to plan the pclusters and compresses again while writing them,
        // then fails the build on any length difference, so the encoder must not carry state between
        // calls even though its LZ4HC search allocates a large working set per cluster.
        // 构建器先压缩一次以规划 pcluster，写入时再压缩一次，并在长度不一致时让构建失败，因此编码器
        // 不得跨调用保留状态——尽管其 LZ4HC 搜索会为每个簇分配较大的工作集。
        var payload = TextLikePayload();
        var first = new byte[ErofsCompression.MaximumOutputSize(payload.Length)];
        var second = new byte[first.Length];
        var firstLength = ErofsCompression.AlgorithmCompress(ErofsAlgorithm.Lz4,
            payload, 0, payload.Length, first, 0, first.Length, 9, highCompression: true, 0, 0);
        var secondLength = ErofsCompression.AlgorithmCompress(ErofsAlgorithm.Lz4,
            payload, 0, payload.Length, second, 0, second.Length, 9, highCompression: true, 0, 0);
        Assert.Equal(firstLength, secondLength);
        Assert.Equal(first.AsSpan(0, firstLength), second.AsSpan(0, secondLength));
    }

    [Fact]
    public void Build_LegacyDense_Lz4hc_KeepsKernelClusterCoverage()
    {
        const byte plain = 0, nonHead = 2;
        var compressible = TextLikePayload();
        var random = RandomBytes(9 * 4096 + 777);
        using var source = new TempDirectory();
        source.WriteBytes("text.bin", compressible);
        source.WriteBytes("random.bin", random);
        var image = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            OptimizeCompression = true,
            Lz4HighCompression = true,
            CompressionLevel = 9,
        });
        using var parser = new ErofsParser(image);
        // HC changes only the byte lengths, so the pre-5.13 invariants must survive it unchanged:
        // every extent decodes to the output its index demands, and a mid-cluster file tail stays
        // raw instead of sharing a head page with the preceding pcluster.
        // HC 只改变字节长度，因此 pre-5.13 的不变量必须原样成立：每个 extent 的解压输出不少于索引
        // 要求的量，且起始于簇内部的文件尾部保持原始数据，而不是与前一个 pcluster 共享 head 页。
        Assert.Equal(0, parser.MaxPclusterKernelCoverageDeficit());
        var rows = parser.DumpIndexClusters("text.bin");
        var tail = rows[..^1].Last(row => row.Type != nonHead);
        Assert.True(tail.Type == plain || tail.ClusterOfs == 0,
            $"text.bin stores its final extent as compressed at clusterofs {tail.ClusterOfs}.");
        Assert.Equal(compressible, parser.ReadFileBytes("text.bin"));
        Assert.Equal(random, parser.ReadFileBytes("random.bin"));
    }

    [Fact]
    public async Task Pack_PackingOverridesLz4hc_PacksSmallerImageThanFastLz4()
    {
        var token = TestContext.Current.CancellationToken;
        var compressible = TextLikePayload();
        using var source = new TempDirectory();
        source.WriteBytes("text.bin", compressible);
        using var workspace = new TempDirectory();
        var imagePath = Path.Combine(workspace.Path, "input.img");
        new ErofsBuilder().Build(source.Path, imagePath, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
        });
        var extracted = Path.Combine(workspace.Path, "extracted");
        var handler = new ErofsFormatHandler();
        Assert.True((await handler.UnpackAsync(imagePath, extracted, cancellationToken: token)).Success);

        var fastPath = await PackWithOverridesAsync(handler, extracted,
            Path.Combine(workspace.Path, "fast.img"), highCompression: false, token);
        var hcPath = await PackWithOverridesAsync(handler, extracted,
            Path.Combine(workspace.Path, "hc.img"), highCompression: true, token);
        var fast = new FileInfo(fastPath).Length;
        var highCompression = new FileInfo(hcPath).Length;
        Assert.True(highCompression < fast,
            $"the lz4hc override produced {highCompression} bytes, not fewer than lz4 at {fast}.");
        using var parser = new ErofsParser(hcPath);
        Assert.Equal(compressible, parser.ReadFileBytes("text.bin"));
    }

    private static async Task<string> PackWithOverridesAsync(
        ErofsFormatHandler handler, string directory, string outputPath, bool highCompression, CancellationToken token)
    {
        using var scope = RepackMetadata.UsePackingOverrides(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ErofsAlgorithm"] = highCompression ? "lz4hc" : "lz4",
            ["ErofsCompressionLevel"] = "9",
        });
        var result = await handler.PackAsync(directory, outputPath, token);
        Assert.True(result.Success, result.Summary);
        return outputPath;
    }

    private static byte[] Decode(byte[] source, int length, int expected)
    {
        var decoded = new byte[expected];
        Assert.Equal(expected, ErofsCompression.Decompress(source, 0, length, decoded, 0, decoded.Length));
        return decoded;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(20260918).NextBytes(bytes);
        return bytes;
    }

    private static byte[] TextLikePayload()
    {
        string[] words = ["erofs", "lz4hc", "pcluster", "clusterofs", "lcluster", "kernel", "decoder", "superblock"];
        var random = new Random(20260918);
        var builder = new StringBuilder(200_000);
        while (builder.Length < 200_000)
        {
            builder.Append(words[random.Next(words.Length)]).Append(random.Next(1000)).Append(' ');
        }
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Builds a payload whose matches are rare and short: 64 random bytes followed by a 12-byte
    /// repeated token, over and over. The token's hash entry survives the short random run between
    /// repeats, so acceleration 1 still finds every token while acceleration 9's growing step skips
    /// past token starts, making the levels produce different output sizes.
    /// <para>构造稀疏匹配语料：64 字节随机数据后跟 12 字节重复 token，循环往复。token 的哈希槽
    /// 能在两次重复之间的短随机段中存活，因此加速度 1 仍能命中全部 token，而加速度 9 的递增跳步
    /// 会越过 token 起始位置，使不同等级产出不同大小的输出。</para>
    /// </summary>
    private static byte[] SparseMatchPayload()
    {
        var random = new Random(20260918);
        using var builder = new MemoryStream(200_000);
        var chunk = new byte[64];
        var token = new byte[12];
        random.NextBytes(token);
        while (builder.Length < 200_000)
        {
            random.NextBytes(chunk);
            builder.Write(chunk, 0, chunk.Length);
            builder.Write(token, 0, token.Length);
        }
        return builder.ToArray();
    }
}
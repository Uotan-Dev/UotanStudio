using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Confirms the EROFS writer routes the picker's codec selector to the builder so non-LZ4 on-disk
/// algorithms can actually be produced. <para>确认 EROFS 写入器将面板的编解码器选择路由到构建器，从而真正产出非 LZ4 的磁盘算法。</para>
/// </summary>
public sealed class ErofsWriterAlgorithmTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-erofs-algo-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "src");
    private string Image => Path.Combine(_root, "out.img");
    private string Extracted => Path.Combine(_root, "extracted");
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public ErofsWriterAlgorithmTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "payload.txt"), SourcePattern());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string SourcePattern() =>
        string.Concat(Enumerable.Repeat("erofs compression payload 0123456789abcdef\n", 4096));

    [Theory]
    [InlineData("lz4", ErofsCompressionAlgorithm.Lz4, false)]
    [InlineData("lz4hc", ErofsCompressionAlgorithm.Lz4, true)]
    [InlineData("LZ4HC", ErofsCompressionAlgorithm.Lz4, true)]
    [InlineData("lzma", ErofsCompressionAlgorithm.Lzma, false)]
    [InlineData("deflate", ErofsCompressionAlgorithm.Deflate, false)]
    [InlineData("zstd", ErofsCompressionAlgorithm.Zstd, false)]
    [InlineData(null, ErofsCompressionAlgorithm.Lz4, false)]
    [InlineData("", ErofsCompressionAlgorithm.Lz4, false)]
    [InlineData("bogus", ErofsCompressionAlgorithm.Lz4, false)]
    public void Parse_Selector_MapsAlgorithmAndHighCompression(string? selector, ErofsCompressionAlgorithm algorithm, bool highCompression)
    {
        var (actual, hc) = ErofsAlgorithmSelectors.Parse(selector);
        Assert.Equal(algorithm, actual);
        Assert.Equal(highCompression, hc);
    }

    [Fact]
    public void Normalize_Selector_IsCaseInsensitiveAndDefaultsToLz4()
    {
        Assert.Equal("lzma", ErofsAlgorithmSelectors.Normalize("Lzma"));
        Assert.Equal("lz4hc", ErofsAlgorithmSelectors.Normalize("LZ4HC"));
        Assert.Equal("lz4", ErofsAlgorithmSelectors.Normalize("unknown"));
        Assert.Equal("lz4", ErofsAlgorithmSelectors.Normalize(null));
    }

    [Theory]
    [InlineData("lz4", "Lz4")]
    [InlineData("lz4hc", "Lz4")]
    [InlineData("lzma", "Lzma")]
    [InlineData("deflate", "Deflate")]
    [InlineData("zstd", "Zstd")]
    public async Task PackCore_Selector_RoutesToBuilderAlgorithm(string selector, string expectedAlgorithm)
    {
        await PackWithSelector(selector);

        // The preserved rebuild profile decodes each compressed inode's on-disk codec, which is the
        // authoritative record of what the writer emitted (independent of the superblock config
        // records, present only when big pclusters are used).
        // 保留的重建档案会解码每个压缩 inode 的磁盘编解码器，这是写入器实际产出内容的权威记录
        // （不依赖仅在使用 big pcluster 时才存在的超级块配置记录）。
        var extractDirectory = Path.Combine(_root, "read-" + Guid.NewGuid().ToString("N"));
        Assert.True((await new ErofsFormatHandler().UnpackAsync(Image, extractDirectory, cancellationToken: Cancellation)).Success);
        var declared = DeclaredAlgorithms(extractDirectory);
        Assert.Contains(expectedAlgorithm, declared);
    }

    private static IEnumerable<string> DeclaredAlgorithms(string extractedDirectory)
    {
        var metadata = RepackMetadata.Read(extractedDirectory)
            ?? throw new InvalidOperationException("Missing EROFS repack metadata.");
        var algorithms = new List<string>();
        foreach (var node in metadata.Configuration["compression"]?["algorithms"]?.AsArray() ?? [])
            algorithms.Add(node!["algorithm"]!.GetValue<string>());
        foreach (var node in metadata.Configuration["inodes"]?.AsArray() ?? [])
            algorithms.Add(node!["algorithm"]!.GetValue<string>());
        return algorithms;
    }

    [Theory]
    [InlineData("lzma")]
    [InlineData("deflate")]
    [InlineData("zstd")]
    public async Task PackCore_NonLz4Selector_ProducesMountableImage(string selector)
    {
        await PackWithSelector(selector);
        var unpack = await new ErofsFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(unpack.Success, unpack.Summary);
        Assert.Equal(SourcePattern(), File.ReadAllText(Path.Combine(Extracted, "payload.txt")));
    }

    /// <summary>
    /// Genuine WSL cross-validation: the managed writer's non-LZ4 image is checked by Alpine's
    /// fsck.erofs. <para>真实 WSL 交叉验证：托管写入器产出的非 LZ4 镜像由 Alpine 的 fsck.erofs 校验。</para>
    /// </summary>
    [Theory]
    [InlineData("lzma")]
    [InlineData("deflate")]
    [InlineData("zstd")]
    public async Task PackCore_NonLz4Selector_PassesFsckErofsInWsl(string selector)
    {
        if (!WslTestHelpers.IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!WslTestHelpers.IsAlpineAvailable()) { Assert.Skip("Alpine not available"); }
        if (!WslTestHelpers.IsToolAvailable("fsck.erofs")) { Assert.Skip("fsck.erofs unavailable"); }

        await PackWithSelector(selector);
        var (output, exitCode) = WslTestHelpers.RunWsl($"fsck.erofs {WslTestHelpers.ToWslPath(Image)}");
        Assert.True(exitCode <= 1 && !output.Contains("[Fail]", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("[ASSERT]", StringComparison.OrdinalIgnoreCase),
            $"fsck.erofs rejected the managed {selector} image (exit={exitCode}):\n{output}");
    }

    private async Task PackWithSelector(string selector)
    {
        // Repack an extracted directory: packing overrides only reach the builder when the input
        // carries repack metadata, mirroring the real edit-then-repack workflow.
        // 重打包一个解包目录：仅当输入带有重打包元数据时，打包覆盖才会进入构建器，与真实的编辑后重打包流程一致。
        var seed = Path.Combine(_root, "seed-" + Guid.NewGuid().ToString("N") + ".img");
        var seedDirectory = Path.Combine(_root, "seed-" + Guid.NewGuid().ToString("N"));
        Assert.True((await new ErofsFormatHandler().PackAsync(Source, seed, Cancellation)).Success);
        Assert.True((await new ErofsFormatHandler().UnpackAsync(seed, seedDirectory, cancellationToken: Cancellation)).Success);

        var overrides = new Dictionary<string, string>
        {
            ["ErofsAlgorithm"] = selector,
            ["ErofsCompressionLevel"] = "9",
        };
        using (RepackMetadata.UsePackingOverrides(overrides))
        {
            var packed = await new ErofsFormatHandler().PackAsync(seedDirectory, Image, Cancellation);
            Assert.True(packed.Success, packed.Summary);
        }
    }
}
using System.Globalization;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Gates the EROFS rebuild compression search behind the process-wide toggle: with the toggle off a preserved
/// rebuild must stay byte-identical to the single inherited-profile build even when the snapshot carries
/// captured estimates, and the bounded search itself must stop as soon as one candidate fits the recorded
/// 64-bit source extent.
/// <para>把 EROFS 重建压缩搜索门控在进程级开关之后：开关关闭时，即便快照已携带采集到的估算，受保留重建
/// 仍必须与单次继承档案构建逐字节一致；有界搜索本身必须在某个候选满足记录的 64 位源范围后立即停止。</para>
/// </summary>
[Collection("Workspace state")]
public sealed class ErofsRebuildOptimizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-erofs-optimizer-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ErofsRebuildOptimizerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _scope.Dispose();
        ErofsBuildDefaults.RebuildOptimization = ErofsBuildDefaults.DefaultRebuildOptimization;
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task ErofsRebuildOptimizerFlagOff_ProducesSameBytesAsPlainBuild()
    {
        var original = ErofsBuildDefaults.RebuildOptimization;
        try
        {
            var sourceTree = WriteSourceTree("flag-off-source");
            var image = Path.Combine(_root, "source.img");
            File.WriteAllBytes(image, BuildLegacyImage(sourceTree));
            var handler = new ErofsFormatHandler();

            ErofsBuildDefaults.RebuildOptimization = true;
            var estimatedTree = Path.Combine(_root, "estimated");
            Assert.True((await handler.UnpackAsync(image, estimatedTree, cancellationToken: Token)).Success);
            ErofsBuildDefaults.RebuildOptimization = false;
            var plainTree = Path.Combine(_root, "plain");
            Assert.True((await handler.UnpackAsync(image, plainTree, cancellationToken: Token)).Success);

            Assert.NotEmpty(Payload(estimatedTree).Rebuild!.Erofs!.CompressionEstimates);
            Assert.Empty(Payload(plainTree).Rebuild!.Erofs!.CompressionEstimates);
            Assert.True(Payload(plainTree).Rebuild!.Erofs!.SourceFilesystemBytes > 0);

            var estimatedOutput = Path.Combine(_root, "estimated.img");
            var plainOutput = Path.Combine(_root, "plain.img");
            IReadOnlyDictionary<string, string>? estimatedReport = null;
            IReadOnlyDictionary<string, string>? plainReport = null;
            Assert.True(FileSystemPreservation.TryRestore(estimatedTree, estimatedOutput, "EROFS", Token,
                report => estimatedReport = report));
            Assert.True(FileSystemPreservation.TryRestore(plainTree, plainOutput, "EROFS", Token,
                report => plainReport = report));
            Assert.Equal(File.ReadAllBytes(plainOutput), File.ReadAllBytes(estimatedOutput));
            Assert.False(estimatedReport!.ContainsKey("CompressionAttempts"));
            Assert.False(plainReport!.ContainsKey("CompressionAttempts"));
            Assert.Contains("inherited from the verified source image",
                estimatedReport["CompressionInference"]);
            Assert.Equal(ErofsSuperblock.IncompatLz4_0Padding,
                ErofsSuperblock.FromImage(File.ReadAllBytes(estimatedOutput)).FeatureIncompat);

            ErofsBuildDefaults.RebuildOptimization = true;
            var searchedOutput = Path.Combine(_root, "searched.img");
            IReadOnlyDictionary<string, string>? searchedReport = null;
            Assert.True(FileSystemPreservation.TryRestore(estimatedTree, searchedOutput, "EROFS", Token,
                report => searchedReport = report));
            Assert.True(searchedReport!.ContainsKey("CompressionAttempts"),
                "the search ran but reported no attempts");
            Assert.True(searchedReport["CompressionAttempts"].Length > 0);
            Assert.False(searchedReport["CompressionInference"]
                .Contains("inherited from the verified source image", StringComparison.Ordinal));
            using (var verify = new ErofsParser(File.ReadAllBytes(searchedOutput)))
            {
                Assert.Equal(CompressiblePayload(), verify.ReadFileBytes("data.bin"));
            }

            var legacyOutput = Path.Combine(_root, "legacy.img");
            IReadOnlyDictionary<string, string>? legacyReport = null;
            Assert.True(FileSystemPreservation.TryRestore(plainTree, legacyOutput, "EROFS", Token,
                report => legacyReport = report));
            Assert.False(legacyReport!.ContainsKey("CompressionAttempts"));
            Assert.Equal(File.ReadAllBytes(plainOutput), File.ReadAllBytes(legacyOutput));
        }
        finally
        {
            ErofsBuildDefaults.RebuildOptimization = original;
        }
    }

    [Fact]
    public void ErofsRebuildOptimizer_WithoutCodecOptions_FallsBackToSingleBuild()
    {
        var directory = WriteSourceTree("no-codecs");
        var profile = new ErofsRebuildParameters { SourceFilesystemBytes = 1L << 20 };
        profile.CompressionEstimates.Add(new ErofsRebuildCompressionEstimate
        {
            Algorithm = "Lz4",
            Samples = 4,
            SampleBytes = 16384,
            Candidates =
            [
                new ErofsRebuildCompressionCandidate { Level = 1, Bytes = 9000, ExactMatches = 4 },
                new ErofsRebuildCompressionCandidate { Level = 12, HighCompression = true, Bytes = 7000 },
            ],
        });
        using var output = new MemoryStream();

        var report = ErofsRebuildOptimizer.Build(directory, output,
            new ErofsOptions { BuildTime = 1700000000UL, Uuid = new byte[16] }, profile, Token);

        Assert.Empty(report);
        Assert.Equal(new ErofsBuilder().Build(directory, new ErofsOptions
        {
            BuildTime = 1700000000UL,
            Uuid = new byte[16],
            OptimizeCompression = true,
        }), output.ToArray());
    }

    [Fact]
    public void ErofsRebuildOptimizer_StopsOnceImageFitsSourceSize()
    {
        var directory = WriteSourceTree("fits");
        var (options, profile) = CaptureEstimates(directory, 1L << 40);
        using var output = new MemoryStream();

        var report = ErofsRebuildOptimizer.Build(directory, output, options, profile, Token);

        Assert.Equal((1L << 40).ToString(CultureInfo.InvariantCulture), report["SourceFilesystemSize"]);
        Assert.Equal("True", report["FitsSourceFilesystemSize"]);
        Assert.Equal("0", report["SizeExcess"]);
        Assert.Single(SplitAttempts(report["CompressionAttempts"]));
        Assert.Equal(long.Parse(report["RebuiltSize"], CultureInfo.InvariantCulture), output.Length);
        Assert.Equal(report["CompressionProfile"], OnlyAttemptProfile(report["CompressionAttempts"]));
        using var verify = new ErofsParser(output.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "data.bin")), verify.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void ErofsRebuildOptimizer_TargetSmallerThanEveryCandidate_KeepsSearchingFurtherCandidates()
    {
        var directory = WriteSourceTree("oversize");
        var (options, profile) = CaptureEstimates(directory, 1);
        using var output = new MemoryStream();

        var report = ErofsRebuildOptimizer.Build(directory, output, options, profile, Token);

        Assert.Equal("False", report["FitsSourceFilesystemSize"]);
        Assert.True(long.Parse(report["SizeExcess"], CultureInfo.InvariantCulture) > 0);
        Assert.True(SplitAttempts(report["CompressionAttempts"]).Length > 1,
            $"the search stopped after one attempt: {report["CompressionAttempts"]}");
    }

    [Fact]
    public void ErofsRebuildOptimizer_SourceExtentAboveUInt32Bytes_ReportsTheFull64BitTarget()
    {
        var directory = WriteSourceTree("extent");
        var beyondUInt32 = (1L << 32) + 4096;
        var (options, profile) = CaptureEstimates(directory, beyondUInt32);
        using var output = new MemoryStream();

        var report = ErofsRebuildOptimizer.Build(directory, output, options, profile, Token);

        Assert.Equal(beyondUInt32.ToString(CultureInfo.InvariantCulture), report["SourceFilesystemSize"]);
        Assert.Equal("True", report["FitsSourceFilesystemSize"]);
        Assert.Equal(0L, long.Parse(report["SizeExcess"], CultureInfo.InvariantCulture));
        Assert.Equal(1L << 44, CaptureEstimates(directory, 1L << 44).Profile.SourceFilesystemBytes!.Value);
        using var verify = new ErofsParser(output.ToArray());
        Assert.Equal(CompressiblePayload(), verify.ReadFileBytes("data.bin"));
    }

    private static byte[] CompressiblePayload()
    {
        var payload = new byte[64 * 1024];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 97 == 0 ? 0xFF : i % 7);
        return payload;
    }

    private static byte[] BuildLegacyImage(string directory)
        => new ErofsBuilder().Build(directory, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
        });

    private string WriteSourceTree(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "data.bin"), CompressiblePayload());
        File.WriteAllText(Path.Combine(root, "notes.txt"), "preserved rebuild fixture payload");
        return root;
    }

    private static (ErofsOptions Options, ErofsRebuildParameters Profile) CaptureEstimates(string directory,
        long sourceFilesystemBytes)
    {
        var original = ErofsBuildDefaults.RebuildOptimization;
        ErofsBuildDefaults.RebuildOptimization = true;
        try
        {
            using var parser = new ErofsParser(BuildLegacyImage(directory));
            var options = parser.ReadRebuildOptions();
            var profile = ErofsRebuildMetadata.Capture(options, parser, sourceFilesystemBytes, Token);
            Assert.NotEmpty(profile.CompressionEstimates);
            Assert.Single(profile.CompressionEstimates);
            return (ErofsRebuildMetadata.Restore(profile), profile);
        }
        finally
        {
            ErofsBuildDefaults.RebuildOptimization = original;
        }
    }

    private static FileSystemPreservationData Payload(string directory)
        => FileSystemPreservation.ReadPayload(RepackMetadata.Read(directory))
        ?? throw new FileSystemCorruptedException("The preservation payload is missing.");

    private static string[] SplitAttempts(string attempts)
        => attempts.Split("; ", StringSplitOptions.RemoveEmptyEntries);

    private static string OnlyAttemptProfile(string attempts)
    {
        var attempt = Assert.Single(SplitAttempts(attempts));
        return attempt[..attempt.IndexOf(" = ", StringComparison.Ordinal)].Trim();
    }
}
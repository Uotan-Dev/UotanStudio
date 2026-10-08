using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Builds an Lz4-compressed EROFS image with the dense pcluster planner enabled and disabled, which is the cost of
/// the probe search plus the write pass, and the difference staging removes from that write pass.
/// <para>分别在启用与禁用密集 pcluster 规划器的情况下构建 Lz4 压缩 EROFS 镜像，体现探测搜索加写出阶段的成本，
/// 以及暂存从写出阶段消除的那部分。</para>
/// </summary>
/// <remarks>
/// Run with <c>dotnet run -c Release --project UotanStudio.Benchmarks -- --filter "*ErofsDensePlanning*" --job short</c>.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 2)]
public class ErofsDensePlanningBenchmarks : IDisposable
{
    private string _root = "";
    private string _source = "";

    [Params(32, 128)]
    public int Files { get; set; }

    [Params(1024, 16 * 1024)]
    public int FileBytes { get; set; }

    [Params(true, false)]
    public bool OptimizeCompression { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-bench-dense-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "tree");
        Directory.CreateDirectory(_source);
        var random = new Random(0x5EED);
        var pattern = new byte[FileBytes];
        random.NextBytes(pattern);
        for (var i = 0; i < FileBytes / 2; i++)
        {
            pattern[i] = pattern[FileBytes - 1 - i];
        }

        for (var index = 0; index < Files; index++)
        {
            File.WriteAllBytes(Path.Combine(_source, $"blob-{index}.bin"), pattern);
        }
    }

    [Benchmark]
    public int Build()
    {
        var image = new ErofsBuilder().Build(_source, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            LegacyCompatibility = true,
            OptimizeCompression = OptimizeCompression,
            Uuid = new byte[16],
            BuildTime = 1700000000,
        });
        return image.Length;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

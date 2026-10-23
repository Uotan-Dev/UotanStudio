using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Measures end-to-end EROFS builds over the deterministic 44 MiB mixed corpus for the compression and
/// pcluster configurations the optimization work targets.
/// <para>在确定性 44 MiB 混合语料上度量优化工作所针对的压缩与 pcluster 配置的端到端 EROFS 构建。</para>
/// </summary>
/// <remarks>
/// The fill planner is an internal layout component, so these benchmarks call the public
/// <see cref="ErofsBuilder"/> path-to-path <c>Build</c> overload it feeds instead of reaching internal types.
/// <para>填充规划器是内部布局组件，因此这些基准调用它所服务的公开 <see cref="ErofsBuilder"/> 路径到路径
/// <c>Build</c> 重载，而不访问内部类型。</para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 2)]
public class ErofsBuildFillBenchmarks : IDisposable
{
    private const int BuildWorkers = 8;

    private string _root = "";
    private string _source = "";
    private string _imagePath = "";
    private int _previousWorkers;

    /// <summary>Creates the corpus once per benchmark case and pins the build worker count.
    /// <para>每个基准用例创建一次语料，并固定构建工作者数量。</para></summary>
    [GlobalSetup]
    public void Setup()
    {
        _previousWorkers = FirmwareParallelism.Workers;
        FirmwareParallelism.Workers = BuildWorkers;
        _root = Path.Combine(Path.GetTempPath(), "uotan-bench-erofs-build-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "tree");
        var corpus = ErofsBenchmarkCorpus.Create(_source);
        _imagePath = Path.Combine(_root, "image.erofs");
        Console.WriteLine(
            $"[erofs-build] corpus={corpus.PayloadBytes} bytes files={corpus.FileCount} " +
            $"symlink={corpus.SymlinkCreated} hardlink={corpus.HardLinkCreated} workers={BuildWorkers}");
    }

    /// <summary>LZ4 fast encoder at a 4 KiB pcluster with the full (non-compacted) index form.
    /// <para>4 KiB pcluster、完整（非紧凑）索引形态下的 LZ4 快速编码器。</para></summary>
    /// <returns>Built image size in bytes. <para>构建出的镜像字节数。</para></returns>
    [Benchmark(Baseline = true)]
    public long Lz4FourKilobyteFullIndex()
        => Build(ErofsCompressionAlgorithm.Lz4, highCompression: false, pclusterSize: 4 * 1024, compressionLevel: 1);

    /// <summary>LZ4HC encoder at a 4 KiB pcluster with the full index form.
    /// <para>4 KiB pcluster、完整索引形态下的 LZ4HC 编码器。</para></summary>
    /// <returns>Built image size in bytes. <para>构建出的镜像字节数。</para></returns>
    [Benchmark]
    public long Lz4HcFourKilobyte()
        => Build(ErofsCompressionAlgorithm.Lz4, highCompression: true, pclusterSize: 4 * 1024, compressionLevel: 9);

    /// <summary>LZ4HC encoder at a 64 KiB pcluster with the full index form and multi-block pclusters.
    /// <para>64 KiB pcluster、完整索引形态与跨块 pcluster 下的 LZ4HC 编码器。</para></summary>
    /// <returns>Built image size in bytes. <para>构建出的镜像字节数。</para></returns>
    [Benchmark]
    public long Lz4HcSixtyFourKilobyte()
        => Build(ErofsCompressionAlgorithm.Lz4, highCompression: true, pclusterSize: 64 * 1024, compressionLevel: 9);

    /// <summary>Zstandard encoder at a 4 KiB pcluster with the full index form.
    /// <para>4 KiB pcluster、完整索引形态下的 Zstandard 编码器。</para></summary>
    /// <returns>Built image size in bytes. <para>构建出的镜像字节数。</para></returns>
    [Benchmark]
    public long ZstdFourKilobyte()
        => Build(ErofsCompressionAlgorithm.Zstd, highCompression: false, pclusterSize: 4 * 1024, compressionLevel: 9);

    private long Build(ErofsCompressionAlgorithm algorithm, bool highCompression, int pclusterSize,
        int compressionLevel)
    {
        var options = new ErofsOptions
        {
            Algorithm = algorithm,
            BlockSize = 4 * 1024,
            MaxPclusterSize = pclusterSize,
            CompressionLevel = compressionLevel,
            Lz4HighCompression = highCompression,
            UseCompactedIndexes = false,
            UseCompacted2BIndexes = false,
            UseExtentIndexes = ErofsTriStateFeature.Disabled,
            Uuid = new byte[16],
            BuildTime = 1_700_000_000UL,
            BuildTimeNanoseconds = 0,
            VolumeName = "uotan-bench"
        };
        new ErofsBuilder().Build(_source, _imagePath, options);
        return new FileInfo(_imagePath).Length;
    }

    /// <summary>Restores the process worker count and removes the corpus and image.
    /// <para>还原进程工作者数量，并删除语料与镜像。</para></summary>
    public void Dispose()
    {
        FirmwareParallelism.Workers = _previousWorkers;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
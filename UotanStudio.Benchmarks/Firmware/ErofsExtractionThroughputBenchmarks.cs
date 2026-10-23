using BenchmarkDotNet.Attributes;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Measures extracting one LZ4 EROFS image with one and eight workers through the public parser, isolating
/// the parallel decode and write path from image construction.
/// <para>用 1 与 8 个工作者经公开解析器提取同一个 LZ4 EROFS 镜像，把并行解码与写入路径同镜像构建隔离开。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 2)]
public class ErofsExtractionThroughputBenchmarks : IDisposable
{
    private string _root = "";
    private string _source = "";
    private string _imagePath = "";
    private string _targetPath = "";
    private int _previousWorkers;
    private int _fileCount;

    /// <summary>Extraction worker count applied to <see cref="FirmwareParallelism.Workers"/> for this case.
    /// <para>本用例应用到 <see cref="FirmwareParallelism.Workers"/> 的解包工作者数量。</para></summary>
    /// <value>One or eight, matching the recorded baseline comparison.
    /// <para>1 或 8，与已记录的基线对比一致。</para></value>
    [Params(1, 8)]
    public int Workers { get; set; }

    /// <summary>Creates the corpus, builds the shared LZ4 image once, then pins the worker count.
    /// <para>创建语料，构建一次共享 LZ4 镜像，然后固定工作者数量。</para></summary>
    [GlobalSetup]
    public void Setup()
    {
        _previousWorkers = FirmwareParallelism.Workers;
        FirmwareParallelism.Workers = Workers;
        _root = Path.Combine(Path.GetTempPath(), "uotan-bench-erofs-extract-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "tree");
        var corpus = ErofsBenchmarkCorpus.Create(_source);
        _imagePath = Path.Combine(_root, "image.erofs");
        BuildImage();
        _fileCount = corpus.FileCount;
        Console.WriteLine(
            $"[erofs-extract] image={new FileInfo(_imagePath).Length} bytes corpus={corpus.PayloadBytes} bytes " +
            $"files={corpus.FileCount} workers={Workers} symlink={corpus.SymlinkCreated} " +
            $"hardlink={corpus.HardLinkCreated}");
    }

    /// <summary>Extracts every entry of the shared image into a fresh temporary tree.
    /// <para>把共享镜像的全部条目解包到全新的临时目录树。</para>
    /// </summary>
    /// <returns>Corpus file count, which keeps the result observable without a second directory walk.
    /// <para>语料文件数量；无需第二次目录遍历即可让结果可观测。</para></returns>
    [Benchmark]
    public int ExtractToDirectory()
    {
        _targetPath = Path.Combine(_root, "extract-" + Guid.NewGuid().ToString("N"));
        using var stream = File.OpenRead(_imagePath);
        using var parser = new ErofsParser(stream);
        parser.ExtractToDirectory(_targetPath);
        return _fileCount;
    }

    /// <summary>Removes the tree written by the measured iteration.
    /// <para>删除被度量迭代写出的目录树。</para></summary>
    [IterationCleanup(Target = nameof(ExtractToDirectory))]
    public void CleanupExtract()
    {
        if (_targetPath.Length > 0 && Directory.Exists(_targetPath))
        {
            Directory.Delete(_targetPath, true);
        }

        _targetPath = "";
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

    private void BuildImage()
    {
        new ErofsBuilder().Build(_source, _imagePath, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            BlockSize = 4 * 1024,
            MaxPclusterSize = 4 * 1024,
            CompressionLevel = 1,
            UseCompactedIndexes = false,
            UseCompacted2BIndexes = false,
            UseExtentIndexes = ErofsTriStateFeature.Disabled,
            Uuid = new byte[16],
            BuildTime = 1_700_000_000UL,
            BuildTimeNanoseconds = 0,
            VolumeName = "uotan-bench"
        });
    }
}

/// <summary>
/// Measures the preservation-aware handler path, which adds metadata capture and source pinning on top of the
/// raw parser extraction measured by <see cref="ErofsExtractionThroughputBenchmarks"/>.
/// <para>度量保留感知的处理器路径，它在 <see cref="ErofsExtractionThroughputBenchmarks"/> 所测的裸解析器解包之上
/// 增加元数据采集与源钉住。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 2)]
public class ErofsHandlerUnpackBenchmarks : IDisposable
{
    private const int HandlerWorkers = 8;

    private string _root = "";
    private string _source = "";
    private string _imagePath = "";
    private string _targetPath = "";
    private int _previousWorkers;

    /// <summary>Creates the corpus, builds the LZ4 image once and pins the handler worker count.
    /// <para>创建语料，构建一次 LZ4 镜像，并固定处理器的工作者数量。</para></summary>
    [GlobalSetup]
    public void Setup()
    {
        _previousWorkers = FirmwareParallelism.Workers;
        FirmwareParallelism.Workers = HandlerWorkers;
        _root = Path.Combine(Path.GetTempPath(), "uotan-bench-erofs-handler-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "tree");
        var corpus = ErofsBenchmarkCorpus.Create(_source);
        _imagePath = Path.Combine(_root, "image.erofs");
        BuildImage();
        Console.WriteLine(
            $"[erofs-handler] image={new FileInfo(_imagePath).Length} bytes corpus={corpus.PayloadBytes} bytes " +
            $"files={corpus.FileCount} workers={HandlerWorkers} symlink={corpus.SymlinkCreated} " +
            $"hardlink={corpus.HardLinkCreated}");
    }

    /// <summary>Unpacks the shared image through <see cref="ErofsFormatHandler.UnpackAsync"/>.
    /// <para>经 <see cref="ErofsFormatHandler.UnpackAsync"/> 解包共享镜像。</para>
    /// </summary>
    /// <returns>Extracted entry count. <para>解出的条目数量。</para></returns>
    /// <exception cref="InvalidOperationException">The handler returned a failed result.
    /// <para>处理器返回失败结果。</para></exception>
    [Benchmark]
    public async Task<int> UnpackAsync()
    {
        _targetPath = Path.Combine(_root, "handler-" + Guid.NewGuid().ToString("N"));
        var handler = new ErofsFormatHandler();
        var result = await handler.UnpackAsync(_imagePath, _targetPath, new UnpackOptions(), CancellationToken.None)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Summary);
        }

        return result.Entries.Count;
    }

    /// <summary>Removes the tree and preservation metadata written by the measured iteration.
    /// <para>删除被度量迭代写出的目录树与保留元数据。</para></summary>
    [IterationCleanup(Target = nameof(UnpackAsync))]
    public void CleanupUnpack()
    {
        if (_targetPath.Length > 0 && Directory.Exists(_targetPath))
        {
            Directory.Delete(_targetPath, true);
        }

        _targetPath = "";
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

    private void BuildImage()
    {
        new ErofsBuilder().Build(_source, _imagePath, new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            BlockSize = 4 * 1024,
            MaxPclusterSize = 4 * 1024,
            CompressionLevel = 1,
            UseCompactedIndexes = false,
            UseCompacted2BIndexes = false,
            UseExtentIndexes = ErofsTriStateFeature.Disabled,
            Uuid = new byte[16],
            BuildTime = 1_700_000_000UL,
            BuildTimeNanoseconds = 0,
            VolumeName = "uotan-bench"
        });
    }
}
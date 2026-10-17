using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using UotanStudio.Benchmarks.Infrastructure;
using UotanStudio.Benchmarks.Inputs;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// The purpose-built workload for a default-GC versus Satori-GC comparison: EROFS pack, unpack and read-only
/// walk, all at default compression settings so a later run can vary only the GC.
/// <para>为默认 GC 与 Satori GC 比较而专门构建的工作负载：EROFS 打包、解包与只读遍历，全部使用默认压缩设置，
/// 使后续运行只需改变 GC。</para>
/// The two other EROFS classes answer planner questions — they sweep <c>OptimizeCompression</c> and worker
/// counts, they feed symmetric half-random payloads, and neither drives a GC mode. This class deliberately
/// fixes every compression input instead: LZ4 at the default level, block size and pcluster, over corpus
/// payloads with realistic compressibility, under the shared <see cref="GcHarness"/> configuration. Only the
/// tree shape varies, because the shapes move allocation differently: a small-file tree stresses inode and
/// dirent allocation, while a large-file tree stresses buffer and pcluster allocation, and a GC change can
/// move one without moving the other.
/// <para>另外两个 EROFS 类回答的是规划器问题：它们遍历 <c>OptimizeCompression</c> 与工作者数量，提供对称的
/// 半随机载荷，且都不驱动 GC 模式。本类则刻意固定每一项压缩输入：LZ4 与默认的压缩级别、块大小和 pcluster，
/// 作用在具备真实压缩率的语料载荷上，并运行在共享的 <see cref="GcHarness"/> 配置下。只有目录树形态变化，
/// 因为不同形态以不同方式移动分配：小文件树压测 inode 与目录项分配，大文件树压测缓冲区与 pcluster 分配，
/// GC 的改动可能只推动其中之一。</para>
/// </summary>
/// <remarks>
/// The parameter space is <see cref="Shape"/> (2) times <see cref="TreeSize"/> (3), evaluated once per
/// benchmark method, so the class materializes <c>2 * 3 * 4 = 24</c> combinations. EROFS builds are the most
/// expensive operation in this project, which is why the space stays well under the 60-combination ceiling.
/// <para>参数空间为 <see cref="Shape"/>（2）乘以 <see cref="TreeSize"/>（3），每个基准方法求值一次，因此本类共
/// 实体化 <c>2 * 3 * 4 = 24</c> 种组合。EROFS 构建是本项目中最昂贵的操作，因此参数空间远低于 60 种组合的上限。</para>
/// </remarks>
[Config(typeof(ErofsGcBaselineConfig))]
public class ErofsGcBaselineBenchmarks : IDisposable
{
    private string _root = "";
    private string _source = "";
    private string _imagePath = "";
    private int _fileCount;
    private long _fileBytes;
    private long _imageLength;

    /// <summary>Shape of the source tree, the axis that separates inode allocation from buffer allocation.
    /// <para>源目录树的形态，即把 inode 分配与缓冲区分配区分开的轴。</para>
    /// </summary>
    [Params(ErofsTreeShape.SmallFiles, ErofsTreeShape.LargeFiles)]
    public ErofsTreeShape Shape { get; set; }

    /// <summary>Size of the source tree within the selected shape.
    /// <para>所选形态下源目录树的大小。</para>
    /// </summary>
    [Params(0, 1, 2)]
    public int TreeSize { get; set; }

    /// <summary>Number of payload files the setup wrote into the source tree.
    /// <para>准备阶段写入源目录树的载荷文件数量。</para>
    /// </summary>
    /// <value>Set by <see cref="Setup"/> from the selected shape and size.
    /// <para>由 <see cref="Setup"/> 依据所选形态与大小设置。</para></value>
    public int FileCount => _fileCount;

    /// <summary>Total payload bytes the source tree holds.
    /// <para>源目录树承载的载荷总字节数。</para>
    /// </summary>
    /// <value>Set by <see cref="Setup"/>; also the scale factor a reader divides the reported allocations by.
    /// <para>由 <see cref="Setup"/> 设置；读取方也用它作为报告分配量的缩放因子。</para></value>
    public long PayloadBytes => _fileBytes;

    /// <summary>Compression algorithm every benchmark in this class builds with.
    /// <para>本类中每个基准构建时使用的压缩算法。</para>
    /// </summary>
    /// <value>Always <see cref="ErofsCompressionAlgorithm.Lz4"/>, the EROFS default; a GC comparison must not
    /// move the codec.
    /// <para>始终为 <see cref="ErofsCompressionAlgorithm.Lz4"/>，即 EROFS 默认值；GC 比较不应改变编解码器。</para></value>
    public ErofsCompressionAlgorithm Algorithm => ErofsCompressionAlgorithm.Lz4;

    /// <summary>Compression level every benchmark in this class builds with.
    /// <para>本类中每个基准构建时使用的压缩级别。</para>
    /// </summary>
    /// <value>The <see cref="ErofsOptions.CompressionLevel"/> default, never overridden here.
    /// <para><see cref="ErofsOptions.CompressionLevel"/> 的默认值，本类从不覆盖。</para></value>
    public int CompressionLevel => new ErofsOptions().CompressionLevel;

    /// <summary>Logical block size every benchmark in this class builds with.
    /// <para>本类中每个基准构建时使用的逻辑块大小。</para>
    /// </summary>
    /// <value>The <see cref="ErofsOptions.BlockSize"/> default, never overridden here.
    /// <para><see cref="ErofsOptions.BlockSize"/> 的默认值，本类从不覆盖。</para></value>
    public int BlockSize => new ErofsOptions().BlockSize;

    /// <summary>Maximum physical cluster size every benchmark in this class builds with.
    /// <para>本类中每个基准构建时使用的最大物理簇大小。</para>
    /// </summary>
    /// <value>The <see cref="ErofsOptions.MaxPclusterSize"/> default (zero, meaning the builder picks the block
    /// size), never overridden here. <para><see cref="ErofsOptions.MaxPclusterSize"/> 的默认值（零，表示打包器
    /// 取用块大小），本类从不覆盖。</para></value>
    public int MaxPclusterSize => new ErofsOptions().MaxPclusterSize;

    /// <summary>Writes the corpus-driven source tree once and builds the image every benchmark shares.
    /// <para>写入由语料驱动的源目录树，并构建各基准共用的镜像。</para>
    /// </summary>
    /// <exception cref="IOException">A payload file could not be written.
    /// <para>某个载荷文件无法写入。</para></exception>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-bench-erofs-gc-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "tree");
        try
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_source);
            WriteSourceTree();
            _imagePath = Path.Combine(_root, "image.erofs");
            new ErofsBuilder().Build(_source, _imagePath, CreateOptions());
            _imageLength = new FileInfo(_imagePath).Length;
        }
        catch
        {
            DeleteRoot();
            throw;
        }
    }

    /// <summary>Builds the image from the source tree at default compression settings.
    /// <para>使用默认压缩设置从源目录树构建镜像。</para>
    /// </summary>
    /// <returns>The byte length of the image the build produced, proving the write pass ran.
    /// <para>本次构建产出的镜像字节长度，用以证明写出阶段确实执行。</para></returns>
    [Benchmark]
    public long Pack()
    {
        var target = Path.Combine(_root, "pack-" + Guid.NewGuid().ToString("N") + ".erofs");
        try
        {
            new ErofsBuilder().Build(_source, target, CreateOptions());
            return new FileInfo(target).Length;
        }
        finally
        {
            DeleteFile(target);
        }
    }

    /// <summary>Extracts the setup-built image into a fresh host directory.
    /// <para>把准备阶段构建的镜像解包到一个全新的宿主目录。</para>
    /// </summary>
    /// <returns>The number of file entries the image holds, proving the walk and extraction ran.
    /// <para>镜像持有的文件条目数，用以证明遍历与解包确实执行。</para></returns>
    [Benchmark]
    public int UnpackToDirectory()
    {
        var target = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = File.OpenRead(_imagePath);
            using var parser = new ErofsParser(stream);
            parser.ExtractToDirectory(target);
            return parser.EnumerateFiles().Count;
        }
        finally
        {
            DeleteDirectory(target);
        }
    }

    /// <summary>Decodes every entry of the setup-built image into a reusable memory stream.
    /// <para>把准备阶段构建的镜像中每个条目解码到一个可复用的内存流。</para>
    /// This path keeps the decoded bytes on the heap instead of handing them to the file system, which is the
    /// allocation profile that separates it from <see cref="UnpackToDirectory"/>.
    /// <para>该路径把解码后的字节留在堆上而不是交给文件系统，这正是它与 <see cref="UnpackToDirectory"/>
    /// 分配特征不同的原因。</para>
    /// </summary>
    /// <returns>The total number of decoded payload bytes, proving every entry was read.
    /// <para>解码后的载荷总字节数，用以证明每个条目都被读取。</para></returns>
    [Benchmark]
    public long UnpackToStream()
    {
        using var stream = File.OpenRead(_imagePath);
        using var parser = new ErofsParser(stream);
        var buffer = new MemoryStream(checked((int)_fileBytes));
        long total = 0;
        foreach (var entry in parser.EnumerateFiles())
        {
            buffer.SetLength(0);
            parser.ReadFile(entry.Path, buffer);
            total += buffer.Length;
        }

        return total;
    }

    /// <summary>Walks the image read-only, enumerating entries and reading each file's bytes.
    /// <para>只读遍历镜像，枚举条目并读取每个文件的字节。</para>
    /// The walk isolates per-entry allocation from the write path: nothing is written to disk, so any measured
    /// allocation comes from inode, dirent and decoded-buffer materialization alone.
    /// <para>该遍历把逐条目分配与写出路径隔离开：不向磁盘写入任何内容，因此测得的分配只来自 inode、目录项与
    /// 解码缓冲区的实体化。</para>
    /// </summary>
    /// <returns>The number of entries enumerated, proving the directory walk ran.
    /// <para>枚举到的条目数，用以证明目录遍历确实执行。</para></returns>
    [Benchmark]
    public int Walk()
    {
        using var stream = File.OpenRead(_imagePath);
        using var parser = new ErofsParser(stream);
        var entries = parser.EnumerateFiles();
        foreach (var entry in entries)
        {
            parser.ReadFileBytes(entry.Path);
        }

        return entries.Count;
    }

    /// <summary>Removes the temp tree and temp image this instance created.
    /// <para>删除本实例创建的临时目录树与临时镜像。</para>
    /// </summary>
    public void Dispose()
    {
        DeleteRoot();
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds the option set shared by every benchmark here at EROFS defaults.
    /// <para>构建本类每个基准共用的、处于 EROFS 默认值的选项集合。</para>
    /// </summary>
    /// <returns>Options that pin only determinism inputs, leaving the compression configuration untouched.
    /// <para>只固定确定性输入的选项，压缩配置保持不动。</para></returns>
    private static ErofsOptions CreateOptions() => new()
    {
        Algorithm = ErofsCompressionAlgorithm.Lz4,
        LegacyCompatibility = true,
        Uuid = new byte[16],
        BuildTime = 1700000000,
    };

    /// <summary>Writes the source tree for the selected shape and size.
    /// <para>为所选形态与大小写入源目录树。</para>
    /// </summary>
    private void WriteSourceTree()
    {
        var (files, bytesPerFile, kind) = ResolveShape();
        _fileCount = files;
        _fileBytes = (long)files * bytesPerFile;
        for (var index = 0; index < files; index++)
        {
            File.WriteAllBytes(
                Path.Combine(_source, $"payload-{index}.bin"),
                BenchCorpus.Bytes(kind, bytesPerFile).ToArray());
        }
    }

    /// <summary>Maps the two parameter axes onto a concrete file count, file size and payload character.
    /// <para>把两个参数轴映射为具体的文件数、文件大小与载荷特征。</para>
    /// </summary>
    /// <returns>The file count, per-file size in MiB and corpus character for this combination.
    /// <para>该组合的文件数、单文件 MiB 大小与语料特征。</para></returns>
    private (int Files, int Mebibytes, PayloadKind Kind) ResolveShape() => Shape switch
    {
        ErofsTreeShape.SmallFiles => TreeSize switch
        {
            0 => (64, 1, PayloadKind.Text),
            1 => (192, 1, PayloadKind.Source),
            _ => (384, 1, PayloadKind.Text),
        },
        _ => TreeSize switch
        {
            0 => (2, 2, PayloadKind.Text),
            1 => (4, 4, PayloadKind.Executable),
            _ => (8, 8, PayloadKind.ZeroFill),
        },
    };

    /// <summary>Deletes the whole temp root this instance owns, tolerating a partially created tree.
    /// <para>删除本实例拥有的整个临时根目录，容忍只创建了一部分的目录树。</para>
    /// </summary>
    private void DeleteRoot() => DeleteDirectory(_root);

    /// <summary>Deletes a directory if it exists, never letting cleanup mask a benchmark failure.
    /// <para>目录存在即删除，绝不让清理过程掩盖基准失败。</para>
    /// </summary>
    /// <param name="path">Directory to remove. <para>要删除的目录。</para></param>
    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes a file if it exists, never letting cleanup mask a benchmark failure.
    /// <para>文件存在即删除，绝不让清理过程掩盖基准失败。</para>
    /// </summary>
    /// <param name="path">File to remove. <para>要删除的文件。</para></param>
    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Source tree shapes the EROFS GC baseline measures.
/// <para>EROFS GC 基线所度量的源目录树形态。</para>
/// </summary>
public enum ErofsTreeShape
{
    /// <summary>Many sub-block files, stressing inode and directory-entry allocation.
    /// <para>大量不足一块的小文件，压测 inode 与目录项分配。</para>
    /// </summary>
    SmallFiles,

    /// <summary>Few multi-megabyte files, stressing buffer and pcluster allocation.
    /// <para>少量数兆字节的大文件，压测缓冲区与 pcluster 分配。</para>
    /// </summary>
    LargeFiles,
}

/// <summary>
/// Pairs the shared <see cref="GcHarness"/> global configuration with its baseline GC job, so the whole class
/// runs under the harness configuration and declares no GC attribute of its own.
/// <para>把共享的 <see cref="GcHarness"/> 全局配置与其基线 GC 作业配成一对，使整个类运行在 harness 配置下，
/// 自身不声明任何 GC 特性。</para>
/// </summary>
/// <remarks>
/// The harness exposes the job and the global configuration as two separate members, and BenchmarkDotNet's
/// <c>[Config]</c> takes a single <see cref="IConfig"/>, so this type is the join point.
/// <para>harness 把作业与全局配置暴露为两个独立成员，而 BenchmarkDotNet 的 <c>[Config]</c> 只接受一个
/// <see cref="IConfig"/>，因此本类型是二者的衔接点。</para>
/// Jobs accumulate rather than replace, so the default-GC versus Satori-GC comparison is expressed by calling
/// <see cref="ManualConfig.AddJob(Job[])"/> once per mode here: BenchmarkDotNet then runs every benchmark
/// method once per job and reports each as its own row, while the class keeps its single
/// <c>[Config]</c> attribute. A class-level <c>[Config]</c> merges with any assembly-level configuration
/// source instead of replacing it, so the jobs added here are additional to whatever the assembly declares.
/// <para>作业是累加而非替换，因此「默认 GC 对比 Satori GC」只需在此按模式各调用一次
/// <see cref="ManualConfig.AddJob(Job[])"/>：BenchmarkDotNet 随后会对每个基准方法按作业各运行一次，
/// 并把每次运行分别报告为独立行，而本类仍只保留一个 <c>[Config]</c> 特性。类级 <c>[Config]</c> 会与程序集级
/// 配置来源合并而非替换，因此此处添加的作业是在程序集所声明内容之上的追加。</para>
/// This class is the costliest in the project, so the number of stacked modes is bounded by runtime rather
/// than by the harness: the parameter space is 24 combinations, and each additional mode adds one full pass
/// over all of them.
/// <para>本类是项目中最昂贵的类，因此可堆叠的模式数量由运行时长而非 harness 决定：参数空间为 24 种组合，
/// 每增加一个模式就会对全部组合再完整跑一遍。</para>
/// </remarks>
public sealed class ErofsGcBaselineConfig : ManualConfig
{
    /// <summary>Creates the configuration by folding the harness globals and the baseline GC job together.
    /// <para>通过把 harness 全局配置与基线 GC 作业合并在一起来创建该配置。</para>
    /// </summary>
    public ErofsGcBaselineConfig()
    {
        var global = GcHarness.GlobalGcConfig();
        AddColumnProvider(global.GetColumnProviders().ToArray());
        AddLogger(global.GetLoggers().ToArray());
        AddExporter(global.GetExporters().ToArray());
        AddAnalyser(global.GetAnalysers().ToArray());
        AddValidator(global.GetValidators().ToArray());
        AddDiagnoser(global.GetDiagnosers().ToArray());
        AddJob(GcHarness.BaselineGc());
    }
}
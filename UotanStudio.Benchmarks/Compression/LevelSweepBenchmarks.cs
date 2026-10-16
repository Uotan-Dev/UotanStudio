using BenchmarkDotNet.Attributes;
using UotanStudio.Benchmarks.Inputs;
using UotanStudio.Compress;

namespace UotanStudio.Benchmarks.Compression;

/// <summary>
/// Sweeps compression settings through the public <see cref="CompressionService"/> entry points so the
/// speed-versus-ratio tradeoff of each codec is readable straight off one BenchmarkDotNet table.
/// <para>通过 <see cref="CompressionService"/> 公共入口扫描压缩设置，使每个编解码器的速度与压缩比权衡可直接从
/// 一张 BenchmarkDotNet 表读出。</para>
/// </summary>
/// <remarks>
/// Each benchmark method returns the produced byte count, which BenchmarkDotNet prints next to the elapsed
/// time; dividing it by the fixed 4 MiB plaintext gives the ratio without a second run.
/// <para>每个基准方法返回产出的字节数，BenchmarkDotNet 会将其与耗时并列打印；除以固定的 4 MiB 明文即得压缩比，
/// 无需第二次运行。</para>
/// <para>四组设置按轴拆成四个方法，而不是把四轴放进同一个 <c>[Params]</c> 集合：合并后组合数为
/// 11 × 19 × 2 = 418，超过每类 200 的组合上限，而拆分后每个方法各自在 200 以内。各组行数在方法
/// <c>&lt;remarks&gt;</c> 中写明。</para>
/// </remarks>
[MemoryDiagnoser]
public class LevelSweepBenchmarks
{
    /// <summary>
    /// Payload size the sweep runs on. The whole-buffer path splits input above 1 MiB into chunks when
    /// parallelism is requested, so 4 MiB gives four chunks and makes the parallel gain measurable.
    /// <para>本次扫描的载荷大小。整块路径在请求并行时把超过 1 MiB 的输入分块，因此 4 MiB 产生四块，使并行收益
    /// 可被度量。</para>
    /// </summary>
    private const int PayloadMebibytes = 4;

    /// <summary>
    /// XZ dictionary window used as the reference point; <see cref="CompressionOptions.DictionarySize"/>
    /// documents 8 MiB (<c>1 &lt;&lt; 23</c>) as the default this sweep varies around.
    /// <para>XZ 字典窗口的参照值；<see cref="CompressionOptions.DictionarySize"/> 文档写明默认值为 8 MiB
    /// （<c>1 &lt;&lt; 23</c>），本扫描在该值上下变化。</para>
    /// </summary>
    private const uint DefaultDictionarySize = 1u << 23;

    private byte[] _payload = [];

    /// <summary>
    /// Corpus class the settings rows are measured on.
    /// <para>设置各行所度量的语料类别。</para>
    /// </summary>
    /// <value>
    /// <c>text</c> (compressible) and <c>compressed</c> (incompressible), because a codec that wins on text can
    /// lose on high-entropy input where every level degenerates to a store. The axis is a string rather than the
    /// corpus <c>PayloadKind</c> enum because BenchmarkDotNet requires <see cref="ParamsAttribute"/> members to
    /// be public, and that enum is internal to this assembly.
    /// <para><c>text</c>（可压缩）与 <c>compressed</c>（不可压缩）：在文本上占优的编解码器，在各级别都退化为
    /// 直接存储的高熵输入上可能反而落后。该轴用字符串而非语料的 <c>PayloadKind</c> 枚举，因为
    /// BenchmarkDotNet 要求 <see cref="ParamsAttribute"/> 成员为 public，而该枚举在本程序集内为 internal。</para>
    /// </value>
    [Params("text", "compressed")]
    public string Corpus { get; set; } = "text";

    /// <summary>
    /// Copies the cached corpus buffer once into a private plaintext for the whole sweep.
    /// <para>一次性把缓存的语料缓冲复制为本扫描私有的明文。</para>
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _payload = BenchCorpus.Bytes(CodecBenchmarks.ResolveCorpus(Corpus), PayloadMebibytes).ToArray();
    }

    /// <summary>
    /// Formats whose unified 0-9 level band actually changes codec behaviour.
    /// <para>统一 0-9 等级带确实会改变编解码器行为的格式。</para>
    /// </summary>
    /// <value>
    /// gzip, zlib and deflate, the three formats routed through
    /// <c>CompressionLevelMapper.ToBclCompressionLevel</c> or <c>ToSharpCompressDeflate</c>, plus brotli,
    /// lz4 and zstd whose unified level is additionally swept over their native range below. lzma, xz, bzip2
    /// and lzop are absent because they ignore <see cref="CompressionOptions.Level"/> entirely, and lz4_legacy
    /// is absent because its level is exercised by the native lz4 sweep.
    /// <para>gzip、zlib、deflate 三种走 <c>CompressionLevelMapper.ToBclCompressionLevel</c> 或
    /// <c>ToSharpCompressDeflate</c> 的格式，外加 brotli、lz4、zstd——它们的统一等级还会在下方的原生范围中
    /// 另行扫描。lzma、xz、bzip2、lzop 不在此列，因为它们完全忽略 <see cref="CompressionOptions.Level"/>；
    /// lz4_legacy 也不在此列，其等级由原生 lz4 扫描覆盖。</para>
    /// </value>
    public static IEnumerable<CompressionFormat> UnifiedLevelFormats =>
    [
        CompressionFormat.Gzip,
        CompressionFormat.Zlib,
        CompressionFormat.Deflate,
        CompressionFormat.Brotli,
        CompressionFormat.Lz4,
        CompressionFormat.Zstd,
    ];

    /// <summary>
    /// The unified 0-9 band, sampled at every value at which the mapping changes behaviour.
    /// <para>统一 0-9 等级带，在每个映射行为发生变化的取值上取样。</para>
    /// </summary>
    /// <value>
    /// 0, 1, 4 and 9. The band is not sampled at all ten values on purpose: the mapping is a step function, so
    /// 0 maps to no compression, 1 through 3 all map to the fastest setting (and 1 additionally to brotli
    /// quality 1), 4 through 8 all map to the optimal setting (and to brotli quality 5), and 9 and above map
    /// to the smallest-size setting (and to brotli quality 11). Sampling every value would therefore report the
    /// same bucket several times under different labels. <see langword="null"/> is not a member of this set
    /// because it means "codec default" rather than a level, and it is measured by
    /// <see cref="CodecBenchmarks"/> instead.
    /// <para>0、1、4、9。故意不在全部十个取值上取样：该映射是阶跃函数——0 映射为不压缩，1 至 3 全部映射为最快设置
    /// （其中 1 对 brotli 另映射为质量 1），4 至 8 全部映射为最优设置（对 brotli 为质量 5），9 及以上映射为最小
    /// 体积设置（对 brotli 为质量 11）。逐值取样只会把同一个档位以不同标签重复报告数次。
    /// <see langword="null"/> 不属于本集合，因为它表示"编解码器默认值"而非某个等级，且已由
    /// <see cref="CodecBenchmarks"/> 度量。</para>
    /// </value>
    public static IEnumerable<int> UnifiedLevels => [0, 1, 4, 9];

    /// <summary>
    /// Brotli's native quality range 0-11, which the unified band collapses to four values.
    /// <para>Brotli 的原生质量范围 0-11，统一等级带把它压缩为四个值。</para>
    /// </summary>
    public static IEnumerable<int> BrotliQualities => Enumerable.Range(0, 12);

    /// <summary>
    /// LZ4's native level range 0-12, which the unified band collapses to four values.
    /// <para>LZ4 的原生等级范围 0-12，统一等级带把它压缩为四个值。</para>
    /// </summary>
    public static IEnumerable<int> Lz4Levels => Enumerable.Range(0, 13);

    /// <summary>
    /// Zstandard's native level range 1-19, which the unified band collapses to four values.
    /// <para>Zstandard 的原生等级范围 1-19，统一等级带把它压缩为四个值。</para>
    /// </summary>
    public static IEnumerable<int> ZstdLevels => Enumerable.Range(1, 19);

    /// <summary>
    /// XZ dictionary windows around the documented 8 MiB default: one smaller window, the default, and two
    /// larger windows.
    /// <para>围绕文档所载 8 MiB 默认值的 XZ 字典窗口：一个更小的窗口、默认值，以及两个更大的窗口。</para>
    /// </summary>
    /// <value>
    /// 1 MiB, 8 MiB, 32 MiB and 64 MiB. 64 MiB exceeds the 4 MiB payload, which is the interesting case: the
    /// encoder stops matching inside the payload and the window only costs setup and memory.
    /// <para>1 MiB、8 MiB、32 MiB 与 64 MiB。64 MiB 超过 4 MiB 的载荷，这是有意思的情形：编码器已无法在载荷内
    /// 匹配，窗口只带来初始化开销与内存占用。</para>
    /// </value>
    public static IEnumerable<uint> DictionarySizes => [1u << 20, DefaultDictionarySize, 1u << 25, 1u << 26];

    /// <summary>
    /// Measures the unified 0-9 level band across every format that honours it.
    /// <para>在所有遵循统一 0-9 等级带的格式上度量该等级带。</para>
    /// </summary>
    /// <remarks>
    /// Rows: 6 formats × 4 levels × 2 corpus classes = 48.
    /// <para>行数：6 格式 × 4 等级 × 2 语料类别 = 48。</para>
    /// </remarks>
    /// <returns>The produced compressed length in bytes. <para>产出的压缩字节数。</para></returns>
    [Benchmark]
    [ArgumentsSource(nameof(UnifiedLevelFormats))]
    public int UnifiedLevelBand(CompressionFormat format) =>
        CompressionService.Compress(_payload, format, new CompressionOptions { Level = UnifiedLevel }).Length;

    /// <summary>
    /// Brotli quality currently selected by <see cref="UnifiedLevelBand"/>.
    /// <para><see cref="UnifiedLevelBand"/> 当前选用的 Brotli 质量。</para>
    /// </summary>
    [ParamsSource(nameof(UnifiedLevels))]
    public int UnifiedLevel { get; set; }

    /// <summary>
    /// Measures Brotli's full native quality range 0-11, the signal the unified band loses.
    /// <para>度量 Brotli 完整的原生质量范围 0-11，即统一等级带丢失的信号。</para>
    /// </summary>
    /// <remarks>
    /// Rows: 12 qualities × 2 corpus classes = 24.
    /// <para>行数：12 个质量值 × 2 语料类别 = 24。</para>
    /// </remarks>
    /// <returns>The produced compressed length in bytes. <para>产出的压缩字节数。</para></returns>
    [Benchmark]
    public int BrotliNativeQuality() =>
        CompressionService.Compress(_payload, CompressionFormat.Brotli, new CompressionOptions { Level = BrotliQuality }).Length;

    /// <summary>
    /// Brotli quality this row sweeps.
    /// <para>本行扫描的 Brotli 质量。</para>
    /// </summary>
    [ParamsSource(nameof(BrotliQualities))]
    public int BrotliQuality { get; set; }

    /// <summary>
    /// Measures LZ4's full native level range 0-12 through the frame codec.
    /// <para>通过帧编解码器度量 LZ4 完整的原生等级范围 0-12。</para>
    /// </summary>
    /// <remarks>
    /// Rows: 13 levels × 2 corpus classes = 26.
    /// <para>行数：13 个等级 × 2 语料类别 = 26。</para>
    /// </remarks>
    /// <returns>The produced compressed length in bytes. <para>产出的压缩字节数。</para></returns>
    [Benchmark]
    public int Lz4NativeLevel() =>
        CompressionService.Compress(_payload, CompressionFormat.Lz4, new CompressionOptions { Level = Lz4Level }).Length;

    /// <summary>
    /// LZ4 level this row sweeps.
    /// <para>本行扫描的 LZ4 等级。</para>
    /// </summary>
    [ParamsSource(nameof(Lz4Levels))]
    public int Lz4Level { get; set; }

    /// <summary>
    /// Measures Zstandard's full native level range 1-19 sequentially, with parallelism left at its default.
    /// <para>在保持并行度默认值的情况下串行度量 Zstandard 完整的原生等级范围 1-19。</para>
    /// </summary>
    /// <remarks>
    /// Rows: 19 levels × 2 corpus classes = 38. The parallel counterpart is
    /// <see cref="ZstdParallelism"/>; the pair is what makes the chunked path's gain measurable.
    /// <para>行数：19 个等级 × 2 语料类别 = 38。其并行对照为 <see cref="ZstdParallelism"/>；两者成对才能度量分块
    /// 路径的收益。</para>
    /// </remarks>
    /// <returns>The produced compressed length in bytes. <para>产出的压缩字节数。</para></returns>
    [Benchmark]
    public int ZstdSequentialLevel() =>
        CompressionService.Compress(_payload, CompressionFormat.Zstd, new CompressionOptions { Level = ZstdLevel }).Length;

    /// <summary>
    /// Zstandard level this row sweeps.
    /// <para>本行扫描的 Zstandard 等级。</para>
    /// </summary>
    [ParamsSource(nameof(ZstdLevels))]
    public int ZstdLevel { get; set; }

    /// <summary>
    /// Measures the Zstandard chunked path against the sequential one at the same levels, which is the pair
    /// that shows whether chunking pays for its per-chunk encoder context.
    /// <para>在相同等级上把 Zstandard 分块路径与串行路径对比度量，这一对数据用于判断分块是否值得为每块单独的
    /// 编码器上下文付出代价。</para>
    /// </summary>
    /// <remarks>
    /// Rows: 3 levels × 2 degrees × 2 corpus classes = 12. Only the endpoints and the default level are swept
    /// because the degree, not the level, is the axis of interest here.
    /// <para>行数：3 个等级 × 2 个并行度 × 2 语料类别 = 12。只扫描端点与默认等级，因为此处的目标轴是并行度而非
    /// 等级。</para>
    /// </remarks>
    /// <returns>The produced compressed length in bytes. <para>产出的压缩字节数。</para></returns>
    [Benchmark]
    public int ZstdParallelism() =>
        CompressionService.Compress(_payload, CompressionFormat.Zstd, new CompressionOptions
        {
            Level = ZstdParallelismLevel,
            MaxDegreeOfParallelism = ZstdParallelismDegree,
        }).Length;

    /// <summary>
    /// Zstandard level used by the parallelism rows.
    /// <para>并行度各行所用的 Zstandard 等级。</para>
    /// </summary>
    /// <value>
    /// 1 (fastest native level), 3 (the codec default) and 19 (slowest and smallest), so the chunked path is
    /// compared against the sequential path at both ends of the range.
    /// <para>1（最快原生等级）、3（编解码器默认值）与 19（最慢、体积最小），使分块路径在范围两端都与串行路径
    /// 比较。</para>
    /// </value>
    [Params(1, 3, 19)]
    public int ZstdParallelismLevel { get; set; }

    /// <summary>
    /// Worker count used by the parallelism rows.
    /// <para>并行度各行所用的工作线程数。</para>
    /// </summary>
    /// <value>
    /// Read from <see cref="ParallelismDegrees"/>, which is 1 (the sequential path) plus
    /// <see cref="Environment.ProcessorCount"/> for this machine. The chunked path caps the request at the
    /// chunk count, so a value above four is not observable on the 4 MiB payload; the full degree is still
    /// requested so the parameter column records the machine the run actually used.
    /// <para>取自 <see cref="ParallelismDegrees"/>，即 1（串行路径）加上本机的
    /// <see cref="Environment.ProcessorCount"/>。分块路径会以块数量为上取整该请求，因此在 4 MiB 载荷上超过 4 的
    /// 取值不可观测；此处仍请求完整并行度，使参数列记录该次运行实际使用的机器。</para>
    /// </value>
    [ParamsSource(nameof(ParallelismDegrees))]
    public int ZstdParallelismDegree { get; set; }

    /// <summary>
    /// Measures the XZ dictionary window at and around its documented 8 MiB default.
    /// <para>在其文档所载 8 MiB 默认值及其上下度量 XZ 字典窗口。</para>
    /// </summary>
    /// <remarks>
    /// Rows: 4 windows × 2 corpus classes = 8.
    /// <para>行数：4 个窗口 × 2 语料类别 = 8。</para>
    /// </remarks>
    /// <returns>The produced compressed length in bytes. <para>产出的压缩字节数。</para></returns>
    [Benchmark]
    public int XzDictionarySize() =>
        CompressionService.Compress(_payload, CompressionFormat.Xz, new CompressionOptions { DictionarySize = XzDictionarySizeValue }).Length;

    /// <summary>
    /// XZ dictionary window this row sweeps.
    /// <para>本行扫描的 XZ 字典窗口。</para>
    /// </summary>
    [ParamsSource(nameof(DictionarySizes))]
    public uint XzDictionarySizeValue { get; set; }

    /// <summary>
    /// Measures the XZ chunked path against the sequential one; XZ is the other format whose encoder accepts
    /// a worker bound through <see cref="CompressionOptions.MaxDegreeOfParallelism"/>.
    /// <para>把 XZ 分块路径与串行路径对比度量；XZ 是另一个其编码器接受
    /// <see cref="CompressionOptions.MaxDegreeOfParallelism"/> 工作线程上限的格式。</para>
    /// </summary>
    /// <remarks>
    /// Rows: 2 degrees × 2 corpus classes = 4. The dictionary window is pinned to the documented default so
    /// the degree is the only varying axis.
    /// <para>行数：2 个并行度 × 2 语料类别 = 4。字典窗口固定为文档默认值，使并行度成为唯一变化轴。</para>
    /// </remarks>
    /// <returns>The produced compressed length in bytes. <para>产出的压缩字节数。</para></returns>
    [Benchmark]
    public int XzParallelism() =>
        CompressionService.Compress(_payload, CompressionFormat.Xz, new CompressionOptions
        {
            DictionarySize = DefaultDictionarySize,
            MaxDegreeOfParallelism = XzParallelismDegree,
        }).Length;

    /// <summary>
    /// Worker count used by the XZ parallelism rows.
    /// <para>XZ 并行度各行所用的工作线程数。</para>
    /// </summary>
    /// <value>
    /// The same pair as <see cref="ZstdParallelismDegree"/>, so the two formats' parallel gain is comparable.
    /// <para>与 <see cref="ZstdParallelismDegree"/> 相同的取值对，使两种格式的并行收益可比。</para>
    /// </value>
    [ParamsSource(nameof(ParallelismDegrees))]
    public int XzParallelismDegree { get; set; }

    /// <summary>
    /// The two worker counts every parallelism axis is swept across: the sequential path, and this machine's
    /// full degree.
    /// <para>每条并行度轴所用的两个工作线程数：串行路径，以及本机的完整并行度。</para>
    /// </summary>
    /// <value>
    /// 1 and <see cref="Environment.ProcessorCount"/>. This is a source rather than a literal
    /// <see cref="ParamsAttribute"/> because attribute arguments must be compile-time constants, and hardcoding
    /// a processor count would make the reported parameter column unrelated to the machine that ran it.
    /// <para>1 与 <see cref="Environment.ProcessorCount"/>。此处用参数源而非字面
    /// <see cref="ParamsAttribute"/>，因为特性实参必须是编译期常量，而硬编码处理器数量会使报告出的参数列与实际
    /// 运行机器无关。</para>
    /// </value>
    public static IEnumerable<int> ParallelismDegrees => [1, Environment.ProcessorCount];
}

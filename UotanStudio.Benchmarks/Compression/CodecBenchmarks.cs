using BenchmarkDotNet.Attributes;
using UotanStudio.Benchmarks.Inputs;
using UotanStudio.Compress;

namespace UotanStudio.Benchmarks.Compression;

/// <summary>
/// Measures every compressible codec in the <see cref="CompressionFormats"/> registry through the public
/// <see cref="CompressionService"/> entry points, on both compressible and incompressible corpus input, so
/// codec ranking by throughput and by produced ratio can be read from one run.
/// <para>通过 <see cref="CompressionService"/> 公共入口度量 <see cref="CompressionFormats"/> 注册表中每个可压缩
/// 编解码器，覆盖可压缩与不可压缩语料，使编解码器的吞吐排名与产出比可在一次运行中读出。</para>
/// </summary>
/// <remarks>
/// The type is data-driven: the format list is an <see cref="ArgumentsSourceAttribute"/> built by filtering
/// <see cref="CompressionFormats.All"/> on <see cref="CompressionFormatInfo.CanCompress"/>, so registering a
/// thirteenth codec adds rows here without touching this file. Both buffer entry points are measured on the
/// same payload and options, which makes the whole-buffer versus streaming buffering cost a visible delta.
/// <para>该类型是数据驱动的：格式列表由 <see cref="ArgumentsSourceAttribute"/> 按
/// <see cref="CompressionFormatInfo.CanCompress"/> 过滤 <see cref="CompressionFormats.All"/> 生成，因此注册第
/// 十三个编解码器时无需改动本文件即可增加行。两个缓冲入口在同一载荷与选项上度量，使整块与流式之间的缓冲
/// 开销成为可见差值。</para>
/// <para>GC 配置由 <c>Infrastructure</c> 中唯一的 <c>IConfigSource</c> 在程序集范围内提供，因此本类不声明
/// <c>[Config]</c>，也不声明任何定义任务级别的特性——类级任务特性会覆盖共享配置的任务，使本类在后续 GC 对比中
/// 被静默排除。</para>
/// </remarks>
[MemoryDiagnoser]
public class CodecBenchmarks
{
    /// <summary>
    /// Payload size every codec is measured on; 4 MiB keeps the full matrix inside a few minutes while staying
    /// well above the 1 MiB chunk boundary the whole-buffer path uses for optional chunking.
    /// <para>每个编解码器度量所用的载荷大小；4 MiB 使整个参数矩阵在数分钟内跑完，同时远高于整块路径做可选分块
    /// 时使用的 1 MiB 边界。</para>
    /// </summary>
    private const int PayloadMebibytes = 4;

    private byte[] _payload = [];
    private byte[] _encoded = [];

    /// <summary>
    /// Corpus class this row is measured on, covering compressible and incompressible extremes.
    /// <para>该行所度量的语料类别，覆盖可压缩与不可压缩两个极端。</para>
    /// </summary>
    /// <value>
    /// One of the corpus kind names accepted by <see cref="BenchCorpus.Bytes"/>: <c>text</c>, <c>executable</c>,
    /// <c>compressed</c> and <c>zero</c>. The axis is a string rather than the corpus
    /// <c>PayloadKind</c> enum because BenchmarkDotNet requires <see cref="ParamsAttribute"/> members to be
    /// public, and that enum is internal to this assembly, so a public property cannot expose it.
    /// <para><see cref="BenchCorpus.Bytes"/> 接受的语料类别名之一：<c>text</c>、<c>executable</c>、
    /// <c>compressed</c> 与 <c>zero</c>。该轴用字符串而非语料的 <c>PayloadKind</c> 枚举，因为
    /// BenchmarkDotNet 要求 <see cref="ParamsAttribute"/> 成员为 public，而该枚举在本程序集内为 internal，
    /// 公共属性无法暴露它。</para>
    /// </value>
    [Params("text", "executable", "compressed", "zero")]
    public string Corpus { get; set; } = "text";

    /// <summary>
    /// Codec under measurement, enumerated from the format registry.
    /// <para>被度量的编解码器，取自格式注册表。</para>
    /// </summary>
    [ParamsSource(nameof(CompressibleFormats))]
    public CompressionFormat Format { get; set; }

    /// <summary>
    /// The codecs that can actually compress, read from the registry rather than hand-listed.
    /// <para>真正具备压缩能力的编解码器，取自注册表而非手工罗列。</para>
    /// </summary>
    /// <value>
    /// Every registered format whose <see cref="CompressionFormatInfo.CanCompress"/> is true, with
    /// <see cref="CompressionFormat.None"/> excluded because it is a passthrough rather than a codec.
    /// <para>所有 <see cref="CompressionFormatInfo.CanCompress"/> 为 true 的注册格式；排除
    /// <see cref="CompressionFormat.None"/>，因为它只是直通而非编解码器。</para>
    /// </value>
    public static IEnumerable<CompressionFormat> CompressibleFormats =>
        CompressionFormats.All
            .Where(info => info.CanCompress && info.Format != CompressionFormat.None)
            .Select(info => info.Format);

    /// <summary>
    /// Copies the cached corpus buffer once, so the codec under test receives a private, stable plaintext.
    /// <para>一次性复制缓存的语料缓冲，使被测编解码器拿到私有且稳定的明文。</para>
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _payload = BenchCorpus.Bytes(ResolveCorpus(Corpus), PayloadMebibytes).ToArray();
        _encoded = CompressionService.Compress(_payload, Format);
    }

    /// <summary>
    /// Maps the public corpus parameter onto the internal corpus kind.
    /// <para>把公开的语料参数映射到内部语料类别。</para>
    /// </summary>
    /// <param name="corpus">The <see cref="Corpus"/> parameter value. <para><see cref="Corpus"/> 参数值。</para></param>
    /// <returns>The corpus kind to read. <para>要读取的语料类别。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The name is not one this class declares.
    /// <para>该名称不是本类声明的取值之一。</para></exception>
    internal static PayloadKind ResolveCorpus(string corpus) => corpus switch
    {
        "text" => PayloadKind.Text,
        "executable" => PayloadKind.Executable,
        "compressed" => PayloadKind.AlreadyCompressed,
        "zero" => PayloadKind.ZeroFill,
        _ => throw new ArgumentOutOfRangeException(nameof(corpus), corpus, "Unknown corpus kind."),
    };

    /// <summary>
    /// Measures the whole-buffer compress entry point with the codec's default options.
    /// <para>用编解码器默认选项度量整块压缩入口。</para>
    /// </summary>
    /// <returns>The produced compressed length in bytes, which paired with the payload size gives the ratio.
    /// <para>产出的压缩字节数；与载荷大小相除即得压缩比。</para></returns>
    [Benchmark]
    public int CompressBuffer() => CompressionService.Compress(_payload, Format).Length;

    /// <summary>
    /// Measures the streaming compress entry point over a <see cref="MemoryStream"/> pair with the same payload
    /// and default options, so the delta against <see cref="CompressBuffer"/> is the buffering cost.
    /// <para>在 <see cref="MemoryStream"/> 对上用相同载荷与默认选项度量流式压缩入口，其与
    /// <see cref="CompressBuffer"/> 的差值即为缓冲开销。</para>
    /// </summary>
    /// <returns>The produced compressed length in bytes. <para>产出的压缩字节数。</para></returns>
    [Benchmark]
    public long CompressStreaming()
    {
        using var input = new MemoryStream(_payload, writable: false);
        using var output = new MemoryStream();
        CompressionService.Compress(input, output, Format);
        return output.Length;
    }

    /// <summary>
    /// Measures the incremental encoder returned by <see cref="CompressionService.CreateCompressionStream"/>,
    /// which is the shape callers use when the plaintext arrives in pieces.
    /// <para>度量 <see cref="CompressionService.CreateCompressionStream"/> 返回的增量编码器，即明文分片到达时
    /// 调用方使用的形态。</para>
    /// </summary>
    /// <returns>The produced compressed length in bytes. <para>产出的压缩字节数。</para></returns>
    [Benchmark]
    public long CompressIncrementalWriter()
    {
        using var output = new MemoryStream();
        using (var writer = CompressionService.CreateCompressionStream(output, Format))
        {
            writer.Write(_payload, 0, _payload.Length);
        }

        return output.Length;
    }

    /// <summary>
    /// Measures the whole-buffer decompress entry point against a payload this codec produced at setup, so the
    /// decode side is ranked on bytes a real encoder emitted rather than on a synthetic frame.
    /// <para>对本次 setup 中由该编解码器产出的数据度量整块解压入口，使解码侧基于真实编码器输出的字节而非合成帧
    /// 进行排名。</para>
    /// </summary>
    /// <returns>The produced plaintext length in bytes, which equals the source payload length.
    /// <para>产出的明文字节数，与源载荷长度相等。</para></returns>
    [Benchmark]
    public int DecompressBuffer() => CompressionService.Decompress(_encoded, Format).Length;

    /// <summary>
    /// Measures the streaming decompress entry point, the bounded-copy overload the production extraction
    /// paths call.
    /// <para>度量流式解压入口，即生产解压路径所用的带上限拷贝重载。</para>
    /// </summary>
    /// <returns>The produced plaintext length in bytes. <para>产出的明文字节数。</para></returns>
    [Benchmark]
    public long DecompressStreaming()
    {
        using var input = new MemoryStream(_encoded, writable: false);
        using var output = new MemoryStream(_payload.Length);
        CompressionService.Decompress(input, output, Format, long.MaxValue, CancellationToken.None);
        return output.Length;
    }
}

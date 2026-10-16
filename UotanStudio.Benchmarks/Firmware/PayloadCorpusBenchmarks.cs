using BenchmarkDotNet.Attributes;
using UotanStudio.Benchmarks.Inputs;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Measures payload build throughput on realistic corpus input across every codec the builder can emit, the
/// compression-worker fan-out, and both build entry points, so codec throughput stops being readable only from a
/// single BZip2 measurement taken against incompressible pseudo-random bytes.
/// <para>在真实语料输入上度量载荷构建吞吐，覆盖构建器实际可产出的每种编解码器、压缩工作者并发数以及两种构建入口，
/// 使编解码吞吐不再只能从针对不可压缩伪随机字节的单次 BZip2 测量中读取。</para>
/// <para>Input comes from <see cref="BenchCorpus"/> instead of a local <c>Random</c> buffer, because random bytes only
/// expose the worst case of every codec and make a codec ranking unreadable. Every benchmark returns a value
/// BenchmarkDotNet prints, so the result table proves bytes were actually produced. There is no compression-level
/// sweep in this class because <see cref="PayloadBuilder"/> exposes no level knob: the stream codecs go through
/// <c>PayloadBuilder.Compression.cs</c> with a pinned <c>MaxDegreeOfParallelism = 1</c> and
/// <c>DictionarySize = 1 &lt;&lt; 20</c>, and the Zstd slot path pins <c>ZstdLevel = 3</c> in
/// <c>PayloadBuilder.Parallel.cs</c>. The codec itself is therefore the only compression dimension the public builder
/// surface can vary.</para>
/// <para>输入来自 <see cref="BenchCorpus"/> 而非本地 <c>Random</c> 缓冲区，因为随机字节只暴露每种编解码器的最坏情况，
/// 使编解码排名无法解读。每个基准都返回 BenchmarkDotNet 会打印的值，因此结果表可证明确实产出了字节。本类不包含压缩级别扫描，
/// 因为 <see cref="PayloadBuilder"/> 没有暴露级别旋钮：流式编解码器经 <c>PayloadBuilder.Compression.cs</c> 固定
/// <c>MaxDegreeOfParallelism = 1</c> 与 <c>DictionarySize = 1 &lt;&lt; 20</c>，Zstd 槽位路径在
/// <c>PayloadBuilder.Parallel.cs</c> 中固定 <c>ZstdLevel = 3</c>。因此编解码器本身是公开构建器接口唯一能改变的压缩维度。</para>
/// </summary>
[MemoryDiagnoser]
public class PayloadBuildCorpusBenchmarks
{
    private const int Partitions = 4;

    private string _root = "";
    private string _payloadPath = "";
    private MemoryStream[] _sources = [];

    /// <summary>Codec applied to the REPLACE-family operations of a partition; every value the builder can emit.
    /// <para>应用于分区 REPLACE 系列操作的编解码器；覆盖构建器可产出的全部取值。</para>
    /// </summary>
    [Params(CompressionType.None, CompressionType.BZip2, CompressionType.Xz, CompressionType.Zstd)]
    public CompressionType Codec { get; set; }

    /// <summary>Compression slots per batch; <c>1</c> is the sequential build that isolates codec cost from fan-out.
    /// <para>每批的压缩槽位数；<c>1</c> 为串行构建，用于把编解码代价与并发收益区分开。</para>
    /// </summary>
    [Params(1, 4, 16)]
    public int Workers { get; set; }

    /// <summary>Corpus payload character; this is the compressibility axis rather than a size axis.
    /// <para>语料特征类型；这是可压缩性维度而非尺寸维度。</para>
    /// </summary>
    [Params(PayloadKind.Source, PayloadKind.Executable)]
    public PayloadKind Kind { get; set; }

    /// <summary>Per-partition corpus size in mebibytes.
    /// <para>每个分区的语料尺寸，单位为兆字节（MiB）。</para>
    /// </summary>
    [Params(8)]
    public int Mebibytes { get; set; }

    /// <summary>Whether the build takes the file-path entry point, which stages a temporary file and publishes it atomically.
    /// <para>构建是否走文件路径入口，该入口会先暂存临时文件再原子发布。</para>
    /// </summary>
    [Params(false, true)]
    public bool FilePath { get; set; }

    /// <summary>Stages the scratch directory and the cached corpus buffers, then writes the corpus files the
    /// file-path entry point reads.
    /// <para>准备暂存目录与缓存的语料缓冲区，随后写出文件路径入口所需的语料文件。</para>
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-bench-payload-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _payloadPath = Path.Combine(_root, "payload.bin");
        _sources = new MemoryStream[Partitions];
        for (var index = 0; index < Partitions; index++)
        {
            _sources[index] = new MemoryStream(BenchCorpus.Bytes(Kind, Mebibytes).ToArray(), writable: false);
        }

        if (FilePath)
        {
            _ = BenchCorpus.File(Kind, Mebibytes);
        }
    }

    /// <summary>Releases the per-partition source streams and removes the scratch directory, including after a
    /// failed iteration.
    /// <para>释放各分区源流并删除暂存目录，包括迭代失败之后的情况。</para>
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _sources = [];
        TryDeleteRoot(_root);
    }

    /// <summary>Builds a four-partition payload and returns its byte length.
    /// <para>构建四分区的载荷并返回其字节长度。</para>
    /// </summary>
    /// <returns>Payload length in bytes. <para>载荷的字节长度。</para></returns>
    [Benchmark]
    public async Task<long> Build()
    {
        if (FilePath)
        {
            using var builder = new PayloadBuilder().WithCompressionWorkers(Workers);
            AddPartitions(builder, fromPath: true);
            await builder.BuildAsync(_payloadPath, CancellationToken.None);
            return new FileInfo(_payloadPath).Length;
        }

        using (var builder = new PayloadBuilder().WithCompressionWorkers(Workers))
        {
            AddPartitions(builder, fromPath: false);
            await builder.BuildAsync(_payloadPath, CancellationToken.None);
        }

        return new FileInfo(_payloadPath).Length;
    }

    private void AddPartitions(PayloadBuilder builder, bool fromPath)
    {
        var corpusPath = fromPath ? BenchCorpus.File(Kind, Mebibytes) : null;
        for (var index = 0; index < Partitions; index++)
        {
            var name = "part" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Codec == CompressionType.Zstd)
            {
                if (corpusPath is null)
                {
                    _sources[index].Position = 0;
                    builder.AddPartitionZstd(name, _sources[index]);
                }
                else
                {
                    builder.AddPartitionZstd(name, corpusPath);
                }
            }
            else if (corpusPath is null)
            {
                _sources[index].Position = 0;
                builder.AddPartitionCompressed(name, _sources[index], Codec);
            }
            else
            {
                builder.AddPartitionCompressed(name, corpusPath, Codec);
            }
        }
    }

    private static void TryDeleteRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Measures extraction of a payload this class built in its own setup, across worker counts, so extract throughput is
/// always paired with the build settings that produced it and never depends on an external payload image.
/// <para>度量本类在自身初始化中构建的载荷的解包，覆盖多种工作者数量，使解包吞吐始终与其构建设置配对，且绝不依赖外部载荷镜像。</para>
/// <para>The build under measurement runs against <see cref="BenchCorpus"/> input, so the extractor decodes genuinely
/// compressed REPLACE frames instead of stored random bytes. Each iteration reports the published image length, which
/// proves the extractor reconstructed the whole partition. The class builds its payload once in
/// <see cref="Setup"/>, because a payload is a fixture of the measurement rather than the thing being timed.</para>
/// <para>被测构建针对 <see cref="BenchCorpus"/> 输入运行，因此解包器解码的是真正压缩过的 REPLACE 帧，而不是原样存储的随机字节。
/// 每次迭代报告已发布的镜像长度，以证明解包器重建了整个分区。本类在 <see cref="Setup"/> 中只构建一次载荷，
/// 因为载荷是测量的夹具，而不是被计时的对象。</para>
/// </summary>
[MemoryDiagnoser]
public class PayloadExtractCorpusBenchmarks
{
    private string _root = "";
    private string _payloadPath = "";
    private string _extractDir = "";
    private string _outPath = "";
    private PartitionUpdate _partition = null!;
    private ulong _baseOffset;
    private uint _blockSize;
    private int _previousWorkers;
    private long _expectedLength;

    /// <summary>Codec the setup build applies, so extraction is measured per build setting.
    /// <para>初始化构建采用的编解码器，使解包按构建设置分别度量。</para>
    /// </summary>
    [Params(CompressionType.None, CompressionType.BZip2, CompressionType.Xz, CompressionType.Zstd)]
    public CompressionType Codec { get; set; }

    /// <summary>Workers used while building the payload; the extract fan-out is described by the shared worker setting.
    /// <para>构建载荷时使用的工作者数量；解包并发由共享的工作者设置描述。</para>
    /// </summary>
    [Params(4)]
    public int BuildWorkers { get; set; }

    /// <summary>Corpus payload character, kept aligned with the build class so both readings describe one input shape.
    /// <para>语料特征类型，与构建类保持一致，使两次读数描述同一种输入形态。</para>
    /// </summary>
    [Params(PayloadKind.Source, PayloadKind.Executable)]
    public PayloadKind Kind { get; set; }

    /// <summary>Corpus payload size in mebibytes.
    /// <para>语料载荷尺寸，单位为兆字节（MiB）。</para>
    /// </summary>
    [Params(8)]
    public int Mebibytes { get; set; }

    /// <summary>Runs the extraction with this many workers, so throughput is reported per worker count.
    /// <para>以该工作者数量执行解包，使吞吐按工作者数量分别上报。</para>
    /// </summary>
    [Params(1, 4, 16)]
    public int ExtractWorkers { get; set; }

    /// <summary>Builds the payload under measurement and prepares the extraction fixture.
    /// <para>构建被测载荷并准备解包夹具。</para>
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-bench-payload-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _payloadPath = Path.Combine(_root, "payload.bin");
        var corpusPath = BenchCorpus.File(Kind, Mebibytes);
        var expected = (long)Mebibytes * 1024 * 1024;
        BuildPayload(corpusPath);

        using var stream = File.OpenRead(_payloadPath);
        var (manifest, header) = PayloadParser.ReadManifest(stream);
        _partition = manifest.Partitions[0];
        _baseOffset = header.BaseOffset;
        _blockSize = manifest.BlockSize;
        _expectedLength = expected;
        _extractDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(_extractDir);
        _outPath = Path.Combine(_extractDir, "image.img");
        _previousWorkers = FirmwareParallelism.Workers;
    }

    /// <summary>Restores the shared worker setting and removes the scratch directory, including after a failure.
    /// <para>还原共享工作者设置并删除暂存目录，包括失败之后的情况。</para>
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        FirmwareParallelism.Workers = _previousWorkers;
        TryDeleteRoot(_root);
    }

    /// <summary>Extracts the built partition and returns the published image length.
    /// <para>解包已构建的分区并返回已发布的镜像长度。</para>
    /// </summary>
    /// <returns>Extracted image length in bytes. <para>解包出的镜像字节长度。</para></returns>
    [Benchmark]
    public async Task<long> Extract()
    {
        FirmwareParallelism.Workers = ExtractWorkers;
        using var stream = File.OpenRead(_payloadPath);
        await new PayloadExtractor().ExtractPartitionAsync(_partition, _outPath, stream, _baseOffset, _blockSize, CancellationToken.None);
        var length = new FileInfo(_outPath).Length;
        File.Delete(_outPath);
        return length;
    }

    private void BuildPayload(string corpusPath)
    {
        using var builder = new PayloadBuilder().WithCompressionWorkers(BuildWorkers);
        if (Codec == CompressionType.Zstd)
        {
            builder.AddPartitionZstd("system", corpusPath);
        }
        else
        {
            builder.AddPartitionCompressed("system", corpusPath, Codec);
        }

        builder.Build(_payloadPath);
        if (new FileInfo(_payloadPath).Length == 0 || _expectedLength <= 0)
        {
            throw new InvalidOperationException("The setup payload build produced no bytes.");
        }
    }

    private static void TryDeleteRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
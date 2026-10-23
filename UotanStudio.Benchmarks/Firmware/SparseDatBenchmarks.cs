using BenchmarkDotNet.Attributes;
using UotanStudio.Benchmarks.Inputs;
using UotanStudio.Compress;
using UotanStudio.Compress.Files;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.OtaDelta.Dat;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Measures Android sparse image I/O from both ends: raw to sparse, sparse to raw, and the two
/// top-scoring primitives <see cref="SparseReader.FromStream"/> and
/// <see cref="SparseFile.WriteRawToStream"/> that the repository's hot-path scan ranks above every
/// other production entry point.
/// <para>度量 Android 稀疏镜像读写的两端：原始转稀疏、稀疏转原始，以及仓库热点扫描中排名高于所有其他生产入口的
/// 两个原语 <see cref="SparseReader.FromStream"/> 与 <see cref="SparseFile.WriteRawToStream"/>。</para>
/// <para>Chunk count rather than byte count drives the index cost, so the data character is swept
/// alongside the image size: a <see cref="PayloadKind.ZeroFill"/> image classifies into a handful of
/// runs while a <see cref="PayloadKind.Text"/> image of the same length emits one chunk per run.
/// <see cref="PayloadKind.AlreadyCompressed"/> and <see cref="PayloadKind.Executable"/> cover the
/// incompressible and the mixed-but-structured cases between those extremes.</para>
/// <para>索引开销由块数量而非字节数决定，因此数据特征与镜像尺寸一并扫描：全零镜像被归入极少数区段，而同样长度的文本镜像
/// 每个区段都会产生一个块。<see cref="PayloadKind.AlreadyCompressed"/> 与 <see cref="PayloadKind.Executable"/>
/// 覆盖这两个极端之间不可压缩与混合但结构化的情形。</para>
/// <para>Param matrix: 4 kinds x 2 sizes x 3 methods = 24 cases, plus the 8-case reader primitives
/// class, for 32 total in this file's sparse half.</para>
/// <para>参数矩阵：4 种特征 × 2 种尺寸 × 3 个方法 = 24 个用例，加上 8 个用例的读取器原语组，本文件稀疏部分共 32 个。</para>
/// </summary>
/// <remarks>
/// <see cref="SparseImageConverter.ConvertRawToSparseCancellable"/> is private and its owning class
/// exposes no cancellable raw-to-sparse entry point, so the conversion benchmarks call the public
/// <see cref="SparseImageConverter.ConvertRawToSparse(Stream, Stream, uint, long?, SparseWriteOptions?)"/>
/// and <see cref="SparseImageConverter.ConvertSparseToRawCancellable"/> overloads instead.
/// <para><see cref="SparseImageConverter.ConvertRawToSparseCancellable"/> 为私有，且其所属类型未公开任何可取消的
/// 原始转稀疏入口，因此转换基准改用公开的
/// <see cref="SparseImageConverter.ConvertRawToSparse(Stream, Stream, uint, long?, SparseWriteOptions?)"/>
/// 与 <see cref="SparseImageConverter.ConvertSparseToRawCancellable"/> 重载。</para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class SparseImageBenchmarks
{
    private const uint BlockSize = 4096;

    private byte[] _raw = [];
    private string _rawPath = string.Empty;
    private string _sparsePath = string.Empty;
    private string _scratchRoot = string.Empty;

    /// <summary>
    /// Gets or sets the payload character the raw input image is generated from, which fixes how many
    /// sparse chunks the shape produces. The axis is a string rather than the corpus
    /// <see cref="PayloadKind"/> enum because <see cref="ParamsAttribute"/> members must be public and
    /// that enum is internal to this assembly, so a public property cannot expose it.
    /// <para>获取或设置生成原始输入镜像所依据的载荷特征，它决定该形态会产生多少稀疏块。该轴用字符串而非语料的
    /// <see cref="PayloadKind"/> 枚举，因为 <see cref="ParamsAttribute"/> 成员必须为 public，而该枚举在本程序集内
    /// 为 internal，公开属性无法暴露它。</para>
    /// </summary>
    [Params("zero", "text", "executable", "compressed")]
    public string Corpus { get; set; } = "text";

    /// <summary>
    /// Gets or sets the input image size in mebibytes.
    /// <para>获取或设置输入镜像的尺寸，单位为兆字节（MiB）。</para>
    /// </summary>
    [Params(8, 32)]
    public int ImageMebibytes { get; set; }

    /// <summary>
    /// Materializes the cached corpus payload and the scratch files shared by this parameter set, and
    /// converts the raw image to sparse once so every benchmark starts from the same artifacts.
    /// <para>实体化缓存的语料载荷与本参数组共用的暂存文件，并预先完成一次原始转稀疏，使每个基准都从相同产物开始。</para>
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _raw = BenchCorpus.Bytes(ResolveCorpus(Corpus), ImageMebibytes).ToArray();
        _scratchRoot = Path.Combine(Path.GetTempPath(), "uotan-bench-sparse-" + Guid.NewGuid().ToString("N"));
        ScratchCleanup.Register(_scratchRoot);
        Directory.CreateDirectory(_scratchRoot);
        _rawPath = Path.Combine(_scratchRoot, "input.img");
        File.WriteAllBytes(_rawPath, _raw);
        _sparsePath = Path.Combine(_scratchRoot, "reference.sparse.img");
        SparseImageConverter.ConvertRawToSparse(_rawPath, _sparsePath, BlockSize);
    }

    /// <summary>
    /// Deletes every file and directory this benchmark type created, including after a failed setup.
    /// <para>删除本基准类型创建的全部文件与目录，包括设置失败之后的情况。</para>
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        ScratchCleanup.Release(ref _scratchRoot);
        ScratchCleanup.Sweep();
    }

    /// <summary>
    /// Produces the sparse container from a raw image and reports the produced sparse size, which
    /// proves the classification and every chunk write actually ran.
    /// <para>由原始镜像生成稀疏容器，并报告生成的稀疏文件大小，以此证明分类与全部块写入确实执行过。</para>
    /// </summary>
    /// <returns>The sparse image length in bytes. <para>稀疏镜像的字节长度。</para></returns>
    [Benchmark(Description = "Sparse_ConvertRawToSparse")]
    public long PackRawToSparse()
    {
        using var input = new FileStream(_rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var output = new FileStream(_sparsePath + ".out", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        SparseImageConverter.ConvertRawToSparse(input, output, BlockSize);
        return output.Length;
    }

    /// <summary>
    /// Expands the sparse container back to a raw image and reports the expanded length, which proves
    /// the whole chunk table was walked and every RAW / FILL / don't-care run was materialized.
    /// <para>将稀疏容器重新展开为原始镜像并报告展开后的长度，以此证明整张块表被遍历且每个 RAW／FILL／don't-care
    /// 区段都被实体化。</para>
    /// </summary>
    /// <returns>The expanded raw image length in bytes. <para>展开后原始镜像的字节长度。</para></returns>
    [Benchmark(Description = "Sparse_ExpandToRaw")]
    public long UnpackSparseToRaw()
    {
        using var sparse = SparseReader.FromImageFile(_sparsePath);
        using var output = new MemoryStream(_raw.Length);
        sparse.WriteRawToStream(output);
        return output.Length;
    }

    /// <summary>
    /// Parses the sparse container through <see cref="SparseReader.FromStream"/> with checksum
    /// validation on, so both the chunk-table walk and the expanded CRC32 pass are timed.
    /// <para>在开启校验和校验的情况下经 <see cref="SparseReader.FromStream"/> 解析稀疏容器，因此块表遍历与展开后
    /// CRC32 趟都会被计时。</para>
    /// </summary>
    /// <returns>The number of chunks the parsed index holds. <para>解析出的索引所包含的块数量。</para></returns>
    [Benchmark(Description = "Sparse_ReaderFromStream_ValidateCrc")]
    public int OpenWithChecksum()
    {
        using var stream = new FileStream(_sparsePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var sparse = SparseReader.FromStream(stream, validateCrc: true);
        return sparse.Chunks.Count;
    }

    /// <summary>
    /// Parses the sparse container through <see cref="SparseReader.FromStream"/> without checksum
    /// validation, isolating the index cost from the expanded CRC32 cost measured by
    /// <see cref="OpenWithChecksum"/>.
    /// <para>在不进行校验和校验的情况下经 <see cref="SparseReader.FromStream"/> 解析稀疏容器，从而把索引开销与
    /// <see cref="OpenWithChecksum"/> 所度量的展开 CRC32 开销分离。</para>
    /// </summary>
    /// <returns>The number of chunks the parsed index holds. <para>解析出的索引所包含的块数量。</para></returns>
    [Benchmark(Description = "Sparse_ReaderFromStream_NoCrc")]
    public int OpenWithoutChecksum()
    {
        using var stream = new FileStream(_sparsePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var sparse = SparseReader.FromStream(stream, validateCrc: false);
        return sparse.Chunks.Count;
    }

    /// <summary>
    /// Maps the public corpus parameter onto the internal corpus kind shared with the DAT class.
    /// <para>把公开的语料参数映射为与 DAT 类共用的内部语料特征类型。</para>
    /// </summary>
    /// <param name="corpus">One of the accepted corpus names. <para>可接受的语料名称之一。</para></param>
    /// <returns>The matching internal corpus kind. <para>对应的内部语料特征类型。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="corpus"/> is not a known name.
    /// <para><paramref name="corpus"/> 不是已知名称。</para></exception>
    internal static PayloadKind ResolveCorpus(string corpus) => corpus switch
    {
        "zero" => PayloadKind.ZeroFill,
        "text" => PayloadKind.Text,
        "executable" => PayloadKind.Executable,
        "compressed" => PayloadKind.AlreadyCompressed,
        _ => throw new ArgumentOutOfRangeException(nameof(corpus), corpus, "Unknown corpus name.")
    };
}

/// <summary>
/// Measures the card-flash DAT delivery path end to end: packing a raw partition image into a
/// <c>.new.dat</c> plus <c>.transfer.list</c> pair, the <c>.new.dat.br</c> Brotli combination the
/// delivery pipeline actually ships, and the two unpack routes the reader offers.
/// <para>端到端度量卡刷 DAT 交付链路：把原始分区镜像打包为 <c>.new.dat</c> 与 <c>.transfer.list</c> 组合、
/// 交付链路实际发布的 <c>.new.dat.br</c> Brotli 组合，以及读取器提供的两条解包路径。</para>
/// <para><see cref="DatWriteOptions.SelfVerifyIncremental"/> defaults to <see langword="true"/> and both
/// the host and the handler leave it on, so the pack benchmarks run with verification on and off as two
/// separate measurements and the delta is exactly the cost the unpack/pack audit flagged as
/// unquantified.</para>
/// <para><see cref="DatWriteOptions.SelfVerifyIncremental"/> 默认为 <see langword="true"/>，且宿主与处理器都保持
/// 开启，因此打包基准把校验开启与关闭作为两次独立测量运行，其差值正是解包/打包审计所指出、此前未被量化的成本。</para>
/// <para>Param matrix: 2 kinds x 2 sizes x 4 pack methods = 16, plus 4 unpack methods x 2 kinds x 2 sizes
/// = 16, for 32 cases in this class.</para>
/// <para>参数矩阵：2 种特征 × 2 种尺寸 × 4 个打包方法 = 16，加上 4 个解包方法 × 2 种特征 × 2 种尺寸 = 16，
/// 本类共 32 个用例。</para>
/// </summary>
/// <remarks>
/// <see cref="DatReader"/> and <see cref="UotanStudio.Firmware.OtaDelta.Dat.DatFormatHandler"/> are
/// internal to <c>UotanStudio.Firmware</c>; the unpack benchmarks therefore drive the public
/// <see cref="DatSplitImporter.ReconstructDatImageAsync"/> path, which joins numbered new-data volumes
/// and replays them through the same <c>DatReader.Unpack</c> implementation. Reaching <c>DatReader</c>
/// directly would require the assembly's existing <c>InternalsVisibleTo("UotanStudio.Benchmarks")</c>
/// grant, which is deliberately not relied on here.
/// <para><see cref="DatReader"/> 与 <see cref="UotanStudio.Firmware.OtaDelta.Dat.DatFormatHandler"/> 对
/// <c>UotanStudio.Firmware</c> 是 internal，因此解包基准改为驱动公开的
/// <see cref="DatSplitImporter.ReconstructDatImageAsync"/> 路径：它拼接带编号的新数据卷，并经同一份
/// <c>DatReader.Unpack</c> 实现回放。直接访问 <c>DatReader</c> 需要该程序集已有的
/// <c>InternalsVisibleTo("UotanStudio.Benchmarks")</c> 授权，此处刻意不依赖它。</para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class DatDeliveryBenchmarks
{
    private const int BrotliLevel = 9;

    private byte[] _raw = [];
    private string _scratchRoot = string.Empty;
    private string _corpusImagePath = string.Empty;
    private string _plainDatPath = string.Empty;
    private string _plainListPath = string.Empty;
    private string _brotliDatPath = string.Empty;
    private string _brotliInputPath = string.Empty;
    private string _brotliListPath = string.Empty;
    private string _splitDirectory = string.Empty;
    private string _splitSelectedPath = string.Empty;
    private string _splitBrotliDirectory = string.Empty;
    private string _splitBrotliSelectedPath = string.Empty;

    /// <summary>
    /// Gets or sets the payload character the packed partition image is generated from, expressed as a
    /// string for the same public-member reason as the sparse class. <c>zero</c> exercises the
    /// <c>zero</c> command path where almost no block reaches the data file, and <c>text</c> exercises
    /// the <c>new</c> path where every block does.
    /// <para>获取或设置被打包分区镜像所依据的载荷特征，出于与稀疏类相同的公开成员原因用字符串表示。<c>zero</c> 走
    /// <c>zero</c> 命令路径，几乎没有块进入数据文件；<c>text</c> 走 <c>new</c> 路径，每个块都会进入。</para>
    /// </summary>
    [Params("zero", "text")]
    public string Corpus { get; set; } = "text";

    /// <summary>
    /// Gets or sets the partition image size in mebibytes. DAT plaintext reaches GB scale in the field,
    /// so the default matrix stays at a runnable size while still crossing many blocks.
    /// <para>获取或设置分区镜像的尺寸，单位为兆字节（MiB）。实际 DAT 明文可达 GB 量级，因此默认矩阵保持在可运行的
    /// 尺寸，同时仍跨越大量块。</para>
    /// </summary>
    [Params(8, 32)]
    public int ImageMebibytes { get; set; }

    /// <summary>
    /// Materializes the cached corpus payload, writes the raw image once, and pre-builds every DAT
    /// artifact the unpack benchmarks consume so setup cost is not attributed to them.
    /// <para>实体化缓存的语料载荷、写入一次原始镜像，并预构建解包基准所消费的全部 DAT 产物，使设置成本不被计入解包。</para>
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _raw = BenchCorpus.Bytes(SparseImageBenchmarks.ResolveCorpus(Corpus), ImageMebibytes).ToArray();
        _scratchRoot = Path.Combine(Path.GetTempPath(), "uotan-bench-dat-" + Guid.NewGuid().ToString("N"));
        ScratchCleanup.Register(_scratchRoot);
        Directory.CreateDirectory(_scratchRoot);
        _corpusImagePath = Path.Combine(_scratchRoot, "system.img");
        File.WriteAllBytes(_corpusImagePath, _raw);

        _plainDatPath = Path.Combine(_scratchRoot, "plain.new.dat");
        _plainListPath = Path.Combine(_scratchRoot, "plain.transfer.list");
        DatWriter.Write(_corpusImagePath, _plainDatPath, _plainListPath, new DatWriteOptions { SelfVerifyIncremental = false });

        _brotliDatPath = Path.Combine(_scratchRoot, "brotli.new.dat.br");
        _brotliListPath = Path.Combine(_scratchRoot, "brotli.transfer.list");
        var brotliInput = Path.Combine(_scratchRoot, "brotli.new.dat");
        _brotliInputPath = brotliInput;
        DatWriter.Write(_corpusImagePath, brotliInput, _brotliListPath, new DatWriteOptions { CompressWithBrotli = true, SelfVerifyIncremental = false });
        File.Move(brotliInput + ".br", _brotliDatPath, overwrite: true);
        _splitDirectory = Path.Combine(_scratchRoot, "split-plain");
        _splitSelectedPath = StageNumberedSet(_plainListPath, _plainDatPath, _splitDirectory, brotli: false);
        _splitBrotliDirectory = Path.Combine(_scratchRoot, "split-brotli");
        _splitBrotliSelectedPath = StageNumberedSet(_brotliListPath, _brotliDatPath, _splitBrotliDirectory, brotli: true);
    }

    /// <summary>
    /// Deletes the scratch tree this benchmark type created, and as a second pass removes any leftover
    /// self-verification image that <see cref="DatWriter"/> stages under the system temporary directory,
    /// so a failed or cancelled iteration cannot leak a GB-scale file.
    /// <para>删除本基准类型创建的暂存目录树，并作为第二遍清理移除 <see cref="DatWriter"/> 在系统临时目录下可能残留的
    /// 自校验镜像，从而使失败或被取消的迭代不会泄漏 GB 量级文件。</para>
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        ScratchCleanup.Release(ref _scratchRoot);
        ScratchCleanup.Sweep();
    }

    /// <summary>
    /// Writes the <c>.new.dat</c> / <c>.transfer.list</c> pair with self-verification on, replaying the
    /// freshly written dataset through the reader and comparing it against the input image.
    /// <para>在开启自校验的情况下写出 <c>.new.dat</c> / <c>.transfer.list</c> 组合：将刚写出的数据集经读取器回放，
    /// 并与输入镜像比较。</para>
    /// </summary>
    /// <returns>The produced <c>.new.dat</c> length in bytes. <para>产出的 <c>.new.dat</c> 字节长度。</para>
    /// <exception cref="InvalidDataException">Self-verification did not rebuild the input image.
    /// <para>自校验未能重建输入镜像。</para></exception>
    [Benchmark(Description = "Dat_Pack_SelfVerifyOn")]
    public long PackDatSelfVerifyOn()
    {
        DatWriter.Write(_corpusImagePath, _plainDatPath, _plainListPath, new DatWriteOptions { SelfVerifyIncremental = true });
        return new FileInfo(_plainDatPath).Length;
    }

    /// <summary>
    /// Writes the same pair with self-verification off. The difference against
    /// <see cref="PackDatSelfVerifyOn"/> is the unquantified replay-and-compare cost the audit flagged.
    /// <para>在关闭自校验的情况下写出同样的组合。它与 <see cref="PackDatSelfVerifyOn"/> 的差值正是审计所指出的、
    /// 此前未被量化的回放并比较成本。</para>
    /// </summary>
    /// <returns>The produced <c>.new.dat</c> length in bytes. <para>产出的 <c>.new.dat</c> 字节长度。</para>
    [Benchmark(Description = "Dat_Pack_SelfVerifyOff", Baseline = true)]
    public long PackDatSelfVerifyOff()
    {
        DatWriter.Write(_corpusImagePath, _plainDatPath, _plainListPath, new DatWriteOptions { SelfVerifyIncremental = false });
        return new FileInfo(_plainDatPath).Length;
    }

    /// <summary>
    /// Writes the card-flash Brotli combination: the transfer list plus the <c>.new.dat.br</c> the
    /// delivery pipeline ships, with self-verification on.
    /// <para>写出卡刷 Brotli 组合：传输清单与交付链路发布的 <c>.new.dat.br</c>，并开启自校验。</para>
    /// </summary>
    /// <returns>The produced <c>.new.dat.br</c> length in bytes. <para>产出的 <c>.new.dat.br</c> 字节长度。</para>
    [Benchmark(Description = "DatBr_Pack_SelfVerifyOn")]
    public long PackDatBrSelfVerifyOn()
    {
        DatWriter.Write(_corpusImagePath, _brotliInputPath, _brotliListPath, new DatWriteOptions { CompressWithBrotli = true, SelfVerifyIncremental = true });
        return new FileInfo(_brotliInputPath + ".br").Length;
    }

    /// <summary>
    /// Writes the same Brotli combination with self-verification off, isolating the verify pass from the
    /// Brotli compression cost measured by <see cref="PackDatBrSelfVerifyOn"/>.
    /// <para>在关闭自校验的情况下写出同样的 Brotli 组合，把校验趟与 <see cref="PackDatBrSelfVerifyOn"/> 所度量的
    /// Brotli 压缩成本分离。</para>
    /// </summary>
    /// <returns>The produced <c>.new.dat.br</c> length in bytes. <para>产出的 <c>.new.dat.br</c> 字节长度。</returns>
    [Benchmark(Description = "DatBr_Pack_SelfVerifyOff")]
    public long PackDatBrSelfVerifyOff()
    {
        DatWriter.Write(_corpusImagePath, _brotliInputPath, _brotliListPath, new DatWriteOptions { CompressWithBrotli = true, SelfVerifyIncremental = false });
        return new FileInfo(_brotliInputPath + ".br").Length;
    }

    /// <summary>
    /// Reconstructs the raw partition image from a numbered plain new-data volume set through the public
    /// import path.
    /// <para>经公开导入路径从带编号的未压缩新数据卷集合重建原始分区镜像。</para>
    /// </summary>
    /// <returns>The reconstructed image length in bytes. <para>重建镜像的字节长度。</para>
    /// <exception cref="InvalidDataException">The joined volume does not match the transfer list.
    /// <para>拼接后的卷与传输清单不符。</para></exception>
    [Benchmark(Description = "Dat_Unpack_Plain")]
    public long UnpackDatPlain()
    {
        var output = Path.Combine(_scratchRoot, "rebuilt-plain.img");
        return Reconstruct(_splitSelectedPath, output);
    }

    /// <summary>
    /// Reconstructs the raw partition image from a numbered Brotli new-data volume set, which decompresses
    /// through the strict Brotli reader before replaying the commands.
    /// <para>经严格 Brotli 读取器解压后再回放命令，从带编号的 Brotli 新数据卷集合重建原始分区镜像。</para>
    /// </summary>
    /// <returns>The reconstructed image length in bytes. <para>重建镜像的字节长度。</para>
    /// <exception cref="InvalidDataException">The joined Brotli volume does not match the transfer list.
    /// <para>拼接后的 Brotli 卷与传输清单不符。</para></exception>
    [Benchmark(Description = "DatBr_Unpack")]
    public long UnpackDatBrotli()
    {
        var output = Path.Combine(_scratchRoot, "rebuilt-brotli.img");
        return Reconstruct(_splitBrotliSelectedPath, output);
    }

    /// <summary>
    /// Compresses the already-written <c>.new.dat</c> to <c>.new.dat.br</c> with the delivery Brotli
    /// level, which is the second half of what <c>ImageOutputService</c> does for a <c>br</c> output.
    /// <para>以交付所用的 Brotli 级别把已写出的 <c>.new.dat</c> 压缩为 <c>.new.dat.br</c>，即
    /// <c>ImageOutputService</c> 产出 <c>br</c> 时的后半段。</para>
    /// </summary>
    /// <returns>The produced Brotli file length in bytes. <para>产出的 Brotli 文件字节长度。</para>
    [Benchmark(Description = "DatBr_Compress")]
    public long CompressDatToBrotli()
    {
        var output = Path.Combine(_scratchRoot, "recompress.new.dat.br");
        CompressionFile.CompressFile(_plainDatPath, output, CompressionFormat.Brotli, new CompressionOptions { Level = BrotliLevel });
        return new FileInfo(output).Length;
    }

    /// <summary>
    /// Decompresses the <c>.new.dat.br</c> back to plaintext using format detection, which is the
    /// standalone decompression half of the card-flash path.
    /// <para>使用格式探测把 <c>.new.dat.br</c> 解压回明文，即卡刷链路的独立解压半段。</para>
    /// </summary>
    /// <returns>The decompressed plaintext length in bytes. <para>解压后明文的字节长度。</para>
    [Benchmark(Description = "DatBr_Decompress")]
    public long DecompressDatBrotli()
    {
        var output = Path.Combine(_scratchRoot, "decompressed.new.dat");
        CompressionFile.DecompressFileAuto(_brotliDatPath, output);
        return new FileInfo(output).Length;
    }

    private static long Reconstruct(string selectedPath, string outputPath)
    {
        DatSplitImporter.ReconstructDatImageAsync(selectedPath, outputPath).GetAwaiter().GetResult();
        return new FileInfo(outputPath).Length;
    }

    private static string StageNumberedSet(string listPath, string dataPath, string directory, bool brotli)
    {
        Directory.CreateDirectory(directory);
        var listName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(listPath));
        var listTarget = Path.Combine(directory, listName + ".transfer.list");
        File.Copy(listPath, listTarget, overwrite: true);
        var dataName = brotli ? listName + ".new.dat.br.0" : listName + ".new.dat.0";
        var dataTarget = Path.Combine(directory, dataName);
        File.Copy(dataPath, dataTarget, overwrite: true);
        return dataTarget;
    }
}

/// <summary>
/// Guarantees that every scratch directory a sparse or DAT benchmark materialized is removed, including
/// when BenchmarkDotNet does not reach <c>[GlobalCleanup]</c>.
/// <para>保证稀疏与 DAT 基准实体化的每个暂存目录都会被删除，包括 BenchmarkDotNet 未能执行
/// <c>[GlobalCleanup]</c> 的情况。</para>
/// <para>Each benchmark process materializes a GB-scale DAT scratch tree in its <c>[GlobalSetup]</c>.
/// Relying on <c>[GlobalCleanup]</c> alone is not sufficient: a host that is torn down after a failed
/// generated-project build, or killed on timeout, never runs it, and an unmanaged scratch tree is left
/// behind. This helper therefore registers every root under a process-exit hook at creation time, and
/// additionally sweeps the benchmark-owned prefixes on exit, so the invariant holds regardless of how
/// the process ends.</para>
/// <para>每个基准进程都会在 <c>[GlobalSetup]</c> 中实体化 GB 量级的 DAT 暂存目录树，仅依赖
/// <c>[GlobalCleanup]</c> 并不充分：生成工程构建失败后被拆除的主机、或因超时被杀死的进程都不会执行它，
/// 从而遗留无人管理的暂存目录树。因此本辅助类在创建时即把每个根目录登记到进程退出钩子，并在退出时额外清扫
/// 基准自有前缀，使该约束与进程结束方式无关。</para>
/// </summary>
internal static class ScratchCleanup
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> Roots = new(StringComparer.Ordinal);
    private static int _hookRegistered;

    /// <summary>
    /// Records a scratch root so the process-exit hook deletes it even when <c>[GlobalCleanup]</c> is
    /// skipped, and installs that hook on first use.
    /// <para>登记一个暂存根目录，使进程退出钩子即使在 <c>[GlobalCleanup]</c> 被跳过时也会删除它，并在首次使用时
    /// 安装该钩子。</para>
    /// </summary>
    /// <param name="path">Absolute scratch root created by the calling benchmark. <para>调用方基准创建的暂存根绝对路径。</para></param>
    internal static void Register(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Gate)
        {
            Roots.Add(path);
        }

        if (Interlocked.Exchange(ref _hookRegistered, 1) == 0)
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Sweep();
        }
    }

    /// <summary>
    /// Deletes one scratch root and deregisters it, clearing the caller's field so a later release
    /// cannot target a path that a new setup has already reused.
    /// <para>删除一个暂存根目录并注销它，同时清空调用方字段，使后续释放不会指向新设置已复用的路径。</para>
    /// </summary>
    /// <param name="path">Scratch root to delete; cleared on return. <para>待删除的暂存根目录；返回时被清空。</para></param>
    internal static void Release(ref string path)
    {
        var target = path;
        path = string.Empty;
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        lock (Gate)
        {
            Roots.Remove(target);
        }

        Delete(target);
    }

    /// <summary>
    /// Deletes every registered scratch root and every benchmark-owned artifact left under the system
    /// temporary directory, including the self-verification images <c>DatWriter</c> stages there.
    /// <para>删除全部已登记的暂存根目录，以及系统临时目录下遗留的全部基准自有产物，包括 <c>DatWriter</c>
    /// 在其中暂存的自校验镜像。</para>
    /// </summary>
    internal static void Sweep()
    {
        string[] pending;
        lock (Gate)
        {
            pending = [.. Roots];
            Roots.Clear();
        }

        foreach (var root in pending)
        {
            Delete(root);
        }

        SweepTempArtifacts();
    }

    private static void SweepTempArtifacts()
    {
        var temp = Path.GetTempPath();
        foreach (var pattern in (string[])["uotan-bench-sparse-*", "uotan-bench-dat-*", "uotan-dat-verify-*.img"])
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(temp, pattern))
                {
                    Delete(entry);
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

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
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
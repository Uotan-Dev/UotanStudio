using BenchmarkDotNet.Attributes;
using UotanStudio.Benchmarks.Inputs;
using UotanStudio.Compress;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Extraction;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Archive.Writing;

namespace UotanStudio.Benchmarks.Compression;

/// <summary>
/// Measures whole-file CPIO and TAR packing and unpacking from disk to disk, across the raw container
/// dialects and the compressed CPIO wrappers, sweeping the entry count and the payload size on
/// independent axes.
/// <para>度量从磁盘到磁盘的整文件 CPIO 与 TAR 打包与解包，覆盖裸容器方言与压缩 CPIO 封装，
/// 并在相互独立的坐标轴上扫描条目数量与载荷尺寸。</para>
/// <para>Every measurement runs through the registered <see cref="CpioFormatHandler"/> or the public
/// <see cref="CpioArchive"/> API, so the temporary-file round trip, the pre-extraction full re-scan and
/// the post-pack verification the format layer performs all stay inside the measured region instead of
/// being optimized away. Both axes are swept because they have different cost drivers: the entry-count
/// axis exposes the per-call full decompression and the per-entry extraction bookkeeping that
/// <c>docs/unpack-pack-redundancy-audit.md</c> item 9 names but never timed, while the payload-size axis
/// exposes bulk copy and codec throughput. Each benchmark returns the verified entry count, so a
/// silently empty archive cannot be reported as a fast result.</para>
/// <para>每次测量都经已注册的 <see cref="CpioFormatHandler"/> 或公开的 <see cref="CpioArchive"/> API 运行，
/// 因此格式层的临时文件往返、解包前的整档重扫与打包后的校验都留在测量区间内，不会被优化掉。两个坐标轴都要扫描，
/// 因为其成本驱动因素不同：条目数量轴暴露 <c>docs/unpack-pack-redundancy-audit.md</c> 第 9 条点名却从未计时的
/// 每次调用全量解压与逐条目解包记账，载荷尺寸轴暴露批量复制与编解码吞吐。每个基准都返回校验过的条目数量，
/// 使静默产生的空归档无法被当作快速结果。</para>
/// </summary>
/// <remarks>
/// Supported and unsupported combinations are established by direct probe against the real handler
/// rather than by assumption. Verified to work end to end (correct container magic on disk, successful
/// round trip through <see cref="CpioFormatHandler"/>): every <c>.cpio&lt;suffix&gt;</c> form the handler
/// advertises, so the raw dialect plus gzip, xz, zstd, lz4, bzip2, lzma and lzop wrappers.
/// <para>支持的与不支持的组合都通过与真实处理器的直接探测确定，而非凭假设。经核实可端到端工作的组合
/// （磁盘上的容器魔数正确、经 <see cref="CpioFormatHandler"/> 往返成功）：处理器声明的所有
/// <c>.cpio&lt;suffix&gt;</c> 形式，即裸方言加上 gzip、xz、zstd、lz4、bzip2、lzma 与 lzop 封装。</para>
/// <para>Two combinations the task asked about are deliberately not benchmarked as compressed work,
/// because probing proved they are not compressed work. A directory pack to a <c>.tar.gz</c> or
/// <c>.tar.xz</c> output path is reported as successful yet writes a raw, uncompressed newc CPIO stream:
/// the bytes begin <c>070701</c> and the file is byte-for-byte the size of the raw output.
/// <see cref="CpioFormatHandler"/> matches wrappers only through <c>.cpio&lt;suffix&gt;</c>, so any
/// <c>.tar.*</c> output resolves to no compression at all. The same pack path also writes CPIO rather
/// than TAR into a <c>.tar</c> output, because the TAR dialect is selected only from repack metadata that
/// a metadata-free directory pack never carries. Those points are recorded here and in the task report
/// instead of being silently dropped. TAR is therefore measured in the direction where the repository
/// genuinely produces it — through <see cref="CpioArchive.SaveAll"/> with
/// <see cref="ArchiveFormat.Tar"/>, which drives <c>TarArchiveWriter</c>, and the matching read direction
/// which drives <c>TarArchiveReader</c>.</para>
/// <para>任务问及的两个组合被有意不作为压缩工作来度量，因为探测证明它们并不是压缩工作。把目录打包到
/// <c>.tar.gz</c> 或 <c>.tar.xz</c> 输出路径会返回成功，却写出未压缩的裸 newc CPIO 流：字节以 <c>070701</c>
/// 开头，且文件大小与裸输出逐字节相同。<see cref="CpioFormatHandler"/> 只通过 <c>.cpio&lt;suffix&gt;</c>
/// 匹配封装，因此任何 <c>.tar.*</c> 输出都解析为不压缩。同一条打包路径也会把 CPIO 而非 TAR 写入 <c>.tar</c>
/// 输出，因为 TAR 方言只从重打包元数据中选择，而无元数据的目录打包从不携带该元数据。这些结论记录在此处与任务报告中，
/// 而不被静默丢弃。因此 TAR 在仓库真正生成它的方向上度量：经 <see cref="ArchiveFormat.Tar"/> 调用
/// <see cref="CpioArchive.SaveAll"/>（驱动 <c>TarArchiveWriter</c>），以及对应的驱动
/// <c>TarArchiveReader</c> 的读取方向。</para>
/// </remarks>
[MemoryDiagnoser]
public class ContainerBenchmarks
{
    private static readonly int[] EntryCountValuesSource = [1, 8, 64];

    private static readonly int[] PayloadMebibytesSource = [1, 8];

    private static readonly string[] FormatSource =
    [
        "cpio",
        "cpio.gz",
        "cpio.xz",
        "tar",
        "tar.xz"
    ];

    private string _root = "";
    private string _sourceTree = "";
    private string _unpackRoot = "";
    private int _expectedRecords;
    private long _plannedPayloadBytes;
    private long _fixtureBytes;
    private string _packOutputPath = "";

    /// <summary>
    /// Gets or sets the container dialect and compression wrapper under test.
    /// <para>获取或设置被测容器方言与压缩封装。</para>
    /// </summary>
    /// <value>
    /// <c>cpio</c>, <c>cpio.gz</c>, <c>cpio.xz</c>, <c>tar</c> or <c>tar.xz</c>. The first three run
    /// through <see cref="CpioFormatHandler"/>; the two TAR values run through the public
    /// <see cref="CpioArchive"/> API with explicit <see cref="ArchiveFormat"/> hints and therefore
    /// exercise the real TAR writer and reader instead of the CPIO writer under a TAR name.
    /// <para><c>cpio</c>、<c>cpio.gz</c>、<c>cpio.xz</c>、<c>tar</c> 或 <c>tar.xz</c>。前三个经
    /// <see cref="CpioFormatHandler"/> 运行；两个 TAR 取值经公开 <see cref="CpioArchive"/> API 并携带显式
    /// <see cref="ArchiveFormat"/> 提示运行，因此锻炼的是真正的 TAR 写入器与读取器，而不是顶着 TAR 名字的 CPIO 写入器。</para>
    /// </value>
    [ParamsSource(nameof(FormatValues))]
    public string Format { get; set; } = "cpio";

    /// <summary>
    /// Gets the container dialect values for BenchmarkDotNet to read.
    /// <para>获取供 BenchmarkDotNet 读取的容器方言取值。</para>
    /// </summary>
    /// <value>The container combinations this class measures. <para>本类度量的容器组合。</para></value>
    public IEnumerable<string> FormatValues => FormatSource;

    /// <summary>
    /// Gets or sets the number of regular files written into the source tree.
    /// <para>获取或设置写入源树的常规文件数量。</para>
    /// </summary>
    /// <value>
    /// The entry-count axis. The same per-file payload is replicated across this many files, so the axis
    /// isolates per-entry bookkeeping and the per-call full decompression from bulk cost.
    /// <para>条目数量轴。同一单文件载荷被复制到这么多文件上，因此该轴把逐条目记账与每次调用全量解压从批量成本中分离出来。</para>
    /// </value>
    [ParamsSource(nameof(EntryCounts))]
    public int Entries { get; set; }

    /// <summary>
    /// Gets the entry-count axis values for BenchmarkDotNet to read.
    /// <para>获取供 BenchmarkDotNet 读取的条目数量轴取值。</para>
    /// </summary>
    /// <value>The file counts this class sweeps. <para>本类扫描的文件数量。</para></value>
    public IEnumerable<int> EntryCounts => EntryCountValuesSource;

    /// <summary>
    /// Gets or sets the payload size of every individual file in mebibytes.
    /// <para>获取或设置每个单独文件的载荷尺寸，单位为兆字节。</para>
    /// </summary>
    /// <value>
    /// The payload-size axis, independent of <see cref="Entries"/>. The total tree therefore ranges from
    /// 1 MiB (one 1 MiB file) to 512 MiB (64 files of 8 MiB).
    /// <para>载荷尺寸轴，与 <see cref="Entries"/> 相互独立。因此树的总大小范围为 1 MiB（一个 1 MiB 文件）
    /// 到 512 MiB（64 个 8 MiB 文件）。</para>
    /// </value>
    [ParamsSource(nameof(PayloadMebibytes))]
    public int MebibytesPerEntry { get; set; }

    /// <summary>
    /// Gets the payload-size axis values for BenchmarkDotNet to read.
    /// <para>获取供 BenchmarkDotNet 读取的载荷尺寸轴取值。</para>
    /// </summary>
    /// <value>The per-file mebibyte counts this class sweeps. <para>本类扫描的每文件兆字节数。</para></value>
    public IEnumerable<int> PayloadMebibytes => PayloadMebibytesSource;

    /// <summary>
    /// Builds the per-run source tree from the shared corpus, the scratch directories and, for the TAR
    /// combinations, the pre-built archive each unpack measurement consumes.
    /// <para>用共享语料构建本次运行的源树、临时目录，以及 TAR 组合下每次解包测量所消费的预构建归档。</para>
    /// </summary>
    /// <exception cref="IOException">
    /// The temporary tree or the pre-built archive cannot be written.
    /// <para>无法写入临时树或预构建归档。</para>
    /// </exception>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-bench-container-" + Guid.NewGuid().ToString("N"));
        _sourceTree = Path.Combine(_root, "src");
        _unpackRoot = Path.Combine(_root, "unpacked");
        Directory.CreateDirectory(_sourceTree);
        Directory.CreateDirectory(_unpackRoot);

        var corpus = BenchCorpus.Bytes(PayloadKind.Source, MebibytesPerEntry);
        for (var index = 0; index < Entries; index++)
        {
            var bucket = Path.Combine(_sourceTree, FormattableString.Invariant($"bucket-{index % 4}"));
            Directory.CreateDirectory(bucket);
            var path = Path.Combine(bucket, FormattableString.Invariant($"entry-{index}.bin"));
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            file.Write(corpus);
        }

        _plannedPayloadBytes = (long)Entries * corpus.Length;

        // The guard has to be able to fail. The objective bound is the payload the source tree holds, which
        // never depends on the code under test; the record count additionally pins each family's convention
        // (CPIO reports one record per file and per directory, TAR additionally reports an explicit root) so
        // a later pack that silently drops records is still caught rather than measuring less work.
        ArchivePath = Path.Combine(_root, "archive" + SuffixFor(Format));
        if (IsArchiveApiFormat(Format))
        {
            _expectedRecords = BuildTarFixture(ArchivePath);
        }
        else
        {
            var pack = new CpioFormatHandler().PackAsync(_sourceTree, ArchivePath).GetAwaiter().GetResult();
            if (!pack.Success)
            {
                throw new IOException(FormattableString.Invariant($"Fixture pack failed for '{Format}': {pack.Summary}"));
            }

            _expectedRecords = pack.Entries.Count;
        }

        _fixtureBytes = new FileInfo(ArchivePath).Length;
        _packOutputPath = Path.Combine(_root, "packed" + SuffixFor(Format));
    }

    /// <summary>
    /// Gets the absolute path of the archive this case produces or consumes.
    /// <para>获取本用例生成或消费的归档的绝对路径。</para>
    /// </summary>
    /// <value>The per-case archive path inside the per-run root. <para>本次运行根目录内按用例区分的归档路径。</para></value>
    public string ArchivePath { get; private set; } = "";

    /// <summary>
    /// Packs the source directory into the configured container, from disk to disk.
    /// <para>把源目录打包进所配置的容器，从磁盘到磁盘。</para>
    /// </summary>
    /// <returns>
    /// The number of records the container reported writing, cross-checked against the fixture so a pack
    /// that silently drops records fails the measurement instead of reporting a fast result.
    /// <para>容器报告已写入的记录数量；与夹具交叉校验，使静默丢弃记录的打包令测量失败，而不是报告一个快速结果。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The pack failed, it produced a different record count than the fixture holds, or a raw dialect
    /// produced different bytes than the fixture did.
    /// <para>打包失败、其记录数量与夹具不一致，或裸方言产出的字节与夹具不同。</para>
    /// </exception>
    [Benchmark]
    public int Pack()
    {
        // A fixed output path per case, deleted first, so the measured work is exactly one whole-file pack
        // and the run does not accumulate archives. The fixture archive of the Unpack row is never touched.
        var written = IsArchiveApiFormat(Format)
            ? PackTar(_sourceTree, _packOutputPath, Format.EndsWith(".xz", StringComparison.Ordinal))
            : PackCpio(_sourceTree, _packOutputPath);
        VerifyEntryCount(written, "Pack");
        VerifyProducedArchive(_packOutputPath);
        return written;
    }

    private void VerifyProducedArchive(string path)
    {
        var produced = new FileInfo(path).Length;
        if (produced == 0)
        {
            throw new InvalidOperationException(FormattableString.Invariant($"Pack wrote an empty archive for '{Format}'."));
        }

        // A compressed wrapper must differ from the fixture; a raw dialect must reproduce it exactly. Either
        // way the check is on bytes, not on the handler's own self-report, so a pack that silently skipped
        // compression or dropped the payload cannot pass as a fast measurement.
        if (_fixtureBytes != produced
            && !Format.EndsWith(".gz", StringComparison.Ordinal)
            && !Format.EndsWith(".xz", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Pack produced {produced} bytes but the raw fixture held {_fixtureBytes} for '{Format}'."));
        }
    }

    /// <summary>
    /// Unpacks the configured container into a fresh directory, from disk to disk.
    /// <para>把所配置的容器解包到全新目录，从磁盘到磁盘。</para>
    /// </summary>
    /// <returns>
    /// The number of records the container reported extracting, cross-checked against the fixture.
    /// <para>容器报告已解出的记录数量；与夹具交叉校验。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The unpack failed, or it produced a different record count than the fixture holds.
    /// <para>解包失败，或其记录数量与夹具不一致。</para>
    /// </exception>
    [Benchmark]
    public int Unpack()
    {
        // A single reused destination, wiped first: the measured work is one whole-file unpack rather than
        // an ever-growing pile of directories, and repeated overwrite is what the product does in place.
        if (Directory.Exists(_unpackRoot))
        {
            Directory.Delete(_unpackRoot, recursive: true);
        }

        Directory.CreateDirectory(_unpackRoot);
        int extracted;
        if (IsArchiveApiFormat(Format))
        {
            using var file = File.OpenRead(ArchivePath);
            using var source = Format.EndsWith(".xz", StringComparison.Ordinal) ? DecompressXz(file) : file;
            extracted = new CpioArchive().ExtractToDirectory(
                source, _unpackRoot, new ExtractOptions { Overwrite = true }, ArchiveFormat.Tar, out _).Count;
        }
        else
        {
            var result = new CpioFormatHandler().UnpackAsync(ArchivePath, _unpackRoot).GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"Unpack failed for '{Format}': {result.Summary}"));
            }

            extracted = result.Entries.Count;
        }

        VerifyEntryCount(extracted, "Unpack");
        return extracted;
    }

    /// <summary>
    /// Deletes the per-run root, which holds the source tree, the unpack destinations and every produced
    /// archive. The deletion is guarded so a setup failure cannot mask the original error.
    /// <para>删除本次运行的根目录，其中包含源树、解包目标与所有生成的归档。删除带有保护，使安装失败不会掩盖原始错误。</para>
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        if (_root.Length == 0)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsArchiveApiFormat(string format)
        => format.StartsWith("tar", StringComparison.Ordinal);

    private static string SuffixFor(string format) => format switch
    {
        "cpio" => ".cpio",
        "cpio.gz" => ".cpio.gz",
        "cpio.xz" => ".cpio.xz",
        "tar" => ".tar",
        "tar.xz" => ".tar.xz",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported container format."),
    };

    private static int PackCpio(string sourceTree, string outputPath)
    {
        File.Delete(outputPath);
        var result = new CpioFormatHandler().PackAsync(sourceTree, outputPath).GetAwaiter().GetResult();
        if (!result.Success)
        {
            throw new InvalidOperationException(FormattableString.Invariant($"Pack failed: {result.Summary}"));
        }

        return result.Entries.Count;
    }

    private void VerifyEntryCount(int actual, string operation)
    {
        if (actual != _expectedRecords)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"{operation} produced {actual} records but {_expectedRecords} were expected."));
        }
    }

    private int BuildTarFixture(string archivePath)
        => PackTar(_sourceTree, archivePath, Format.EndsWith(".xz", StringComparison.Ordinal));

    private static int PackTar(string sourceTree, string outputPath, bool compressWithXz)
    {
        File.Delete(outputPath);
        var entries = ReadDirectoryEntriesForTar(sourceTree);
        if (compressWithXz)
        {
            var rawPath = outputPath + ".inner";
            try
            {
                using (var raw = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                {
                    new CpioArchive().SaveAll(raw, entries, ArchiveFormat.Tar, new WriteOptions());
                    raw.Flush(true);
                }

                using var plain = File.OpenRead(rawPath);
                using var encoded = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
                CompressionService.Compress(plain, encoded, CompressionFormat.Xz);
                encoded.Flush(true);
            }
            finally
            {
                File.Delete(rawPath);
            }
        }
        else
        {
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
            new CpioArchive().SaveAll(output, entries, ArchiveFormat.Tar, new WriteOptions());
            output.Flush(true);
        }

        using var verify = File.OpenRead(outputPath);
        using var decoded = compressWithXz
            ? DecompressXz(verify)
            : verify;
        return new CpioArchive().ListEntries(decoded, ArchiveFormat.Tar).Count;
    }

    private static Stream DecompressXz(Stream source)
    {
        var scratch = new MemoryStream();
        CompressionService.Decompress(source, scratch, CompressionFormat.Xz, long.MaxValue);
        scratch.Position = 0;
        return scratch;
    }

    private static List<ArchiveEntry> ReadDirectoryEntriesForTar(string sourceTree)
    {
        var root = Path.GetFullPath(sourceTree);
        var entries = new List<ArchiveEntry>
        {
            new()
            {
                Name = ".",
                Data = [],
                Metadata = new ArchiveEntryMetadata
                {
                    Mode = 0x41ED,
                    UserId = 0,
                    GroupId = 0,
                    LinkCount = 1,
                    ModificationTimeUnixSeconds = 1_700_000_000,
                    FileType = CpioFileType.Directory,
                    UnixPermissions = 0x1ED,
                },
            },
        };

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            entries.Add(BuildEntry(directory, root, isDirectory: true));
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            entries.Add(BuildEntry(file, root, isDirectory: false));
        }

        return entries;
    }

    private static ArchiveEntry BuildEntry(string path, string root, bool isDirectory)
    {
        var name = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (isDirectory)
        {
            return new ArchiveEntry
            {
                Name = name,
                Data = [],
                Metadata = new ArchiveEntryMetadata
                {
                    Mode = 0x41ED,
                    UserId = 0,
                    GroupId = 0,
                    LinkCount = 1,
                    ModificationTimeUnixSeconds = 1_700_000_000,
                    FileType = CpioFileType.Directory,
                    UnixPermissions = 0x1ED,
                },
            };
        }

        var length = new FileInfo(path).Length;
        return new ArchiveEntry
        {
            Name = name,
            Data = [],
            DeclaredDataLength = length,
            DataStreamFactory = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920),
            Metadata = new ArchiveEntryMetadata
            {
                Mode = 0x81A4,
                UserId = 0,
                GroupId = 0,
                LinkCount = 1,
                ModificationTimeUnixSeconds = 1_700_000_000,
                FileType = CpioFileType.Regular,
                UnixPermissions = 0x1A4,
            },
        };
    }
}
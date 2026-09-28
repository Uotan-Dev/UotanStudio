using System.IO.Compression;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Firmware.OtaDelta.Payload;

namespace UotanStudio.Firmware.VendorFirmware.Workflow;

/// <summary>
/// Placement and safety limits for a Samsung incremental-OTA merge: decompression and archive-member budgets,
/// the <c>prism</c> skip policy, destination-overwrite policy, and the optional post-merge extraction hook.
/// <para>Samsung 增量 OTA 合并的布局与安全限制：解压与归档成员预算、<c>prism</c> 跳过策略、目标覆盖策略，
/// 以及合并后的可选提取钩子。</para>
/// </summary>
internal sealed class SamsungIncrementalMergeOptions
{
    /// <summary>Cumulative cap on bytes produced while inflating an LZ4-wrapped base image.
    /// <para>LZ4 包裹基座镜像解压产出字节的累计上限。</para></summary>
    /// <value>Defaults to 1 TiB; must be positive. <para>默认 1 TiB；必须为正数。</para></value>
    public long MaximumDecompressedBaseBytes { get; set; } = 1L << 40;

    /// <summary>Cap on one materialized DAT archive member (<c>.new.dat</c>, <c>.patch.dat</c> or transfer list).
    /// <para>单个 DAT 归档成员（<c>.new.dat</c>、<c>.patch.dat</c> 或 transfer list）的物化上限。</para></summary>
    /// <value>Defaults to 1 TiB; must be positive. <para>默认 1 TiB；必须为正数。</para></value>
    public long MaximumDatMemberBytes { get; set; } = 1L << 40;

    /// <summary>Skips the vendor <c>prism</c> partition in DAT mode, mirroring the reference merger.
    /// <para>在 DAT 模式下跳过厂商 <c>prism</c> 分区，与参考合并器一致。</para></summary>
    /// <value>Defaults to <see langword="true"/>; skipped stems are reported in the merge result.
    /// <para>默认 <see langword="true"/>；被跳过的词干会记入合并结果。</para></value>
    public bool SkipPrismPartition { get; set; } = true;

    /// <summary>Allows replacing an already-published merged image.
    /// <para>允许替换已发布的合并镜像。</para></summary>
    /// <value>Defaults to <see langword="true"/>; when <see langword="false"/> any existing destination aborts
    /// the merge before a single image is rebuilt.
    /// <para>默认 <see langword="true"/>；为 <see langword="false"/> 时任何已存在的目标都会在重建任何镜像之前中止合并。</para></value>
    public bool Overwrite { get; set; } = true;

    /// <summary>Callback invoked once per published merged image; the reference tool's optional auto-extract step
    /// is deliberately left to the caller. <para>每个已发布合并镜像调用一次的回调；参考工具可选的自动提取步骤由调用方决定。</para></summary>
    /// <value>An optional async hook receiving the merged image descriptor; <see langword="null"/> by default.
    /// <para>可选的异步钩子，接收合并镜像描述符；默认为 <see langword="null"/>。</para></value>
    public Func<SamsungMergedPartitionImage, CancellationToken, Task>? OnPartitionMerged { get; set; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDecompressedBaseBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDatMemberBytes);
    }
}

/// <summary>
/// One merged partition image published into the output directory.
/// <para>发布到输出目录的一个合并分区镜像。</para>
/// </summary>
/// <param name="PartitionName">Partition name used for the output file (A/B slot suffix removed in DAT mode,
/// manifest name preserved in payload mode). <para>输出文件使用的分区名（DAT 模式去除 A/B 槽位后缀，payload 模式保留清单名）。</para></param>
/// <param name="OutputImagePath">Absolute path of the published image. <para>已发布镜像的绝对路径。</para></param>
/// <param name="SizeBytes">Published image size. <para>已发布镜像的大小（字节）。</para></param>
public sealed record SamsungMergedPartitionImage(string PartitionName, string OutputImagePath, long SizeBytes);

/// <summary>
/// Outcome of a Samsung incremental merge: every published image and the partition stems skipped by policy.
/// <para>Samsung 增量合并的结果：全部已发布镜像，以及按策略跳过的分区词干。</para>
/// </summary>
/// <param name="MergedPartitions">Images published into the output directory, in merge order.
/// <para>按合并顺序发布到输出目录的镜像。</para></param>
/// <param name="SkippedPartitions">Stems skipped without merging (the <c>prism</c> policy in DAT mode).
/// <para>未合并即跳过的词干（DAT 模式的 <c>prism</c> 策略）。</para></param>
public sealed record SamsungIncrementalMergeResult(
    IReadOnlyList<SamsungMergedPartitionImage> MergedPartitions,
    IReadOnlyList<string> SkippedPartitions);

/// <summary>
/// One partition entry reported by <c>list(payload)</c>: name, declared new size, operation count, and whether
/// its operations read a base image (source-dependent types).
/// <para><c>list(payload)</c> 报告的单个分区条目：名称、声明的新大小、操作数量，以及其操作是否读取基座镜像（源依赖类型）。</para>
/// </summary>
/// <param name="PartitionName">Manifest partition name. <para>清单中的分区名。</para></param>
/// <param name="NewSizeBytes">Total new-image bytes declared by the destination extents.
/// <para>目标 extent 声明的新镜像总字节数。</para></param>
/// <param name="OperationCount">Number of install operations for the partition.
/// <para>该分区的安装操作数量。</para></param>
/// <param name="RequiresSourceImages"><see langword="true"/> when at least one operation consumes a base image.
/// <para>至少有一个操作消耗基座镜像时为 <see langword="true"/>。</para></param>
public sealed record SamsungPayloadPartitionInfo(string PartitionName, long NewSizeBytes, int OperationCount,
    bool RequiresSourceImages);

/// <summary>
/// The <c>list(payload)</c> answer: manifest block size and one entry per named partition.
/// <para><c>list(payload)</c> 的应答：清单块大小与每个有名字分区的条目。</para>
/// </summary>
/// <param name="BlockSize">Manifest block size in bytes. <para>清单块大小（字节）。</para></param>
/// <param name="Partitions">Partition entries in manifest order. <para>按清单顺序排列的分区条目。</para></param>
public sealed record SamsungPayloadMergePlan(uint BlockSize, IReadOnlyList<SamsungPayloadPartitionInfo> Partitions);

/// <summary>
/// Managed equivalent of Samsung's closed-source incremental-OTA mergers (<c>samsung_dat_merge</c> and
/// <c>samsung_payload_merge</c>). DAT mode takes a base image (a single partition image or a
/// <c>super.img</c>, optionally LZ4-wrapped), locates each <c>.transfer.list</c>/<c>.new.dat[.br]</c>/
/// <c>.patch.dat[.br]</c> triple inside a ZIP by basename (or as loose siblings), strips A/B slot suffixes when
/// pairing partitions, skips the <c>prism</c> stem, replays every triple through the block-OTA execution
/// pipeline, and publishes merged images into the output directory only after all merges succeeded. Payload
/// mode lists manifest partitions with their source-dependency flags and merges selected partitions against a
/// directory of extracted old images. All limits, cancellation checks and atomic publications are enforced by
/// the reused firmware pipelines.
/// <para>三星闭源增量 OTA 合并器（<c>samsung_dat_merge</c> 与 <c>samsung_payload_merge</c>）的托管等价实现。
/// DAT 模式接收基座镜像（单分区镜像或 <c>super.img</c>，可选 LZ4 封装），在 ZIP 内按文件名（而非完整路径）
/// 定位每组 <c>.transfer.list</c>/<c>.new.dat[.br]</c>/<c>.patch.dat[.br]</c> 三元组（或直接使用同目录散装文件），
/// 配对分区时剥离 A/B 槽位后缀，跳过 <c>prism</c> 词干，将每组三元组交由块 OTA 执行管线回放，并在全部合并成功后
/// 才把合并镜像发布到输出目录。Payload 模式列出清单分区及其源依赖标记，并把选定分区针对解包后的旧镜像目录合并。
/// 所有上限、取消检查与原子发布均由复用的固件管线保证。</para>
/// </summary>
internal sealed class SamsungIncrementalMergeService
{
    private const int BufferSize = 64 * 1024;
    private const long MaximumTransferListBytes = 16L * 1024 * 1024;
    private const int MaximumArchiveEntries = 100_000;
    private const int MaximumTriples = 10_000;

    private static readonly string[] SlotSuffixes = ["_a", "_b"];

    /// <summary>
    /// Lists the payload's named partitions and their source-dependency flags without touching any image.
    /// <para>仅列出载荷中的有名字分区及其源依赖标记，不读取任何镜像。</para>
    /// </summary>
    /// <param name="payloadPath">Payload file or ZIP update package containing <c>payload.bin</c>.
    /// <para>载荷文件，或包含 <c>payload.bin</c> 的 ZIP 升级包。</para></param>
    /// <param name="cancellationToken">Cancels metadata reads. <para>取消元数据读取。</para></param>
    /// <returns>The block size and one entry per named partition.
    /// <para>块大小与每个有名字分区的条目。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="payloadPath"/> is blank. <para><paramref name="payloadPath"/> 为空白。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="payloadPath"/> does not exist. <para><paramref name="payloadPath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The payload or its manifest is unreadable. <para>载荷或其清单不可读。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<SamsungPayloadMergePlan> ListPayloadAsync(string payloadPath,
        CancellationToken cancellationToken = default)
        => ListPayloadCoreAsync(payloadPath, cancellationToken);

    private static async Task<SamsungPayloadMergePlan> ListPayloadCoreAsync(string payloadPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        var path = Path.GetFullPath(payloadPath);
        if (!File.Exists(path))
            throw new FileNotFoundException("The payload file is missing.", path);
        var metadata = await PayloadParser.GetPayloadMetadataAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var entries = metadata.Manifest.Partitions
            .Where(partition => !string.IsNullOrEmpty(partition.PartitionName))
            .Select(partition => new SamsungPayloadPartitionInfo(
                partition.PartitionName,
                PayloadParser.GetPartitionSize(partition, metadata.BlockSize),
                partition.Operations.Count,
                partition.Operations.Any(operation => PayloadExtractor.IsSourceDependent(operation.Type))))
            .ToList();
        return new SamsungPayloadMergePlan(metadata.BlockSize, entries);
    }

    /// <summary>
    /// Runs the DAT-mode merge described by <see cref="SamsungIncrementalMergeService"/>.
    /// <para>执行 <see cref="SamsungIncrementalMergeService"/> 所描述的 DAT 模式合并。</para>
    /// </summary>
    /// <param name="baseImagePath">Base image: a single partition image, a <c>super.img</c>, or an
    /// LZ4-wrapped variant of either. <para>基座镜像：单分区镜像、<c>super.img</c>，或二者的 LZ4 封装变体。</para></param>
    /// <param name="otaSourcePath">ZIP package holding the DAT triples, or any loose triple member whose
    /// siblings live beside it. <para>存放 DAT 三元组的 ZIP 包，或以其同目录散装成员形式给出的任一三元组文件。</para></param>
    /// <param name="outputDirectory">Directory that receives the merged <c>&lt;partition&gt;.img</c> files;
    /// created when missing. <para>接收合并后 <c>&lt;分区&gt;.img</c> 文件的目录，缺失时创建。</para></param>
    /// <param name="options">Merge policy; <see langword="null"/> uses defaults.
    /// <para>合并策略；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="progress">Optional stage progress sink. <para>可选的阶段进度回调。</para></param>
    /// <param name="cancellationToken">Checked before every phase and inside the replay pipelines; cancellation
    /// before publication leaves the output directory untouched. <para>在每个阶段前及回放管线内检查；发布前取消不会改动输出目录。</para></param>
    /// <returns>The published images and skipped stems. <para>已发布镜像与被跳过的词干。</para></returns>
    /// <exception cref="ArgumentException">A path is blank or a DAT member name is not a safe file name.
    /// <para>路径为空，或 DAT 成员名不是安全文件名。</para></exception>
    /// <exception cref="InvalidDataException">A triple is incomplete, two stems collide after slot stripping,
    /// a source-dependent transfer list has no base image, or a size budget is exceeded.
    /// <para>三元组不完整、两个词干剥离槽位后缀后冲突、依赖源的传输清单缺少基座镜像，或超出大小预算。</para></exception>
    /// <exception cref="IOException">A destination exists while <see cref="SamsungIncrementalMergeOptions.Overwrite"/>
    /// is disabled. <para>禁用 <see cref="SamsungIncrementalMergeOptions.Overwrite"/> 时目标已存在。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<SamsungIncrementalMergeResult> MergeDatAsync(string baseImagePath, string otaSourcePath,
        string outputDirectory, SamsungIncrementalMergeOptions? options = null, IFirmwareProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? new SamsungIncrementalMergeOptions();
        effective.Validate();
        return Task.Run(() => MergeDatCoreAsync(baseImagePath, otaSourcePath, outputDirectory, effective,
            progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Runs the payload-mode merge: selected manifest partitions are rebuilt against extracted old images from
    /// <paramref name="oldImageDirectory"/> and published into <paramref name="outputDirectory"/> only after the
    /// whole selection succeeds.
    /// <para>执行 payload 模式合并：把选定清单分区针对 <paramref name="oldImageDirectory"/> 中的旧镜像重建，
    /// 并在全部选定分区成功后才发布到 <paramref name="outputDirectory"/>。</para>
    /// </summary>
    /// <param name="payloadPath">Payload file or ZIP update package. <para>载荷文件或 ZIP 升级包。</para></param>
    /// <param name="oldImageDirectory">Directory of extracted old partition images used as base images;
    /// A/B slot suffixes are paired away when matching. <para>用作基座的解包旧镜像目录；匹配时会剥离 A/B 槽位后缀。</para></param>
    /// <param name="selectedPartitions">Partition names to merge, matched exactly or after slot stripping;
    /// <see langword="null"/> or empty merges every named partition.
    /// <para>要合并的分区名，按精确或剥离槽位后缀匹配；为 <see langword="null"/> 或空时合并全部有名字分区。</para></param>
    /// <param name="outputDirectory">Destination directory for the merged images.
    /// <para>合并镜像的目标目录。</para></param>
    /// <param name="options">Merge policy; <see langword="null"/> uses defaults.
    /// <para>合并策略；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="progress">Optional stage progress sink. <para>可选的阶段进度回调。</para></param>
    /// <param name="cancellationToken">Checked around listing, extraction and publication.
    /// <para>在列表、提取与发布前后检查。</para></param>
    /// <returns>The published images; <see cref="SamsungIncrementalMergeResult.SkippedPartitions"/> is empty
    /// because the <c>prism</c> policy applies to DAT mode only.
    /// <para>已发布镜像；<see cref="SamsungIncrementalMergeResult.SkippedPartitions"/> 恒为空，
    /// 因为 <c>prism</c> 策略仅适用于 DAT 模式。</para></returns>
    /// <exception cref="ArgumentException">A path is blank or a partition name is unusable.
    /// <para>路径为空或分区名不可用。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The old-image directory is missing. <para>旧镜像目录缺失。</para></exception>
    /// <exception cref="InvalidDataException">A selected partition is absent from the manifest, or a
    /// source-dependent partition has no unambiguous old image.
    /// <para>选定分区不在清单中，或依赖源的分区没有唯一可匹配的旧镜像。</para></exception>
    /// <exception cref="IOException">A destination exists while <see cref="SamsungIncrementalMergeOptions.Overwrite"/>
    /// is disabled. <para>禁用 <see cref="SamsungIncrementalMergeOptions.Overwrite"/> 时目标已存在。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<SamsungIncrementalMergeResult> MergePayloadAsync(string payloadPath, string oldImageDirectory,
        IReadOnlyCollection<string>? selectedPartitions, string outputDirectory,
        SamsungIncrementalMergeOptions? options = null, IFirmwareProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? new SamsungIncrementalMergeOptions();
        effective.Validate();
        return Task.Run(() => MergePayloadCoreAsync(payloadPath, oldImageDirectory, selectedPartitions,
            outputDirectory, effective, progress, cancellationToken), cancellationToken);
    }

    private static async Task<SamsungIncrementalMergeResult> MergeDatCoreAsync(string baseImagePath,
        string otaSourcePath, string outputDirectory, SamsungIncrementalMergeOptions options,
        IFirmwareProgress? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(otaSourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var baseFile = Path.GetFullPath(baseImagePath);
        if (!File.Exists(baseFile))
            throw new FileNotFoundException("The Samsung incremental base image is missing.", baseFile);
        var otaSource = Path.GetFullPath(otaSourcePath);
        if (!File.Exists(otaSource) && !Directory.Exists(otaSource))
            throw new FileNotFoundException("The incremental OTA source is missing.", otaSource);
        var output = Path.GetFullPath(outputDirectory);

        progress?.Report(5, "Collecting Samsung DAT triples");
        using var scratch = new SamsungMergeScratch();
        var triples = CollectDatTriples(otaSource, scratch, options, cancellationToken);

        var processed = new List<(SamsungDatTriple Triple, string Plain)>();
        var skipped = new List<string>();
        var plainSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var triple in triples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plain = StripSlotSuffix(triple.Stem);
            if (plain.Length == 0)
                throw new ArgumentException($"The DAT stem '{triple.Stem}' has no usable partition name.");
            if (options.SkipPrismPartition && string.Equals(plain, "prism", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(triple.Stem);
                continue;
            }

            if (!plainSeen.Add(plain))
                throw new InvalidDataException(
                    $"The slot-stripped partition name '{plain}' is ambiguous across DAT triples.");
            var destination = Path.Combine(output, plain + ".img");
            if (!options.Overwrite && File.Exists(destination))
                throw new IOException($"Output already exists and overwrite is disabled: {destination}");
            processed.Add((triple, plain));
        }

        if (processed.Count == 0)
            throw new InvalidDataException("The incremental OTA source contains no mergeable DAT triple.");

        progress?.Report(15, "Resolving base partition images");
        var bases = await ResolveBaseImagesAsync(baseFile,
            [.. processed.Select(item => item.Plain)], scratch, options, cancellationToken).ConfigureAwait(false);

        var staging = Path.Combine(output, ".samsung-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(staging);
            for (var index = 0; index < processed.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (triple, plain) = processed[index];
                progress?.Report(15 + 70.0 * index / processed.Count, $"Merging {plain}");
                MergeOneTriple(triple, plain, bases, staging, cancellationToken);
            }

            progress?.Report(90, "Publishing merged images");
            var published = await PublishAsync(staging, output, options, cancellationToken).ConfigureAwait(false);
            progress?.Report(100, $"Merged {published.Count} partitions");
            return new SamsungIncrementalMergeResult(published, skipped);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static void MergeOneTriple(SamsungDatTriple triple, string plain,
        IReadOnlyDictionary<string, string> bases, string staging, CancellationToken cancellationToken)
    {
        if (triple.NewDataPath is null)
            throw new InvalidDataException($"The DAT triple '{triple.Stem}' has no .new.dat member.");
        if (new FileInfo(triple.TransferListPath).Length > MaximumTransferListBytes)
            throw new InvalidDataException($"The transfer list of '{triple.Stem}' exceeds the size limit.");
        var list = TransferListParser.Parse(triple.TransferListPath);
        var needsSource = list.Commands.Any(command => command.Kind is TransferListCommandKind.Move
            or TransferListCommandKind.Stash or TransferListCommandKind.Diff or TransferListCommandKind.Imgdiff);
        var needsPatch = list.Commands.Any(command => command.Kind is TransferListCommandKind.Diff
            or TransferListCommandKind.Imgdiff);
        if (needsPatch && triple.PatchDataPath is null)
            throw new InvalidDataException($"The DAT triple '{triple.Stem}' needs a .patch.dat member.");
        string? source = null;
        if (needsSource && !bases.TryGetValue(plain, out source))
            throw new InvalidDataException(
                $"No base image is available for partition '{plain}' required by '{triple.Stem}'.");

        DatReader.Unpack(triple.TransferListPath, triple.NewDataPath, triple.PatchDataPath, source,
            Path.Combine(staging, plain + ".img"), brotliCompressed: false, progress: null,
            cancellationToken: cancellationToken);
    }

    private static List<SamsungDatTriple> CollectDatTriples(string otaSource, SamsungMergeScratch scratch,
        SamsungIncrementalMergeOptions options, CancellationToken cancellationToken)
    {
        var groups = new SortedDictionary<string, SamsungDatTripleGroup>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(otaSource))
        {
            foreach (var path in Directory.EnumerateFiles(otaSource))
                Register(groups, Path.GetFileName(path), path, true, MaximumTransferListBytes);
        }
        else if (IsZipContainer(otaSource))
        {
            using var archive = ZipFile.OpenRead(otaSource);
            var count = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    continue;
                if (++count > MaximumArchiveEntries)
                    throw new InvalidDataException("The incremental OTA ZIP contains too many entries.");
                var entryLeaf = Path.GetFileName(entry.FullName.Replace('\\', '/'));
                Register(groups, entryLeaf, entry.FullName.Replace('\\', '/'), false,
                    options.MaximumDatMemberBytes);
            }

            foreach (var group in groups.Values)
                group.Materialize(archive, scratch, options.MaximumDatMemberBytes, cancellationToken);
        }
        else if (DatFileSet.IsArtifact(Path.GetFileName(otaSource)))
        {
            var directory = Path.GetDirectoryName(otaSource)!;
            foreach (var path in Directory.EnumerateFiles(directory))
                Register(groups, Path.GetFileName(path), path, true, MaximumTransferListBytes);
        }
        else
        {
            throw new InvalidDataException(
                "The OTA source must be a ZIP package or a DAT artifact with its siblings beside it.");
        }

        if (groups.Count == 0)
            throw new InvalidDataException("No DAT triple was found in the OTA source.");
        if (groups.Count > MaximumTriples)
            throw new InvalidDataException("The OTA source contains too many DAT triples.");
        return [.. groups.Values.Select(group => group.ToTriple())];
    }

    private static void Register(SortedDictionary<string, SamsungDatTripleGroup> groups, string leaf,
        string location, bool isPhysicalPath, long transferListCap)
    {
        if (!DatFileSet.IsArtifact(leaf) || leaf.Length == 0)
            return;
        var stem = DatFileSet.Stem(leaf);
        if (stem.Length == 0 || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"The DAT member name '{leaf}' is not a safe file name.");
        if (!groups.TryGetValue(stem, out var group))
            groups[stem] = group = new SamsungDatTripleGroup(stem, isPhysicalPath);
        group.Add(leaf, location, transferListCap);
    }

    private static bool IsZipContainer(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return true;
        Span<byte> magic = stackalloc byte[4];
        using var file = File.OpenRead(path);
        return file.Read(magic) == 4 && magic[0] == 0x50 && magic[1] == 0x4B
            && magic[2] is 0x03 or 0x05 or 0x07 && magic[3] is 0x04 or 0x06 or 0x08;
    }

    private static async Task<Dictionary<string, string>> ResolveBaseImagesAsync(string baseFile,
        IReadOnlyList<string> plainStems, SamsungMergeScratch scratch, SamsungIncrementalMergeOptions options,
        CancellationToken cancellationToken)
    {
        var effective = baseFile;
        if (TryDetectLz4(baseFile, out var lz4Format))
        {
            effective = scratch.NewFile("samsung-base-decompressed.img");
            using (var input = File.OpenRead(baseFile))
            using (var output = new FileStream(effective, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.SequentialScan))
                CompressionService.Decompress(input, output, lz4Format, options.MaximumDecompressedBaseBytes,
                    cancellationToken);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = TryReadLpMetadata(effective);
        if (metadata is null)
        {
            var plainBase = StripSlotSuffix(WithoutImageExtension(Path.GetFileName(effective)));
            foreach (var stem in plainStems)
                if (string.Equals(stem, plainBase, StringComparison.OrdinalIgnoreCase))
                    result[stem] = effective;
            return result;
        }

        var extractionDirectory = scratch.NewDirectory("samsung-super");
        var wanted = new List<string>();
        var names = metadata.Partitions.Select(partition => partition.NameString).ToArray();
        foreach (var stem in plainStems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exact = names
                .Where(name => string.Equals(name, stem, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (exact.Length == 1)
            {
                result[stem] = Path.Combine(extractionDirectory, exact[0] + ".img");
                wanted.Add(exact[0]);
                continue;
            }

            var stripMatches = names
                .Where(name => string.Equals(StripSlotSuffix(name), stem, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (stripMatches.Length > 1)
                throw new InvalidDataException(
                    $"The slot layout for partition '{stem}' is ambiguous in the super image.");
            if (stripMatches.Length == 1)
            {
                result[stem] = Path.Combine(extractionDirectory, stripMatches[0] + ".img");
                wanted.Add(stripMatches[0]);
            }
        }

        if (wanted.Count == 0)
            return result;
        var extraction = await new LpFormatHandler()
            .ExtractSelectedAsync(effective, extractionDirectory, wanted, cancellationToken).ConfigureAwait(false);
        if (!extraction.Success)
            throw new InvalidDataException($"Logical partition extraction failed: {extraction.Summary}");
        return result;
    }

    private static LpMetadata? TryReadLpMetadata(string path)
    {
        try
        {
            return MetadataReader.ReadFromImageFile(path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool TryDetectLz4(string path, out CompressionFormat format)
    {
        format = CompressionFormat.None;
        Span<byte> magic = stackalloc byte[4];
        using var file = File.OpenRead(path);
        if (file.Read(magic) != 4)
            return false;
        if (magic[0] == 0x02 && magic[1] == 0x21 && magic[2] == 0x4C && magic[3] == 0x18)
        {
            format = CompressionFormat.Lz4Legacy;
            return true;
        }

        if (magic[0] == 0x04 && magic[1] == 0x22 && magic[2] == 0x4D && magic[3] == 0x18)
        {
            format = CompressionFormat.Lz4;
            return true;
        }

        return false;
    }

    private static async Task<SamsungIncrementalMergeResult> MergePayloadCoreAsync(string payloadPath,
        string oldImageDirectory, IReadOnlyCollection<string>? selectedPartitions, string outputDirectory,
        SamsungIncrementalMergeOptions options, IFirmwareProgress? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldImageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = Path.GetFullPath(payloadPath);
        if (!File.Exists(payload))
            throw new FileNotFoundException("The payload file is missing.", payload);
        var oldDirectory = Path.GetFullPath(oldImageDirectory);
        if (!Directory.Exists(oldDirectory))
            throw new DirectoryNotFoundException($"The old-image directory is missing: {oldDirectory}");
        var output = Path.GetFullPath(outputDirectory);

        progress?.Report(5, "Listing payload partitions");
        var plan = await ListPayloadCoreAsync(payload, cancellationToken).ConfigureAwait(false);
        var requested = selectedPartitions is { Count: > 0 }
            ? selectedPartitions.ToArray()
            : [.. plan.Partitions.Select(partition => partition.PartitionName)];

        progress?.Report(20, "Pairing old partition images");
        var oldImages = Directory.EnumerateFiles(oldDirectory, "*.img")
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
        var sourceImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<string>();
        foreach (var request in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request) || request.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"The selected partition name '{request}' is not usable.");
            var matches = plan.Partitions
                .Where(partition => MatchesPartition(partition.PartitionName, request))
                .ToList();
            if (matches.Count == 0)
                throw new InvalidDataException($"The payload does not contain partition '{request}'.");
            foreach (var entry in matches)
            {
                if (selected.Contains(entry.PartitionName, StringComparer.OrdinalIgnoreCase))
                    continue;
                var destination = Path.Combine(output, entry.PartitionName + ".img");
                if (!options.Overwrite && File.Exists(destination))
                    throw new IOException($"Output already exists and overwrite is disabled: {destination}");
                if (entry.RequiresSourceImages)
                    sourceImages[entry.PartitionName] = ResolveOldImage(oldImages, entry.PartitionName);
                selected.Add(entry.PartitionName);
            }
        }

        if (selected.Count == 0)
            throw new InvalidDataException("The payload contains no named partition to merge.");

        var staging = Path.Combine(output, ".samsung-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(staging);
            progress?.Report(40, "Merging payload partitions");
            await PayloadParser.ExtractAsync(payload, [.. selected], staging, cancellationToken,
                sourceImages: sourceImages).ConfigureAwait(false);
            progress?.Report(90, "Publishing merged images");
            var published = await PublishAsync(staging, output, options, cancellationToken).ConfigureAwait(false);
            progress?.Report(100, $"Merged {published.Count} partitions");
            return new SamsungIncrementalMergeResult(published, []);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static bool MatchesPartition(string manifestName, string request) =>
        string.Equals(manifestName, request, StringComparison.OrdinalIgnoreCase)
        || string.Equals(StripSlotSuffix(manifestName), StripSlotSuffix(request), StringComparison.OrdinalIgnoreCase);

    private static string ResolveOldImage(IReadOnlyDictionary<string, string> oldImages, string manifestName)
    {
        if (oldImages.TryGetValue(manifestName, out var exact)
            || oldImages.TryGetValue(StripSlotSuffix(manifestName), out exact))
            return exact;
        var plain = StripSlotSuffix(manifestName);
        var matches = oldImages
            .Where(entry => string.Equals(StripSlotSuffix(entry.Key), plain, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidDataException($"No old image is available for partition '{manifestName}'.");
        if (matches.Length > 1)
            throw new InvalidDataException(
                $"The slot layout for partition '{manifestName}' is ambiguous in the old-image directory.");
        return matches[0];
    }

    private static async Task<List<SamsungMergedPartitionImage>> PublishAsync(string staging, string output,
        SamsungIncrementalMergeOptions options, CancellationToken cancellationToken)
    {
        var published = new List<SamsungMergedPartitionImage>();
        foreach (var path in Directory.EnumerateFiles(staging).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leaf = Path.GetFileName(path);
            if (!leaf.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unexpected merge staging member: {leaf}");
            var destination = Path.Combine(output, leaf);
            File.Move(path, destination, overwrite: options.Overwrite);
            published.Add(new SamsungMergedPartitionImage(
                Path.GetFileNameWithoutExtension(leaf), destination, new FileInfo(destination).Length));
        }

        if (options.OnPartitionMerged is { } hook)
            foreach (var image in published)
                await hook(image, cancellationToken).ConfigureAwait(false);
        return published;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal static string StripSlotSuffix(string name)
    {
        foreach (var suffix in SlotSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                return name[..^suffix.Length];
        return name;
    }

    internal static string WithoutImageExtension(string name)
    {
        var result = name;
        foreach (var suffix in new[] { ".lz4", ".img" })
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && result.Length > suffix.Length)
                result = result[..^suffix.Length];
        return result;
    }

    private sealed record SamsungDatTriple(string Stem, string TransferListPath, string? NewDataPath,
        string? PatchDataPath);

    private sealed class SamsungDatTripleGroup(string stem, bool locationsArePaths)
    {
        private string? transferList;
        private string? newData;
        private string? patchData;

        public void Add(string leaf, string location, long transferListCap)
        {
            if (DatFileSet.IsTransferList(leaf))
            {
                if (locationsArePaths && new FileInfo(location).Length > transferListCap)
                    throw new InvalidDataException($"The transfer list of '{stem}' exceeds the size limit.");
                Assign(location, ref transferList);
                return;
            }

            if (DatFileSet.IsNewData(leaf))
            {
                Assign(location, ref newData);
                return;
            }

            Assign(location, ref patchData);
        }

        public void Materialize(ZipArchive archive, SamsungMergeScratch scratch, long memberCap,
            CancellationToken cancellationToken)
        {
            if (locationsArePaths)
                return;
            transferList = MaterializeEntry(archive, transferList, scratch, memberCap, cancellationToken);
            newData = MaterializeEntry(archive, newData, scratch, memberCap, cancellationToken);
            patchData = MaterializeEntry(archive, patchData, scratch, memberCap, cancellationToken);
        }

        public SamsungDatTriple ToTriple()
        {
            if (transferList is null)
                throw new InvalidDataException($"The DAT triple '{stem}' has no transfer list.");
            return new SamsungDatTriple(stem, transferList, newData, patchData);
        }

        private void Assign(string location, ref string? slot)
        {
            if (slot is not null)
                throw new InvalidDataException($"The DAT triple '{stem}' has duplicate members at '{location}'.");
            slot = location;
        }

        private static string? MaterializeEntry(ZipArchive archive, string? entryName, SamsungMergeScratch scratch,
            long memberCap, CancellationToken cancellationToken)
        {
            if (entryName is null)
                return null;
            var entry = archive.GetEntry(entryName)
                ?? throw new InvalidDataException($"The ZIP member '{entryName}' disappeared.");
            var separator = entryName.LastIndexOf('/');
            var leaf = separator < 0 ? entryName : entryName[(separator + 1)..];
            var target = scratch.NewFile(leaf);
            if (File.Exists(target))
                throw new InvalidDataException($"The ZIP members collide on the file name '{leaf}'.");
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                long total = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = input.Read(buffer);
                    if (read == 0)
                        break;
                    total = checked(total + read);
                    if (total > memberCap)
                        throw new InvalidDataException(
                            $"The ZIP member '{entryName}' exceeds the DAT member budget.");
                    output.Write(buffer, 0, read);
                }
            }

            return target;
        }
    }

    private sealed class SamsungMergeScratch : IDisposable
    {
        public SamsungMergeScratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "uotan-samsung-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string NewFile(string name) => Path.Combine(Root, name);

        public string NewDirectory(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => TryDeleteDirectory(Root);
    }
}
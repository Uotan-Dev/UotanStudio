using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Compress;
using UotanStudio.Compress.Compressors;
using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Services.Delivery;

/// <summary><para>导出过程中的进度快照：百分比、消息文本及可选的字节/条目/阶段详细信息。</para>
/// A progress snapshot during an export: percent, message text, and optional byte/item/stage detail.</summary>
internal sealed record DeliveryProgress(double Percent, string Message,
    (long Processed, long Total)? Bytes = null, string? Stage = null,
    (long Processed, long Total)? Items = null, string? Detail = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// A dimension the delivery equivalence gate tracks, mirroring the library façade one-to-one, used to state whether the
/// output kind requires that dimension to be equivalent.
/// <para>交付等价门禁关注的维度，与库门面的四个维度一一对应，用于说明某一维度在该输出种类下是否被要求等价。</para>
/// </summary>
internal enum DeliveryEquivalenceDimension
{
    /// <summary>Byte identity: whether the staged copy and the selected source are the same bytes. <para>字节一致性：暂存副本与所选源镜像是否同一份字节。</para></summary>
    Byte,

    /// <summary>Inode-structure equivalence, inheritable only from preserved-rebuild metadata. <para>inode 结构等价性，只能继承自受保留重建的等价元数据。</para></summary>
    Structure,

    /// <summary>File-content equivalence, inheritable only from preserved-rebuild metadata. <para>文件内容等价性，只能继承自受保留重建的等价元数据。</para></summary>
    Content,

    /// <summary><para>容量等价性，交付仅记录不要求，因为重建后的镜像不承诺适合原分区容量。</para>
    /// Capacity equivalence, which delivery records but never requires because a rebuilt image makes no fit promise.</summary>
    Capacity,
}

/// <summary>
/// The equivalence outcome for one staged image: the four-dimension report plus the set of dimensions this output kind
/// actually requires. Only a dimension that is both required and judged not equivalent aborts the delivery; an
/// unevaluated dimension never aborts it and is never reported as verified.
/// <para>单个暂存镜像的等价评估结果：四维度报告加上本条输出真正要求等价的维度集合。只有同时出现在要求集合里
/// 且被判为不等价的维度才会中止交付；不评估的维度既不会中止交付，也绝不被报告成已通过。</para>
/// </summary>
/// <param name="FileName">The staged image filename. <para>暂存镜像的文件名。</para></param>
/// <param name="Report">The four-dimension report in library façade form. <para>库门面形式的四维度报告。</para></param>
/// <param name="RequiredDimensions">The dimensions this output kind requires. <para>本条输出要求等价的维度。</para></param>
internal sealed record DeliveryStagedEquivalence(
    string FileName,
    FilesystemEquivalenceReport Report,
    IReadOnlyList<DeliveryEquivalenceDimension> RequiredDimensions)
{
    /// <summary>
    /// Returns the failure text of a required dimension judged not equivalent, or null when no required dimension failed.
    /// <para>返回被要求却判定为不等价的维度文本；没有任何维度要求失败时返回 null。</para>
    /// </summary>
    /// <value>The abort message for a failed required dimension, or null. <para>中止交付所用的失败说明，或 null。</para></value>
    internal string? RequiredFailure
    {
        get
        {
            foreach (var dimension in RequiredDimensions)
            {
                if (StateOf(dimension) == FirmwareEquivalenceState.NotEquivalent)
                    return $"Staged image failed required {Name(dimension)} equivalence: {FileName}";
            }

            return null;
        }
    }

    /// <summary>
    /// Renders the four dimension states plus the requirement set as one diagnostic line, spelling unevaluated
    /// dimensions honestly as not_evaluated.
    /// <para>把四维度状态与要求集合渲染成一行可诊断文本，未评估的维度如实写作 not_evaluated。</para>
    /// </summary>
    /// <returns><para>形如 <c>byte=equivalent(required); structure=not_evaluated</c> 的单行文本。</para>
    /// A single line such as <c>byte=equivalent(required); structure=not_evaluated</c>.</returns>
    internal string Format()
        => string.Join("; ", Enum.GetValues<DeliveryEquivalenceDimension>()
            .Select(dimension => $"{Name(dimension)}={StateText(dimension)}"));

    private string StateText(DeliveryEquivalenceDimension dimension)
    {
        var state = StateOf(dimension) switch
        {
            FirmwareEquivalenceState.Equivalent => "equivalent",
            FirmwareEquivalenceState.NotEquivalent => "not_equivalent",
            _ => "not_evaluated",
        };
        return RequiredDimensions.Contains(dimension) ? state + "(required)" : state;
    }

    private FirmwareEquivalenceState StateOf(DeliveryEquivalenceDimension dimension) => dimension switch
    {
        DeliveryEquivalenceDimension.Byte => Report.ByteEquivalence,
        DeliveryEquivalenceDimension.Structure => Report.StructureEquivalence,
        DeliveryEquivalenceDimension.Content => Report.ContentEquivalence,
        _ => Report.CapacityEquivalence,
    };

    private static string Name(DeliveryEquivalenceDimension dimension) => dimension switch
    {
        DeliveryEquivalenceDimension.Byte => "byte",
        DeliveryEquivalenceDimension.Structure => "structure",
        DeliveryEquivalenceDimension.Content => "content",
        _ => "capacity",
    };
}

/// <summary>Prepares platform tools in a staging directory for a delivery plan. <para>按交付计划在暂存目录准备平台工具。</para></summary>
internal interface IDeliveryTools
{
    /// <summary>
    /// Prepares the tools a delivery needs (platform-tools and zstd) into the bin layout under the staging directory.
    /// <para>把交付所需工具（platform-tools 与 zstd）解压/下载到 staging 目录下的 bin 布局。</para>
    /// </summary>
    Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token);
}

/// <summary><para>先在暂存目录里逐个准备每个文件，再发布：覆盖调用方批准的目标，保留其余文件。</para>
/// Prepares every file privately, then publishes: overwrites caller-approved destinations and keeps the rest.</summary>
internal sealed class DeliveryOutputService(IDeliveryTools tools, IDeliverySigningKeyProvider? signingKeys = null)
{
    /// <summary>Firmware partition names written under <c>firmware-update/</c>; the frozen set reduces each image's
    /// placement decision to a single hash lookup.
    /// <para>写入 <c>firmware-update/</c> 的固件分区名；冻结集合让每个镜像的归属判定只做一次哈希查找。</para></summary>
    private static readonly FrozenSet<string> FirmwareUpdateImages =
        new[] { "abl", "aop_config", "aop", "bluetooth", "cpucp", "devcfg", "dsp", "dtbo", "featenabler", "hyp",
            "imagefv", "keymaster", "modem", "multiimgqti", "qupfw", "recovery", "shrm", "tz", "uefi",
            "uefisecapp", "vbmeta_system", "vbmeta", "vendor_boot", "xbl_config", "xbl_ramdump", "xbl" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary><para>进度元数据里记录生成型容器等价状态的固定键名。</para>
    /// Fixed progress-metadata key recording the equivalence state of the generated containers.</summary>
    internal const string GeneratedContainerEquivalenceKey = "generated-containers";

    /// <summary><para>生成型容器（super / payload / ZIP / ZST）没有任何源身份，四个维度如实记为未评估，
    /// 绝不写成已通过。</para>
    /// Generated containers (super / payload / ZIP / ZST) hold no source identity, so all four dimensions read
    /// not_evaluated rather than a fabricated pass.</summary>
    internal const string GeneratedContainerEquivalence =
        "byte=not_evaluated; structure=not_evaluated; content=not_evaluated; capacity=not_evaluated";

    /// <summary><para>受保留重建等价元数据中记录源镜像摘要的键，与库的 FileSystemPreservation 报告一致。</para>
    /// Metadata key carrying the source digest of a preserved rebuild, matching the library's FileSystemPreservation report.</summary>
    internal const string RecordedSourceHashKey = "SourceImageHash";

    /// <summary><para>受保留重建等价元数据中记录源镜像长度的键，与库的 FileSystemPreservation 报告一致。</para>
    /// Metadata key carrying the source length of a preserved rebuild, matching the library's FileSystemPreservation report.</summary>
    internal const string RecordedSourceLengthKey = "SourceImageLength";

    private static string OrganizedImagePath(DeliveryPlan plan, string stage, DeliveryFile file)
    {
        if (!plan.PackSuper) return Path.Combine(stage, "images", file.FileName);
        return Path.Combine(stage, OrganizedImageRelativePath(file));
    }

    /// <summary>
    /// Computes a single image's relative path in the organized layout: firmware images go under firmware-update/, others at the root.
    /// <para>计算单个镜像在组织布局下的相对路径：固件镜像放入 firmware-update/，其余放根目录。</para>
    /// </summary>
    internal static string OrganizedImageRelativePath(DeliveryFile file)
    {
        var stem = Path.GetFileNameWithoutExtension(file.FileName);
        return FirmwareUpdateImages.Contains(stem) ? Path.Combine("firmware-update", file.FileName) : file.FileName;
    }

    /// <summary>
    /// Prepares every file privately in a staging directory, then atomically publishes to the output: overwrites
    /// caller-approved destinations and keeps the rest. Validation rejects false positives yet forbids the output
    /// from overwriting a selected source image—the output directory must not equal any source's own directory
    /// (avoiding images/images nesting), and no written destination may coincide with a selected source path.
    /// <para>在暂存目录中逐一准备每个文件后原子发布到输出目录：覆盖调用方批准的目标，保留其余已有文件。
    /// 校验在消除误报的同时不允许输出覆盖已选源镜像——输出目录不得等于任一源镜像所在目录（避免 images/images 嵌套），
    /// 且按组织布局写出的目标路径不得与任一选中源镜像重合。</para>
    /// </summary>
    internal async Task<string[]> ExportAsync(DeliveryPlan plan, IProgress<DeliveryProgress>? progress = null, CancellationToken token = default,
        IReadOnlySet<string>? overwrite = null)
    {
        plan = plan.ForOutput();
        DeliveryValidation.Validate(plan);
        if (string.IsNullOrWhiteSpace(plan.OutputDirectory)) throw new InvalidDataException("Select an output directory.");
        var output = Path.GetFullPath(plan.OutputDirectory);
        CheckDirectory(output);
        var sources = plan.Files.Concat(plan.Container).Where(f => f.SourcePath.Length > 0).ToArray();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var plannedOutputs = PlannedOutputPaths(plan).Select(path => Path.GetFullPath(path)).ToArray();
        foreach (var source in sources)
        {
            if (string.Equals(Path.GetFullPath(output), GetDirectoryNameOrRoot(source.SourcePath), comparison))
                throw new InvalidDataException("Output must not target a source image's own directory.");
            var sourcePath = Path.GetFullPath(source.SourcePath);
            for (var i = 0; i < plannedOutputs.Length; i++)
                if (string.Equals(sourcePath, plannedOutputs[i], comparison))
                    throw new InvalidDataException("Output would overwrite a source image: " + source.SourcePath);
        }
        foreach (var source in new[] { plan.Payload.DynamicPartitionsFile, plan.Payload.SignatureFile, plan.Payload.SigningKeyPath, plan.Payload.PostinstallFile, plan.Package.SigningKeyPath, plan.Package.CertificatePath }.Where(p => p.Length > 0))
            if (IsUnder(source, output)) throw new InvalidDataException("Output must not contain Payload configuration inputs.");
        if (DeliveryPackageBuilder.HasDat(plan) && IsUnder(DeliveryRecoveryUpdater.Resolve(plan), output))
            throw new InvalidDataException("Output must not contain the source Recovery updater.");
        if (plan.OrganizeDirectory && !plan.PackPayload && (plan.Windows || plan.Linux || plan.MacOS || plan.Txt || plan.Recovery && plan.Scripts.ContainsKey("update-binary"))
            && plan.ScriptFingerprint != DeliveryScriptGenerator.Fingerprint(plan))
            throw new InvalidDataException("Scripts are stale; generate them again.");
        var parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Invalid output directory.");
        foreach (var destination in PlannedOutputPaths(plan))
        {
            CheckDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination)) throw new IOException("A directory blocks the output: " + destination);
            if (overwrite is null && File.Exists(destination)) throw new IOException("Output already exists: " + destination);
        }
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".uotan-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var imageDirectory = plan.OrganizeDirectory && !plan.PackSuper ? Path.Combine(stage, "images") : stage;
        Directory.CreateDirectory(imageDirectory);
        try
        {
            progress?.Report(new(0, "Checking inputs", Stage: "Delivery_ProgressChecking"));
            if (plan.PackPayload) await DeliveryPayloadBuilder.BuildAsync(plan, Path.Combine(imageDirectory, "payload.bin"), progress, token, signingKeys).ConfigureAwait(false);
            if (plan.PackSuper)
            {
                if (plan.Super.Empty)
                    await DeliverySuperBuilder.BuildAsync(plan.Container, plan.Super,
                        Path.Combine(imageDirectory, "super_empty.img"),
                        null, token,
                        new InlineProgress<DeliveryProgress>(p => progress?.Report(p with { Percent = p.Percent * .55 }))).ConfigureAwait(false);
                else
                {
                    var superOutput = Path.Combine(imageDirectory, plan.Super.Zstd ? "super.img" : SuperFileName(plan.Super));
                    await DeliverySuperBuilder.BuildAsync(plan.Container, plan.Super with { Empty = false, Sparse = plan.Super.Sparse && !plan.OrganizeDirectory }, superOutput,
                        null, token,
                        new InlineProgress<DeliveryProgress>(p => progress?.Report(p with { Percent = p.Percent * .40 }))).ConfigureAwait(false);
                    if (plan.Super.Zstd)
                    {
                        await Task.Run(() =>
                        {
                            using var input = File.OpenRead(superOutput);
                            using var destination = new FileStream(Path.Combine(imageDirectory, SuperFileName(plan.Super)), FileMode.CreateNew, FileAccess.Write);
                            using var tracked = new DeliveryWriteProgressStream(destination,
                                _ => progress?.Report(new(45 + input.Position * 10.0 / Math.Max(1, input.Length), "Compressing Super",
                                    (input.Position, input.Length), "Delivery_ProgressCompress")), token);
                            ZstdCompressor.Compress(input, tracked, new CompressionOptions
                            {
                                Level = DeliverySuperOptions.ResolveZstdLevel(plan.Super),
                                MaxDegreeOfParallelism = plan.Super.ZstdChunkedParallel ? FirmwareParallelism.Workers : null
                            });
                        }, token).ConfigureAwait(false);
                        File.Delete(superOutput);
                    }
                }
            }
            var sourceIdentities = new Dictionary<string, (string Hash, long Length)>(StringComparer.Ordinal);
            if (plan.OrganizeDirectory)
            {
                var copy = plan.Files.Where(f => !f.Generated).ToArray();
                var copyLengths = copy.Select(f =>
                {
                    if (!plan.PackSuper) return new FileInfo(f.SourcePath).Length;
                    using var image = MetadataReader.OpenImage(f.SourcePath);
                    return image.Length;
                }).ToArray();
                var copyTotal = copyLengths.Sum();
                long copied = 0;
                if (copy.Length > 0) progress?.Report(new(55, "Copying images", (0, copyTotal), "Delivery_ProgressCopy"));
                for (var i = 0; i < copy.Length; i++)
                {
                    DeliveryValidation.CheckSource(copy[i], DeliveryPackageBuilder.IsDatArtifact(copy[i].FileName)
                        && copy[i].FileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));
                    var index = i;
                    var completed = copied;
                    var destination = OrganizedImagePath(plan, stage, copy[i]);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var identity = await CopyFileAsync(copy[i].SourcePath, destination, token,
                        p => progress?.Report(new(55 + (index + p / 100) * 20 / Math.Max(1, copy.Length), "Copying images",
                            (completed + (long)(copyLengths[index] * p / 100), copyTotal), "Delivery_ProgressCopy", Detail: copy[index].FileName)),
                        expandSparse: plan.PackSuper).ConfigureAwait(false);
                    if (identity.HasValue) sourceIdentities[copy[i].SourcePath] = identity.Value;
                    copied += copyLengths[i];
                }
                await tools.PrepareAsync(plan, stage, progress, token).ConfigureAwait(false);
                foreach (var (name, contents) in plan.Scripts)
                {
                    if (!DeliveryValidation.IsScriptName(name))
                        throw new InvalidDataException("Invalid script filename: " + name);
                    if (Encoding.UTF8.GetByteCount(contents) > 1024 * 1024) throw new InvalidDataException("Script exceeds 1 MiB.");
                    if (name == "updater-script" || name == "update-binary") continue;
                    var path = Path.Combine(stage, name);
                    await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false), token).ConfigureAwait(false);
                    if (name.EndsWith(".sh"))
                        DeliveryUnixPermissions.SetExecutable(path);
                }
                var payloadNotice = plan.Payload.SigningKeyPath.Length == 0 ? "Unsigned full payload. " : "RSA SHA-256 signed full payload. ";
                var selfInstaller = plan.Recovery && !plan.PackPayload && !plan.PackSuper || plan.PackSuper && !plan.Super.Empty;
                var readme = (plan.PackPayload ? payloadNotice + ZipFileName(plan, "ota.zip") + " is an A/B OTA package, not a Fastboot package. Unzipping payload.bin does not make it flashable with fastboot. Recovery must support A/B OTA and trust the Payload signing key; disabling ZIP verification does not disable Payload verification. Missing device/build metadata may require target-specific OTA integration. Standalone images are separate from this OTA.\n\n" : "") + "UotanStudio delivery\n\n" + plan.Information + "\n\nImages: " + (plan.PackSuper ? "./" : "images/") + "\nTools: bin/\n"
                    + "Linux/macOS: chmod +x Flash*.sh bin/Linux/* bin/macOS/* before running when ZIP extraction loses executable permissions.\n"
                    + "TXT: select the intended complete variant. The legacy toolbox does not enforce alternative groups or device checks.\n"
                    + "Legacy TXT paths containing spaces may not work in the old toolbox.\n"
                    + "Scripts target the partition names and slot policy selected by the author.\n"
                    + "An unlocked bootloader is required. Images verified by AVB also need matching patched vbmeta, otherwise the device may refuse to boot.\n"
                    + (selfInstaller ? "The package carries its own update-binary and needs a custom recovery; stock recovery rejects packages it cannot verify.\n" : "")
                    + (plan.PackSuper ? "The Super image is built with the Super size entered in the plan; the scripts compare it against the device reading before flashing super.\n" : "");
                await File.WriteAllTextAsync(Path.Combine(stage, "README.txt"), readme, token).ConfigureAwait(false);
            }
            await DeliveryPackageBuilder.BuildAsync(plan, stage, imageDirectory, token, progress, signingKeys).ConfigureAwait(false);
            if (plan.PackPayload && plan.OrganizeDirectory)
            {
                File.Delete(Path.Combine(imageDirectory, "payload.bin"));
                File.Delete(Path.Combine(imageDirectory, "payload_properties.txt"));
            }
            if (plan.PackPayload && !plan.OrganizeDirectory)
                File.Delete(Path.Combine(imageDirectory, "payload_properties.txt"));
            var stagedFiles = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var (_, stagedHashes) = await VerifyStagedEquivalenceWithHashesAsync(plan, stage, progress, token, sourceIdentities).ConfigureAwait(false);
            if (plan.OrganizeDirectory)
            {
                long totalBytes = 0;
                foreach (var file in stagedFiles)
                    if (!stagedHashes.ContainsKey(file)) totalBytes = checked(totalBytes + new FileInfo(file).Length);
                long processedBytes = 0;
                progress?.Report(new(90, "Verifying output", Stage: "Delivery_ProgressVerify", Bytes: (0, totalBytes)));
                var hashLines = new string[stagedFiles.Length];
                var degree = Math.Max(1, Math.Min(stagedFiles.Length, FirmwareParallelism.Workers));
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, stagedFiles.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = token },
                    async (i, ct) =>
                    {
                        var line = Path.GetRelativePath(stage, stagedFiles[i]).Replace('\\', '/');
                        if (stagedHashes.TryGetValue(stagedFiles[i], out var cached))
                        {
                            hashLines[i] = cached + "  " + line;
                            return;
                        }
                        var buffer = new byte[1024 * 1024];
                        await using var input = new FileStream(stagedFiles[i], FileMode.Open, FileAccess.Read, FileShare.Read,
                            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var fileLength = input.Length;
                        var hashAlgorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
                        {
                            hashAlgorithm.AppendData(buffer, 0, read);
                            var done = Interlocked.Add(ref processedBytes, read);
                            progress?.Report(new(90 + done * 9.0 / Math.Max(1, totalBytes), "Verifying output",
                                Stage: "Delivery_ProgressVerify", Bytes: (done, totalBytes), Detail: Path.GetFileName(stagedFiles[i])));
                        }
                        var hash = hashAlgorithm.GetHashAndReset();
                        hashAlgorithm.Dispose();
                        if (input.Length != fileLength) throw new IOException("Output changed while verifying.");
                        hashLines[i] = Convert.ToHexStringLower(hash) + "  " + line;
                    }).ConfigureAwait(false);
                var hashes = new StringBuilder();
                foreach (var line in hashLines) hashes.AppendLine(line);
                await File.WriteAllTextAsync(Path.Combine(stage, "checksums.sha256"), hashes.ToString(), token).ConfigureAwait(false);
            }
            if (plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty)
            {
                var archive = Path.Combine(stage, "super.zip");
                await CreateSuperArchiveAsync(stage, archive, token).ConfigureAwait(false);
                if (plan.Package.SigningKeyPath.Length > 0)
                    await DeliveryPackageBuilder.SignAsync(archive, plan.Package, token).ConfigureAwait(false);
                foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
                    .Where(path => !path.Equals(archive, StringComparison.OrdinalIgnoreCase)).ToArray())
                    File.Delete(file);
                foreach (var directory in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length).ToArray())
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            var result = Publish(stage, output, token, overwrite);
            progress?.Report(new(100, "Output complete", Stage: "Delivery_ProgressDone"));
            return result;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    /// <summary>
    /// Verifies every staged image copy through the library's public equivalence façade before checksums are written and
    /// folds the four dimensions into per-file progress metadata. The requirement matrix follows the output kind: a pure
    /// copy requires the byte dimension, which is evaluated for real against the source image's digest and length, while
    /// structure, content and capacity stay unevaluated because no library computation runs on that path; a copy carrying
    /// preserved-rebuild metadata whose recorded source identity still matches the source on disk additionally requires
    /// structure and content, and records capacity without requiring it because delivery never promises the rebuilt image
    /// still fits the original partition (Super fit is hard-checked separately by DeliverySuperCapacity); metadata that
    /// cannot be bound to the source because it records no source identity inherits nothing and still must not fail,
    /// while metadata whose recorded identity no longer matches the source is stale evidence and aborts the delivery
    /// rather than being silently downgraded; Super packing expands sparse sources into their logical image, whose bytes
    /// legitimately differ and whose source-side evidence no longer describes the artifact, so every dimension stays
    /// unevaluated there; generated containers (super.img / super_empty.img / payload.bin / ota.zip / super.zip /
    /// recovery.zip) hold no source identity at all and are reported honestly as unevaluated instead of verified.
    /// Only a dimension that is both required and judged not equivalent aborts the delivery; an unevaluated dimension
    /// neither fails nor passes. Nothing has been published when that happens, the staging directory is discarded, and
    /// the caller's previous output stays untouched.
    /// <para>在写校验和之前，逐个校验交付暂存的镜像副本，并把四维度结果按文件汇总进进度元数据。要求矩阵按输出种类划定：</para>
    /// <para>· 纯拷贝（组织目录输出且未做 Super 展开）：字节维度用源镜像的真实摘要与长度评估并被要求等价；
    /// 结构、内容与容量在交付路径上没有任何库侧计算，保持不评估。</para>
    /// <para>· 附带受保留重建等价元数据、且元数据记录的源摘要与长度与磁盘上的源镜像完全一致：结构与内容维度成为要求项，
    /// 容量只记录不要求——交付不承诺重建后的镜像仍适合原分区容量，Super 的容量由 DeliverySuperCapacity 单独硬校验。</para>
    /// <para>· 元数据无法与源镜像绑定（缺少记录的源摘要或长度）时不继承任何维度，也不失败，只如实报告不评估；
    /// 元数据记录的源身份与磁盘上的源镜像不符时说明证据已过期，直接中止交付而不是悄悄降级。</para>
    /// <para>· Super 打包会把稀疏源展开为逻辑镜像，产出字节本就与源不同，且源侧的重建证据不再描述展开后的产物，
    /// 因此该模式下四个维度一律不评估，仅在 Details 中保留源侧元数据供诊断。</para>
    /// <para>· 生成型容器（super.img / super_empty.img / payload.bin / ota.zip / super.zip / recovery.zip）没有源身份，
    /// 四个维度如实记为不评估，绝不当作已通过。</para>
    /// <para>只有“被要求且被判定为不等价”的维度才会中止交付；不评估既不会让交付失败，也绝不报告为成功。中止时尚未
    /// 发布任何文件，暂存目录由调用方的 finally 清理，用户既有输出保持不变。</para>
    /// </summary>
    internal static async Task<IReadOnlyList<DeliveryStagedEquivalence>> VerifyStagedEquivalenceAsync(DeliveryPlan plan, string stage,
        IProgress<DeliveryProgress>? progress, CancellationToken token,
        IReadOnlyDictionary<string, (string Hash, long Length)>? fusedSourceIdentities = null)
        => (await VerifyStagedEquivalenceWithHashesAsync(plan, stage, progress, token, fusedSourceIdentities).ConfigureAwait(false)).Rows;

    /// <summary>
    /// Performs the same equivalence verification as <see cref="VerifyStagedEquivalenceAsync"/> and additionally returns the
    /// SHA-256 of every staged image whose byte dimension was evaluated (lowercase hexadecimal, keyed by the staged absolute
    /// path) so the checksums.sha256 stage can reuse them instead of re-reading the same multi-gigabyte images.
    /// <para>与 <see cref="VerifyStagedEquivalenceAsync"/> 相同的等价校验，同时返回每个已评估字节维度的暂存镜像
    /// SHA-256（小写十六进制，键为暂存绝对路径），供 checksums.sha256 阶段复用，避免对同一批 GB 级镜像再做一次
    /// 全量读取。</para>
    /// </summary>
    /// <param name="plan">Delivery plan supplying the file set and options under verification.
    /// <para>提供待校验文件集合与选项的交付计划。</para></param>
    /// <param name="stage">Staged output directory holding the copied images. <para>保存已拷贝镜像的暂存输出目录。</para></param>
    /// <param name="progress">Optional progress sink; may be <see langword="null"/>. <para>可选进度接收端，可为 <see langword="null"/>。</para></param>
    /// <param name="token">Cancels hashing and verification. <para>取消哈希与校验的令牌。</para></param>
    /// <param name="fusedSourceIdentities">
    /// Source image identities folded out of the staging copy pass, keyed by the verbatim
    /// <see cref="DeliveryFile.SourcePath"/>; when an entry is present the source image is not SHA-256ed a second time.
    /// Only pure byte copies are recorded, and files without an entry are still read from disk.
    /// <para>暂存复制阶段顺带算出的源镜像身份，键为 <see cref="DeliveryFile.SourcePath"/> 原文；命中时本方法不再对
    /// 源镜像做第二次全量 SHA-256。只收录逐字节纯拷贝得到的条目，缺少条目的文件仍按原路径重读。</para>
    /// </param>
    internal static async Task<(IReadOnlyList<DeliveryStagedEquivalence> Rows, IReadOnlyDictionary<string, string> StagedSha256)>
        VerifyStagedEquivalenceWithHashesAsync(DeliveryPlan plan, string stage,
        IProgress<DeliveryProgress>? progress, CancellationToken token,
        IReadOnlyDictionary<string, (string Hash, long Length)>? fusedSourceIdentities = null)
    {
        var results = new List<DeliveryStagedEquivalence>();
        var stagedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!plan.OrganizeDirectory) return (results, stagedHashes);
        var indices = new List<int>();
        for (var i = 0; i < plan.Files.Count; i++)
        {
            if (plan.Files[i].Generated || plan.Files[i].SourcePath.Length == 0) continue;
            indices.Add(i);
        }
        var evaluated = new (DeliveryStagedEquivalence Result, string? StagedSha256)?[indices.Count];
        var failures = new Exception?[indices.Count];
        var degree = Math.Max(1, Math.Min(indices.Count, FirmwareParallelism.Workers));
        await Parallel.ForEachAsync(
            Enumerable.Range(0, indices.Count),
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = token },
            async (slot, ct) =>
            {
                var file = plan.Files[indices[slot]];
                var staged = OrganizedImagePath(plan, stage, file);
                if (!File.Exists(staged))
                {
                    failures[slot] = new IOException("Staged image is missing: " + file.FileName);
                    return;
                }
                progress?.Report(new(90, "Verifying image equivalence",
                    Stage: "Delivery_ProgressEquivalence", Detail: file.FileName));
                var result = await EvaluateStagedEquivalenceAsync(file, staged, plan.PackSuper, ct,
                    FusedSourceIdentity(fusedSourceIdentities, file)).ConfigureAwait(false);
                var failure = result.Result.RequiredFailure;
                if (failure is not null)
                {
                    failures[slot] = new IOException(failure);
                    return;
                }
                evaluated[slot] = result;
            }).ConfigureAwait(false);
        foreach (var failure in failures)
            if (failure is not null) throw failure;
        for (var slot = 0; slot < evaluated.Length; slot++)
        {
            if (evaluated[slot] is not { } entry) continue;
            results.Add(entry.Result);
            if (entry.StagedSha256 is { } hash) stagedHashes[OrganizedImagePath(plan, stage, plan.Files[indices[slot]])] = hash;
        }
        if (results.Count > 0)
        {
            var summary = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GeneratedContainerEquivalenceKey] = GeneratedContainerEquivalence,
            };
            foreach (var result in results) summary[result.FileName] = result.Format();
            progress?.Report(new(90, "Image equivalence verified", Stage: "Delivery_ProgressEquivalence", Metadata: summary));
        }
        return (results, stagedHashes);
    }

    /// <summary>
    /// Evaluates one staged image across the four dimensions and lists the dimensions this output kind requires. The byte
    /// dimension is evaluated only when the copy was not expanded into its logical image, because it measures whether the
    /// staged copy and the selected source are the same bytes; structure, content and capacity can only be inherited from
    /// preserved-rebuild metadata, and only when that metadata's recorded source digest and length still match the source
    /// on disk, since otherwise the evidence describes a different file. The inherited byte verdict stays readable in
    /// Details, while the report's byte dimension is always replaced by delivery's own copy check.
    /// <para>评估单个暂存镜像的四个等价维度并给出本条输出所要求的维度。字节维度只在拷贝未被展开为逻辑镜像时评估，
    /// 因为该维度衡量的是“暂存副本与所选源是否同一份字节”；结构、内容与容量只能继承自受保留重建的等价元数据，
    /// 且只有当元数据记录的源摘要与长度与磁盘上的源镜像完全一致时才继承——否则这份证据描述的不是眼前这份文件。
    /// 继承而来的字节判定保留在 Details 中原文可查，报告的字节维度始终替换为交付自己算出的拷贝校验结果。</para>
    /// </summary>
    /// <param name="file">Delivered file entry whose equivalence is evaluated. <para>要评估等价性的交付条目。</para></param>
    /// <param name="stagedPath">Staged copy of the source image. <para>源镜像的暂存副本路径。</para></param>
    /// <param name="expandedToLogicalImage">True when staging expanded the source through the sparse layout, so the
    /// container bytes no longer describe the source. <para>暂存已按稀疏布局展开源数据时为 true，此时容器字节不再等同源字节。</para></param>
    /// <param name="token">Cancels hashing and verification. <para>取消哈希与校验。</para></param>
    /// <param name="fusedIdentity">
    /// The digest and length of the same source bytes folded out of the staging copy pass; when null the source image is
    /// re-read here instead. It is only supplied for a pure byte copy (that is, when
    /// <paramref name="expandedToLogicalImage"/> is <see langword="false"/> and the copy was not expanded through the
    /// sparse layout), so it describes exactly those bytes, and the staged copy is still read and hashed separately so a
    /// botched copy keeps getting caught.
    /// <para>暂存复制阶段顺带算出的同一份源字节摘要与长度；为 null 时本方法自己重读源镜像。它只在逐字节纯拷贝
    /// （即 <paramref name="expandedToLogicalImage"/> 为 false，拷贝未按稀疏布局展开）时才会被提供，因此描述的就是
    /// 同一批字节；暂存副本仍会单独读取并哈希，拷贝错误照样能被发现。</para>
    /// </param>
    /// <returns>The equivalence row plus the staged image's lowercase SHA-256 when the byte dimension was evaluated,
    /// otherwise <see langword="null"/> for the hash.
    /// <para>等价结果行；评估了字节维度时附带暂存镜像的小写 SHA-256，否则为 <see langword="null"/>。</para></returns>
    internal static async Task<(DeliveryStagedEquivalence Result, string? StagedSha256)> EvaluateStagedEquivalenceAsync(DeliveryFile file,
        string stagedPath, bool expandedToLogicalImage, CancellationToken token,
        (string Hash, long Length)? fusedIdentity = null)
    {
        var details = file.RebuildEquivalence ?? new Dictionary<string, string>();
        var required = new List<DeliveryEquivalenceDimension>();
        var inherited = false;
        FirmwareEquivalenceState byteState = FirmwareEquivalenceState.NotEvaluated;
        string? stagedSha256 = null;
        if (!expandedToLogicalImage)
        {
            required.Add(DeliveryEquivalenceDimension.Byte);
            var (sourceHash, sourceLength) = fusedIdentity ?? await ReadSourceIdentityAsync(file.SourcePath, token).ConfigureAwait(false);
            if (file.RebuildEquivalence is { Count: > 0 } && TryRecordedSourceIdentity(details, out var recordedHash, out var recordedLength))
            {
                if (sourceLength != recordedLength
                    || !string.Equals(sourceHash, recordedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Equivalence metadata no longer matches its source image: " + file.FileName);
                inherited = true;
            }

            (byteState, stagedSha256) = await Task.Run(() =>
            {
                using var produced = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.SequentialScan);
                if (produced.Length != sourceLength) return (FirmwareEquivalenceState.NotEquivalent, null);
                using var hashing = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = produced.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    hashing.AppendData(buffer, 0, read);
                }
                var hash = Convert.ToHexStringLower(hashing.GetHashAndReset());
                var state = string.Equals(hash, sourceHash, StringComparison.OrdinalIgnoreCase)
                    ? FirmwareEquivalenceState.Equivalent
                    : FirmwareEquivalenceState.NotEquivalent;
                return (state, (string?)hash);
            }, token).ConfigureAwait(false);
        }

        if (inherited)
        {
            required.Add(DeliveryEquivalenceDimension.Structure);
            required.Add(DeliveryEquivalenceDimension.Content);
        }

        var provenance = FilesystemEquivalenceReport.FromMetadata(details);
        return (new DeliveryStagedEquivalence(file.FileName, new FilesystemEquivalenceReport
        {
            ByteEquivalence = byteState,
            StructureEquivalence = inherited ? provenance.StructureEquivalence : FirmwareEquivalenceState.NotEvaluated,
            ContentEquivalence = inherited ? provenance.ContentEquivalence : FirmwareEquivalenceState.NotEvaluated,
            CapacityEquivalence = inherited ? provenance.CapacityEquivalence : FirmwareEquivalenceState.NotEvaluated,
            Details = details,
        }, required), stagedSha256);
    }

    /// <summary>
    /// Reads an image's lowercase SHA-256 digest and length through a bounded streaming buffer, observing the token per
    /// block. It is skipped when the staging copy already handed over those same bytes; see <see cref="CopyFileAsync"/>.
    /// <para>读取镜像的 SHA-256 摘要（小写十六进制）与长度，流式有界缓冲，取消令牌逐块观察。暂存复制已经交出过
    /// 同一批字节时不再走这里，见 <see cref="CopyFileAsync"/>。</para>
    /// </summary>
    private static async Task<(string Hash, long Length)> ReadSourceIdentityAsync(string path, CancellationToken token)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = source.Length;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        return (hash, length);
    }

    /// <summary>
    /// Looks up the source identity the staging copy recorded for this image; returns <see langword="null"/> when nothing
    /// was recorded (a caller outside the organized layout, or an expanded copy) so the caller falls back to re-reading.
    /// <para>取出暂存复制阶段为该镜像记下的源身份；没有记录（例如非组织目录布局的调用方或展开过的拷贝）时返回
    /// <see langword="null"/>，由调用方回退到重读源镜像。</para>
    /// </summary>
    private static (string Hash, long Length)? FusedSourceIdentity(
        IReadOnlyDictionary<string, (string Hash, long Length)>? identities, DeliveryFile file)
        => identities is not null && identities.TryGetValue(file.SourcePath, out var identity) ? identity : null;

    /// <summary>
    /// Extracts the source digest and length the library recorded (SourceImageHash, SourceImageLength) to decide whether
    /// the evidence still describes the selected source; a missing or malformed key means "cannot bind" and is never guessed.
    /// <para>从等价元数据里取出库记录的源摘要与源长度（SourceImageHash、SourceImageLength），用于判断这份证据是否
    /// 仍描述当前选中的源镜像；两键任一缺失或格式不合法都按“无法绑定”处理，不猜测。</para>
    /// </summary>
    private static bool TryRecordedSourceIdentity(IReadOnlyDictionary<string, string> details, out string hash, out long length)
    {
        hash = "";
        length = 0;
        if (!details.TryGetValue(RecordedSourceHashKey, out var recordedHash)
            || recordedHash.Length != 64 || !recordedHash.All(static character => char.IsAsciiHexDigit(character)))
            return false;
        if (!details.TryGetValue(RecordedSourceLengthKey, out var recordedLengthText)
            || !long.TryParse(recordedLengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var recordedLength)
            || recordedLength < 0)
            return false;
        hash = recordedHash;
        length = recordedLength;
        return true;
    }

    /// <summary>
    /// Returns the generated container filename in the organized layout (ota.zip / super.zip / super_empty.img / super.img / super.zst / payload.bin).
    /// <para>返回组织布局下生成的容器文件名（ota.zip / super.zip / super_empty.img / super.img / super.zst / payload.bin）。</para>
    /// </summary>
    internal static string ContainerFileName(DeliveryPlan plan) => plan.PackPayload
        ? (plan.OrganizeDirectory ? ZipFileName(plan, "ota.zip") : "payload.bin")
        : plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty
            ? ZipFileName(plan, "super.zip")
            : plan.Super.Empty ? "super_empty.img" : SuperFileName(plan.Super);

    /// <summary><para>打包成 ZIP 时使用用户指定的压缩包名称（补 .zip），空则回退默认名。</para>
    /// Uses the user-specified archive name (appending .zip) when packing into a ZIP; falls back to the default.</summary>
    internal static string ZipFileName(DeliveryPlan plan, string fallback) =>
        plan.OrganizeDirectory && plan.ArchiveName.Trim().Length > 0 ? plan.ArchiveName.Trim() + ".zip" : fallback;

    /// <summary><para>返回 Super 镜像文件名：压缩时为 super.zst，否则为 super.img。</para>
    /// Returns the Super image filename: super.zst when compressed, otherwise super.img.</summary>
    internal static string SuperFileName(DeliverySuperOptions options) => options.Zstd ? "super.zst" : "super.img";

    private static async Task CreateSuperArchiveAsync(string stage, string archivePath, CancellationToken token)
    {
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var source in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(archivePath, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetRelativePath(stage, source).Replace('\\', '/');
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2009, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if (name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                || name.Equals("META-INF/com/google/android/update-binary", StringComparison.Ordinal))
                entry.ExternalAttributes = 0x81ED << 16;
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = entry.Open();
            await input.CopyToAsync(destination, 1024 * 1024, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Streams a source file to a destination with progress; when expandSparse is set, sparse images are expanded in the
    /// staging directory. An unexpanded copy is byte for byte, so the bytes read from the source are folded into a SHA-256
    /// on the way through and the source identity (lowercase digest plus length) is returned, which saves equivalence
    /// verification a second full read of the same multi-gigabyte source bytes. The expanded path yields the logical image
    /// rather than the source file's bytes, so a digest would say nothing about them and <see langword="null"/> is returned.
    /// <para>流式复制源文件到目标并报告进度；expandSparse 时把稀疏镜像展开（Recovery 按物理块写入桌面暂存目录）。
    /// 未展开时这份拷贝是逐字节的，因此顺带把读到的源字节并入 SHA-256，返回源身份（小写十六进制摘要与长度），
    /// 让等价校验不必再对同一批 GB 级源字节做第二次全量读取；展开路径产出的是逻辑镜像而不是源文件的字节，
    /// 摘要对它没有意义，返回 <see langword="null"/>。</para>
    /// </summary>
    /// <returns>The source digest and length for a pure byte copy, or <see langword="null"/> when the copy was expanded.
    /// <para>逐字节拷贝返回源的摘要与长度；展开过的拷贝返回 <see langword="null"/>。</para></returns>
    internal static async Task<(string Hash, long Length)?> CopyFileAsync(string source, string destination, CancellationToken token, Action<double>? progress = null,
        bool expandSparse = false)
    {
        await using var input = expandSparse ? MetadataReader.OpenImage(source)
            : new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var length = input.Length;
        using IncrementalHash? hashing = expandSparse ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long copied = 0, last = 0;
        while (copied < length)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Source truncated while copying.");
            hashing?.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            copied += count;
            if (Environment.TickCount64 - last >= 100) { progress?.Invoke(copied * 100.0 / Math.Max(1, length)); last = Environment.TickCount64; }
        }
        if (input.Length != length) throw new IOException("Source changed while copying.");
        progress?.Invoke(100);
        if (hashing is null) return null;
        return (Convert.ToHexStringLower(hashing.GetHashAndReset()), length);
    }

    /// <summary><para>计算本次输出会写到的全部目标文件路径（完整绝对路径，含组织目录布局、脚本与容器）。</para>
    /// Computes every destination file path this export would write: full absolute paths including the organized layout, scripts, and containers.</summary>
    internal static IEnumerable<string> PlannedOutputPaths(DeliveryPlan plan)
    {
        plan = plan.ForOutput();
        var output = Path.GetFullPath(plan.OutputDirectory);
        if (plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty)
            return [Path.Combine(output, ZipFileName(plan, "super.zip"))];
        var plannedNames = plan.OrganizeDirectory
            ? plan.Files.Select(f => f.Generated && plan.PackPayload ? ZipFileName(plan, "ota.zip") : plan.PackSuper ? OrganizedImageRelativePath(f) : Path.Combine("images", f.FileName)).Concat(plan.Scripts.Keys.Where(name => name is not ("updater-script" or "update-binary"))).Concat(["README.txt", "checksums.sha256"])
            : [ContainerFileName(plan)];
        if (plan.PackPayload && plan.OrganizeDirectory) plannedNames = plannedNames.Append(ZipFileName(plan, "ota.zip"));
        if (plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty)
            plannedNames = plannedNames.Concat(["META-INF/com/google/android/update-binary", "META-INF/com/google/android/updater-script"]);
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper) plannedNames = plannedNames.Append(ZipFileName(plan, "recovery.zip"));
        return plannedNames.Distinct(StringComparer.Ordinal).Select(name => Path.Combine(output, name));
    }

    /// <summary><para>提前扫描输出目录，返回本次输出会覆盖的已存在目标文件（排序去重）。</para>
    /// Scans the output directory in advance and returns planned destination files that already exist, sorted and deduplicated.</summary>
    internal static string[] FindExistingOutputs(DeliveryPlan plan)
        => [.. PlannedOutputPaths(plan).Where(File.Exists).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string[] Publish(string stage, string output, CancellationToken token, IReadOnlySet<string>? overwrite = null)
    {
        var files = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Select(p => (Source: p, Destination: Path.Combine(output, Path.GetRelativePath(stage, p)))).ToArray();
        var moved = new List<(string Source, string Destination)>();
        var created = new List<string>();
        var backups = new List<(string Backup, string Destination)>();
        var skipped = new List<string>();
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var parent = Path.GetDirectoryName(file.Destination)!;
                var missing = new Stack<string>();
                for (var p = parent; !Directory.Exists(p); p = Path.GetDirectoryName(p)!) missing.Push(p);
                while (missing.TryPop(out var p)) { Directory.CreateDirectory(p); created.Add(p); }
                CheckDirectory(parent);
                if (Path.Exists(file.Destination))
                {
                    if (Directory.Exists(file.Destination)) throw new IOException("A directory blocks the output: " + file.Destination);
                    if (overwrite is null || !overwrite.Contains(file.Destination))
                    {
                        skipped.Add(file.Destination);
                        continue;
                    }
                    var backup = Path.Combine(parent, ".uotan-original-" + Guid.NewGuid().ToString("N"));
                    File.Move(file.Destination, backup, false);
                    backups.Add((backup, file.Destination));
                }
                File.Move(file.Source, file.Destination, false);
                moved.Add(file);
            }
            foreach (var backup in backups) File.Delete(backup.Backup);
            return [.. moved.Select(f => f.Destination)];
        }
        catch
        {
            foreach (var file in moved.AsEnumerable().Reverse()) File.Move(file.Destination, file.Source, false);
            foreach (var backup in backups.AsEnumerable().Reverse()) if (File.Exists(backup.Backup)) File.Move(backup.Backup, backup.Destination, false);
            foreach (var p in created.AsEnumerable().Reverse()) if (!Directory.EnumerateFileSystemEntries(p).Any()) Directory.Delete(p);
            throw;
        }
    }

    /// <summary>
    /// Walks up to the deepest already existing non-link directory, throwing if any checked component is blocked by a
    /// file or is a symlink, so the output directory can be created safely. The walk stops there because ancestors above
    /// it are chosen by the user or the operating system, not by the delivery plan; macOS resolves
    /// <c>Path.GetTempPath()</c> through the <c>/var</c> to <c>/private/var</c> symbolic link, so an unbounded walk
    /// rejects every legitimate output directory.
    /// <para>
    /// 沿目录向上逐级检查到已存在的最深非链接目录为止：任一被检查的组件被文件占用或为符号链接则抛错，
    /// 保证输出目录可安全创建。边界以上的祖先由用户或操作系统选定而不受交付计划控制，因此不参与判定；
    /// macOS 上 <c>/var</c> 是指向 <c>/private/var</c> 的符号链接，临时目录必定经由它解析，
    /// 无界遍历会拒绝所有合法的输出目录。
    /// </para>
    /// </summary>
    internal static void CheckDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var boundary = DeepestExistingAncestor(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var p = full;
            p is not null && !string.Equals(p, boundary, comparison);
            p = Path.GetDirectoryName(p))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(p);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked output directories are not supported: " + p);
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("A file blocks the output directory: " + p);
        }
        if (File.Exists(boundary))
        {
            throw new IOException("A file blocks the output directory: " + boundary);
        }
        if (Directory.Exists(boundary) && (File.GetAttributes(boundary) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Linked output directories are not supported: " + boundary);
        }
    }

    /// <summary>
    /// Returns the deepest existing non-link component. A symbolic link is skipped so the walk can keep searching for a
    /// real directory boundary above it, while the component walk still inspects every such link and therefore still
    /// rejects a link planted inside the output root.
    /// <para>
    /// 返回路径上已存在的最深非链接组件。链接本身会被跳过并继续向上寻找真正的目录边界，
    /// 而遍历循环仍会逐级检查这些链接，因此在输出根目录内植入符号链接依然会被拒绝。
    /// </para>
    /// </summary>
    private static string DeepestExistingAncestor(string full)
    {
        for (var current = Path.GetDirectoryName(full); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            return current;
        }
        return Path.GetPathRoot(full) ?? full;
    }

    private static bool IsUnder(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary><para>返回路径所在目录；已是目录则原样返回。空/根路径回退自身。</para>
    /// Returns the containing directory of a path, or the path itself when it is already a directory.</summary>
    private static string GetDirectoryNameOrRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var full = Path.GetFullPath(path);
        return Directory.Exists(full)
            ? Path.TrimEndingDirectorySeparator(full)
            : Path.GetDirectoryName(full) is { Length: > 0 } parent ? Path.TrimEndingDirectorySeparator(parent) : full;
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
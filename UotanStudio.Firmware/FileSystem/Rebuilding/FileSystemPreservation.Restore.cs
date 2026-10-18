using System.Globalization;
using System.Text;
using System.Text.Json;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.
/// <para>保留源镜像语义，并把编辑后的宿主文件树解析为使用原始或继承 Linux 属性的文件系统构建器。</para>
/// </summary>
public static partial class FileSystemPreservation
{
    /// <summary>Rebuilds the extraction tree using validated source metadata, without retaining the original image. An optional output limit bounds the emitted file extent before publication. For sparse output this is the container size, not the logical partition size.
    /// <para>使用经过校验的源元数据重建解包文件树，不保留原始镜像。可选的输出上限会在发布前限制写出文件大小； 对稀疏输出而言该上限是容器大小，而非逻辑分区大小。</para></summary>
    /// <param name="inputDirectory">Extracted tree whose <c>.uotan</c> metadata carries the preservation payload.
    /// <para>其 <c>.uotan</c> 元数据携带保留负载的解包目录。</para></param>
    /// <param name="outputPath">Image file to write atomically (overwriting); it must lie outside the extracted tree and its preservation data.
    /// <para>以原子方式写出（允许覆盖）的镜像文件；必须位于解包树及其保留数据之外。</para></param>
    /// <param name="format">Image format name; must equal the metadata format, including the sparse container name.
    /// <para>镜像格式名；必须与元数据格式一致，稀疏容器同理。</para></param>
    /// <param name="cancellationToken">Cancels manifest reads, hashing, rebuilding and verification.
    /// <para>取消清单读取、哈希、重建与校验。</para></param>
    /// <param name="rebuildReport">Optional callback receiving the post-publish equivalence report (byte identity, structure, content and capacity flags, plus the EROFS compression profile keys) after a successful preserved rebuild; it is invoked for every preserved rebuild format. May be <see langword="null"/>.
    /// <para>可选回调；受保留重建成功发布后接收等价报告（字节一致性、结构、内容与容量标记，以及 EROFS 压缩档案 键），对每种受保留重建格式都会触发。可为 <see langword="null"/>。</para></param>
    /// <param name="maximumOutputBytes">Optional positive hard cap on written bytes enforced during publication, or <see langword="null"/> for no limit.
    /// <para>可选的正数写出字节硬上限，在发布期间强制；<see langword="null"/> 表示不限制。</para></param>
    /// <param name="attributeEdits">Optional ownership/permission edits applied to the edit plan before building; guards and bounds are enforced per <see cref="FileSystemAttributeEdit"/>. May be <see langword="null"/>.
    /// <para>可选的属主/权限编辑，构建前应用到编辑计划；守卫与边界按 <see cref="FileSystemAttributeEdit"/> 所述强制。 可为 <see langword="null"/>。</para></param>
    /// <param name="xattrEdits">Optional user-namespace xattr edits applied before building. May be <see langword="null"/>.
    /// <para>可选的 user 命名空间扩展属性编辑，构建前应用。可为 <see langword="null"/>。</para></param>
    /// <param name="progress">Optional staged reporter for the rebuild phases: the extracted-tree projection is reported as <see cref="FileSystemWorkStage.VerifyingFiles"/>, the image build as <see cref="FileSystemWorkStage.BuildingImage"/>, and the rebuilt-image re-read plus the published-image digest as <see cref="FileSystemWorkStage.VerifyingImage"/>. May be <see langword="null"/>.
    /// <para>可选的重建阶段回调：解包树投影上报为 <see cref="FileSystemWorkStage.VerifyingFiles"/>，镜像构建上报为 <see cref="FileSystemWorkStage.BuildingImage"/>，重建镜像的复读与已发布镜像的摘要上报为 <see cref="FileSystemWorkStage.VerifyingImage"/>。可为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the image was rebuilt and verified; <see langword="false"/> when no preservation metadata exists (or the sparse-container early return applies), leaving the caller to a fresh directory build.
    /// <para>成功重建并校验时返回 <see langword="true"/>；无保留元数据（或命中稀疏容器提前返回情形）时返回 <see langword="false"/>，此时由调用方执行全新目录构建。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumOutputBytes"/> is zero or negative.
    /// <para><paramref name="maximumOutputBytes"/> 为零或负数。</para></exception>
    /// <exception cref="FileSystemException">The metadata format mismatches, the preservation payload is missing, oversized or configuration-inconsistent, an edit guard fails, or the container is not editable.
    /// <para>元数据格式不匹配、保留负载缺失/超限/配置不一致、编辑守卫失败，或容器不支持编辑。</para></exception>
    /// <exception cref="IOException">The output path lies inside the protected trees, or content or attributes changed during rebuilding.
    /// <para>输出路径位于受保护目录树内，或重建期间内容/属性发生变化。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public static bool TryRestore(string inputDirectory, string outputPath, string format,
        CancellationToken cancellationToken = default,
        Action<IReadOnlyDictionary<string, string>>? rebuildReport = null,
        long? maximumOutputBytes = null,
        IReadOnlyList<FileSystemAttributeEdit>? attributeEdits = null,
        IReadOnlyList<FileSystemXattrEdit>? xattrEdits = null,
        IProgress<FileSystemByteProgress>? progress = null)
        => TryRestoreCore(inputDirectory, outputPath, format, cancellationToken, rebuildReport,
            maximumOutputBytes, attributeEdits, xattrEdits, progress);

    private static bool TryRestoreCore(string inputDirectory, string outputPath, string format,
        CancellationToken cancellationToken, Action<IReadOnlyDictionary<string, string>>? rebuildReport,
        long? maximumOutputBytes, IReadOnlyList<FileSystemAttributeEdit>? attributeEdits,
        IReadOnlyList<FileSystemXattrEdit>? xattrEdits, IProgress<FileSystemByteProgress>? progress)
    {
        if (maximumOutputBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        }
        var metadata = RepackMetadata.Read(inputDirectory);
        if (metadata is null) return false;
        if (metadata.FormatName != format) throw new FileSystemCorruptedException("Filesystem metadata format does not match the requested image format.");
        if (format == "Android Sparse Image (simg)" && metadata.Configuration.Count == 0
            && metadata.FileSystemPreservation is null
            && File.Exists(Path.Combine(inputDirectory, Path.GetFileNameWithoutExtension(metadata.SourceFileName) + ".raw")))
            return false;
        var manifest = ReadPayload(metadata)
            ?? throw new FileSystemCorruptedException("Filesystem preservation data is missing. Re-extract the source image before rebuilding it.");
        if (manifest.Format != format) throw new FileSystemUnsupportedFeatureException("Unsupported filesystem preservation data.");
        ValidateConfiguration(metadata, manifest);
        var packingOptions = new Dictionary<string, string>(metadata.PackingOptions, StringComparer.Ordinal);
        if (RepackMetadata.PackingOverrides is { } overrides)
        {
            foreach (var (key, value) in overrides)
            {
                packingOptions[key] = value;
            }
        }
        var metadataDirectory = Path.GetDirectoryName(RepackMetadata.ResolveActualMetadataPath(inputDirectory))!;
        var projection = Scan(inputDirectory, metadataDirectory, metadata.SourceFileName, cancellationToken,
            FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.VerifyingFiles));
        var fullOutput = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var protectedRoot in new[] { inputDirectory, metadataDirectory })
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
            if (fullOutput.Equals(root, comparison) || fullOutput.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Rebuilt output must be outside the extracted tree and its preservation data.");
        }
        var tables = FileSystemMetadataOverrides.LoadTables(metadata);
        ValidateInodeFlags(manifest.ParsedEntries, tables.Rebuild?.InnerFormat ?? format);
        var plan = new FileSystemEditPlan(inputDirectory, manifest, projection, RepackMetadata.DetectPartitionName(metadata.SourceFileName));
        if ((format == "F2FS" || tables.Rebuild?.InnerFormat == "F2FS")
            && RepackMetadata.PackingOverrides is { } explicitOptions
            && explicitOptions.TryGetValue("EnableCompression", out var enabled))
        {
            var algorithm = ParseF2fsCompressionAlgorithm(packingOptions, tables.Rebuild);
            foreach (var entry in plan.Entries.Values.Where(entry => (entry.Mode & 0xF000) == 0x8000))
            {
                if (entry.Metadata is not { } inodeMetadata)
                {
                    continue;
                }
                entry.Metadata = inodeMetadata with
                {
                    F2fsCompression = IsTrue(enabled)
                        ? new F2fsInodeCompression(algorithm switch
                        {
                            CompressionAlgorithm.Lzo => 0,
                            CompressionAlgorithm.Lz4 => 1,
                            CompressionAlgorithm.Zstd => 2,
                            _ => throw new FileSystemUnsupportedFeatureException("Unsupported F2FS compression algorithm.")
                        }, 4, checked((ushort)(IntegerOption(packingOptions, "CompressionLevel", 0) << 8)))
                        : null
                };
            }
        }
        if (xattrEdits is not null) { plan.ApplyUserXattrs(xattrEdits, cancellationToken); }
        var effectiveFsConfig = new Dictionary<string, FsConfigEntry>(StringComparer.Ordinal);
        if (attributeEdits is not null)
        {
            foreach (var (path, attributes) in plan.ApplyAttributes(attributeEdits, cancellationToken))
            {
                effectiveFsConfig[path] = attributes;
            }
        }
        using var editScope = plan.Enter();
        Dictionary<string, string>? optimizerReport = null;
        FileExtraction.WriteAtomic(fullOutput, output =>
        {
            using var bounded = maximumOutputBytes is { } capacity
                ? new CapacityWriteStream(output, capacity, cancellationToken) : null;
            optimizerReport = BuildEdited(inputDirectory, (Stream?)bounded ?? output, format, effectiveFsConfig,
                new Dictionary<string, string>(StringComparer.Ordinal), tables.Rebuild,
                packingOptions, output, Path.GetDirectoryName(fullOutput)!, cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
            VerifyRebuilt(output, plan, cancellationToken, progress);
            var current = RepackMetadata.Read(inputDirectory);
            if (current is null || HashConfiguration(current) != HashConfiguration(metadata))
                throw new IOException("Filesystem content or attributes changed during rebuilding.");
        }, overwrite: true);
        if (rebuildReport is not null)
            rebuildReport(BuildEquivalenceReport(format, manifest, tables.Rebuild, fullOutput, optimizerReport,
                cancellationToken, progress));
        return true;
    }

    private static string? ValidateConfiguration(RepackMetadata metadata, FileSystemPreservationData manifest)
    {
        if (HashConfiguration(metadata) == manifest.ConfigurationHash) return null;
        if (metadata.FormatName == "EROFS" && manifest.ConfigurationJson is { Length: > 0 })
        {
            var original = JsonSerializer.Deserialize(manifest.ConfigurationJson, RepackJsonContext.Default.RepackMetadata)
                ?? throw new FileSystemCorruptedException("Invalid original filesystem configuration.");
            var modified = JsonSerializer.Deserialize(JsonSerializer.Serialize(metadata, RepackJsonContext.Default.RepackMetadata), RepackJsonContext.Default.RepackMetadata)!;
            if (modified.PackingOptions.TryGetValue("VolumeName", out var requested)
                && original.PackingOptions.TryGetValue("VolumeName", out var previous)
                && Encoding.UTF8.GetByteCount(requested) <= 16 && !requested.Contains('\0'))
            {
                modified.PackingOptions["VolumeName"] = previous;
                if (HashConfiguration(modified) == manifest.ConfigurationHash) return requested;
            }
        }
        throw new FileSystemUnsupportedFeatureException("Filesystem configuration changed. The current writer cannot guarantee preservation of every source feature after this change.");
    }

    /// <summary>Report keys that always describe the published file, so a rebuild search never overrides
    /// them with its candidate-stream measurements.
    /// <para>始终描述已发布文件的报告键，重建搜索绝不会用候选流的测量值覆盖它们。</para></summary>
    private static readonly string[] PublishedSizeKeys =
    [
        "RebuiltSize",
        "SourceFilesystemSize",
        "FitsSourceFilesystemSize",
        "SizeExcess",
    ];

    /// <summary>
    /// Builds the post-publish equivalence report. Byte identity streams the produced image against the source
    /// digest and length recorded in the preservation payload; structure and content reflect the verification
    /// that already ran; capacity compares the produced extent against the source filesystem baseline,
    /// using the logical extent for sparse containers. The EROFS compression profile keys are inherited verbatim
    /// so existing result consumers keep working. When the bounded rebuild search ran, its keys are overlaid
    /// afterwards, except the keys that describe the published file (<c>RebuiltSize</c>,
    /// <c>SourceFilesystemSize</c>, <c>FitsSourceFilesystemSize</c> and <c>SizeExcess</c>): those keep the
    /// post-publish recomputation, because the search measured candidate streams in a temporary directory while
    /// the report has to describe the image actually on disk. The search therefore overrides
    /// <c>CompressionProfile</c>, <c>CompressionInference</c> and adds <c>CompressionAttempts</c>.
    /// <para>构建发布后的等价报告。字节一致性把产出镜像流式计入摘要并与保留负载记录的源摘要和长度比对；结构与
    /// 内容反映已经执行过的校验；容量把产出范围与源文件系统基线比较，稀疏容器使用逻辑范围。EROFS 压缩档案键
    /// 保持原样继承，使既有结果消费方继续可用。有界重建搜索执行过后，其键会在最后叠加，但描述已发布文件的键
    /// （<c>RebuiltSize</c>、<c>SourceFilesystemSize</c>、<c>FitsSourceFilesystemSize</c> 与
    /// <c>SizeExcess</c>）除外：这些值沿用发布后的重新计算，因为搜索测量的是临时目录里的候选流，而报告必须
    /// 描述磁盘上真正产出的镜像。因此搜索会覆盖 <c>CompressionProfile</c>、<c>CompressionInference</c>，
    /// 并新增 <c>CompressionAttempts</c>。</para>
    /// </summary>
    /// <param name="format">Published filesystem format name. <para>已发布的文件系统格式名。</para></param>
    /// <param name="manifest">Preservation payload carrying the source digest and length.
    /// <para>携带源摘要与长度的保留负载。</para></param>
    /// <param name="rebuild">Recorded rebuild parameters; <see langword="null"/> when the snapshot stored none.
    /// <para>记录的重建参数；快照未存储时为 <see langword="null"/>。</para></param>
    /// <param name="publishedPath">Published image path the report describes.
    /// <para>报告所描述的已发布镜像路径。</para></param>
    /// <param name="optimizerReport">Search keys overlaid onto the report; may be <see langword="null"/>.
    /// <para>叠加到报告上的搜索键；可为 <see langword="null"/>。</para></param>
    /// <param name="token">Cancels the byte identity digest scan.
    /// <para>取消字节一致性摘要扫描。</para></param>
    /// <param name="progress">Optional staged reporter; the byte identity digest scan is reported as
    /// <see cref="FileSystemWorkStage.VerifyingImage"/> and may be <see langword="null"/>.
    /// <para>可选的分阶段回调；字节一致性摘要扫描上报为
    /// <see cref="FileSystemWorkStage.VerifyingImage"/>，可为 <see langword="null"/>。</para></param>
    private static Dictionary<string, string> BuildEquivalenceReport(string format, FileSystemPreservationData manifest,
        RebuildParameters? rebuild, string publishedPath, IReadOnlyDictionary<string, string>? optimizerReport,
        CancellationToken token, IProgress<FileSystemByteProgress>? progress = null)
    {
        var rebuiltLength = new FileInfo(publishedPath).Length;
        var report = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RebuiltSize"] = rebuiltLength.ToString(CultureInfo.InvariantCulture),
            ["SourceImageLength"] = manifest.SourceLength.ToString(CultureInfo.InvariantCulture),
        };
        report[EquivalenceMetadataKeys.Structure] = "True";
        report[EquivalenceMetadataKeys.Content] = "True";

        var capacityLength = rebuiltLength;
        if (format == "Android Sparse Image (simg)")
        {
            using var container = SparseContainerAccess.Resolve().OpenLogicalView(publishedPath);
            capacityLength = container.Logical.Length;
            report["RebuiltLogicalSize"] = capacityLength.ToString(CultureInfo.InvariantCulture);
        }

        if (manifest.SourceDigest.Length > 0)
        {
            report[EquivalenceMetadataKeys.Byte] =
                EvaluateByteIdentity(manifest, publishedPath, report, token, progress).ToString();
        }

        if (format == "EROFS" && rebuild?.Erofs is { } erofsProfile)
        {
            report["CompressionProfile"] = string.Join(", ", erofsProfile.Codecs
                .OrderBy(codec => codec.Algorithm, StringComparer.Ordinal)
                .Select(codec => $"{codec.Algorithm}:{codec.CompressionLevel}"));
            report["CompressionInference"] = string.Join("; ", erofsProfile.Codecs
                .OrderBy(codec => codec.Algorithm, StringComparer.Ordinal)
                .Select(codec => $"{codec.Algorithm}: inherited from the verified source image; encoder identity and original level remain unproven"));
        }

        var paddedFallback = rebuild?.SourceFilesystemBytes is not > 0;
        var target = paddedFallback ? rebuild?.SourceLength : rebuild?.SourceFilesystemBytes;
        if (target is > 0)
        {
            report["SourceFilesystemSize"] = target.Value.ToString(CultureInfo.InvariantCulture);
            report[EquivalenceMetadataKeys.Capacity] = (capacityLength <= target.Value).ToString();
            report["SizeExcess"] = Math.Max(0, capacityLength - target.Value).ToString(CultureInfo.InvariantCulture);
            if (paddedFallback)
            {
                report["SourceFilesystemSizeBasis"] = "padded image length (re-extract the source to record the filesystem block count)";
            }
        }

        if (optimizerReport is not null)
        {
            foreach (var (key, value) in optimizerReport)
            {
                if (!PublishedSizeKeys.Contains(key)) report[key] = value;
            }
        }

        return report;
    }

    /// <summary>
    /// Compares the published image with the source identity recorded at extraction and records both digests.
    /// The comparison streams the published file, so an edited rebuild reports byte difference while an
    /// untouched round trip reports identity; a capture that recorded no digest leaves the dimension
    /// unevaluated instead of guessing.
    /// <para>把已发布镜像与解包时记录的源身份比较，并记下两侧摘要。比较对已发布文件流式计算，因此编辑过的重建报告
    /// 字节不同，而未经编辑的往返报告完全一致；采集时未记录摘要则让该维度保持不评估，而不是猜测。</para>
    /// </summary>
    /// <param name="manifest">Preservation payload carrying the recorded source digest and length.
    /// <para>携带记录的源摘要与长度的保留负载。</para></param>
    /// <param name="publishedPath">Published image the report describes.
    /// <para>报告所描述的已发布镜像。</para></param>
    /// <param name="report">Report dictionary receiving <c>SourceImageHash</c> and <c>ProducedImageHash</c>.
    /// <para>接收 <c>SourceImageHash</c> 与 <c>ProducedImageHash</c> 的报告字典。</para></param>
    /// <param name="token">Cancels the digest read. <para>取消摘要读取。</para></param>
    /// <param name="progress">Reporter receiving the digest scan as
    /// <see cref="FileSystemWorkStage.VerifyingImage"/>; <see langword="null"/> reports nothing.
    /// <para>把摘要扫描上报为 <see cref="FileSystemWorkStage.VerifyingImage"/> 的回调；<see langword="null"/> 时不报告。</para></param>
    /// <returns><see langword="true"/> when the published image matches the recorded
    /// source digest and length, otherwise <see langword="false"/>.
    /// <para>已发布镜像与记录的源摘要及长度一致时返回 <see langword="true"/>，否则返回 <see langword="false"/>。</para></returns>
    private static bool EvaluateByteIdentity(FileSystemPreservationData manifest,
        string publishedPath, IDictionary<string, string> report, CancellationToken token,
        IProgress<FileSystemByteProgress>? progress)
    {
        using var published = File.OpenRead(publishedPath);
        var length = published.Length;
        var digest = DigestPublished(published, length, token, progress);
        report["SourceImageHash"] = manifest.SourceDigest;
        report["ProducedImageHash"] = digest;
        return length == manifest.SourceLength
            && string.Equals(digest, manifest.SourceDigest, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hashes the published image while reporting the scan as <see cref="FileSystemWorkStage.VerifyingImage"/>.
    /// The published bytes are hashed as written, so a sparse container is compared as its container file rather
    /// than as the logical filesystem it carries.
    /// <para>对已发布镜像计算摘要，并把该扫描上报为 <see cref="FileSystemWorkStage.VerifyingImage"/>。摘要针对写出的
    /// 已发布字节，因此稀疏容器按容器文件比较，而不是按它承载的逻辑文件系统比较。</para>
    /// </summary>
    /// <param name="published">Readable stream over the published image, positioned at its start; the caller
    /// keeps ownership. <para>已发布镜像的可读流，定位在起始处；所有权仍归调用方。</para></param>
    /// <param name="length">Published length used as the progress denominator.
    /// <para>已发布长度，用作进度分母。</para></param>
    /// <param name="token">Cancels the scan between blocks. <para>在数据块之间取消扫描。</para></param>
    /// <param name="progress">Optional staged reporter; <see langword="null"/> reports nothing.
    /// <para>可选的分阶段回调；<see langword="null"/> 时不报告。</para></param>
    /// <returns>The published image digest, lowercase hexadecimal.
    /// <para>已发布镜像摘要，小写十六进制。</para></returns>
    private static string DigestPublished(Stream published, long length, CancellationToken token,
        IProgress<FileSystemByteProgress>? progress)
    {
        var counter = progress is null ? null
            : new ExtractionByteCounter(Math.Max(1, length),
                FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.VerifyingImage), token);
        using var hashing = new HashingReadStream(published, leaveOpen: true);
        var buffer = new byte[65536];
        int read;
        while ((read = hashing.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            counter?.Add(read);
        }
        counter?.Complete();
        return hashing.GetHashHex();
    }
}
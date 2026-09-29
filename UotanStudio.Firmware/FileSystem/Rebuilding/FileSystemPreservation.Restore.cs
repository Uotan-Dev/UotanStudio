using System.Globalization;
using System.Text;
using System.Text.Json;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Extraction;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.
/// <para>保留源镜像语义，并把编辑后的宿主文件树解析为使用原始或继承 Linux 属性的文件系统构建器。</para>
/// </summary>
public static partial class FileSystemPreservation
{
    /// <summary>
    /// Rebuilds the extraction tree using validated source metadata, without retaining the original image.
    /// An optional output limit bounds the emitted file extent before publication. For sparse output this
    /// is the container size, not the logical partition size.
    /// <para>使用经过校验的源元数据重建解包文件树，不保留原始镜像。可选的输出上限会在发布前限制写出文件大小；
    /// 对稀疏输出而言该上限是容器大小，而非逻辑分区大小。</para>
    /// </summary>
    /// <param name="inputDirectory">Extracted tree whose <c>.uotan</c> metadata references the preservation snapshot.
    /// <para>其 <c>.uotan</c> 元数据引用了保留快照的解包目录。</para></param>
    /// <param name="outputPath">Image file to write atomically (overwriting); it must lie outside the extracted tree
    /// and its preservation data. <para>以原子方式写出（允许覆盖）的镜像文件；必须位于解包树及其保留数据之外。</para></param>
    /// <param name="format">Image format name; must equal the metadata format, including the sparse container name.
    /// <para>镜像格式名；必须与元数据格式一致，稀疏容器同理。</para></param>
    /// <param name="cancellationToken">Cancels manifest reads, hashing, rebuilding and verification.
    /// <para>取消清单读取、哈希、重建与校验。</para></param>
    /// <param name="rebuildReport">Optional callback receiving the post-publish equivalence report (byte identity,
    /// structure, content and capacity flags, plus the EROFS compression profile keys) after a successful preserved
    /// rebuild; it is invoked for every preserved rebuild format. May be <see langword="null"/>.
    /// <para>可选回调；受保留重建成功发布后接收等价报告（字节一致性、结构、内容与容量标记，以及 EROFS 压缩档案
    /// 键），对每种受保留重建格式都会触发。可为 <see langword="null"/>。</para></param>
    /// <param name="maximumOutputBytes">Optional positive hard cap on written bytes enforced during publication, or
    /// <see langword="null"/> for no limit. <para>可选的正数写出字节硬上限，在发布期间强制；<see langword="null"/> 表示不限制。</para></param>
    /// <param name="attributeEdits">Optional ownership/permission edits applied to the edit plan before building;
    /// guards and bounds are enforced per <see cref="FileSystemAttributeEdit"/>. May be <see langword="null"/>.
    /// <para>可选的属主/权限编辑，构建前应用到编辑计划；守卫与边界按 <see cref="FileSystemAttributeEdit"/> 所述强制。
    /// 可为 <see langword="null"/>。</para></param>
    /// <param name="xattrEdits">Optional user-namespace xattr edits applied before building. May be <see langword="null"/>.
    /// <para>可选的 user 命名空间扩展属性编辑，构建前应用。可为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the image was rebuilt and verified; <see langword="false"/> when no
    /// preservation metadata exists (or the sparse-container early return applies), leaving the caller to a fresh
    /// directory build.
    /// <para>成功重建并校验时返回 <see langword="true"/>；无保留元数据（或命中稀疏容器提前返回情形）时返回
    /// <see langword="false"/>，此时由调用方执行全新目录构建。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumOutputBytes"/> is zero or negative.
    /// <para><paramref name="maximumOutputBytes"/> 为零或负数。</para></exception>
    /// <exception cref="FileSystemException">The metadata format mismatches, the preservation snapshot is missing,
    /// oversized, hash- or configuration-inconsistent, an edit guard fails, or the container is not editable.
    /// <para>元数据格式不匹配、保留快照缺失/超限/哈希或配置不一致、编辑守卫失败，或容器不支持编辑。</para></exception>
    /// <exception cref="IOException">The output path lies inside the protected trees, or content or attributes changed
    /// during rebuilding. <para>输出路径位于受保护目录树内，或重建期间内容/属性发生变化。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
    public static bool TryRestore(string inputDirectory, string outputPath, string format,
        CancellationToken cancellationToken = default,
        Action<IReadOnlyDictionary<string, string>>? rebuildReport = null,
        long? maximumOutputBytes = null,
        IReadOnlyList<FileSystemAttributeEdit>? attributeEdits = null,
        IReadOnlyList<FileSystemXattrEdit>? xattrEdits = null)
        => TryRestoreCore(inputDirectory, outputPath, format, cancellationToken, rebuildReport,
            maximumOutputBytes, attributeEdits, xattrEdits);

    private static bool TryRestoreCore(string inputDirectory, string outputPath, string format,
        CancellationToken cancellationToken, Action<IReadOnlyDictionary<string, string>>? rebuildReport,
        long? maximumOutputBytes, IReadOnlyList<FileSystemAttributeEdit>? attributeEdits,
        IReadOnlyList<FileSystemXattrEdit>? xattrEdits, string? snapshotDirectory = null)
    {
        if (maximumOutputBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        }
        var metadata = snapshotDirectory is null ? RepackMetadata.Read(inputDirectory)
            : RepackMetadata.Read(snapshotDirectory, RepackMetadataContext.LocalDirectory);
        if (metadata is null) return false;
        if (metadata.FormatName != format) throw new FileSystemCorruptedException("Filesystem metadata format does not match the requested image format.");
        if (format == "Android Sparse Image (simg)" && metadata.Configuration.Count == 0
            && metadata.FileSystemPreservationId is null && metadata.FileSystemPreservationHash is null
            && File.Exists(Path.Combine(inputDirectory, Path.GetFileNameWithoutExtension(metadata.SourceFileName) + ".raw")))
            return false;
        if (metadata.FileSystemPreservationId is not { Length: 32 } id || !Guid.TryParseExact(id, "N", out _))
            throw new FileSystemCorruptedException("Filesystem preservation data is missing. Re-extract the source image before rebuilding it.");
        var metadataDirectory = snapshotDirectory ?? Path.GetDirectoryName(RepackMetadata.ResolveActualMetadataPath(inputDirectory))!;
        var directory = FileExtraction.ResolvePath(metadataDirectory, FolderName + "/" + id);
        var manifestPath = FileExtraction.ResolvePath(directory, "manifest.json");
        if (new FileInfo(manifestPath).Length > MaximumManifestBytes) throw new FileSystemCorruptedException("Filesystem preservation manifest exceeds its size limit.");
        if (metadata.FileSystemPreservationHash != HashFile(manifestPath, cancellationToken))
            throw new FileSystemCorruptedException("Filesystem preservation manifest hash mismatch.");
        FileSystemPreservationManifest manifest;
        using (var file = File.OpenRead(manifestPath))
        {
            if (file.Length > MaximumManifestBytes) throw new FileSystemCorruptedException("Filesystem preservation manifest exceeds its size limit.");
            manifest = JsonSerializer.Deserialize(file, FileSystemPreservationJsonContext.Default.FileSystemPreservationManifest)
                ?? throw new FileSystemCorruptedException("Invalid filesystem preservation manifest.");
        }
        if (manifest.Version is not (1 or 2) || manifest.Format != format
            || manifest.Projection is null || manifest.Sidecars is null || manifest.Projection.Count > MaximumEntries)
            throw new FileSystemUnsupportedFeatureException("Unsupported filesystem preservation manifest.");
        ValidateConfiguration(metadata, manifest);
        var packingOptions = new Dictionary<string, string>(metadata.PackingOptions, StringComparer.Ordinal);
        if (RepackMetadata.PackingOverrides is { } overrides)
        {
            foreach (var (key, value) in overrides)
            {
                packingOptions[key] = value;
            }
        }
        if (!Same(ReadSidecars(metadataDirectory, metadata.SourceFileName, id, cancellationToken,
                    includeReferenceFiles: manifest.Version == 1),
                manifest.Version == 1 ? manifest.Sidecars : StructuredSidecars(manifest.Sidecars)))
            throw new FileSystemUnsupportedFeatureException("Filesystem configuration changed. The current writer cannot guarantee preservation of every source feature after this change.");
        var projection = Scan(inputDirectory, metadataDirectory, metadata.SourceFileName, cancellationToken);
        if (snapshotDirectory is not null)
        {
            ValidateSnapshotProjection(manifest, projection);
            manifest.Projection = projection;
        }
        var fullOutput = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var protectedRoot in new[] { inputDirectory, metadataDirectory })
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedRoot));
            if (fullOutput.Equals(root, comparison) || fullOutput.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new IOException("Rebuilt output must be outside the extracted tree and its preservation data.");
        }
        var tables = FileSystemMetadataOverrides.LoadTables(metadataDirectory, metadata.SourceFileName, id,
            requireStructured: manifest.Version >= 2);
        if (manifest.Version >= 2)
        {
            ValidateInodeFlags(manifest.ParsedEntries, tables.Rebuild?.InnerFormat ?? format);
        }
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
        var effectiveFsConfig = manifest.Version >= 2 ? new Dictionary<string, FsConfigEntry>(StringComparer.Ordinal)
            : new Dictionary<string, FsConfigEntry>(tables.FsConfig, StringComparer.Ordinal);
        if (attributeEdits is not null)
        {
            foreach (var (path, attributes) in plan.ApplyAttributes(attributeEdits, cancellationToken))
            {
                effectiveFsConfig[path] = attributes;
            }
        }
        using var editScope = plan.Enter();
        FileExtraction.WriteAtomic(fullOutput, output =>
        {
            using var bounded = maximumOutputBytes is { } capacity
                ? new CapacityWriteStream(output, capacity, cancellationToken) : null;
            BuildEdited(inputDirectory, (Stream?)bounded ?? output, format, effectiveFsConfig,
                manifest.Version >= 2 ? new Dictionary<string, string>(StringComparer.Ordinal) : tables.FileContexts, tables.Rebuild,
                packingOptions, output, Path.GetDirectoryName(fullOutput)!, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
            if (manifest.Version >= 2)
            {
                VerifyRebuilt(output, plan, cancellationToken);
            }
            var current = snapshotDirectory is null ? RepackMetadata.Read(inputDirectory)
                : RepackMetadata.Read(snapshotDirectory, RepackMetadataContext.LocalDirectory);
            if (current is null || HashConfiguration(current) != HashConfiguration(metadata)
                || current.FileSystemPreservationHash != metadata.FileSystemPreservationHash
                || !Same(ReadSidecars(metadataDirectory, metadata.SourceFileName, id, cancellationToken,
                            includeReferenceFiles: manifest.Version == 1),
                        manifest.Version == 1 ? manifest.Sidecars : StructuredSidecars(manifest.Sidecars))
                || !Same(Scan(inputDirectory, metadataDirectory, metadata.SourceFileName, cancellationToken), projection))
                throw new IOException("Filesystem content or attributes changed during rebuilding.");
        }, overwrite: true);
        if (rebuildReport is not null)
            rebuildReport(BuildEquivalenceReport(format, manifest, tables.Rebuild, fullOutput, cancellationToken));
        return true;
    }

    private static string? ValidateConfiguration(RepackMetadata metadata, FileSystemPreservationManifest manifest)
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

    /// <summary>
    /// Builds the post-publish equivalence report. Byte identity streams the produced image against the source
    /// digest and length recorded in the manifest; structure and content reflect the verification that already ran
    /// for version 2 manifests; capacity compares the produced extent against the source filesystem baseline,
    /// using the logical extent for sparse containers. The EROFS compression profile keys are inherited verbatim
    /// so existing result consumers keep working.
    /// <para>构建发布后的等价报告。字节一致性把产出镜像流式计入摘要并与清单记录的源摘要和长度比对；结构与内容
    /// 反映版本 2 清单已经执行过的校验；容量把产出范围与源文件系统基线比较，稀疏容器使用逻辑范围。EROFS 压缩
    /// 档案键保持原样继承，使既有结果消费方继续可用。</para>
    /// </summary>
    private static Dictionary<string, string> BuildEquivalenceReport(string format, FileSystemPreservationManifest manifest,
        RebuildParameters? rebuild, string publishedPath, CancellationToken token)
    {
        var rebuiltLength = new FileInfo(publishedPath).Length;
        var producedHash = HashFile(publishedPath, token);
        var report = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RebuiltSize"] = rebuiltLength.ToString(CultureInfo.InvariantCulture),
            ["EquivalentBytes"] = (rebuiltLength == manifest.SourceLength
                && string.Equals(producedHash, manifest.SourceHash, StringComparison.OrdinalIgnoreCase)).ToString(),
            ["SourceImageLength"] = manifest.SourceLength.ToString(CultureInfo.InvariantCulture),
            ["ProducedImageHash"] = producedHash,
            ["SourceImageHash"] = manifest.SourceHash,
        };
        if (manifest.Version >= 2)
        {
            report["EquivalentStructure"] = "True";
            report["EquivalentContent"] = "True";
        }

        var capacityLength = rebuiltLength;
        if (format == "Android Sparse Image (simg)")
        {
            using var container = SparseContainerAccess.Resolve().OpenLogicalView(publishedPath);
            capacityLength = container.Logical.Length;
            report["RebuiltLogicalSize"] = capacityLength.ToString(CultureInfo.InvariantCulture);
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
            report["FitsSourceFilesystemSize"] = (capacityLength <= target.Value).ToString();
            report["SizeExcess"] = Math.Max(0, capacityLength - target.Value).ToString(CultureInfo.InvariantCulture);
            if (paddedFallback)
            {
                report["SourceFilesystemSizeBasis"] = "padded image length (re-extract the source to record the filesystem block count)";
            }
        }

        return report;
    }
}
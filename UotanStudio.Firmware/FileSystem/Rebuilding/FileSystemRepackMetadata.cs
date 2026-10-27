using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>Owns source filesystem configuration, sidecar generation and preservation capture.
/// <para>负责源文件系统配置、附属文件生成与保留式捕获。</para></summary>
internal static class FileSystemRepackMetadata
{
    internal static void Configure(RepackMetadata metadata, string sourcePath, string outputDirectory, bool sparse = false)
    {
        metadata.Configuration = FileSystemRepackConfiguration.Read(sourcePath, sparse);
        metadata.CanPackDirectory = !sparse || File.Exists(Path.Combine(outputDirectory,
            Path.GetFileNameWithoutExtension(sourcePath) + ".raw"));
        string[] optionKeys = metadata.FormatName switch
        {
            "EXT4" => ["BlockSize"],
            "EROFS" => ["BlockSize", "VolumeName"],
            "F2FS" => ["BlockSize", "SegmentSize", "VolumeName"],
            _ => []
        };
        foreach (var key in optionKeys)
        {
            if (metadata.SourceProperties.TryGetValue(key, out var value))
            {
                metadata.PackingOptions[key] = value;
            }
        }
        if (metadata.Configuration.ContainsKey("superblock"))
        {
            metadata.PackingNotes += metadata.Configuration["fileSystem"]?.GetValue<string>() == "EROFS"
                ? " EROFS edits inherit codec parameters, UUID, volume and inode attributes from the verified original image; compression level is not recorded on disk. Physical layout is regenerated."
                : " configuration.superblock records the original filesystem metadata for inspection; only packingOptions are applied when rebuilding the filesystem.";
        }
    }

    internal static Task CaptureAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken, bool sparse = false)
        => CaptureAsyncCore(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken, sparse,
            snapshot: null);

    /// <summary>
    /// Captures metadata from a frozen EROFS snapshot the extraction already produced, so the flat export and the
    /// preservation payload reuse its entry list and rebuild parameters instead of parsing the image again.
    /// <para>从解包已产出的 EROFS 冻结快照采集元数据，使扁平导出与保留负载复用其条目清单和重建参数，而不是再次解析镜像。</para>
    /// </summary>
    /// <param name="metadata">Metadata instance being assembled. <para>正在组装的元数据实例。</para></param>
    /// <param name="sourcePath">Source image path recorded in the payload; only its length is read.
    /// <para>记录进负载的源镜像路径；仅读取其长度。</para></param>
    /// <param name="outputDirectory">Extraction directory whose files are verified afterwards.
    /// <para>随后校验其文件的解包目录。</para></param>
    /// <param name="metadataDirectory">Directory receiving the flat exports and the metadata JSON.
    /// <para>接收扁平导出与元数据 JSON 的目录。</para></param>
    /// <param name="cancellationToken">Cancels capture and verification.
    /// <para>取消采集与校验。</para></param>
    /// <param name="snapshot">Frozen EROFS snapshot, or <see langword="null"/> to read the source the usual way;
    /// a snapshot is disposed when capture ends, including on failure.
    /// <para>EROFS 冻结快照，或为 <see langword="null"/> 时按常规方式读取来源；快照在采集结束时释放，失败时同样释放。</para></param>
    /// <returns>A completed task. <para>已完成的任务。</para></returns>
    internal static Task CaptureAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken, ErofsPreservationSnapshot? snapshot)
        => CaptureAsyncCore(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken, sparse: false,
            snapshot);

    private static Task CaptureAsyncCore(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken, bool sparse,
        ErofsPreservationSnapshot? snapshot)
    {
        if (sparse && metadata.Configuration["fileSystem"]?.GetValue<string>() is not ("EXT4" or "EROFS" or "F2FS"))
        {
            snapshot?.Dispose();
            return Task.CompletedTask;
        }
        try
        {
            Directory.CreateDirectory(metadataDirectory);
            List<string> exportWarnings = GenerateFileSystemMetadata(metadataDirectory,
                RepackMetadata.DetectPartitionName(Path.GetFileName(sourcePath)),
                metadata.FormatName, sourcePath, metadata.Configuration, cancellationToken, snapshot);
            metadata.CanPackDirectory = true;
            metadata.PackingNotes = "Retains structured inode metadata, not the original image or file contents. Rebuilding requires the extracted host tree; attributes and file content are verified before publication.";
            if (exportWarnings.Count > 0)
            {
                metadata.PackingNotes += $" Flat metadata export reported {exportWarnings.Count} issue(s): {string.Join("; ", exportWarnings)}";
            }
            if (snapshot is null)
            {
                FileSystemPreservation.CaptureAsync(sourcePath, metadata, cancellationToken, reportSourceVerification: !sparse);
            }
            else
            {
                FileSystemPreservation.CaptureAsync(sourcePath, metadata, snapshot, cancellationToken, reportSourceVerification: !sparse);
            }
        }
        finally
        {
            snapshot?.Dispose();
        }
        FileSystemPreservation.VerifyExtractedFiles(outputDirectory, sourcePath, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the extraction-time flat export (<c>fs_config</c>, <c>file_contexts</c> and <c>partition_info</c>)
    /// beside the metadata file and reports what the export could not carry. Every row the space-delimited grammar
    /// cannot express, and the whole export when it cannot run at all, becomes a message in the returned list:
    /// the lossless channel is the structured payload, so an export gap must be visible to whoever reads the
    /// metadata instead of being logged and forgotten.
    /// <para>在元数据文件旁写出解包期的扁平导出物（<c>fs_config</c>、<c>file_contexts</c> 与
    /// <c>partition_info</c>），并报告导出未能承载的内容。空格分隔语法无法表达的每一行，以及完全无法运行的整份
    /// 导出，都会成为返回列表中的一条说明：无损通道始终是结构化负载，因此导出缺口必须对读取元数据的人可见，
    /// 而不是写进日志就被遗忘。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the flat files are written to.
    /// <para>写出扁平文件的目录。</para></param>
    /// <param name="partitionName">File-name prefix for the three exports.
    /// <para>三份导出文件使用的文件名前缀。</para></param>
    /// <param name="formatName">Format name reported by the handler being captured.
    /// <para>正在采集的处理器所报告的格式名。</para></param>
    /// <param name="sourcePath">Source image whose parsed entries feed the export.
    /// <para>其解析条目用于喂给导出的源镜像。</para></param>
    /// <param name="configuration">Source configuration providing the superblock object.
    /// <para>提供超级块对象的来源配置。</para></param>
    /// <param name="cancellationToken">Cancels parsing and the sparse-container read.
    /// <para>取消解析与稀疏容器读取。</para></param>
    /// <param name="snapshot">Frozen EROFS snapshot whose cached entry list feeds the export, or
    /// <see langword="null"/> to parse the source. <para>其缓存条目清单用于喂给导出的 EROFS 冻结快照，或为
    /// <see langword="null"/> 时解析来源。</para></param>
    /// <returns>One message per skipped row plus a message describing a failed export; empty when the export was
    /// complete. <para>每条被跳过的行对应一条说明，另有失败导出的一句话说明；导出完整时为空。</para></returns>
    private static List<string> GenerateFileSystemMetadata(string metadataDirectory, string partitionName,
        string formatName, string sourcePath, JsonObject configuration, CancellationToken cancellationToken,
        ErofsPreservationSnapshot? snapshot)
    {
        List<string> warnings = [];
        try
        {
            var superblock = configuration.ContainsKey("superblock") ? configuration["superblock"] as JsonObject : null;
            if (formatName == "Android Sparse Image (simg)")
            {
                using var container = SparseContainerAccess.Resolve().OpenLogicalView(sourcePath);
                var logical = container.Logical;
                switch (configuration["fileSystem"]?.GetValue<string>())
                {
                    case "EXT4":
                        using (var parser = new Ext4.Ext4Parser(logical))
                        {
                            warnings.AddRange(FileSystemMetadataGenerator.GenerateForExt4(metadataDirectory, partitionName, parser, superblock));
                        }
                        break;
                    case "EROFS":
                        using (var parser = new Erofs.Parser.ErofsParser(logical))
                        {
                            warnings.AddRange(FileSystemMetadataGenerator.GenerateForErofs(metadataDirectory, partitionName, parser, superblock));
                        }
                        break;
                    case "F2FS":
                        using (var parser = new F2fs.F2fsParser(logical))
                        {
                            warnings.AddRange(FileSystemMetadataGenerator.GenerateForF2fs(metadataDirectory, partitionName, parser, superblock));
                        }
                        break;
                }
                return warnings;
            }

            if (formatName == "EROFS" && snapshot is not null)
            {
                warnings.AddRange(snapshot.GenerateFlatMetadata(metadataDirectory, partitionName, superblock));
                return warnings;
            }

            using var stream = File.OpenRead(sourcePath);
            switch (formatName)
            {
                case "EXT4":
                    using (var parser = new Ext4.Ext4Parser(stream))
                    {
                        warnings.AddRange(FileSystemMetadataGenerator.GenerateForExt4(metadataDirectory, partitionName, parser, superblock));
                    }

                    break;
                case "EROFS":
                    using (var parser = FileSystemPreservation.OpenErofsParser(stream, cancellationToken))
                    {
                        warnings.AddRange(FileSystemMetadataGenerator.GenerateForErofs(metadataDirectory, partitionName, parser, superblock));
                    }

                    break;
                case "F2FS":
                    using (var parser = new F2fs.F2fsParser(stream))
                    {
                        warnings.AddRange(FileSystemMetadataGenerator.GenerateForF2fs(metadataDirectory, partitionName, parser, superblock));
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileSystemException or NotSupportedException)
        {
            warnings.Add($"the {partitionName}_fs_config, {partitionName}_file_contexts and {partitionName}_info export did not run: {ex.Message}");
        }

        return warnings;
    }
}
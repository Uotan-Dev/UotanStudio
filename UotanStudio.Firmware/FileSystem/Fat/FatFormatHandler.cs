using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.Fat;

/// <summary>
/// Read-only format handler for FAT12, FAT16 and FAT32 volume images: detection by the volume boot record
/// and its BIOS parameter block, metadata reading and tree extraction. Extraction tolerates a vendor volume
/// whose declared capacity exceeds the file it ships in, publishing the covered entries and reporting the
/// unreadable tail instead of failing the whole image.
/// <para>FAT12、FAT16 与 FAT32 卷镜像的只读格式处理器：按卷引导记录及其 BIOS 参数块识别、读取元数据并解包
/// 目录树。解包容忍厂商卷的声明容量大于实际文件长度：仍发布覆盖到的条目并报告不可读的尾部，而不是让整张镜像
/// 失败。</para>
/// </summary>
internal sealed class FatFormatHandler : IWritableFormatHandler, IRepackMetadataProvider
{
    /// <summary>Name of the format this handler serves.
    /// <para>本处理器所服务格式的名称。</para></summary>
    /// <value>The constant <c>"FAT"</c>. <para>常量 <c>"FAT"</c>。</para></value>
    public string FormatName => "FAT";

    /// <summary>File extensions this handler registers.
    /// <para>本处理器注册的文件扩展名。</para></summary>
    /// <value>The list <c>img</c> and <c>fat</c>; an extension alone never overrides the boot-record check.
    /// <para>列表 <c>img</c> 与 <c>fat</c>；仅凭扩展名不会取代卷引导记录校验。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["img", "fat"];

    /// <summary>
    /// Determines whether the file carries an accepted FAT volume boot record.
    /// <para>判断文件是否携带本处理器接受的 FAT 卷引导记录。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path; <see langword="null"/>, blank, missing or unreadable
    /// paths short-circuit to <see langword="false"/>.
    /// <para>候选文件路径；为 <see langword="null"/>、空白、不存在或不可读时直接返回 <see langword="false"/>。</para></param>
    /// <returns><see langword="true"/> when the boot record and geometry pass validation; any read failure is
    /// reported as a non-match. <para>引导记录与几何信息通过校验时为 <see langword="true"/>；任何读取失败按不匹配处理。</para></returns>
    public bool CanHandle(string filePath) => FatParser.IsFatImage(filePath);

    /// <summary>Reads the volume geometry and lists every entry path without extracting file data.
    /// <para>读取卷几何信息并列出全部条目路径，不解出文件数据。</para></summary>
    /// <param name="filePath">FAT volume image, opened read-only on a worker thread.
    /// <para>FAT 卷镜像；在工作线程上以只读方式打开。</para></param>
    /// <param name="cancellationToken">Token checked around the tree walk.
    /// <para>在目录树遍历前后检查的令牌。</para></param>
    /// <returns>An <c>Ok</c> result whose metadata carries the BIOS parameter block fields, entry counts and the capacity-equivalence verdict, and whose entries list the image paths; a <c>Fail</c> result with the error message when the volume cannot be parsed.
    /// <para><c>Ok</c> 结果的元数据包含 BIOS 参数块字段、条目计数与容量等价判定，明细行列出镜像路径；镜像无法解析 时返回带错误消息的 <c>Fail</c> 结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var parser = new FatParser(filePath);
                var entries = parser.ListFiles();
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = Metadata(parser, entries);
                return FirmwareOpResult.Ok(
                    $"{parser.Variant} volume with {metadata["FileCount"]} files.",
                    [.. entries.Select(entry => entry.Path)],
                    metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read {FormatName} metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);

    /// <summary>Extracts the FAT tree into the output directory.
    /// <para>将 FAT 目录树解包到输出目录。</para></summary>
    /// <param name="filePath">Source image; the input is never modified.
    /// <para>源镜像；输入文件不会被修改。</para></param>
    /// <param name="outputDirectory">Destination tree; entries are written with their image paths.
    /// <para>目标目录树；条目按镜像内路径写出。</para></param>
    /// <param name="options">Unpack options; <see langword="null"/> overwrites existing files, reports no byte progress and applies the default output budget.
    /// <para>解包选项；为 <see langword="null"/> 时覆盖已存在文件、不报告字节进度并使用默认输出预算。</para></param>
    /// <param name="cancellationToken">Token checked throughout parsing and extraction.
    /// <para>在解析与解包全程检查的令牌。</para></param>
    /// <returns>An <c>Ok</c> result listing the extracted file paths and reporting how many entries could not be read because the volume is truncated, or a <c>Fail</c> result when the image or extraction fails.
    /// <para>返回列出已解出文件路径的 <c>Ok</c> 结果并说明有多少条目因卷被截断而无法读取；镜像或解包失败时返回 <c>Fail</c> 结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var parser = new FatParser(filePath);
                var byteProgress = FileSystemByteProgress.ForStage(options?.FileSystemProgress,
                    FileSystemWorkStage.Extracting);
                var (extracted, unreadable) = parser.ExtractToDirectory(
                    outputDirectory,
                    overwrite: options?.Overwrite ?? true,
                    maximumOutputBytes: options?.MaximumOutputBytes ?? new UnpackOptions().MaximumOutputBytes,
                    byteProgress,
                    cancellationToken);
                var summary = unreadable.Count == 0
                    ? $"Extracted {extracted.Count} files from {parser.Variant} volume '{parser.VolumeLabel}' to '{outputDirectory}'."
                    : $"Extracted {extracted.Count} files from {parser.Variant} volume '{parser.VolumeLabel}' to '{outputDirectory}'; "
                      + $"{unreadable.Count} entries store clusters beyond the end of the truncated image and were not written: "
                      + string.Join(", ", unreadable.Take(8));
                var metadata = Metadata(parser, parser.ListFiles());
                metadata["UnreadableEntries"] = unreadable.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return FirmwareOpResult.Ok(summary, [.. extracted], metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack {FormatName} image '{filePath}': {ex.Message}");
            }
        }, cancellationToken);

    /// <summary>Packs a directory into a FAT12/FAT16/FAT32 volume image.
    /// <para>把目录打包为 FAT12/FAT16/FAT32 卷镜像。</para></summary>
    /// <param name="inputPath">Directory whose contents become the volume. Packing options recorded beside the
    /// directory (from an earlier extraction of the same volume) reproduce its variant, sector geometry, reserved
    /// sectors, root-directory slots and declared capacity; without them a fresh volume is planned from the content.
    /// <para>内容将构成该卷的目录。目录旁记录的打包选项（来自同一卷早前的解包）会复现其变体、扇区几何、保留扇区、
    /// 根目录槽位与声明容量；没有这些选项时按内容全新规划卷。</para></param>
    /// <param name="outputPath">Destination image path; the parent directory is created and an existing file is
    /// replaced only after the image has been written successfully.
    /// <para>目标镜像路径；自动创建父目录，仅在镜像成功写出后才替换已存在的文件。</para></param>
    /// <param name="cancellationToken">Cancels planning and writing.
    /// <para>取消规划与写入。</para></param>
    /// <returns>An <c>Ok</c> result with the written geometry, or a <c>Fail</c> result when the input is not a
    /// directory or the options cannot be represented by the variant.
    /// <para>成功时返回携带已写出几何的 <c>Ok</c> 结果；输入不是目录或选项无法被该变体表示时返回 <c>Fail</c> 结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackCore(inputPath, outputPath, null, cancellationToken);

    Task<FirmwareOpResult> IWritableFormatHandler.PackAsync(string inputPath, string outputPath,
        IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken)
        => PackCore(inputPath, outputPath, progress, cancellationToken);

    private Task<FirmwareOpResult> PackCore(string inputPath, string outputPath,
        IProgress<FileSystemByteProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(inputPath))
                {
                    return FirmwareOpResult.Fail($"Input path is not a directory: '{inputPath}'.");
                }

                var options = FileSystemPackingOptions.OptionsFor(inputPath, FormatName);
                var report = FatBuilder.Build(inputPath, outputPath, BuildOptions(options), progress, cancellationToken);
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Format"] = report.Variant,
                    ["VolumeSize"] = report.VolumeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["BytesPerSector"] = report.BytesPerSector.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["SectorsPerCluster"] = report.SectorsPerCluster.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["TotalClusters"] = report.TotalClusters.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["EntryCount"] = report.EntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["FileCount"] = report.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["TotalDataBytes"] = report.DataBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                return FirmwareOpResult.Ok(
                    $"Packed '{inputPath}' into '{outputPath}' ({report.Variant}, {metadata["VolumeSize"]} bytes).",
                    metadata: metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to pack '{inputPath}' into a FAT volume: {ex.Message}");
            }
        }, cancellationToken);

    /// <summary>
    /// Records the geometry of the source volume so a later directory pack reproduces it instead of planning a new
    /// layout. FAT carries no POSIX ownership, permissions or extended attributes, so no preservation payload is
    /// captured; the packing notes state that the attributes come from the extracted host tree.
    /// <para>记录源卷的几何，使后续按目录打包时复现它而不是重新规划布局。FAT 不承载 POSIX 属主、权限或扩展属性，
    /// 因此不采集保留负载；打包说明指出属性来自解包后的宿主目录树。</para>
    /// </summary>
    /// <param name="metadata">Metadata instance being assembled. <para>正在组装的元数据实例。</para></param>
    /// <param name="sourcePath">Source volume image whose geometry is read.
    /// <para>其几何被读取的源卷镜像。</para></param>
    /// <param name="outputDirectory">Extraction directory; only recorded for diagnostics.
    /// <para>解包目录；仅用于诊断记录。</para></param>
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
        metadata.PackingNotes = " FAT rebuilds inherit the recorded variant, sector geometry, reserved sectors, root-directory slots and declared capacity; both allocation tables are regenerated and file timestamps come from the extracted host tree. FAT has no POSIX ownership, permission or extended-attribute channel.";
        try
        {
            using var parser = new FatParser(sourcePath);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            metadata.PackingOptions["FatVariant"] = parser.Variant.ToLowerInvariant();
            metadata.PackingOptions["BytesPerSector"] = parser.BytesPerSector.ToString(culture);
            metadata.PackingOptions["SectorsPerCluster"] = parser.SectorsPerCluster.ToString(culture);
            metadata.PackingOptions["ReservedSectors"] = parser.ReservedSectorCount.ToString(culture);
            metadata.PackingOptions["RootDirectoryEntries"] = parser.MaxRootDirectoryEntries.ToString(culture);
            metadata.PackingOptions["VolumeName"] = parser.VolumeLabel;
            metadata.PackingOptions["TotalBytes"] = parser.DeclaredVolumeBytes.ToString(culture);
            if (parser.FatCount != 2)
            {
                metadata.PackingNotes += $" The source stores {parser.FatCount} allocation table(s); the rebuild always writes two mirror copies.";
            }

            if (parser.DeclaredVolumeBytes != parser.ImageBytes)
            {
                metadata.PackingNotes += " The source declares a capacity that differs from its image length: " + FatVolumeTree.CapacityText(parser.DeclaredVolumeBytes, parser.ImageBytes) + ".";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileSystemException or UnauthorizedAccessException)
        {
            metadata.PackingNotes += $" The source volume geometry could not be recorded ({ex.Message}); the rebuild plans a fresh layout from the extracted content.";
        }
    }

    private static FatBuildOptions BuildOptions(IReadOnlyDictionary<string, string> options)
    {
        var mediaDescriptor = FileSystemPackingOptions.IntegerOption(options, "MediaDescriptor", 0xF8);
        if (mediaDescriptor is < 0 or > 255)
        {
            throw new InvalidDataException($"Unsupported FAT media descriptor value {mediaDescriptor}; expected 0 through 255.");
        }

        long? totalBytes = null;
        if (options.TryGetValue("TotalBytes", out var total))
        {
            if (!long.TryParse(total, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                throw new InvalidDataException($"Unsupported FAT TotalBytes value '{total}'; expected an integer byte count.");
            }

            totalBytes = parsed;
        }

        return new FatBuildOptions
        {
            Variant = ParseVariant(options),
            BytesPerSector = FileSystemPackingOptions.IntegerOption(options, "BytesPerSector", 512),
            SectorsPerCluster = FileSystemPackingOptions.IntegerOption(options, "SectorsPerCluster", 0),
            ReservedSectors = FileSystemPackingOptions.IntegerOption(options, "ReservedSectors", 0),
            RootDirectoryEntries = FileSystemPackingOptions.IntegerOption(options, "RootDirectoryEntries", 0),
            VolumeLabel = options.TryGetValue("VolumeName", out var label) ? label : string.Empty,
            HiddenSectors = FileSystemPackingOptions.IntegerOption(options, "HiddenSectors", 0),
            MediaDescriptor = (byte)mediaDescriptor,
            TotalBytes = totalBytes,
        };
    }

    private static FatVariant ParseVariant(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("FatVariant", out var value) || string.IsNullOrWhiteSpace(value)
            || value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return FatVariant.Auto;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "fat12" or "12" => FatVariant.Fat12,
            "fat16" or "16" => FatVariant.Fat16,
            "fat32" or "32" => FatVariant.Fat32,
            _ => throw new InvalidDataException(
                $"Unsupported FAT variant '{value}'; expected auto, fat12, fat16 or fat32."),
        };
    }

    private static Dictionary<string, string> Metadata(FatParser parser, IReadOnlyList<FatVolumeEntry> entries)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var files = 0;
        var totalDataBytes = 0L;
        foreach (var entry in entries)
        {
            if (entry.Kind == FatVolumeEntryKind.File)
            {
                files++;
                totalDataBytes = checked(totalDataBytes + entry.Size);
            }
        }

        return new Dictionary<string, string>
        {
            ["Format"] = parser.Variant,
            ["VolumeName"] = parser.VolumeLabel,
            ["VolumeSize"] = parser.DeclaredVolumeBytes.ToString(culture),
            ["ImageSize"] = parser.ImageBytes.ToString(culture),
            ["BytesPerSector"] = parser.BytesPerSector.ToString(culture),
            ["SectorsPerCluster"] = parser.SectorsPerCluster.ToString(culture),
            ["FatCount"] = parser.FatCount.ToString(culture),
            ["ReservedSectors"] = parser.ReservedSectorCount.ToString(culture),
            ["RootDirectoryEntries"] = parser.MaxRootDirectoryEntries.ToString(culture),
            ["TotalClusters"] = parser.TotalClusters.ToString(culture),
            ["ClusterSize"] = checked((long)parser.BytesPerSector * parser.SectorsPerCluster).ToString(culture),
            ["EntryCount"] = entries.Count.ToString(culture),
            ["FileCount"] = files.ToString(culture),
            ["TotalDataBytes"] = totalDataBytes.ToString(culture),
            ["CapacityEquivalence"] = FatVolumeTree.CapacityText(parser.DeclaredVolumeBytes, parser.ImageBytes),
        };
    }
}
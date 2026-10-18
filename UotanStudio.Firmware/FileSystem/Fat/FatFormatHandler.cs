using UotanStudio.Firmware.Core;

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
internal sealed class FatFormatHandler : IReadonlyFormatHandler
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
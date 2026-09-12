using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// <para>一次定向提取的结果：成功提取的容器内部条目及其输出文件路径。</para>
/// Result of one targeted extraction: extracted inner entries and their output file paths.
/// </summary>
/// <param name="Entries"><para>内部条目名（分区名或 ZIP 内路径）。</para>Inner entry names (partition names or ZIP paths).</param>
/// <param name="OutputPaths"><para>输出文件绝对路径，与 <paramref name="Entries"/> 一一对应。</para>Output absolute paths, one-to-one with <paramref name="Entries"/>.</param>
public sealed record ContainerExtractionResult(IReadOnlyList<string> Entries, IReadOnlyList<string> OutputPaths)
{
    /// <summary><para>空结果（无提取或全部失败）。</para>Empty result (nothing extracted or all failed).</summary>
    public static ContainerExtractionResult Empty { get; } = new([], []);
}

/// <summary>
/// <para>新建项目定向提取器：从外部原包或项目内原包按勾选条目提取到显式输出目录，
/// 不指定目录时保留输出到原包旁的兼容行为。super 按 LP 分区流式裁剪；payload 经
/// <c>PayloadParser.ExtractAsync</c> 按分区名提取；ZIP 按条目路径流式解出。
/// 全部有界流式、AOT 安全（无反射），失败时抛出异常由调用方统一处理。</para>
/// New-project targeted extractor: extracts selected entries from a container already copied into
/// the sub-project <c>images/</c> into standalone img files next to the container. Super partitions
/// are stream-cropped by LP extents; payload partitions go through <c>PayloadParser.ExtractAsync</c>;
/// ZIP members are stream-extracted by entry path. All bounded streaming, AOT-safe (no reflection);
/// failures throw and are handled by the caller.
/// </summary>
public static class ContainerEntryExtractor
{
    /// <summary>
    /// <para>从容器按勾选条目定向提取（同步包装，供后台线程调用）。</para>
    /// Extracts the selected entries from a container (sync wrapper for background threads).
    /// </summary>
    /// <param name="containerPath"><para>容器文件路径（已在 images/ 内）。</para>Container file path (already inside images/).</param>
    /// <param name="selectedNames">
    /// <para>勾选的内部条目：super/payload 分区名，或 ZIP 条目完整路径。</para>
    /// Selected inner entries: super/payload partition names, or full ZIP entry paths.
    /// </param>
    /// <param name="fromPayloadOnly">
    /// <para>仅提取 payload 分区（ZIP 容器只勾选 payload 部分时为 true）。</para>
    /// Extract payload partitions only (true when a ZIP's payload section alone is selected).
    /// </param>
    /// <param name="cancellationToken"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns><para>提取结果。</para>The extraction result.</returns>
    public static async Task<ContainerExtractionResult> ExtractAsync(
        string containerPath,
        IReadOnlyList<string> selectedNames,
        bool fromPayloadOnly = false,
        CancellationToken cancellationToken = default, string? outputDirectory = null, IProgress<double>? progress = null,
        IProgress<(long Processed, long Total)>? byteProgress = null)
    {
        if (string.IsNullOrWhiteSpace(containerPath) || !File.Exists(containerPath)
            || selectedNames is null || selectedNames.Count == 0)
        {
            return ContainerExtractionResult.Empty;
        }

        outputDirectory ??= Path.GetDirectoryName(containerPath) ?? ".";
        Directory.CreateDirectory(outputDirectory);
        var kind = ContainerStructureAnalyzer.Analyze(containerPath).Kind;
        return kind switch
        {
            ContainerKind.Super => await ExtractSuperAsync(containerPath, selectedNames, cancellationToken, outputDirectory, progress, byteProgress).ConfigureAwait(false),
            ContainerKind.Payload => await ExtractPayloadAsync(containerPath, selectedNames, cancellationToken, outputDirectory, progress, byteProgress).ConfigureAwait(false),
            ContainerKind.Zip => await ExtractZipAsync(containerPath, selectedNames, fromPayloadOnly, cancellationToken, outputDirectory, progress, byteProgress).ConfigureAwait(false),
            _ => ContainerExtractionResult.Empty
        };
    }

    /// <summary>
    /// <para>从 super 镜像按分区名流式裁剪出独立 img（读取 LP 元数据定位 extent，逐段复制）。</para>
    /// Crops standalone imgs from a super image by partition names (LP extents located from metadata,
    /// copied segment by segment).
    /// </summary>
    private static async Task<ContainerExtractionResult> ExtractSuperAsync(
        string containerPath, IReadOnlyList<string> partitionNames, CancellationToken cancellationToken, string outputDirectory, IProgress<double>? progress, IProgress<(long Processed, long Total)>? byteProgress)
    {
        var metadata = MetadataReader.ReadFromImageFile(containerPath);
        var sectorSize = (long)MetadataFormat.LP_SECTOR_SIZE;
        var names = new HashSet<string>(partitionNames, StringComparer.OrdinalIgnoreCase);
        var outputNames = new List<string>();
        var outputPaths = new List<string>();

        var selectedPartitions = metadata.Partitions.Where(partition => names.Contains(partition.NameString)).ToArray();
        if (selectedPartitions.Length != names.Count) throw new InvalidDataException("A selected LP partition is missing.");
        long totalBytes = 0;
        foreach (var partition in selectedPartitions)
            foreach (var extent in metadata.Extents.GetRange(checked((int)partition.FirstExtentIndex), checked((int)partition.NumExtents)))
                totalBytes = checked(totalBytes + checked((long)extent.NumSectors * sectorSize));
        if (totalBytes > 64L * 1024 * 1024 * 1024) throw new InvalidDataException("Selected LP partitions exceed the extraction budget.");
        long copiedBytes = 0;
        progress?.Report(0);
        byteProgress?.Report((0, totalBytes));
        await using var container = MetadataReader.OpenImage(containerPath);
        foreach (var partition in metadata.Partitions)
        {
            if (!names.Contains(partition.NameString))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = BuildOutputPath(Path.Combine(outputDirectory, Path.GetFileName(containerPath)), partition.NameString);
            var temporary = outputPath + ".extract-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                {

                    // 线性 extent 段逐段复制为连续分区镜像；缺失段按规范补零。
                    // Linear extents are copied segment by segment into a contiguous partition image;
                    // gaps between extents are zero-filled per the format semantics.
                    ulong sectors = 0;
                    for (var i = 0; i < partition.NumExtents; i++)
                    {
                        var extent = metadata.Extents[(int)partition.FirstExtentIndex + i];
                        var extentBytes = checked((long)extent.NumSectors * sectorSize);
                        if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_ZERO)
                        {
                            output.Seek(extentBytes, SeekOrigin.Current);
                        }
                        else
                        {
                            if (extent.TargetType != MetadataFormat.LP_TARGET_TYPE_LINEAR || extent.TargetSource != 0)
                                throw new InvalidDataException("Missing backing device or unsupported LP extent.");
                            var offset = checked((long)extent.TargetData * sectorSize);
                            if (offset > container.Length || extentBytes > container.Length - offset)
                                throw new InvalidDataException("LP extent exceeds the source image.");
                            container.Seek(offset, SeekOrigin.Begin);
                            await CopyWithProgressAsync(container, output, extentBytes, copiedBytes, totalBytes, progress, cancellationToken, byteProgress).ConfigureAwait(false);
                        }

                        copiedBytes = checked(copiedBytes + extentBytes);
                        progress?.Report(totalBytes > 0 ? copiedBytes * 100.0 / totalBytes : 100);
                        byteProgress?.Report((copiedBytes, totalBytes));
                        sectors = checked(sectors + extent.NumSectors);
                    }

                    output.SetLength(checked((long)sectors * sectorSize));
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, outputPath);
            }
            finally { File.Delete(temporary); }
            outputNames.Add(partition.NameString);
            outputPaths.Add(outputPath);
        }

        progress?.Report(100);
        byteProgress?.Report((totalBytes, totalBytes));
        return new ContainerExtractionResult(outputNames, outputPaths);
    }

    /// <summary>
    /// <para>经 <c>PayloadParser.ExtractAsync</c> 按分区名从 payload 提取（输出到容器所在目录）。</para>
    /// Extracts partitions from a payload by name via <c>PayloadParser.ExtractAsync</c>
    /// (output next to the container).
    /// </summary>
    private static async Task<ContainerExtractionResult> ExtractPayloadAsync(
        string containerPath, IReadOnlyList<string> partitionNames, CancellationToken cancellationToken, string outputDirectory,
        IProgress<double>? progress, IProgress<(long Processed, long Total)>? byteProgress = null)
    {
        var outputDir = outputDirectory;
        // Use one aggregate byte sample for both the bar and the textual statistics.
        await UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadParser.ExtractAsync(
            containerPath, [.. partitionNames], outputDir, cancellationToken, byteProgress: new CallbackProgress<(long Processed, long Total)>(sample =>
            {
                progress?.Report(sample.Total > 0 ? sample.Processed * 100.0 / sample.Total : 0);
                byteProgress?.Report(sample);
            })).ConfigureAwait(false);
        progress?.Report(100);

        // PayloadParser 以 <分区名>.img 命名输出；逐一确认存在后回填结果。
        // PayloadParser names outputs "<partition>.img"; confirm each exists before reporting.
        var outputNames = new List<string>();
        var outputPaths = new List<string>();
        foreach (var name in partitionNames)
        {
            var path = Path.Combine(outputDir, name + ".img");
            if (File.Exists(path))
            {
                outputNames.Add(name);
                outputPaths.Add(path);
            }
            else
            {
                throw new InvalidOperationException($"Payload partition '{name}' was not extracted. payload 分区 {name} 未提取成功。");
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return new ContainerExtractionResult(outputNames, outputPaths);
    }

    /// <summary>
    /// <para>从 ZIP 按条目路径流式解出镜像；<paramref name="fromPayloadOnly"/> 为 true 时
    /// 转而对内含 payload.bin 执行 payload 分区提取。</para>
    /// Stream-extracts images from a ZIP by entry path; when <paramref name="fromPayloadOnly"/> is
    /// true, instead extracts payload partitions from the contained payload.bin.
    /// </summary>
    private static async Task<ContainerExtractionResult> ExtractZipAsync(
        string containerPath, IReadOnlyList<string> selectedNames, bool fromPayloadOnly, CancellationToken cancellationToken, string outputDirectory, IProgress<double>? progress, IProgress<(long Processed, long Total)>? byteProgress)
    {
        if (fromPayloadOnly)
        {
            // 只勾选了 payload 分区：直接走 ZIP 包裹 payload 的提取路径。
            // Only payload partitions are selected: extract through the ZIP-wrapped payload path.
            return await ExtractPayloadAsync(containerPath, selectedNames, cancellationToken, outputDirectory, progress, byteProgress).ConfigureAwait(false);
        }

        var outputDir = outputDirectory;
        var names = new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
        var outputNames = new List<string>();
        var outputPaths = new List<string>();

        await using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var selectedEntries = archive.Entries.Where(entry => names.Contains(entry.FullName) && !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (selectedEntries.Length != names.Count) throw new InvalidDataException("A selected ZIP entry is missing or duplicated.");
        var totalBytes = selectedEntries.Sum(entry => entry.Length);
        if (totalBytes > 64L * 1024 * 1024 * 1024) throw new InvalidDataException("Selected ZIP entries exceed the extraction budget.");
        long copiedBytes = 0;
        progress?.Report(0);
        byteProgress?.Report((0, totalBytes));
        foreach (var entry in selectedEntries)
        {
            if (!names.Contains(entry.FullName) || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = BuildOutputPath(Path.Combine(outputDirectory, Path.GetFileName(containerPath)), entry.Name);
            var temporary = outputPath + ".extract-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                {
                    await using var entryStream = entry.Open();
                    // 有界复制：以条目声明长度为准，提前 EOF 抛错。
                    // Bounded copy: the declared entry length is authoritative; early EOF throws.
                    await CopyWithProgressAsync(entryStream, output, entry.Length, copiedBytes, totalBytes, progress, cancellationToken, byteProgress).ConfigureAwait(false);
                    copiedBytes += entry.Length;
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, outputPath);
            }
            finally { File.Delete(temporary); }
            outputNames.Add(entry.FullName);
            outputPaths.Add(outputPath);
        }

        progress?.Report(100);
        byteProgress?.Report((totalBytes, totalBytes));
        return new ContainerExtractionResult(outputNames, outputPaths);
    }

    /// <summary>
    /// <para>构造输出路径：与容器同目录（images/）原地产出 <c>&lt;条目名&gt;.img</c>；重名时追加序号。</para>
    /// Builds the output path: <c>&lt;entry&gt;.img</c> produced in place next to the container
    /// (inside images/); collisions get a numeric suffix.
    /// </summary>
    private static string BuildOutputPath(string containerPath, string entryName)
    {
        var directory = Path.GetDirectoryName(containerPath) ?? ".";
        var safeEntry = SanitizeFileName(entryName);
        // super/payload 分区名无扩展名，补 .img；ZIP 成员保留原始文件名（可能已带扩展名）。
        // super/payload partition names have no extension and get .img; ZIP members keep their names.
        if (!Path.HasExtension(safeEntry))
        {
            safeEntry += ".img";
        }

        var candidate = Path.Combine(directory, safeEntry);
        Directory.CreateDirectory(directory);

        var index = 1;
        while (File.Exists(candidate))
        {
            var withoutExtension = Path.GetFileNameWithoutExtension(safeEntry);
            var extension = Path.GetExtension(safeEntry);
            candidate = Path.Combine(directory, $"{withoutExtension}_{index++}{extension}");
        }

        return candidate;
    }

    /// <summary>Copies an exact byte range with throttled progress and bounded memory.</summary>
    private static async Task CopyWithProgressAsync(Stream source, Stream output, long length,
        long completed, long total, IProgress<double>? progress, CancellationToken cancellationToken, IProgress<(long Processed, long Total)>? byteProgress)
    {
        var buffer = new byte[65536];
        var lastReport = Environment.TickCount64;
        while (length > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Container entry was truncated.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            length -= read;
            completed += read;
            if (Environment.TickCount64 - lastReport >= 100 || length == 0)
            {
                progress?.Report(total > 0 ? completed * 100.0 / total : 100);
                byteProgress?.Report((completed, total));
                lastReport = Environment.TickCount64;
            }
        }
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Replaces invalid filename characters in an internal entry name.</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "entry" : sanitized;
    }
}

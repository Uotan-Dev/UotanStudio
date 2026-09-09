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
/// <para>新建项目定向提取器：把已复制进子项目 <c>images/</c> 的容器按勾选条目提取为独立 img 文件，
/// 输出与容器同目录（images/）。super 按 LP 分区流式裁剪；payload 经
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerPath) || !File.Exists(containerPath)
            || selectedNames is null || selectedNames.Count == 0)
        {
            return ContainerExtractionResult.Empty;
        }

        var kind = ContainerStructureAnalyzer.Analyze(containerPath).Kind;
        return kind switch
        {
            ContainerKind.Super => await ExtractSuperAsync(containerPath, selectedNames, cancellationToken).ConfigureAwait(false),
            ContainerKind.Payload => await ExtractPayloadAsync(containerPath, selectedNames, cancellationToken).ConfigureAwait(false),
            ContainerKind.Zip => await ExtractZipAsync(containerPath, selectedNames, fromPayloadOnly, cancellationToken).ConfigureAwait(false),
            _ => ContainerExtractionResult.Empty
        };
    }

    /// <summary>
    /// <para>从 super 镜像按分区名流式裁剪出独立 img（读取 LP 元数据定位 extent，逐段复制）。</para>
    /// Crops standalone imgs from a super image by partition names (LP extents located from metadata,
    /// copied segment by segment).
    /// </summary>
    private static async Task<ContainerExtractionResult> ExtractSuperAsync(
        string containerPath, IReadOnlyList<string> partitionNames, CancellationToken cancellationToken)
    {
        var metadata = MetadataReader.ReadFromImageFile(containerPath);
        var sectorSize = (long)MetadataFormat.LP_SECTOR_SIZE;
        var names = new HashSet<string>(partitionNames, StringComparer.OrdinalIgnoreCase);
        var outputNames = new List<string>();
        var outputPaths = new List<string>();

        await using var container = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        foreach (var partition in metadata.Partitions)
        {
            if (!names.Contains(partition.NameString))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = BuildOutputPath(containerPath, partition.NameString);
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);

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
                    container.Seek(checked((long)extent.TargetData * sectorSize), SeekOrigin.Begin);
                    await StreamCopy.CopyExactlyAsync(container, output, extentBytes, cancellationToken).ConfigureAwait(false);
                }

                sectors += extent.NumSectors;
            }

            output.SetLength(checked((long)sectors * sectorSize));
            outputNames.Add(partition.NameString);
            outputPaths.Add(outputPath);
        }

        return new ContainerExtractionResult(outputNames, outputPaths);
    }

    /// <summary>
    /// <para>经 <c>PayloadParser.ExtractAsync</c> 按分区名从 payload 提取（输出到容器所在目录）。</para>
    /// Extracts partitions from a payload by name via <c>PayloadParser.ExtractAsync</c>
    /// (output next to the container).
    /// </summary>
    private static async Task<ContainerExtractionResult> ExtractPayloadAsync(
        string containerPath, IReadOnlyList<string> partitionNames, CancellationToken cancellationToken)
    {
        var outputDir = Path.GetDirectoryName(containerPath) ?? ".";
        UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadParser.Extract(
            containerPath, [.. partitionNames], outputDir);

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
        string containerPath, IReadOnlyList<string> selectedNames, bool fromPayloadOnly, CancellationToken cancellationToken)
    {
        if (fromPayloadOnly)
        {
            // 只勾选了 payload 分区：直接走 ZIP 包裹 payload 的提取路径。
            // Only payload partitions are selected: extract through the ZIP-wrapped payload path.
            return await ExtractPayloadAsync(containerPath, selectedNames, cancellationToken).ConfigureAwait(false);
        }

        var outputDir = Path.GetDirectoryName(containerPath) ?? ".";
        var names = new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
        var outputNames = new List<string>();
        var outputPaths = new List<string>();

        await using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            if (!names.Contains(entry.FullName) || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = BuildOutputPath(containerPath, entry.Name);
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            await using var entryStream = entry.Open();
            // 有界复制：以条目声明长度为准，提前 EOF 抛错。
            // Bounded copy: the declared entry length is authoritative; early EOF throws.
            await StreamCopy.CopyExactlyAsync(entryStream, output, entry.Length, cancellationToken).ConfigureAwait(false);
            outputNames.Add(entry.FullName);
            outputPaths.Add(outputPath);
        }

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

    /// <summary>
    /// <para>把内部条目名净化为安全文件名：替换路径分隔与非法字符，保留可读性。</para>
    /// Sanitizes an inner entry name into a safe file name: path separators and illegal characters
    /// are replaced while readability is kept.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "entry" : sanitized;
    }
}

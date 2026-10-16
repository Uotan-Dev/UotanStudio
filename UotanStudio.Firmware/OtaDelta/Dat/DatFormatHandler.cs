using System.Buffers.Binary;
using System.Globalization;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.OtaDelta.Dat;

/// <summary>
/// Inspects, reads and writes Android block-OTA DAT image pairs (a transfer list plus its companion
/// new/patch data file, optionally Brotli-compressed).
/// <para>检查、读取并写入 Android 块 OTA DAT 镜像对（transfer list 及配套的新镜像/补丁数据文件，可选 Brotli 压缩）。</para>
/// </summary>
internal sealed class DatFormatHandler : IFormatHandler
{
    private static readonly string[] DataFileSuffixes = DatFileSet.DataSuffixes;

    /// <summary>Display name of the handled format. <para>所处理格式的显示名称。</para></summary>
    /// <value>The fixed label <c>Android DAT (transfer list)</c>. <para>固定标签 <c>Android DAT (transfer list)</c>。</para></value>
    public string FormatName => "Android DAT (transfer list)";

    /// <summary>Registered file extensions for the format. <para>该格式注册的文件扩展名。</para></summary>
    /// <value>A single entry <c>list</c>, without a leading dot; full DAT names are matched by content probing.
    /// <para>仅含 <c>list</c> 一项，不带点号；完整 DAT 名称通过内容探测匹配。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["list"];

    /// <summary>
    /// Determines whether this handler can process the given transfer-list or DAT data file.
    /// <para>判断该处理器能否处理指定的 transfer list 或 DAT 数据文件。</para>
    /// </summary>
    /// <param name="filePath">Path to probe; blank or non-existent paths are rejected without I/O beyond an existence check.
    /// <para>待探测路径；空串或不存在的路径仅经存在性检查即被拒绝。</para></param>
    /// <returns><see langword="true"/> when the file passes its structural or pairing check, <see langword="false"/> otherwise.
    /// <para>文件通过结构或配对检查时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        if (DatFileSet.IsDataFile(Path.GetFileName(filePath)))
        {
            return FindTransferList(filePath) is not null;
        }

        return LooksLikeTransferList(filePath);
    }

    /// <summary>
    /// Reads transfer-list metadata without unpacking: format version, command/block totals, block size, and the companion data file's type/size when present.
    /// <para>不解包读取 transfer list 元数据：格式版本、命令/块总数、块大小，以及配套数据文件的类型/大小（若存在）。</para>
    /// </summary>
    /// <param name="filePath">Transfer-list or companion data file path to inspect.
    /// <para>待检查的 transfer list 或配套数据文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the metadata read; cancellation faults the returned task.
    /// <para>取消元数据读取；取消时返回的任务将以异常结束。</para></param>
    /// <returns>A result carrying version, command/block totals, block size and companion data-file type/size metadata,
    /// or a failure when no transfer list resolves or a read error occurs.
    /// <para>结果携带版本、命令/块总数、块大小及配套数据文件类型/大小元数据；无法解析 transfer list 或读取失败时返回失败结果。</para></returns>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var transferListPath = FindTransferList(filePath);
                if (transferListPath is null)
                {

                    var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
                    if (ext is not ("dat" or "br") && LooksLikeTransferList(filePath))
                    {
                        transferListPath = filePath;
                    }
                }

                if (transferListPath is null)
                {
                    return FirmwareOpResult.Fail(
                        $"No transfer list found for '{filePath}'.  未找到对应的 transfer list。");
                }

                TransferList transferList;
                using (var reader = File.OpenText(transferListPath))
                {
                    transferList = TransferListParser.Parse(reader);
                }

                cancellationToken.ThrowIfCancellationRequested();

                var dataPath = FindDataFile(filePath);
                var (dataType, dataSize) = AnalyzeDataFile(dataPath);

                var commandBreakdown = transferList.Commands
                    .GroupBy(c => c.Kind)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());

                var metadata = new Dictionary<string, string>
                {
                    ["Format"] = FormatName,
                    ["Version"] = transferList.Version.ToString(CultureInfo.InvariantCulture),
                    ["CommandCount"] = transferList.Commands.Count.ToString(CultureInfo.InvariantCulture),
                    ["NewBlocks"] = transferList.NewBlocks.ToString(CultureInfo.InvariantCulture),
                    ["NewDataBlocks"] = transferList.NewDataBlocks.ToString(CultureInfo.InvariantCulture),
                    ["TotalBlocks"] = transferList.TotalBlocks.ToString(CultureInfo.InvariantCulture),
                    ["BlockSize"] = DatReader.BlockSize.ToString(CultureInfo.InvariantCulture),
                    ["TransferList"] = Path.GetFileName(transferListPath),
                    ["DataFile"] = dataPath is null ? "(not found)  未找到" : Path.GetFileName(dataPath),
                    ["DataFileType"] = dataType,
                    ["DataFileSize"] = dataSize.ToString(CultureInfo.InvariantCulture),
                };

                var dataLabel = dataPath is null ? "(not found)" : Path.GetFileName(dataPath);
                var summary =
                    $"Android DAT transfer list v{transferList.Version}: {transferList.Commands.Count} commands, " +
                    $"{transferList.NewDataBlocks} data blocks, block size {DatReader.BlockSize}; " +
                    $"data file '{dataLabel}' ({dataType}).";

                return FirmwareOpResult.Ok(
                    summary,
                    [.. commandBreakdown.Select(kv => $"{kv.Key}: {kv.Value}")],
                    metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read DAT metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Reconstructs the partition image from a full-OTA DAT pair.
    /// <para>由全量 OTA DAT 镜像对重建分区镜像。</para>
    /// </summary>
    /// <param name="filePath">Transfer-list or companion data file path identifying the DAT pair.
    /// <para>标识 DAT 镜像对的 transfer list 或配套数据文件路径。</para></param>
    /// <param name="outputDirectory">Directory that receives the reconstructed <c>&lt;stem&gt;.img</c>.
    /// <para>接收重建出的 <c>&lt;词干&gt;.img</c> 的目录。</para></param>
    /// <param name="options">Optional unpack options; its progress sink receives percentage updates. May be <see langword="null"/>.
    /// <para>可选解包选项；其进度接收器收到百分比更新。可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the reconstruction; cancellation faults the returned task.
    /// <para>取消重建；取消时返回的任务将以异常结束。</para></param>
    /// <returns>Success with the output image name and size metadata, or a failure when the pair is incomplete,
    /// the list does not parse, the pair is incremental, or writing fails.
    /// <para>成功时返回输出镜像名与大小元数据；镜像对不完整、解析失败、增量对或写入失败时返回失败结果。</para></returns>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transferListPath = FindTransferList(filePath);
        if (transferListPath is null)
        {
            return Task.FromResult(FirmwareOpResult.Fail(
                $"No transfer list found for '{filePath}'. 未找到对应的 transfer list。"));
        }

        var dataPath = FindDataFile(filePath);
        if (dataPath is null)
        {
            return Task.FromResult(FirmwareOpResult.Fail(
                "No .new.dat/.new.dat.br companion for the transfer list. 未找到配套的 .new.dat 数据文件。"));
        }

        TransferList transferList;
        try
        {
            transferList = TransferListParser.Parse(transferListPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(FirmwareOpResult.Fail(
                $"Failed to parse transfer list '{transferListPath}': {ex.Message}"));
        }

        if (transferList.Commands.Any(c => c.Kind is TransferListCommandKind.Move or TransferListCommandKind.Stash
                or TransferListCommandKind.Diff or TransferListCommandKind.Imgdiff))
        {
            return Task.FromResult(FirmwareOpResult.Fail(
                "Incremental DAT extraction needs a source image, which this handler does not accept. " +
                "Convert a full OTA instead. 增量 DAT 解包需要基座镜像，本处理器不接受基座；请改用于全量 OTA。"));
        }

        var outputName = DatFileSet.Stem(Path.GetFileName(transferListPath));
        if (string.IsNullOrEmpty(outputName) || outputName.Contains('/') || outputName.Contains('\\')
            || outputName.Contains("..", StringComparison.Ordinal))
        {
            return Task.FromResult(FirmwareOpResult.Fail($"Invalid DAT output name derived from '{transferListPath}'."));
        }

        var outputImagePath = Path.GetFullPath(Path.Combine(outputDirectory, outputName + ".img"));
        var brotliCompressed = DatFileSet.IsBrotli(dataPath);
        IFirmwareProgress? firmwareProgress = options?.Progress;
        var progress = firmwareProgress is null ? null : new FirmwareProgressAdapter(firmwareProgress, "Unpacking DAT 解包 DAT");

        return Task.Run(() =>
        {
            try
            {
                DatReader.Unpack(transferListPath, dataPath, outputImagePath, brotliCompressed, progress, cancellationToken);
                var bytes = new FileInfo(outputImagePath).Length;
                return FirmwareOpResult.Ok(
                    $"Reconstructed {outputName}.img ({bytes} bytes) from DAT transfer list v{transferList.Version}. " +
                    $"由 transfer list v{transferList.Version} 重建 {outputName}.img（{bytes} 字节）。",
                    [Path.GetFileName(outputImagePath)],
                    new Dictionary<string, string>
                    {
                        ["OutputImage"] = outputImagePath,
                        ["TransferList"] = Path.GetFileName(transferListPath),
                        ["DataFile"] = Path.GetFileName(dataPath),
                        ["BrotliCompressed"] = brotliCompressed ? "true" : "false",
                        ["SizeBytes"] = bytes.ToString(CultureInfo.InvariantCulture),
                    });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack DAT '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Packs a source image (raw or Android-sparse) into the DAT image pair.
    /// <para>将源镜像（原始或 Android 稀疏）打包为 DAT 镜像对。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Source image path. A sparse image is converted to raw before block-splitting.
    /// <para>源镜像路径。稀疏镜像会先转为原始镜像再分块。</para>
    /// </param>
    /// <param name="outputPath">
    /// Either a directory (the pair takes the input image's stem) or an explicit
    /// <c>&lt;stem&gt;.transfer.list</c> path (the sibling data file is derived from it).
    /// <para>目录（以源镜像词干命名镜像对）或显式 <c>&lt;词干&gt;.transfer.list</c> 路径（同级数据文件由它推导）。</para>
    /// </param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Success with the written pair file names and total byte count, or a failure when the input,
    /// derived names or the staged write is rejected.
    /// <para>成功时返回写出的镜像对文件名与总字节数；输入、派生名称或暂存写入被拒时返回失败结果。</para></returns>
    /// <remarks>
    /// Both files land together or the previous pair stays intact; on failure the staging
    /// directory and backups are removed so a partial write never replaces the user's existing
    /// <c>.new.dat</c> / <c>.transfer.list</c> with mismatched halves.
    /// <para>两个文件要么一起落盘，要么保留原有镜像对不变；失败时清理暂存与备份，避免半写状态覆盖用户已有的
    /// <c>.new.dat</c> / <c>.transfer.list</c> 组合。</para>
    /// </remarks>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackAsync(inputPath, outputPath, (IProgress<FileSystemByteProgress>?)null, cancellationToken);

    /// <summary>
    /// Packs a source image (raw or Android-sparse) into the DAT image pair, reporting byte progress.
    /// <para>将源镜像（原始或 Android 稀疏）打包为 DAT 镜像对，并上报字节进度。</para>
    /// </summary>
    /// <param name="inputPath">Source image path; a sparse image is converted to raw before block-splitting.
    /// <para>源镜像路径；稀疏镜像会先转为原始镜像再分块。</para></param>
    /// <param name="outputPath">Either a directory (the pair takes the input image's stem) or an explicit
    /// <c>&lt;stem&gt;.transfer.list</c> path (the sibling data file is derived from it).
    /// <para>目录（以源镜像词干命名镜像对）或显式 <c>&lt;词干&gt;.transfer.list</c> 路径（同级数据文件由它推导）。</para></param>
    /// <param name="progress">Byte progress sink for the DAT writing stage; may be <see langword="null"/>.
    /// <para>DAT 写入阶段的字节进度接收器；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the pack; after rollback the returned task faults with the cancellation.
    /// <para>取消打包；回滚后返回的任务以取消异常结束。</para></param>
    /// <returns>Success with the written pair file names and total byte count, or a failure when the input is
    /// missing, derived names or directories conflict, or the staged write fails.
    /// <para>成功时返回写出的镜像对文件名与总字节数；输入缺失、派生名称或目录冲突、暂存写入失败时返回失败结果。</para></returns>
    /// <remarks>
    /// Both files land together or the previous pair stays intact; on failure the staging directory and backups
    /// are removed so a partial write never replaces the user's existing <c>.new.dat</c> / <c>.transfer.list</c>
    /// with mismatched halves.
    /// <para>两个文件要么一起落盘，要么保留原有镜像对不变；失败时清理暂存与备份，避免半写状态覆盖用户已有的
    /// <c>.new.dat</c> / <c>.transfer.list</c> 组合。</para>
    /// </remarks>
    public Task<FirmwareOpResult> PackAsync(
        string inputPath,
        string outputPath,
        IProgress<FileSystemByteProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                return FirmwareOpResult.Fail(
                    $"Input image not found: {inputPath ?? "<empty>"}  找不到输入镜像。");
            }

            var inputFull = Path.GetFullPath(inputPath);
            var outputFull = Path.GetFullPath(outputPath);
            var brotli = ReadBrotliFlag();
            var maxRangeBlocks = ReadMaxRangeBlocks();
            string transferListPath, dataPath;
            if (outputFull.EndsWith(DatFileSet.TransferListSuffix, StringComparison.OrdinalIgnoreCase))
            {
                transferListPath = outputFull;
                var listStem = outputFull[..^DatFileSet.TransferListSuffix.Length];
                dataPath = listStem + (brotli ? ".new.dat.br" : ".new.dat");
            }
            else
            {
                var inputStem = Path.GetFileNameWithoutExtension(inputFull);
                if (string.IsNullOrEmpty(inputStem) || inputStem.Contains('/', StringComparison.Ordinal)
                    || inputStem.Contains('\\', StringComparison.Ordinal) || inputStem.Contains("..", StringComparison.Ordinal))
                {
                    return FirmwareOpResult.Fail($"Invalid DAT stem derived from '{inputFull}'.");
                }

                transferListPath = Path.Combine(outputFull, inputStem + DatFileSet.TransferListSuffix);
                dataPath = Path.Combine(outputFull, inputStem + (brotli ? ".new.dat.br" : ".new.dat"));
            }

            var directory = Path.GetDirectoryName(transferListPath)!;
            if (!string.Equals(directory, Path.GetDirectoryName(dataPath), StringComparison.Ordinal))
            {
                return FirmwareOpResult.Fail(
                    "Transfer list and data file must sit in the same directory. 两者必须位于同一目录。");
            }

            Directory.CreateDirectory(directory);
            var staging = Path.Combine(directory, ".uotan-dat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var stem = DatFileSet.Stem(Path.GetFileName(transferListPath));
            var stagedList = Path.Combine(staging, Path.GetFileName(transferListPath));
            var stagedDataPlain = Path.Combine(staging, stem + ".new.dat");
            var published = new List<string>();
            var backups = new List<(string Target, string Backup)>();
            try
            {
                var effectiveInput = inputFull;
                if (IsSparse(inputFull))
                {
                    var raw = Path.Combine(staging, "raw.img");
                    SparseImageConverter.ConvertSparseToRaw([inputFull], raw);
                    effectiveInput = raw;
                }

                var writeOptions = new DatWriteOptions
                {
                    CompressWithBrotli = brotli,
                    MaxCommandRangeBlocks = maxRangeBlocks,
                };
                var byteProgress = progress is null
                    ? null
                    : FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.WritingDat);
                DatWriter.Write(effectiveInput, stagedDataPlain, stagedList, writeOptions, byteProgress);
                cancellationToken.ThrowIfCancellationRequested();

                var stagedDataFinal = brotli ? stagedDataPlain + ".br" : stagedDataPlain;
                if (!File.Exists(stagedDataFinal) || !File.Exists(stagedList))
                {
                    return FirmwareOpResult.Fail(
                        $"DatWriter did not produce the expected artifacts under '{staging}'.");
                }

                BackUpIfExists(transferListPath, staging, backups);
                BackUpIfExists(dataPath, staging, backups);

                File.Move(stagedList, transferListPath);
                published.Add(transferListPath);
                File.Move(stagedDataFinal, dataPath);
                published.Add(dataPath);

                var size = new FileInfo(transferListPath).Length + new FileInfo(dataPath).Length;
                return FirmwareOpResult.Ok(
                    $"Wrote DAT image pair '{Path.GetFileName(transferListPath)}' + "
                    + $"'{Path.GetFileName(dataPath)}' ({size} bytes). 已写出 DAT 镜像对。",
                    [Path.GetFileName(transferListPath), Path.GetFileName(dataPath)],
                    new Dictionary<string, string>
                    {
                        ["TransferList"] = transferListPath,
                        ["DataFile"] = dataPath,
                        ["BrotliCompressed"] = brotli ? "true" : "false",
                        ["TotalBytes"] = size.ToString(CultureInfo.InvariantCulture),
                    });
            }
            catch (OperationCanceledException)
            {
                RollbackPublished(published, backups);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                    or ArgumentException or InvalidOperationException)
            {
                RollbackPublished(published, backups);
                return FirmwareOpResult.Fail($"Failed to pack DAT '{inputFull}': {ex.Message}");
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }, cancellationToken);
    }

    private static void BackUpIfExists(
        string target,
        string staging,
        List<(string Target, string Backup)> backups)
    {
        if (!File.Exists(target))
        {
            return;
        }

        var backup = Path.Combine(staging, ".backup-" + Guid.NewGuid().ToString("N"));
        File.Move(target, backup);
        backups.Add((target, backup));
    }

    private static void RollbackPublished(List<string> published, List<(string Target, string Backup)> backups)
    {
        foreach (var path in published)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var (target, backup) in backups)
        {
            try { if (File.Exists(backup)) File.Move(backup, target, overwrite: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsSparse(string path)
    {
        try
        {
            var header = FormatDetector.ReadHeader(path, 4);
            return header.Length == 4
                && BinaryPrimitives.ReadUInt32LittleEndian(header) == SparseFormat.SparseHeaderMagic;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool ReadBrotliFlag()
        => RepackMetadata.PackingOverrides is { } overrides
            && overrides.TryGetValue("DatBrotli", out var value)
            && bool.TryParse(value, out var parsed) && parsed;

    private static int ReadMaxRangeBlocks()
        => RepackMetadata.PackingOverrides is { } overrides
            && overrides.TryGetValue("DatMaxCommandRangeBlocks", out var raw)
            && int.TryParse(raw, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed : 0;

    /// <summary>
    /// Resolves the companion transfer list for a given path.
    /// <para>解析指定路径对应的 transfer list：*.transfer.list 路径返回自身；数据文件路径按标准 &lt;stem&gt;.transfer.list 命名解析，找不到时回退到同目录下任一 *.transfer.list 同级文件。</para>
    /// </summary>
    private static string? FindTransferList(string path)
    {
        if (path.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        foreach (var suffix in DataFileSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = path[..^suffix.Length] + ".transfer.list";
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                break;
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                foreach (var candidate in Directory.EnumerateFiles(dir, "*.transfer.list"))
                {
                    return candidate;
                }
            }
        }
        catch (IOException)
        {

        }
        catch (UnauthorizedAccessException)
        {

        }

        return null;
    }

    /// <summary>
    /// Resolves the companion data file for the opened path: the transfer list's .new.dat / .new.dat.br sibling, or the data file itself when it was opened directly.
    /// <para>解析所打开路径对应的数据文件：transfer list 的 .new.dat/.new.dat.br 同级文件， 或直接打开数据文件时返回其自身。</para>
    /// </summary>
    private static string? FindDataFile(string path)
    {
        if (!path.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var stem = path[..^".transfer.list".Length];
        foreach (var suffix in DataFileSuffixes)
        {
            var candidate = stem + suffix;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Classifies the data file: Brotli (.br) by extension, Android-sparse by the leading 4-byte little-endian magic, otherwise raw.
    /// <para>对数据文件分类：.br 扩展名视为 Brotli，头部 4 字节小端魔数命中视为 Android 稀疏格式， 其余视为原始数据。</para>
    /// </summary>
    private static (string Type, long Size) AnalyzeDataFile(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return ("(not found)  未找到", 0);
        }

        var size = new FileInfo(path).Length;
        if (path.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
        {
            return ("brotli (.br)", size);
        }

        try
        {
            var header = FormatDetector.ReadHeader(path, 4);
            if (header.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(header) == SparseFormat.SparseHeaderMagic)
            {
                return ("android-sparse", size);
            }
        }
        catch (IOException)
        {

        }
        catch (UnauthorizedAccessException)
        {

        }

        return ("raw", size);
    }

    /// <summary>
    /// Bounded structural probe for transfer-list text: the first line must be a positive integer version and the second a non-negative block count; v2+ also requires the two stash-count lines.
    /// <para>transfer list 文本的有界结构探测：首行为正整数版本号、第二行为非负块数；v2+ 还要求两个暂存计数行。</para>
    /// </summary>
    private static bool LooksLikeTransferList(string path)
    {
        try
        {
            using var reader = new StreamReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan));
            if (reader.ReadLine() is not { } versionLine
                || !int.TryParse(versionLine, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                || version < 1)
            {
                return false;
            }

            if (reader.ReadLine() is not { } blocksLine
                || !long.TryParse(blocksLine, NumberStyles.None, CultureInfo.InvariantCulture, out var newBlocks)
                || newBlocks < 0)
            {
                return false;
            }

            if (version >= 2)
            {
                if (reader.ReadLine() is not { } stashLine
                    || !int.TryParse(stashLine, NumberStyles.None, CultureInfo.InvariantCulture, out var stashEntries)
                    || stashEntries < 0)
                {
                    return false;
                }

                if (reader.ReadLine() is not { } maxStashLine
                    || !long.TryParse(maxStashLine, NumberStyles.None, CultureInfo.InvariantCulture, out var maxStash)
                    || maxStash < 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
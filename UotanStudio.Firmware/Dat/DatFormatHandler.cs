using System.Buffers.Binary;
using System.Globalization;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Sparse;

namespace UotanStudio.Firmware.Dat;

/// <summary>
/// Format handler for Android OTA DAT image pairs: a <c>.transfer.list</c> plus its companion
/// <c>.new.dat</c> / <c>.new.dat.br</c> data file. Exposes read-only metadata (transfer list version,
/// command/block counts, block size, companion data file type/size); unpacking and packing are
/// intentionally not wired into this handler.
/// <para>Android OTA DAT 镜像对的格式处理器：<c>.transfer.list</c> 与其配套的 <c>.new.dat</c> /
/// <c>.new.dat.br</c> 数据文件。仅提供只读元数据（transfer list 版本、命令/块数、块大小、配套数据文件的
/// 类型/大小）；有意不接入解包与打包。</para>
/// </summary>
public sealed class DatFormatHandler : IFormatHandler
{
    private static readonly string[] DataFileSuffixes = [".new.dat.br", ".new.dat", ".patch.dat.br", ".patch.dat"];

    /// <summary>
    /// Gets the human-readable format name.
    /// <para>获取人类可读的格式名称。</para>
    /// </summary>
    public string FormatName => "Android DAT (transfer list)";

    /// <summary>
    /// Gets the typical file extensions (without dot, lowercase): <c>list</c> for the transfer list.
    /// <c>dat</c> is intentionally <b>not</b> registered: the application layer already routes bare
    /// <c>.dat</c> files to the hex editor (the residual strongly-binary set in the app's
    /// <c>FileOpenMode</c>), and claiming it here would override that existing ownership — the companion
    /// data file is instead recognized by content/context sniffing (a sibling <c>*.transfer.list</c>) in
    /// phase two of <see cref="FormatDetector.Detect"/>.
    /// <para>获取典型文件扩展名（不带点，小写）：transfer list 为 <c>list</c>。<c>dat</c> 有意<b>不</b>注册：
    /// 应用层已将裸 <c>.dat</c> 文件路由到十六进制编辑器（应用 <c>FileOpenMode</c> 的残留强二进制集合），
    /// 在此注册会覆盖该既有归属；配套数据文件改为通过 <see cref="FormatDetector.Detect"/> 阶段二的内容/上下文
    /// 嗅探（存在同目录 <c>*.transfer.list</c>）识别。</para>
    /// </summary>
    public IReadOnlyList<string> FileExtensions { get; } = ["list"];

    /// <summary>
    /// Determines whether this handler can process the given file: a <c>.list</c> file must look like a
    /// transfer list (leading version / block-count lines), while a <c>.dat</c> file must be paired with a
    /// sibling <c>*.transfer.list</c>. Extensionless content sniffing also recognizes transfer-list text.
    /// <para>判断该处理器能否处理指定文件：<c>.list</c> 文件须形如 transfer list（开头的版本/块数行），
    /// <c>.dat</c> 文件须存在同目录配套的 <c>*.transfer.list</c>。无扩展名的内容嗅探同样能识别 transfer list 文本。</para>
    /// </summary>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext == "dat")
        {
            return FindTransferList(filePath) is not null;
        }

        return LooksLikeTransferList(filePath);
    }

    /// <summary>
    /// Reads transfer-list metadata without unpacking: format version, command/block totals, block size,
    /// and the companion data file's type/size when present.
    /// <para>不解包读取 transfer list 元数据：格式版本、命令/块总数、块大小，以及配套数据文件的类型/大小（若存在）。</para>
    /// </summary>
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
                    // 非标准命名的 transfer list（如 foo.list）仍可按内容识别；只对文本类扩展名做有界探测，
                    // 避免读取大型二进制 .dat/.br 数据文件的整行。
                    // A transfer list with a non-standard name (e.g. foo.list) is still recognized by content;
                    // the bounded probe is restricted to text-like extensions to avoid reading whole lines of
                    // large binary .dat/.br data files.
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
    /// Not supported: unpacking a DAT image pair is intentionally out of scope for the read-only handler.
    /// <para>不支持：DAT 镜像对解包有意不纳入该只读处理器。</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FirmwareOpResult.Fail(
            "Unpacking a DAT image pair is not supported by this read-only handler.  该只读处理器不支持解包 DAT 镜像对。"));

    /// <summary>
    /// Not supported: packing a DAT image pair is intentionally out of scope for the read-only handler.
    /// <para>不支持：DAT 镜像对打包有意不纳入该只读处理器。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => Task.FromResult(FirmwareOpResult.Fail(
            "Packing a DAT image pair is not supported by this read-only handler.  该只读处理器不支持打包 DAT 镜像对。"));

    /// <summary>
    /// Resolves the companion transfer list for a given path. A <c>*.transfer.list</c> path resolves to
    /// itself; a data-file path resolves via the standard <c>&lt;stem&gt;.transfer.list</c> naming, falling
    /// back to any sibling <c>*.transfer.list</c> in the same directory.
    /// <para>解析指定路径对应的 transfer list：<c>*.transfer.list</c> 路径返回自身；数据文件路径按标准
    /// <c>&lt;stem&gt;.transfer.list</c> 命名解析，找不到时回退到同目录下任一 <c>*.transfer.list</c> 同级文件。</para>
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
            // 同级扫描为尽力而为，忽略 I/O 失败。
            // Best-effort sibling scan; ignore I/O failures.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above. 同上。
        }

        return null;
    }

    /// <summary>
    /// Resolves the companion data file for the opened path: the transfer list's <c>.new.dat</c> /
    /// <c>.new.dat.br</c> sibling, or the data file itself when it was opened directly.
    /// <para>解析所打开路径对应的数据文件：transfer list 的 <c>.new.dat</c>/<c>.new.dat.br</c> 同级文件，
    /// 或直接打开数据文件时返回其自身。</para>
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
    /// Classifies the data file: Brotli (<c>.br</c>) by extension, Android-sparse by the leading 4-byte
    /// little-endian magic, otherwise raw. Returns the type label and the file size in bytes.
    /// <para>对数据文件分类：<c>.br</c> 扩展名视为 Brotli，头部 4 字节小端魔数命中视为 Android 稀疏格式，
    /// 其余视为原始数据。返回类型标签与文件字节大小。</para>
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
            // 忽略：回落到「raw」。
            // Ignore: fall through to "raw".
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above. 同上。
        }

        return ("raw", size);
    }

    /// <summary>
    /// Bounded structural probe for transfer-list text: the first line must be a positive integer version
    /// and the second a non-negative block count; v2+ also requires the two stash-count lines. Only a few
    /// leading lines are read, never the whole file.
    /// <para>transfer list 文本的有界结构探测：首行为正整数版本号、第二行为非负块数；v2+ 还要求两个暂存计数行。
    /// 仅读取开头数行，绝不读取整个文件。</para>
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
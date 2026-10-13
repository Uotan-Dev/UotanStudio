using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Services.Compare;

/// <summary>
/// Reads comparison inputs and runs the diff algorithms over injected dependencies.
/// <para>读取比对输入，基于注入的依赖运行差异比对算法。</para>
/// </summary>
/// <param name="binary">Single-pass binary comparison component. <para>单遍二进制比对组件。</para></param>
/// <param name="text">Line-level text diff component. <para>行级文本差异组件。</para></param>
/// <param name="folders">Directory tree comparison component. <para>目录树比对组件。</para></param>
/// <param name="detectFormat">Resolver turning a path into its readonly format handler. <para>按路径解析只读格式处理器的委托。</para></param>
/// <param name="registeredFormats">Provider of the registered readonly format handlers. <para>已注册只读格式处理器的提供者。</para></param>
/// <param name="reportWarning">Sink receiving non-fatal warning text. <para>接收非致命警告文本的委托。</para></param>
internal sealed class ComparisonService(
    BinaryDiffService binary,
    TextDiffService text,
    FolderCompareService folders,
    Func<string, IReadonlyFormatHandler?> detectFormat,
    Func<IEnumerable<IReadonlyFormatHandler>> registeredFormats,
    Action<string> reportWarning) : IComparisonService
{
    internal const int MaximumTextLines = 2_000_000;
    internal const long MaximumTextBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Creates an instance over the standalone comparison components and the firmware format registry.
    /// <para>基于独立比对组件与固件格式注册表创建实例。</para>
    /// </summary>
    /// <returns>The configured service instance. <para>配置完成的服务实例。</para></returns>
    internal static ComparisonService CreateDefault() => new(
        new BinaryDiffService(), new TextDiffService(), new FolderCompareService(),
        path =>
        {
            FirmwareFormatRegistry.RegisterAll();
            return FormatDetector.Detect(path);
        },
        () =>
        {
            FirmwareFormatRegistry.RegisterAll();
            return FormatDetector.RegisteredHandlers;
        },
        warning => LogService.Instance.Write(LogLevel.Warn, "Compare", warning));

    /// <summary>
    /// Probes text content while keeping registered firmware extensions in binary mode.
    /// <para>探测文本内容，已注册的固件扩展名始终按二进制处理。</para>
    /// </summary>
    /// <param name="path">File path to probe. <para>要探测的文件路径。</para></param>
    /// <param name="cancellationToken">Token observing the probe. <para>观测探测过程的取消令牌。</para></param>
    /// <returns>True when the file is text-like. <para>文件形似文本时为 true。</para></returns>
    public Task<bool> IsTextLikeFileAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handlers = registeredFormats();
        if (!binary.IsTextFile(path)) return false;

        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension.Length == 0 || !handlers.Any(handler => handler.FileExtensions.Any(
            candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)));
    }, cancellationToken);

    /// <summary>
    /// Enumerates every entry below a directory as left-only items in ordinal relative-path order.
    /// <para>枚举目录下的全部条目，按相对路径序数顺序记为仅左侧条目。</para>
    /// </summary>
    /// <param name="path">Directory to enumerate. <para>要枚举的目录。</para></param>
    /// <param name="cancellationToken">Token observing the enumeration. <para>观测枚举过程的取消令牌。</para></param>
    /// <returns>The ordered directory items. <para>排序后的目录条目。</para></returns>
    public Task<IReadOnlyList<FolderCompareItem>> ReadDirectoryAsync(string path, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<FolderCompareItem>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<FolderCompareItem> entries = [];
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = Directory.Exists(entry);
            entries.Add(new FolderCompareItem
            {
                RelativePath = Path.GetRelativePath(path, entry).Replace('\\', '/'),
                Status = FolderItemStatus.LeftOnly,
                IsDirectory = isDirectory,
                LeftSize = isDirectory ? -1 : new FileInfo(entry).Length,
                RightSize = -1,
            });
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return entries;
    }, cancellationToken);

    /// <summary>
    /// Reads BOM-aware text bounded by the two-million-line and 256 MiB limits.
    /// <para>读取带 BOM 探测的文本，受两百万行与 256 MiB 上限约束。</para>
    /// </summary>
    /// <param name="path">Text file to read. <para>要读取的文本文件。</para></param>
    /// <param name="cancellationToken">Token observing the read. <para>观测读取过程的取消令牌。</para></param>
    /// <returns>The lines of the file. <para>文件的各行。</para></returns>
    /// <exception cref="FormatException">The file exceeds the line or byte limit. <para>文件超出行数或字节上限。</para></exception>
    public Task<IReadOnlyList<string>> ReadTextAsync(string path, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<string>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.SequentialScan);
        if (stream.Length > MaximumTextBytes)
            throw new FormatException($"Text file exceeds the {MaximumTextBytes} byte limit.");

        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 81920, leaveOpen: false);
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lines.Count >= MaximumTextLines)
                throw new FormatException($"Text file exceeds the {MaximumTextLines} line limit.");
            lines.Add(line);
        }

        return lines;
    }, cancellationToken);

    /// <summary>
    /// Writes text lines back with the detected encoding and line ending, swapping a staging file in.
    /// <para>按探测到的编码与换行风格写回文本行，先写临时文件再原子替换到位。</para>
    /// </summary>
    /// <param name="path">Target file path. <para>目标文件路径。</para></param>
    /// <param name="lines">Lines to write. <para>要写入的行。</para></param>
    /// <param name="cancellationToken">Token observing the write. <para>观测写入过程的取消令牌。</para></param>
    public Task SaveTextAsync(string path, IReadOnlyList<string> lines, CancellationToken cancellationToken) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(lines);
        cancellationToken.ThrowIfCancellationRequested();

        var (encoding, lineEnding, endsWithNewLine) = DetectTextLayout(path);
        var builder = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(lineEnding);
            }

            builder.Append(lines[index] ?? string.Empty);
        }

        if (endsWithNewLine && lines.Count > 0)
        {
            builder.Append(lineEnding);
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        var staging = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(staging, builder.ToString(), encoding);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }, cancellationToken);

    /// <summary>
    /// Detects a text encoding from its BOM, the first line ending in the leading 64 KiB, and whether
    /// the file ends with a newline.
    /// <para>按 BOM 探测文本编码，取前 64 KiB 中出现的第一个换行风格，并判断文件末尾是否有换行。</para>
    /// </summary>
    /// <param name="path">Text file to inspect. <para>要检查的文本文件。</para></param>
    /// <returns>The encoding, line ending and trailing-newline flag. <para>编码、换行风格与末尾换行标记。</para></returns>
    private static (Encoding Encoding, string LineEnding, bool EndsWithNewLine) DetectTextLayout(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[4];
        var headerLength = stream.Read(header, 0, header.Length);
        var encoding = DetectEncoding(header, headerLength);

        stream.Seek(0, SeekOrigin.Begin);
        var sample = new byte[Math.Min(65536, Math.Max(1, stream.Length))];
        var sampleLength = stream.Read(sample, 0, sample.Length);
        var lineEnding = "\n";
        for (var index = 0; index < sampleLength; index++)
        {
            if (sample[index] != (byte)'\n')
            {
                continue;
            }

            lineEnding = index > 0 && sample[index - 1] == (byte)'\r' ? "\r\n" : "\n";
            break;
        }

        var endsWithNewLine = false;
        var tail = new byte[2];
        if (stream.Length >= 1)
        {
            stream.Seek(Math.Max(0, stream.Length - tail.Length), SeekOrigin.Begin);
            var tailLength = stream.Read(tail, 0, tail.Length);
            if (tailLength >= 1)
            {
                endsWithNewLine = tail[tailLength - 1] == (byte)'\n';
            }
        }

        return (encoding, lineEnding, endsWithNewLine);
    }

    /// <summary>
    /// Selects the encoding indicated by a byte-order mark, falling back to UTF-8 without one.
    /// <para>按字节顺序标记选择编码，没有标记时回退为不带 BOM 的 UTF-8。</para>
    /// </summary>
    /// <param name="header">Leading bytes read from the file. <para>从文件读到的起始字节。</para></param>
    /// <param name="length">Number of valid bytes in the header. <para>头部中有效字节的数量。</para></param>
    /// <returns>The encoding matching the byte-order mark. <para>与字节顺序标记对应的编码。</para></returns>
    private static Encoding DetectEncoding(byte[] header, int length)
    {
        if (length >= 4 && header[0] == 0xFF && header[1] == 0xFE && header[2] == 0x00 && header[3] == 0x00)
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        }

        if (length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (length >= 2 && header[0] == 0xFF && header[1] == 0xFE)
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }

        if (length >= 2 && header[0] == 0xFE && header[1] == 0xFF)
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    /// <summary>
    /// Reads firmware metadata through the readonly format handler detected for the file.
    /// <para>经为该文件检测到的只读格式处理器读取固件元数据。</para>
    /// </summary>
    /// <param name="path">File to inspect. <para>要检查的文件。</para></param>
    /// <param name="cancellationToken">Token observing the read. <para>观测读取过程的取消令牌。</para></param>
    /// <returns>The metadata result, or an unrecognized result when no read succeeded. <para>元数据结果；读取未成功时为未识别结果。</para></returns>
    public Task<ComparisonMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handler = detectFormat(path);
        if (handler is null) return ComparisonMetadata.Unrecognized;

        var result = await handler.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Success)
        {
            reportWarning($"Metadata read failed for '{path}': {result.Summary}");
            return ComparisonMetadata.Unrecognized;
        }

        return new ComparisonMetadata(handler.FormatName, result.Summary,
            result.Metadata, result.Entries, result.Components);
    }, cancellationToken);

    /// <summary>
    /// Compares two text files with the bounded line-diff algorithm over file streams.
    /// <para>通过文件流以有上限的行级差异算法比较两个文本文件。</para>
    /// </summary>
    /// <param name="left">Left file path. <para>左文件路径。</para></param>
    /// <param name="right">Right file path. <para>右文件路径。</para></param>
    /// <param name="cancellationToken">Token observing the comparison. <para>观测比对过程的取消令牌。</para></param>
    /// <returns>The aligned diff lines. <para>对齐后的差异行。</para></returns>
    public Task<IReadOnlyList<TextDiffLine>> CompareTextAsync(string left, string right, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<TextDiffLine>>(() =>
    {
        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return text.DiffLines(leftStream, rightStream, cancellationToken);
    }, cancellationToken);

    /// <summary>
    /// Compares two directories with the recursive structure and content comparison.
    /// <para>使用递归的结构与内容比较来比较两个目录。</para>
    /// </summary>
    /// <param name="left">Left directory path. <para>左目录路径。</para></param>
    /// <param name="right">Right directory path. <para>右目录路径。</para></param>
    /// <param name="cancellationToken">Token observing the comparison. <para>观测比对过程的取消令牌。</para></param>
    /// <returns>The folder comparison result. <para>文件夹比对结果。</para></returns>
    public Task<FolderCompareResult> CompareDirectoriesAsync(string left, string right, CancellationToken cancellationToken) =>
        folders.CompareAsync(left, right, cancellationToken);

    /// <summary>
    /// Scans binary differences between two files and marks the supplied row-diff bitmap.
    /// <para>扫描两个文件之间的二进制差异，并在传入的行差异位图上标记。</para>
    /// </summary>
    /// <param name="left">Left file path. <para>左文件路径。</para></param>
    /// <param name="right">Right file path. <para>右文件路径。</para></param>
    /// <param name="rowBitmap">Row-diff bitmap to fill. <para>要写入的行差异位图。</para></param>
    /// <param name="cancellationToken">Token observing the scan. <para>观测扫描过程的取消令牌。</para></param>
    /// <returns>The binary diff statistics. <para>二进制差异统计信息。</para></returns>
    public Task<BinaryDiffStats> CompareBinaryAsync(string left, string right, byte[] rowBitmap, CancellationToken cancellationToken) =>
        Task.Run(() => binary.CompareStats(left, right, rowBitmap, cancellationToken), cancellationToken);

    /// <summary>
    /// Copies one relative file from the source directory to the same relative position of the target.
    /// <para>把源目录下的一个相对文件复制到目标目录的相同相对位置。</para>
    /// </summary>
    /// <param name="source">Source directory path. <para>源目录路径。</para></param>
    /// <param name="target">Target directory path. <para>目标目录路径。</para></param>
    /// <param name="relativePath">Relative file path inside the source directory. <para>源目录内的相对文件路径。</para></param>
    /// <param name="cancellationToken">Token observing the copy. <para>观测复制过程的取消令牌。</para></param>
    public Task CopyAsync(string source, string target, string relativePath, CancellationToken cancellationToken) =>
        folders.CopyAsync(source, target, relativePath, cancellationToken);

    /// <summary>
    /// Deletes one relative file inside the given directory through the safe relative-path resolver.
    /// <para>经安全的相对路径解析器删除指定目录内的一个相对文件。</para>
    /// </summary>
    /// <param name="directory">Directory containing the file. <para>包含该文件的目录。</para></param>
    /// <param name="relativePath">Relative file path inside the directory. <para>目录内的相对文件路径。</para></param>
    /// <param name="cancellationToken">Token observing the deletion. <para>观测删除过程的取消令牌。</para></param>
    public Task DeleteAsync(string directory, string relativePath, CancellationToken cancellationToken) =>
        folders.DeleteAsync(directory, relativePath, cancellationToken);
}
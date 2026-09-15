using System.Text;
using UotanStudio.Firmware;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Services.Compare;

/// <summary>
/// Reads comparison inputs and runs existing algorithms off the UI thread. Instances hold
/// dependencies only; cancellation, current paths, caches and publication belong to the caller.
/// </summary>
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

    /// <summary>Wires desktop defaults at the service boundary, retaining the public legacy singletons.</summary>
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

    /// <inheritdoc />
    public Task<bool> IsTextLikeFileAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handlers = registeredFormats();
        if (!binary.IsTextFile(path)) return false;

        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension.Length == 0 || !handlers.Any(handler => handler.FileExtensions.Any(
            candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)));
    }, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FolderCompareItem>> ReadDirectoryAsync(string path, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<FolderCompareItem>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<FolderCompareItem>();
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

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ReadTextAsync(string path, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<string>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.SequentialScan);
        // This API owns a seekable file stream: enforce the original byte cap before decoding.
        if (stream.Length > MaximumTextBytes)
            throw new FormatException($"Text file exceeds the {MaximumTextBytes} byte limit.");

        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 81920, leaveOpen: false);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lines.Count >= MaximumTextLines)
                throw new FormatException($"Text file exceeds the {MaximumTextLines} line limit.");
            lines.Add(line);
        }

        return lines;
    }, cancellationToken);

    /// <inheritdoc />
    public Task SaveTextAsync(string path, IReadOnlyList<string> lines, CancellationToken cancellationToken) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(lines);
        cancellationToken.ThrowIfCancellationRequested();

        // 原文件的编码（按 BOM）与换行风格必须保留：文本对比的编辑只改行内容，不改变文件形态。
        // The original file's encoding (from its BOM) and line ending must survive: editing a text comparison changes
        // line content, not the file's shape.
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
            // 同目录内原子替换：写入失败或取消时保留用户原有文件。
            // Atomic swap inside the same directory: a failed or cancelled write keeps the user's existing file.
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
    /// <para>探测文本文件的编码、换行风格与末尾是否有换行：编码按 BOM 判定（无 BOM 视为 UTF-8），换行风格取
    /// 文件前 64 KiB 中出现的第一个换行（默认 LF），末尾换行按最后一个字节判定。</para>
    /// Detects a text file's encoding, line ending and trailing newline: the encoding comes from the BOM (UTF-8 without a
    /// BOM otherwise), the line ending is the first one found in the first 64 KiB (LF by default) and the trailing
    /// newline comes from the last bytes.
    /// </summary>
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

    /// <summary>按 BOM 判定编码；无 BOM 时使用不带 BOM 的 UTF-8。</summary>
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<IReadOnlyList<TextDiffLine>> CompareTextAsync(string left, string right, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<TextDiffLine>>(() =>
    {
        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return text.DiffLines(leftStream, rightStream, cancellationToken);
    }, cancellationToken);

    /// <inheritdoc />
    public Task<FolderCompareResult> CompareDirectoriesAsync(string left, string right, CancellationToken cancellationToken) =>
        folders.CompareAsync(left, right, cancellationToken);

    /// <inheritdoc />
    public Task<BinaryDiffStats> CompareBinaryAsync(string left, string right, byte[] rowBitmap, CancellationToken cancellationToken) =>
        Task.Run(() => binary.CompareStats(left, right, rowBitmap, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task CopyAsync(string source, string target, string relativePath, CancellationToken cancellationToken) =>
        folders.CopyAsync(source, target, relativePath, cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(string directory, string relativePath, CancellationToken cancellationToken) =>
        folders.DeleteAsync(directory, relativePath, cancellationToken);
}

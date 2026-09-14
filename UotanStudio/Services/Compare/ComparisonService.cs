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
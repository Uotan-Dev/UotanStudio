using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Compress.Zip;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Recognizes a SpacemiT bootable-card ZIP package and streams its images into a raw card image.
/// A package qualifies when it carries a GPT <c>partition_*.json</c> table whose image references
/// all resolve inside the archive; the ZIP entries are never unpacked to a temporary directory.
/// <para>识别 SpacemiT 可启动卡 ZIP 包并把包内镜像流式合成为裸卡镜像。包内含一张 GPT
/// <c>partition_*.json</c> 分区表、且其镜像引用全部能在包内解析时才算卡包；ZIP 条目永不解包到临时目录。</para>
/// </summary>
public sealed class SpacemiTCardPackage
{
    private const int MaxEntryNameLength = 512;
    private const long SectorSizeBytes = 512;
    private const long GptReserveBlocks = 34;
    private const long ImageAlignBytes = 4096;
    private const string BootCardTable = "partition_universal.json";
    private const string FlashCardTable = "partition_flash.json";

    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RegexOverallBudget = TimeSpan.FromSeconds(2);
    private static readonly ZipContainerOptions CardBounds = new(
        MaxEntries: 4096,
        MaxEntryBytes: 512L << 30,
        MaxTotalBytes: 16L << 40);

    private SpacemiTCardPackage(string packagePath, SpacemiTPartitionTable table,
        string tableEntryName, long estimatedImageSizeBytes)
    {
        PackagePath = packagePath;
        Table = table;
        TableEntryName = tableEntryName;
        EstimatedImageSizeBytes = estimatedImageSizeBytes;
    }

    /// <summary>
    /// Gets the absolute path of the ZIP package.
    /// <para>获取 ZIP 包的绝对路径。</para>
    /// </summary>
    public string PackagePath { get; }

    /// <summary>
    /// Gets the parsed GPT partition table selected from the package.
    /// <para>获取从包中选出的已解析 GPT 分区表。</para>
    /// </summary>
    public SpacemiTPartitionTable Table { get; }

    /// <summary>
    /// Gets the archive entry name of the selected partition table.
    /// <para>获取所选分区表在归档内的条目名。</para>
    /// </summary>
    public string TableEntryName { get; }

    /// <summary>
    /// Gets the number of partitions declared by the table.
    /// <para>获取分区表声明的分区数。</para>
    /// </summary>
    public int PartitionCount => Table.Partitions.Count;

    /// <summary>
    /// Gets a lower-bound estimate, in bytes, of the synthesized card image, used for the selection summary.
    /// <para>获取合成卡镜像大小的估算下界（字节），用于选择阶段的摘要显示。</para>
    /// </summary>
    public long EstimatedImageSizeBytes { get; }

    /// <summary>
    /// Recognizes a card package at <paramref name="packagePath"/>, validating that every image the table
    /// references resolves inside the archive.
    /// <para>识别 <paramref name="packagePath"/> 处的卡包，并校验分区表引用的每个镜像都能在包内解析。</para>
    /// </summary>
    /// <param name="packagePath">Path to the ZIP archive. <para>ZIP 归档路径。</para></param>
    /// <param name="cancellationToken">Cancels the recognition. <para>取消识别。</para></param>
    /// <returns>The recognized package, or <see langword="null"/> when the file is not a readable ZIP or holds no
    /// usable GPT partition table. <para>识别出的卡包；文件不是可读 ZIP 或不含可用 GPT 分区表时为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidDataException">A GPT table is present but a ZIP entry or image reference is unsafe,
    /// missing or ambiguous. <para>存在 GPT 表，但某 ZIP 条目或镜像引用不安全、缺失或有歧义。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static SpacemiTCardPackage? TryOpen(string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPath = Path.GetFullPath(packagePath);
        ZipArchive archive;
        try
        {
            archive = ZipContainer.OpenRead(fullPath, CardBounds);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (archive)
        {
            var index = BuildIndex(archive, cancellationToken);
            var (table, tableName) = FindTable(index, cancellationToken);
            if (table is null)
            {
                return null;
            }

            var estimate = EstimateImageSize(table, index, cancellationToken);
            return new SpacemiTCardPackage(fullPath, table, tableName!, estimate);
        }
    }

    /// <summary>
    /// Synthesizes the raw card image at <paramref name="destinationPath"/> by streaming the ZIP entries through
    /// <see cref="SpacemiTCardImageWriter"/>; the archive stays open only for the duration of this call.
    /// <para>经 <see cref="SpacemiTCardImageWriter"/> 把 ZIP 条目流式合成为 <paramref name="destinationPath"/> 处的裸卡镜像；
    /// 归档仅在本调用期间保持打开。</para>
    /// </summary>
    /// <param name="destinationPath">Destination raw image path. <para>目标裸镜像路径。</para></param>
    /// <param name="progress">Optional synthesis progress sink. <para>可选的合成进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels the synthesis. <para>取消合成。</para></param>
    /// <returns>The final image size and resolved per-partition layout. <para>最终镜像大小与解析出的逐分区布局。</para></returns>
    /// <exception cref="InvalidDataException">An image reference is missing, ambiguous or unsafe.
    /// <para>镜像引用缺失、有歧义或不安全。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public SpacemiTCardImageResult Synthesize(string destinationPath,
        IProgress<SpacemiTCardImageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipContainer.OpenRead(PackagePath, CardBounds);
        var index = BuildIndex(archive, cancellationToken);

        SpacemiTCardContent? Resolve(string reference)
        {
            var entry = index.Resolve(reference, cancellationToken);
            return new SpacemiTCardContent(entry.Open, entry.Length);
        }

        return SpacemiTCardImageWriter.Write(Table, Resolve, destinationPath, progress, cancellationToken);
    }

    private static CardEntryIndex BuildIndex(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entries = new List<(string Name, ZipArchiveEntry Entry)>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = entry.FullName;
            if (raw.Length == 0 || raw[^1] is '/' or '\\')
            {
                continue;
            }

            if (raw.Length > MaxEntryNameLength)
            {
                throw new InvalidDataException($"A ZIP entry name exceeds {MaxEntryNameLength} characters.");
            }

            string normalized;
            try
            {
                normalized = ZipContainer.NormalizeEntryPath(raw);
            }
            catch (ArgumentException e)
            {
                throw new InvalidDataException($"The ZIP entry name '{raw}' is not a safe relative path.", e);
            }

            entries.Add((normalized, entry));
        }

        return new CardEntryIndex(entries);
    }

    private static (SpacemiTPartitionTable? Table, string? Name) FindTable(
        CardEntryIndex index, CancellationToken cancellationToken)
    {
        var candidates = index.Entries
            .Where(static item => IsPartitionTableName(Basename(item.Name)))
            .OrderBy(static item => RankTableName(Basename(item.Name)))
            .ThenBy(static item => item.Name, StringComparer.Ordinal);

        foreach (var (name, entry) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpacemiTPartitionTable table;
            try
            {
                using var stream = entry.Open();
                table = SpacemiTPartitionTable.Parse(stream);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException)
            {
                continue;
            }

            if (table.Format.Equals(SpacemiTConstants.PartitionFormatGpt, StringComparison.Ordinal))
            {
                return (table, name);
            }
        }

        return (null, null);
    }

    private static long EstimateImageSize(SpacemiTPartitionTable table, CardEntryIndex index,
        CancellationToken cancellationToken)
    {
        long cursor = 0;
        foreach (var entry in table.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentLength = 0L;
            if (entry.Images.Count == 1)
            {
                contentLength = index.Resolve(entry.Images[0], cancellationToken).Length;
            }
            else if (entry.Images.Count > 1)
            {
                throw new InvalidDataException(
                    $"Partition '{entry.Name}' declares {entry.Images.Count} images; a card image supports exactly one.");
            }

            var offset = entry.Offset < 0 ? cursor : entry.Offset;
            var extent = entry.Size > 0 ? entry.Size : contentLength;
            cursor = Math.Max(cursor, checked(offset + extent));
        }

        var estimate = checked(cursor + GptReserveBlocks * SectorSizeBytes);
        return (estimate + ImageAlignBytes - 1) / ImageAlignBytes * ImageAlignBytes;
    }

    private static bool IsPartitionTableName(string name) =>
        name.StartsWith("partition_", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && name.Length > "partition_.json".Length;

    private static int RankTableName(string name) =>
        name.Equals(BootCardTable, StringComparison.OrdinalIgnoreCase) ? 0
        : name.Equals(FlashCardTable, StringComparison.OrdinalIgnoreCase) ? 1
        : 2;

    private static string Basename(string name) => name[(name.LastIndexOf('/') + 1)..];

    private sealed class CardEntryIndex
    {
        private readonly List<(string Name, ZipArchiveEntry Entry)> _entries;

        public CardEntryIndex(List<(string Name, ZipArchiveEntry Entry)> entries) => _entries = entries;

        public IReadOnlyList<(string Name, ZipArchiveEntry Entry)> Entries => _entries;

        public ZipArchiveEntry Resolve(string reference, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                throw new InvalidDataException("The partition table declares an empty image reference.");
            }

            if (reference.Length > SpacemiTCardPackage.MaxEntryNameLength)
            {
                throw new InvalidDataException(
                    $"An image reference exceeds {SpacemiTCardPackage.MaxEntryNameLength} characters.");
            }

            return reference.Length > 1 && reference[0] == '/' && reference[^1] == '/'
                ? ResolveRegex(reference, cancellationToken)
                : ResolvePlain(reference, cancellationToken);
        }

        private ZipArchiveEntry ResolvePlain(string reference, CancellationToken cancellationToken)
        {
            string normalized;
            try
            {
                normalized = ZipContainer.NormalizeEntryPath(reference);
            }
            catch (ArgumentException e)
            {
                throw new InvalidDataException($"The image reference '{reference}' is not a safe relative path.", e);
            }

            var exact = new List<ZipArchiveEntry>();
            foreach (var (name, entry) in _entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(name, normalized, StringComparison.Ordinal))
                {
                    exact.Add(entry);
                }
            }

            if (exact.Count == 1)
            {
                return exact[0];
            }

            if (exact.Count > 1)
            {
                throw new InvalidDataException(
                    $"The image reference '{reference}' is ambiguous: {exact.Count} entries share that path.");
            }

            var basename = SpacemiTCardPackage.Basename(normalized);
            var byName = new List<ZipArchiveEntry>();
            foreach (var (name, entry) in _entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(SpacemiTCardPackage.Basename(name), basename, StringComparison.Ordinal))
                {
                    byName.Add(entry);
                }
            }

            if (byName.Count == 1)
            {
                return byName[0];
            }

            if (byName.Count == 0)
            {
                throw new InvalidDataException($"The image reference '{reference}' is missing from the card package.");
            }

            throw new InvalidDataException(
                $"The image reference '{reference}' is ambiguous: {byName.Count} entries share that name.");
        }

        private ZipArchiveEntry ResolveRegex(string reference, CancellationToken cancellationToken)
        {
            Regex regex;
            try
            {
                regex = new Regex(reference[1..^1], RegexOptions.CultureInvariant,
                    SpacemiTCardPackage.RegexMatchTimeout);
            }
            catch (ArgumentException e)
            {
                throw new InvalidDataException($"The regex image reference '{reference}' is invalid.", e);
            }

            var clock = Stopwatch.StartNew();
            var matches = new List<ZipArchiveEntry>();
            foreach (var (name, entry) in _entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > SpacemiTCardPackage.RegexOverallBudget)
                {
                    throw new InvalidDataException("Regex image resolution exceeded its time budget.");
                }

                try
                {
                    if (regex.IsMatch(name))
                    {
                        matches.Add(entry);
                    }
                }
                catch (RegexMatchTimeoutException e)
                {
                    throw new InvalidDataException($"The regex image reference '{reference}' timed out.", e);
                }
            }

            return matches.Count switch
            {
                1 => matches[0],
                0 => throw new InvalidDataException($"The regex image reference '{reference}' matches no entry."),
                _ => throw new InvalidDataException(
                    $"The regex image reference '{reference}' is ambiguous: {matches.Count} entries match."),
            };
        }
    }
}
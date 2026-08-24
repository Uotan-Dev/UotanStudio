namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>NB0 提取操作的选项。</para>
/// Options for NB0 extraction operations.
/// </summary>
public sealed class ExtractionOptions
{
    /// <summary>
    /// <para>是否覆盖已存在的文件。默认为 true。</para>
    /// Whether to overwrite existing files. Defaults to true.
    /// </summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>
    /// <para>条目名称过滤谓词;返回 false 的条目将被跳过。默认为 null(全部提取)。</para>
    /// Entry name filter predicate; entries returning false are skipped. Defaults to null (extract all).
    /// </summary>
    public Func<string, bool>? FileFilter { get; set; }
}

/// <summary>
/// <para>NB0 提取操作的结果。</para>
/// The result of an NB0 extraction operation.
/// </summary>
public sealed class ExtractionResult
{
    /// <summary>
    /// <para>获取或设置成功提取的条目数。</para>
    /// Gets or sets the number of successfully extracted entries.
    /// </summary>
    public int ExtractedFileCount { get; set; }

    /// <summary>
    /// <para>获取或设置提取过程中发生的错误列表。</para>
    /// Gets or sets the list of errors collected during extraction.
    /// </summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// <para>获取或设置提取过程中产生的警告列表。</para>
    /// Gets or sets the list of warnings collected during extraction.
    /// </summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// <para>获取提取是否成功(无错误)。</para>
    /// Gets whether the extraction succeeded (no errors).
    /// </summary>
    public bool Success => Errors.Count == 0;

    /// <inheritdoc />
    public override string ToString() => $"Extracted {ExtractedFileCount} files, {Errors.Count} errors, {Warnings.Count} warnings";
}

/// <summary>
/// <para>提供从 NB0 固件文件中提取条目的功能。</para>
/// Provides functionality to extract entries from NB0 firmware files.
/// </summary>
/// <remarks>
/// <para>使用默认缓冲区大小初始化 Nb0Extractor 的新实例。</para>
/// Initializes a new instance of Nb0Extractor with the default buffer size.
/// </remarks>
public sealed class Nb0Extractor(int bufferSize = Nb0Extractor.DefaultBufferSize)
{
    private const int DefaultBufferSize = 1024 * 1024;

    private readonly int _bufferSize = bufferSize;

    /// <summary>
    /// <para>从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// Extracts all entries from an NB0 firmware file to the output directory.
    /// </summary>
    public ExtractionResult Extract(string nb0FilePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(nb0FilePath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        using var stream = new FileStream(nb0FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.SequentialScan);
        return ExtractFromStream(stream, outputDirectory, options, progress);
    }

    /// <summary>
    /// <para>异步从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// Asynchronously extracts all entries from an NB0 firmware file to the output directory.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(string nb0FilePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nb0FilePath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        using var stream = new FileStream(nb0FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ExtractFromStreamAsync(stream, outputDirectory, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>从包含 NB0 固件数据的流中提取所有条目到输出目录。</para>
    /// Extracts all entries from a stream containing NB0 firmware data to the output directory.
    /// </summary>
    public ExtractionResult ExtractFromStream(Stream stream, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        var result = new ExtractionResult();
        var metadata = Nb0Parser.ParseFromStream(stream);

        result.Warnings.AddRange(metadata.Warnings);
        Directory.CreateDirectory(outputDirectory);

        var total = metadata.Entries.Count;
        var completed = 0;

        foreach (var entry in metadata.Entries)
        {
            if (options?.FileFilter is not null && !options.FileFilter(entry.Name))
            {
                completed++;
                continue;
            }

            try
            {
                var safeName = SanitizeEntryName(entry.Name);
                var outPath = Path.Combine(outputDirectory, safeName);
                if (File.Exists(outPath) && options?.Overwrite != true)
                {
                    completed++;
                    continue;
                }

                var data = ExtractEntryToBuffer(stream, entry);
                File.WriteAllBytes(outPath, data);
                result.ExtractedFileCount++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to extract '{entry.Name}': {ex.Message}");
            }

            completed++;
            progress?.Report(new Nb0ExtractionProgress { TotalEntries = total, CompletedEntries = completed, CurrentEntryName = entry.Name });
        }

        return result;
    }

    /// <summary>
    /// <para>异步从包含 NB0 固件数据的流中提取所有条目到输出目录。</para>
    /// Asynchronously extracts all entries from a stream containing NB0 firmware data to the output directory.
    /// </summary>
    public async Task<ExtractionResult> ExtractFromStreamAsync(Stream stream, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        var result = new ExtractionResult();
        var metadata = await Nb0Parser.ParseFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);

        result.Warnings.AddRange(metadata.Warnings);
        Directory.CreateDirectory(outputDirectory);

        var total = metadata.Entries.Count;
        var completed = 0;

        foreach (var entry in metadata.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options?.FileFilter is not null && !options.FileFilter(entry.Name))
            {
                completed++;
                continue;
            }

            try
            {
                var safeName = SanitizeEntryName(entry.Name);
                var outPath = Path.Combine(outputDirectory, safeName);
                if (File.Exists(outPath) && options?.Overwrite != true)
                {
                    completed++;
                    continue;
                }

                var data = await ExtractEntryToBufferAsync(stream, entry, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(outPath, data, cancellationToken).ConfigureAwait(false);
                result.ExtractedFileCount++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to extract '{entry.Name}': {ex.Message}");
            }

            completed++;
            progress?.Report(new Nb0ExtractionProgress { TotalEntries = total, CompletedEntries = completed, CurrentEntryName = entry.Name });
        }

        return result;
    }

    /// <summary>
    /// <para>将单个条目提取到字节缓冲区。</para>
    /// Extracts a single entry into a byte buffer.
    /// </summary>
    public byte[] ExtractEntryToBuffer(Stream stream, Nb0FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);

        stream.Position = entry.FileDataOffset + entry.Offset;
        var data = new byte[entry.Size];
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// <para>异步将单个条目提取到字节缓冲区。</para>
    /// Asynchronously extracts a single entry into a byte buffer.
    /// </summary>
    public async Task<byte[]> ExtractEntryToBufferAsync(Stream stream, Nb0FileEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);

        stream.Position = entry.FileDataOffset + entry.Offset;
        var data = new byte[entry.Size];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return data;
    }

    private static string SanitizeEntryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
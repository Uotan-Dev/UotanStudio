using System.Buffers;
using System.Security.Cryptography;

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

        for (var i = 0; i < metadata.Entries.Count; i++)
        {
            var entry = metadata.Entries[i];
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

                var tempPath = CreateTempPath(outPath);
                try
                {
                    var md5 = ExtractEntryToFile(stream, entry, tempPath);
                    VerifyComputedMd5(entry, i, md5, metadata);
                    File.Move(tempPath, outPath, overwrite: true);
                    result.ExtractedFileCount++;
                }
                finally
                {
                    File.Delete(tempPath);
                }
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

        for (var i = 0; i < metadata.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = metadata.Entries[i];
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

                var tempPath = CreateTempPath(outPath);
                try
                {
                    var md5 = await ExtractEntryToFileAsync(stream, entry, tempPath, cancellationToken).ConfigureAwait(false);
                    VerifyComputedMd5(entry, i, md5, metadata);
                    File.Move(tempPath, outPath, overwrite: true);
                    result.ExtractedFileCount++;
                }
                finally
                {
                    File.Delete(tempPath);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
    /// <para>将单个条目以分块流式方式复制到目标文件，同时增量计算 MD5 摘要。</para>
    /// Streams a single entry to the target file in chunks, incrementally computing its MD5.
    /// </summary>
    /// <param name="stream">The NB0 source stream. <para>NB0 源流。</para></param>
    /// <param name="entry">The entry to extract. <para>要提取的条目。</para></param>
    /// <param name="outputPath">Destination file path. <para>目标文件路径。</para></param>
    /// <returns>The 16-byte MD5 digest of the extracted entry data. <para>提取条目数据的 16 字节 MD5 摘要。</para></returns>
    public byte[] ExtractEntryToFile(Stream stream, Nb0FileEntry entry, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, _bufferSize);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        try
        {
            var remaining = entry.Size;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Entry '{entry.Name}' ended unexpectedly after {entry.Size - remaining} of {entry.Size} bytes.");
                }

                output.Write(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        output.Flush();
        return md5.GetHashAndReset();
    }

    /// <summary>
    /// <para>异步将单个条目以分块流式方式复制到目标文件，同时增量计算 MD5 摘要。</para>
    /// Asynchronously streams a single entry to the target file in chunks while incrementally computing its MD5.
    /// </summary>
    /// <param name="stream">The NB0 source stream. <para>NB0 源流。</para></param>
    /// <param name="entry">The entry to extract. <para>要提取的条目。</para></param>
    /// <param name="outputPath">Destination file path. <para>目标文件路径。</para></param>
    /// <param name="cancellationToken">The cancellation token. <para>取消令牌。</para></param>
    /// <returns>The 16-byte MD5 digest of the extracted entry data. <para>提取条目数据的 16 字节 MD5 摘要。</para></returns>
    public async Task<byte[]> ExtractEntryToFileAsync(Stream stream, Nb0FileEntry entry, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, _bufferSize, FileOptions.Asynchronous);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        try
        {
            var remaining = entry.Size;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Entry '{entry.Name}' ended unexpectedly after {entry.Size - remaining} of {entry.Size} bytes.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                md5.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return md5.GetHashAndReset();
    }

    /// <summary>
    /// <para>将单个条目提取到字节缓冲区。</para>
    /// Extracts a single entry into a byte buffer.
    /// </summary>
    public byte[] ExtractEntryToBuffer(Stream stream, Nb0FileEntry entry)
    {
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
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
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
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

    /// <summary>
    /// <para>校验条目范围：offset &lt;= 长度 且 size &lt;= 长度 - offset，超出流长度时拒绝。</para>
    /// Validates the entry range: offset &lt;= length and size &lt;= length - offset,
    /// rejecting ranges that exceed the stream length.
    /// </summary>
    private static long ValidateEntryRange(Stream stream, Nb0FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.FileDataOffset < 0 || entry.Offset < 0 || entry.Size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), $"Entry '{entry.Name}' has a negative offset or size.");
        }

        long start;
        try { start = checked(entry.FileDataOffset + entry.Offset); }
        catch (OverflowException ex)
        {
            throw new Nb0CorruptedException($"Entry '{entry.Name}' start offset overflows Int64.", ex);
        }
        if (stream.CanSeek && (start > stream.Length || entry.Size > stream.Length - start))
        {
            throw new Nb0CorruptedException(
                $"Entry '{entry.Name}' range (offset 0x{start:X}, size {entry.Size}) exceeds the stream length {stream.Length}.");
        }
        return start;
    }

    /// <summary>
    /// <para>将提取时增量计算的 MD5 与归档内 MD5 记录比对，不一致时添加警告。</para>
    /// Compares the incrementally computed MD5 against the archive's MD5 records and
    /// adds a warning on mismatch.
    /// </summary>
    private static string CreateTempPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

    private static void VerifyComputedMd5(Nb0FileEntry entry, int entryIndex, byte[] computedMd5, Nb0Metadata metadata)
    {
        if (metadata.Md5Records.Count == 0)
        {
            return;
        }

        var record = Nb0Md5Helper.FindMd5Record(entry, entryIndex, metadata);
        if (record is not null && !Nb0Md5Helper.Md5Equals(computedMd5, record.Md5Checksum))
        {
            throw new InvalidDataException($"MD5 mismatch for entry '{entry.Name}'.");
        }
    }
}
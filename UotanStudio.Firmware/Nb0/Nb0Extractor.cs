using System.Buffers;
using System.Security.Cryptography;

using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Nb0;

public sealed class ExtractionOptions
{
    public bool Overwrite { get; set; } = true;

    public Func<string, bool>? FileFilter { get; set; }
}

public sealed class ExtractionResult
{
    public int ExtractedFileCount { get; set; }

    public List<string> Errors { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public bool Success => Errors.Count == 0;

    /// <summary>
    /// Returns a string representation of this value.
    /// <para>返回此值的字符串表示。</para>
    /// </summary>
    public override string ToString() => $"Extracted {ExtractedFileCount} files, {Errors.Count} errors, {Warnings.Count} warnings";
}

public sealed class Nb0Extractor(int bufferSize = Nb0Extractor.DefaultBufferSize)
{
    private const int DefaultBufferSize = 1024 * 1024;

    private readonly int _bufferSize = bufferSize;

    /// <summary>
    /// Extracts all entries from an NB0 firmware file to the output directory.
    /// <para>从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// </summary>
    public ExtractionResult Extract(string nb0FilePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(nb0FilePath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        using var stream = new FileStream(nb0FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.SequentialScan);
        return ExtractFromStream(stream, outputDirectory, options, progress);
    }

    /// <summary>
    /// Asynchronously extracts all entries from an NB0 firmware file to the output directory.
    /// <para>异步从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(string nb0FilePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nb0FilePath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        using var stream = new FileStream(nb0FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ExtractFromStreamAsync(stream, outputDirectory, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts all entries from a stream containing NB0 firmware data to the output directory.
    /// <para>从包含 NB0 固件数据的流中提取所有条目到输出目录。</para>
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
    /// Asynchronously extracts all entries from a stream containing NB0 firmware data to the output directory.
    /// <para>异步从包含 NB0 固件数据的流中提取所有条目到输出目录。</para>
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
    /// Streams a single entry to the target file in chunks, incrementally computing its MD5.
    /// <para>将单个条目以分块流式方式复制到目标文件，同时增量计算 MD5 摘要。</para>
    /// </summary>
    public byte[] ExtractEntryToFile(Stream stream, Nb0FileEntry entry, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        byte[]? hash = null;
        FileExtraction.WriteAtomic(outputPath, output => hash = ExtractEntryCore(stream, entry, output));
        return hash!;
    }

    private byte[] ExtractEntryCore(Stream stream, Nb0FileEntry entry, Stream output)
    {
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
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
    /// Asynchronously streams a single entry to the target file in chunks while incrementally computing its MD5.
    /// <para>异步将单个条目以分块流式方式复制到目标文件，同时增量计算 MD5 摘要。</para>
    /// </summary>
    public async Task<byte[]> ExtractEntryToFileAsync(Stream stream, Nb0FileEntry entry, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        byte[]? hash = null;
        await FileExtraction.WriteAtomicAsync(outputPath, async output =>
            hash = await ExtractEntryCoreAsync(stream, entry, output, cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return hash!;
    }

    private async Task<byte[]> ExtractEntryCoreAsync(Stream stream, Nb0FileEntry entry, Stream output,
        CancellationToken cancellationToken)
    {
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
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
    /// Extracts a single entry into a byte buffer.
    /// <para>将单个条目提取到字节缓冲区。</para>
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
    /// Asynchronously extracts a single entry into a byte buffer.
    /// <para>异步将单个条目提取到字节缓冲区。</para>
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
    /// Validates the entry range: offset &lt;= length and size &lt;= length - offset, rejecting ranges that exceed the stream length.
    /// <para>校验条目范围：offset &lt;= 长度 且 size &lt;= 长度 - offset，超出流长度时拒绝。</para>
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
    /// Compares the incrementally computed MD5 against the archive's MD5 records and adds a warning on mismatch.
    /// <para>将提取时增量计算的 MD5 与归档内 MD5 记录比对，不一致时添加警告。</para>
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
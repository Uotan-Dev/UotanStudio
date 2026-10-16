using UotanStudio.Firmware.VendorFirmware.Nb0.Models;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.VendorFirmware.Nb0;

internal static class Nb0Md5Helper
{
    /// <summary>
    /// Finds the corresponding MD5 record by offset or entry index.
    /// <para>根据偏移量或条目索引查找对应的 MD5 记录。</para>
    /// </summary>
    internal static Nb0Md5Record? FindMd5Record(Nb0FileEntry entry, int entryIndex, Nb0Metadata metadata)
    {
        var absoluteOffset = entry.FileDataOffset + entry.Offset;
        if (absoluteOffset is >= 0 and <= uint.MaxValue)
        {
            if (metadata.Md5RecordByOffset is { } byOffset && byOffset.TryGetValue((uint)absoluteOffset, out var record))
            {
                return record;
            }
        }

        for (var i = 0; i < metadata.Md5Records.Count; i++)
        {
            if (absoluteOffset >= 0 && absoluteOffset <= uint.MaxValue && metadata.Md5Records[i].Offset == (uint)absoluteOffset)
            {
                return metadata.Md5Records[i];
            }
        }

        return entryIndex < metadata.Md5Records.Count ? metadata.Md5Records[entryIndex] : null;
    }

    /// <summary>
    /// Computes the MD5 digest of the data.
    /// <para>计算数据的 MD5 摘要。</para>
    /// </summary>
    internal static byte[] ComputeMd5(byte[] data) => Md5.ComputeHash(data);

    /// <summary>
    /// Compares whether two MD5 digests are equal.
    /// <para>比较两个 MD5 摘要是否相等。</para>
    /// </summary>
    internal static bool Md5Equals(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);
}

/// <summary>
/// Outcome of an MD5 verification pass over one NB0 firmware file.
/// <para>对单个 NB0 固件文件执行 MD5 校验后的结果。</para>
/// </summary>
public sealed class CheckResult
{
    /// <summary>Firmware is intact: MD5 records exist and every entry digest matched.
    /// <para>固件完整：存在 MD5 记录且每个条目的摘要均匹配。</para></summary>
    /// <value><see langword="true"/> when verification passed; <see langword="false"/> when a record is
    /// missing or a digest mismatched. <para>校验通过时为 <see langword="true"/>；记录缺失或摘要不匹配时为 <see langword="false"/>。</para></value>
    public bool IsValid { get; set; }

    /// <summary>Number of entries whose computed MD5 equals the archive record.
    /// <para>计算 MD5 与归档记录相等的条目数量。</para></summary>
    /// <value>The count of verified entries. <para>已校验通过的条目数。</para></value>
    public int VerifiedCount { get; set; }

    /// <summary>Failure descriptions: missing MD5 record, digest mismatch, or absence of any MD5 record.
    /// <para>失败说明：MD5 记录缺失、摘要不匹配，或完全没有 MD5 记录。</para></summary>
    /// <value>One message per failure; empty when everything verified. <para>每个失败一条消息；全部通过时为空。</para></value>
    public List<string> Errors { get; set; } = [];

    /// <summary>Non-fatal integrity observations collected while parsing the container.
    /// <para>解析容器期间收集的非致命完整性观察项。</para></summary>
    /// <value>The metadata warning list; empty when none. <para>元数据警告列表；无警告时为空。</para></value>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Facade over the NB0 bounded context: metadata parsing, entry extraction, MD5 verification and
/// JSON export in one entry point.
/// <para>NB0 有界上下文的门面：在一个入口中提供元数据解析、条目提取、MD5 校验与 JSON 导出。</para>
/// </summary>
public sealed class Nb0Processor
{
    /// <summary>
    /// Parses an NB0 firmware file and returns metadata.
    /// <para>解析 NB0 固件文件并返回元数据。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the NB0 file. <para>NB0 文件路径。</para>
    /// </param>
    /// <returns>Parsed metadata including entries, MD5 records and warnings.
    /// <para>解析后的元数据，包含条目、MD5 记录与警告。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is <see langword="null"/> or empty. <para><paramref name="filePath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The entry count exceeds the supported bound or the header table is larger than the file.
    /// <para>条目数超出支持上限，或头部表大于文件。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The data ends in the middle of a header. <para>数据在头部中间位置结束。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The file cannot be opened for reading. <para>无法以读取方式打开文件。</para>
    /// </exception>
    public Nb0Metadata Parse(string filePath) => Nb0Parser.Parse(filePath);

    /// <summary>
    /// Asynchronously parses an NB0 firmware file and returns metadata.
    /// <para>异步解析 NB0 固件文件并返回元数据。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the NB0 file. <para>NB0 文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before each entry header read. <para>在读取每个条目头之前检查。</para>
    /// </param>
    /// <returns>Parsed metadata including entries, MD5 records and warnings.
    /// <para>解析后的元数据，包含条目、MD5 记录与警告。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is <see langword="null"/> or empty. <para><paramref name="filePath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The entry count exceeds the supported bound or the header table is larger than the file.
    /// <para>条目数超出支持上限，或头部表大于文件。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para>
    /// </exception>
    public Task<Nb0Metadata> ParseAsync(string filePath, CancellationToken cancellationToken = default)
        => Nb0Parser.ParseAsync(filePath, cancellationToken);

    /// <summary>
    /// Extracts all entries from an NB0 firmware file to the output directory.
    /// <para>从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the NB0 file. <para>NB0 文件路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory; created when absent. Each entry is written through a temporary file and
    /// moved atomically, with invalid file-name characters replaced by underscores.
    /// <para>目标目录；不存在时创建。每个条目先写入临时文件后原子移动，非法文件名字符替换为下划线。</para>
    /// </param>
    /// <param name="options">
    /// Overwrite and per-entry filter behaviour; <see langword="null"/> overwrites and extracts everything.
    /// <para>覆盖与逐条目过滤行为；为 <see langword="null"/> 时覆盖全部并提取所有条目。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry; <see langword="null"/> disables reporting.
    /// <para>每个条目处理后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <returns>Result counting extracted files and collecting per-entry errors; <c>Success</c> is
    /// <see langword="false"/> when any entry failed.
    /// <para>统计提取数量并收集逐条目错误的结果；任一条目失败时 <c>Success</c> 为 <see langword="false"/>。</para></returns>
    /// <exception cref="Nb0CorruptedException">
    /// The container structure fails validation; per-entry failures do not throw and stay in the result.
    /// <para>容器结构校验失败时抛出；逐条目失败不抛出，保留在结果中。</para>
    /// </exception>
    public ExtractionResult Extract(string filePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null)
        => new Nb0Extractor().Extract(filePath, outputDirectory, options, progress);

    /// <summary>
    /// Asynchronously extracts all entries from an NB0 firmware file to the output directory.
    /// <para>异步从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the NB0 file. <para>NB0 文件路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory; created when absent. <para>目标目录；不存在时创建。</para>
    /// </param>
    /// <param name="options">
    /// Overwrite and per-entry filter behaviour; <see langword="null"/> overwrites and extracts everything.
    /// <para>覆盖与逐条目过滤行为；为 <see langword="null"/> 时覆盖全部并提取所有条目。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry; <see langword="null"/> disables reporting.
    /// <para>每个条目处理后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before each entry and during data copy; cancellation is rethrown, not recorded as a per-entry error.
    /// <para>在每个条目前及数据复制期间检查；取消会重新抛出，不作为逐条目错误记录。</para>
    /// </param>
    /// <returns>Result counting extracted files and collecting per-entry errors.
    /// <para>统计提取数量并收集逐条目错误的结果。</para></returns>
    /// <exception cref="Nb0CorruptedException">
    /// The container structure fails validation. <para>容器结构校验失败。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para>
    /// </exception>
    public Task<ExtractionResult> ExtractAsync(string filePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
        => new Nb0Extractor().ExtractAsync(filePath, outputDirectory, options, progress, cancellationToken);

    /// <summary>
    /// Verifies the MD5 checksums of entries in an NB0 firmware file.
    /// <para>校验 NB0 固件文件中各条目的 MD5 校验和。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the NB0 file. <para>NB0 文件路径。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry; <see langword="null"/> disables reporting.
    /// <para>每个条目处理后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <returns>Verification outcome with per-entry errors and a verified count; <c>IsValid</c> is
    /// <see langword="false"/> when MD5 records are absent or a digest mismatches.
    /// <para>包含逐条目错误与已校验计数的结果；无 MD5 记录或摘要不匹配时 <c>IsValid</c> 为 <see langword="false"/>。</para></returns>
    /// <exception cref="Nb0CorruptedException">
    /// The container structure fails validation. <para>容器结构校验失败。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The file cannot be opened for reading. <para>无法以读取方式打开文件。</para>
    /// </exception>
    public CheckResult Check(string filePath, IProgress<Nb0CheckProgress>? progress = null)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return CheckFromStream(stream, progress);
    }

    /// <summary>
    /// Asynchronously verifies the MD5 checksums of entries in an NB0 firmware file.
    /// <para>异步校验 NB0 固件文件中各条目的 MD5 校验和。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the NB0 file. <para>NB0 文件路径。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry; <see langword="null"/> disables reporting.
    /// <para>每个条目处理后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked once before the file is opened; the verification loop itself runs synchronously.
    /// <para>在打开文件前检查一次；校验循环本身同步执行。</para>
    /// </param>
    /// <returns>Verification outcome with per-entry errors and a verified count.
    /// <para>包含逐条目错误与已校验计数的结果。</para></returns>
    /// <exception cref="Nb0CorruptedException">
    /// The container structure fails validation. <para>容器结构校验失败。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled before the file was opened.
    /// <para><paramref name="cancellationToken"/> 在打开文件前被触发。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The file cannot be opened for reading. <para>无法以读取方式打开文件。</para>
    /// </exception>
    public async Task<CheckResult> CheckAsync(string filePath, IProgress<Nb0CheckProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var result = CheckFromStream(stream, progress);
        return await Task.FromResult(result).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the MD5 checksums of entries in NB0 firmware data from a stream.
    /// <para>从流校验 NB0 固件数据中各条目的 MD5 校验和。</para>
    /// </summary>
    /// <param name="stream">
    /// Readable, seekable NB0 data; the stream stays open and its position may move arbitrarily.
    /// Each verified entry is read fully into memory.
    /// <para>可读、可寻址的 NB0 数据流；流保持打开且位置会任意移动。每个被校验的条目会完整读入内存。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry; <see langword="null"/> disables reporting.
    /// <para>每个条目处理后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <returns>Verification outcome; <c>IsValid</c> is <see langword="false"/> when MD5 records are
    /// absent, a record is missing for an entry, or a digest mismatches.
    /// <para>校验结果；当无 MD5 记录、条目缺少记录或摘要不匹配时 <c>IsValid</c> 为 <see langword="false"/>。</para></returns>
    /// <exception cref="Nb0CorruptedException">
    /// The container structure fails validation or an entry range exceeds the stream length.
    /// <para>容器结构校验失败，或条目范围超出流长度。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> cannot be read or seeked. <para><paramref name="stream"/> 不可读取或寻址。</para>
    /// </exception>
    public CheckResult CheckFromStream(Stream stream, IProgress<Nb0CheckProgress>? progress = null)
    {
        var result = new CheckResult { IsValid = true };
        var metadata = Nb0Parser.ParseFromStream(stream);
        result.Warnings.AddRange(metadata.Warnings);

        if (!metadata.HasMd5Records)
        {
            result.IsValid = false;
            result.Errors.Add("No MD5 records found; cannot verify entries.");
            return result;
        }

        var total = metadata.Entries.Count;
        var workers = Core.FirmwareParallelism.Workers;
        var digests = new byte[]?[total];
        var missingRecord = ResolveMissingRecords(metadata);
        var buffers = GatherEntryBuffers(stream, metadata, missingRecord);

        Core.FirmwareParallelism.ForEach(metadata.Entries, (_, i) =>
        {
            if (buffers[i] is null)
            {
                return;
            }

            digests[i] = Nb0Md5Helper.ComputeMd5(buffers[i]!);
        }, workers);

        for (var i = 0; i < total; i++)
        {
            if (missingRecord[i])
            {
                result.Errors.Add($"No MD5 record found for entry '{metadata.Entries[i].Name}'.");
                result.IsValid = false;
            }
            else
            {
                var record = Nb0Md5Helper.FindMd5Record(metadata.Entries[i], i, metadata)!;
                if (Nb0Md5Helper.Md5Equals(digests[i]!, record.Md5Checksum))
                {
                    result.VerifiedCount++;
                }
                else
                {
                    result.Errors.Add($"MD5 mismatch for entry '{metadata.Entries[i].Name}'.");
                    result.IsValid = false;
                }
            }

            progress?.Report(new Nb0CheckProgress { TotalEntries = total, CompletedEntries = i + 1, CurrentEntryName = metadata.Entries[i].Name });
        }

        return result;
    }

    /// <summary>
    /// Marks the entries whose MD5 record is absent while leaving the rest to the hashing stage.
    /// <para>标记缺少 MD5 记录的条目，其余交由摘要阶段处理。</para>
    /// </summary>
    private static IReadOnlyList<bool> ResolveMissingRecords(Nb0Metadata metadata)
    {
        var missing = new bool[metadata.Entries.Count];
        for (var i = 0; i < metadata.Entries.Count; i++)
        {
            missing[i] = Nb0Md5Helper.FindMd5Record(metadata.Entries[i], i, metadata) is null;
        }

        return missing;
    }

    /// <summary>
    /// Reads every outstanding entry on the calling thread into a per-index slot, so the shared stream is only
    /// ever repositioned by one thread at a time and the hashing stage can then run concurrently.
    /// <para>在调用线程上把每个待处理条目读入各自的索引槽位，从而共享流同一时刻只由一个线程重新定位，
    /// 之后摘要阶段即可并发执行。</para>
    /// </summary>
    private static byte[]?[] GatherEntryBuffers(Stream stream, Nb0Metadata metadata, IReadOnlyList<bool> missingRecord)
    {
        var buffers = new byte[]?[metadata.Entries.Count];
        for (var i = 0; i < metadata.Entries.Count; i++)
        {
            if (!missingRecord[i])
            {
                buffers[i] = new Nb0Extractor().ExtractEntryToBuffer(stream, metadata.Entries[i]);
            }
        }

        return buffers;
    }

    /// <summary>
    /// Returns the metadata of an NB0 firmware file in JSON format.
    /// <para>以 JSON 格式返回 NB0 固件文件的元数据。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the NB0 file. <para>NB0 文件路径。</para>
    /// </param>
    /// <param name="compact">
    /// <see langword="true"/> removes indentation and line breaks; <see langword="false"/> produces
    /// two-space indented output. <para>为 <see langword="true"/> 时去除缩进与换行；为 <see langword="false"/> 时使用两空格缩进。</para>
    /// </param>
    /// <returns>A JSON object with <c>entryCount</c>, <c>totalSize</c>, <c>dataSectionOffset</c>, the
    /// inferred firmware fields, and an <c>entries</c> array of name/offset/size records.
    /// <para>包含 <c>entryCount</c>、<c>totalSize</c>、<c>dataSectionOffset</c>、推断固件字段，
    /// 以及由 name/offset/size 记录组成的 <c>entries</c> 数组的 JSON 对象。</para></returns>
    /// <exception cref="Nb0CorruptedException">
    /// The container structure fails validation. <para>容器结构校验失败。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The file cannot be opened for reading. <para>无法以读取方式打开文件。</para>
    /// </exception>
    public string ParseAsJson(string filePath, bool compact = false)
    {
        var metadata = Parse(filePath);
        return MetadataToJson(metadata, compact);
    }

    private static string MetadataToJson(Nb0Metadata metadata, bool compact)
    {
        var sb = new System.Text.StringBuilder();
        var indent = compact ? "" : "  ";
        var newline = compact ? "" : Environment.NewLine;

        sb.Append("{").Append(newline);
        sb.Append($"{indent}\"entryCount\": {metadata.EntryCount},").Append(newline);
        sb.Append($"{indent}\"totalSize\": {metadata.TotalSize},").Append(newline);
        sb.Append($"{indent}\"dataSectionOffset\": {metadata.DataSectionOffset},").Append(newline);

        if (!string.IsNullOrEmpty(metadata.InferredFirmwareType))
        {
            sb.Append($"{indent}\"inferredFirmwareType\": \"{metadata.InferredFirmwareType}\",").Append(newline);
        }
        if (!string.IsNullOrEmpty(metadata.InferredDeviceModel))
        {
            sb.Append($"{indent}\"inferredDeviceModel\": \"{metadata.InferredDeviceModel}\",").Append(newline);
        }
        if (!string.IsNullOrEmpty(metadata.InferredVersion))
        {
            sb.Append($"{indent}\"inferredVersion\": \"{metadata.InferredVersion}\",").Append(newline);
        }

        sb.Append($"{indent}\"entries\": [").Append(newline);
        for (var i = 0; i < metadata.Entries.Count; i++)
        {
            var e = metadata.Entries[i];
            var i2 = indent + indent;
            sb.Append($"{i2}{{").Append(newline);
            sb.Append($"{i2}{indent}\"name\": \"{EscapeJson(e.Name)}\",").Append(newline);
            sb.Append($"{i2}{indent}\"offset\": {e.Offset},").Append(newline);
            sb.Append($"{i2}{indent}\"size\": {e.Size}");
            if (!string.IsNullOrEmpty(e.InferredType))
            {
                sb.Append(",").Append(newline).Append($"{i2}{indent}\"inferredType\": \"{e.InferredType}\"");
            }
            sb.Append(newline);
            sb.Append($"{i2}}}{(i < metadata.Entries.Count - 1 ? "," : "")}").Append(newline);
        }
        sb.Append($"{indent}]").Append(newline);
        sb.Append("}");

        return sb.ToString();
    }

    private static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
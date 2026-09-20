using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Extraction;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Parsing;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0;

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
    internal static byte[] ComputeMd5(byte[] data) => MD5.HashData(data);

    /// <summary>
    /// Compares whether two MD5 digests are equal.
    /// <para>比较两个 MD5 摘要是否相等。</para>
    /// </summary>
    internal static bool Md5Equals(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);
}

public sealed class CheckResult
{
    public bool IsValid { get; set; }

    public int VerifiedCount { get; set; }

    public List<string> Errors { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

public sealed class Nb0Processor
{
    /// <summary>
    /// Parses an NB0 firmware file and returns metadata.
    /// <para>解析 NB0 固件文件并返回元数据。</para>
    /// </summary>
    public Nb0Metadata Parse(string filePath) => Nb0Parser.Parse(filePath);

    /// <summary>
    /// Asynchronously parses an NB0 firmware file and returns metadata.
    /// <para>异步解析 NB0 固件文件并返回元数据。</para>
    /// </summary>
    public Task<Nb0Metadata> ParseAsync(string filePath, CancellationToken cancellationToken = default)
        => Nb0Parser.ParseAsync(filePath, cancellationToken);

    /// <summary>
    /// Extracts all entries from an NB0 firmware file to the output directory.
    /// <para>从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// </summary>
    public ExtractionResult Extract(string filePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null)
        => new Nb0Extractor().Extract(filePath, outputDirectory, options, progress);

    /// <summary>
    /// Asynchronously extracts all entries from an NB0 firmware file to the output directory.
    /// <para>异步从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// </summary>
    public Task<ExtractionResult> ExtractAsync(string filePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
        => new Nb0Extractor().ExtractAsync(filePath, outputDirectory, options, progress, cancellationToken);

    /// <summary>
    /// Verifies the MD5 checksums of entries in an NB0 firmware file.
    /// <para>校验 NB0 固件文件中各条目的 MD5 校验和。</para>
    /// </summary>
    public CheckResult Check(string filePath, IProgress<Nb0CheckProgress>? progress = null)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return CheckFromStream(stream, progress);
    }

    /// <summary>
    /// Asynchronously verifies the MD5 checksums of entries in an NB0 firmware file.
    /// <para>异步校验 NB0 固件文件中各条目的 MD5 校验和。</para>
    /// </summary>
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
        var completed = 0;
        for (var i = 0; i < metadata.Entries.Count; i++)
        {
            var entry = metadata.Entries[i];
            var record = Nb0Md5Helper.FindMd5Record(entry, i, metadata);
            if (record is null)
            {
                result.Errors.Add($"No MD5 record found for entry '{entry.Name}'.");
                result.IsValid = false;
            }
            else
            {
                var data = new Nb0Extractor().ExtractEntryToBuffer(stream, entry);
                var computed = Nb0Md5Helper.ComputeMd5(data);
                if (Nb0Md5Helper.Md5Equals(computed, record.Md5Checksum))
                {
                    result.VerifiedCount++;
                }
                else
                {
                    result.Errors.Add($"MD5 mismatch for entry '{entry.Name}'.");
                    result.IsValid = false;
                }
            }

            completed++;
            progress?.Report(new Nb0CheckProgress { TotalEntries = total, CompletedEntries = completed, CurrentEntryName = entry.Name });
        }

        return result;
    }

    /// <summary>
    /// Returns the metadata of an NB0 firmware file in JSON format.
    /// <para>以 JSON 格式返回 NB0 固件文件的元数据。</para>
    /// </summary>
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
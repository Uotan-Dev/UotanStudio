using System.Text.Json;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Collects bounded geometry logs for a single owned getstorageinfo transaction.</summary>
internal sealed class FirehoseStorageReport
{
    private readonly List<string> _reports = [];
    private int _characters;

    internal void Observe(string log)
    {
        if (!log.Contains("\"storage_info\"", StringComparison.Ordinal))
        {
            return;
        }
        _characters = checked(_characters + log.Length);
        if (_characters > 64 * 1024 || _reports.Count == 8)
        {
            throw new FirehoseException("Storage geometry logs exceed the transaction limit.");
        }
        _reports.Add(log);
    }

    internal FirehoseStorageGeometry? Parse(int partition)
    {
        FirehoseStorageGeometry? result = null;
        foreach (var report in _reports)
        {
            var start = report.IndexOf('{');
            if (start < 0)
            {
                throw new FirehoseException("Storage geometry log has no JSON object.");
            }
            try
            {
                using var document = JsonDocument.Parse(report.AsMemory(start), new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                RejectDuplicateProperties(root);
                var info = root.GetProperty("storage_info");
                RejectDuplicateProperties(info);
                var blockSize = info.GetProperty("block_size").GetInt64();
                var totalBlocks = info.GetProperty("total_blocks").GetInt64();
                if (info.TryGetProperty("physical_partition_number", out var identity) && identity.GetInt32() != partition)
                {
                    throw new FirehoseException("Storage geometry identifies a different physical partition.");
                }

                var geometry = new FirehoseStorageGeometry(partition, blockSize, totalBlocks);
                if (result is not null && result != geometry)
                {
                    throw new FirehoseException("The target returned conflicting storage geometry reports.");
                }
                result = geometry;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                or FormatException or OverflowException or ArgumentOutOfRangeException)
            {
                throw new FirehoseException("Invalid storage geometry JSON or capacity.", ex);
            }
        }
        return result;
    }

    /// <summary>
    /// Leniently merges the descriptive fields of every buffered report; missing or malformed fields
    /// are skipped because descriptive storage facts are optional.
    /// <para>宽松合并所有缓冲报告的描述性字段；描述性存储信息是可选的，缺失或格式异常的字段
    /// 一律跳过。</para>
    /// </summary>
    internal FirehoseStorageDetails? ParseDetails()
    {
        FirehoseStorageDetails? result = null;
        foreach (var report in _reports)
        {
            var start = report.IndexOf('{');
            if (start < 0)
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(report.AsMemory(start), new JsonDocumentOptions { MaxDepth = 8 });
                var info = document.RootElement.GetProperty("storage_info");
                var parsed = new FirehoseStorageDetails
                {
                    StorageType = GetText(info, "storage_type") ?? GetText(info, "type"),
                    MemType = GetText(info, "mem_type"),
                    PageSize = GetInteger(info, "page_size"),
                    Density = GetInteger(info, "density"),
                    SerialNumber = GetText(info, "serial_num"),
                    ManufacturerId = GetText(info, "manufacturer_id"),
                    FirmwareVersion = GetText(info, "fw_ver"),
                    PhysicalPartitionNumber = (int?)GetInteger(info, "physical_partition_number"),
                };
                if (result is null)
                {
                    result = parsed;
                    continue;
                }
                result = result with
                {
                    StorageType = result.StorageType ?? parsed.StorageType,
                    MemType = result.MemType ?? parsed.MemType,
                    PageSize = result.PageSize ?? parsed.PageSize,
                    Density = result.Density ?? parsed.Density,
                    SerialNumber = result.SerialNumber ?? parsed.SerialNumber,
                    ManufacturerId = result.ManufacturerId ?? parsed.ManufacturerId,
                    FirmwareVersion = result.FirmwareVersion ?? parsed.FirmwareVersion,
                    PhysicalPartitionNumber = result.PhysicalPartitionNumber ?? parsed.PhysicalPartitionNumber,
                };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                or FormatException or OverflowException or ArgumentOutOfRangeException)
            {
                continue;
            }
        }
        return result;
    }

    private static string? GetText(JsonElement info, string name) => info.TryGetProperty(name, out var value)
        ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        }
        : null;

    private static long? GetInteger(JsonElement info, string name)
    {
        if (!info.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var number))
        {
            return null;
        }
        return number;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new FirehoseException("Duplicate storage geometry JSON properties are ambiguous.");
            }
        }
    }
}
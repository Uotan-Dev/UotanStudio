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
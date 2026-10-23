using System.Text.Json;
using System.Text.Json.Nodes;

namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// One entry of a SpacemiT partition table JSON (the <c>partition_*.json</c> files inside
/// titan flasher packages).
/// <para>SpacemiT 分区表 JSON（titan 烧录包内 <c>partition_*.json</c> 文件）中的一个条目。</para>
/// </summary>
/// <param name="Name">Partition name, used as the <c>flash:&lt;name&gt;</c> target. <para>分区名，用作 <c>flash:&lt;name&gt;</c> 的目标。</para></param>
/// <param name="Offset">Byte offset of the partition on storage; -1 when absent. <para>分区在存储上的字节偏移；缺失时为 -1。</para></param>
/// <param name="Size">Partition size in bytes; -1 means "until end of storage". <para>分区大小（字节）；-1 表示“直到存储末尾”。</para></param>
/// <param name="Images">Image file paths to flash, in order; empty when the entry has no image reference. <para>按顺序刷写的镜像文件路径；条目未引用镜像时为空。</para></param>
/// <param name="Compress">Vendor compression hint (e.g. "gzip-5"); retained for fidelity, the transfer itself is currently uncompressed. <para>厂商压缩提示（如 "gzip-5"）；仅为保真保留，当前传输本身不压缩。</para></param>
/// <param name="Hidden">Whether the partition is marked hidden in the table. <para>分区在表中是否标记为隐藏。</para></param>
public sealed record SpacemiTPartitionEntry(
    string Name,
    long Offset,
    long Size,
    IReadOnlyList<string> Images,
    string? Compress,
    bool Hidden);

/// <summary>
/// A parsed SpacemiT partition table: a format (GPT or MTD) and an ordered partition list.
/// <para>解析后的 SpacemiT 分区表：format（GPT 或 MTD）与有序分区列表。</para>
/// </summary>
public sealed class SpacemiTPartitionTable
{
    private const int MaxPartitions = 512;
    private const int MaxImagesPerPartition = 64;

    /// <summary>
    /// Gets the table format sent to the device as <c>flash:&lt;format&gt;</c> when writing
    /// the partition table ("gpt" or "mtd").
    /// <para>获取分区表 format；写表时以 <c>flash:&lt;format&gt;</c> 发给设备（"gpt" 或 "mtd"）。</para>
    /// </summary>
    public string Format { get; }

    /// <summary>Gets the ordered partition entries. <para>获取有序的分区条目。</para></summary>
    public IReadOnlyList<SpacemiTPartitionEntry> Partitions { get; }

    /// <summary>
    /// Initializes a parsed partition table.
    /// <para>以解析结果初始化分区表。</para>
    /// </summary>
    public SpacemiTPartitionTable(string format, IReadOnlyList<SpacemiTPartitionEntry> partitions)
    {
        Format = format;
        Partitions = partitions;
    }

    /// <summary>
    /// Loads a partition table from a file in the flash package.
    /// <para>从烧录包中的文件加载分区表。</para>
    /// </summary>
    /// <param name="path">Path to a <c>partition_*.json</c> file. <para><c>partition_*.json</c> 文件路径。</para></param>
    /// <returns>The parsed table. <para>解析后的分区表。</para></returns>
    /// <exception cref="InvalidDataException">The file is not a valid SpacemiT partition table. <para>文件不是有效的 SpacemiT 分区表。</para></exception>
    /// <exception cref="IOException">The file cannot be read. <para>文件无法读取。</para></exception>
    public static SpacemiTPartitionTable Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream);
    }

    /// <summary>
    /// Parses a partition table from untrusted JSON.
    /// <para>从不可信 JSON 解析分区表。</para>
    /// </summary>
    /// <param name="stream">JSON source, read to EOF bounded by the parser. <para>JSON 来源，由解析器有界地读到末尾。</para></param>
    /// <returns>The parsed table. <para>解析后的分区表。</para></returns>
    /// <exception cref="InvalidDataException">The JSON is not a valid SpacemiT partition table. <para>JSON 不是有效的 SpacemiT 分区表。</para></exception>
    public static SpacemiTPartitionTable Parse(Stream stream)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(stream,
                new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    MaxDepth = 16,
                });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed partition table JSON.", e);
        }

        if (root is not JsonObject obj)
        {
            throw new InvalidDataException("Partition table root must be an object.");
        }

        var format = obj["format"]?.GetValue<string>()?.Trim() ?? string.Empty;
        if (format.Length == 0 || format.Length > 16)
        {
            throw new InvalidDataException("Partition table is missing a usable 'format' value.");
        }

        if (obj["partitions"] is not JsonArray array || array.Count == 0)
        {
            throw new InvalidDataException("Partition table has no 'partitions' array.");
        }

        if (array.Count > MaxPartitions)
        {
            throw new InvalidDataException($"Partition table declares {array.Count} partitions, above the {MaxPartitions} limit.");
        }

        var partitions = new List<SpacemiTPartitionEntry>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject entry)
            {
                throw new InvalidDataException("Partition entries must be objects.");
            }

            var name = entry["name"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Length > 64)
            {
                throw new InvalidDataException("Partition name is missing or too long.");
            }

            var images = ParseImages(entry["image"]);
            var partition = new SpacemiTPartitionEntry(
                name,
                TryParseSize(entry["offset"]),
                TryParseSize(entry["size"]),
                images,
                entry["compress"]?.GetValue<string>()?.Trim(),
                entry["hidden"]?.GetValue<bool>() ?? false);
            partitions.Add(partition);
        }

        return new SpacemiTPartitionTable(format, partitions);
    }

    private static IReadOnlyList<string> ParseImages(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var single) => [single],
        JsonArray array => ParseImageArray(array),
        _ => [],
    };

    private static IReadOnlyList<string> ParseImageArray(JsonArray array)
    {
        if (array.Count > MaxImagesPerPartition)
        {
            throw new InvalidDataException($"Partition image list declares {array.Count} entries, above the {MaxImagesPerPartition} limit.");
        }

        var images = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var image) && image.Trim().Length > 0)
            {
                images.Add(image.Trim());
            }
        }

        return images;
    }

    private static long TryParseSize(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return -1;
        }

        var parsed = SpacemiTSizeParser.Parse(text);
        return parsed < -1 ? -1 : parsed;
    }
}
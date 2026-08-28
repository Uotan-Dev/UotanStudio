using System.Globalization;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>Rockchip parameter 文件中的单个分区条目。</para>
/// A single partition entry from a Rockchip parameter file.
/// </summary>
public sealed class RockchipPartition
{
    /// <summary>
    /// <para>初始化分区条目。</para>
    /// Initializes a partition entry.
    /// </summary>
    /// <param name="name">分区名（如 boot）。 Partition name (e.g. boot).</param>
    /// <param name="sizeSectors">分区大小（512 字节扇区）。 Partition size in 512-byte sectors.</param>
    /// <param name="offsetSectors">分区起始 LBA（512 字节扇区）。 Partition start LBA in 512-byte sectors.</param>
    public RockchipPartition(string name, long sizeSectors, long offsetSectors)
    {
        Name = name;
        SizeSectors = sizeSectors;
        OffsetSectors = offsetSectors;
    }

    /// <summary>
    /// <para>获取分区名。</para>
    /// Gets the partition name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>获取分区大小（512 字节扇区）。</para>
    /// Gets the partition size in 512-byte sectors.
    /// </summary>
    public long SizeSectors { get; }

    /// <summary>
    /// <para>获取分区起始 LBA（512 字节扇区）。</para>
    /// Gets the partition start LBA in 512-byte sectors.
    /// </summary>
    public long OffsetSectors { get; }

    /// <summary>
    /// <para>返回分区条目的字符串表示。</para>
    /// Returns a string representation of the partition entry.
    /// </summary>
    public override string ToString() => $"RockchipPartition[{Name}, LBA 0x{OffsetSectors:X}, {SizeSectors} sectors]";
}

/// <summary>
/// <para>解析 Rockchip 固件 parameter 文件中的分区表（<c>CMDLINE: mtdparts=...</c>）。</para>
/// Parses the partition table from a Rockchip firmware parameter file
/// (<c>CMDLINE: mtdparts=...</c>).
/// </summary>
public static class RockchipParameterParser
{
    /// <summary>Rockchip 扇区大小（字节）。 Rockchip sector size in bytes.</summary>
    public const int SectorSize = 512;

    /// <summary>
    /// <para>解析 parameter 文件并返回其分区表。</para>
    /// Parses a parameter file and returns its partition table.
    /// </summary>
    /// <param name="filePath">parameter 文件路径。 Path to the parameter file.</param>
    /// <returns>解析出的分区列表（保持文件中的顺序）。 The parsed partitions, in file order.</returns>
    /// <exception cref="InvalidDataException">文件不存在、缺少 <c>CMDLINE</c> 或 <c>mtdparts=</c>，或条目格式无效时抛出。</exception>
    public static IReadOnlyList<RockchipPartition> Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Parameter file path must not be empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Rockchip parameter file not found: {filePath} parameter 文件不存在。", filePath);
        }

        var lines = File.ReadAllLines(filePath);
        string? cmdline = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("CMDLINE:", StringComparison.OrdinalIgnoreCase))
            {
                cmdline = line["CMDLINE:".Length..].Trim();
                break;
            }
        }

        return cmdline is null
            ? throw new InvalidDataException($"No 'CMDLINE:' line found in parameter file: {filePath} parameter 文件中未找到 CMDLINE 行。")
            : ParseCommandLine(cmdline);
    }

    /// <summary>
    /// <para>从 <c>CMDLINE</c> 值解析分区表。</para>
    /// Parses the partition table from a <c>CMDLINE</c> value.
    /// </summary>
    /// <param name="cmdline">CMDLINE 的值。 The CMDLINE value.</param>
    /// <returns>解析出的分区列表。 The parsed partitions.</returns>
    /// <exception cref="InvalidDataException">缺少 <c>mtdparts=</c> 或条目格式无效时抛出。</exception>
    public static IReadOnlyList<RockchipPartition> ParseCommandLine(string cmdline)
    {
        var mtdIdx = cmdline.IndexOf("mtdparts=", StringComparison.OrdinalIgnoreCase);
        if (mtdIdx < 0)
        {
            throw new InvalidDataException("No 'mtdparts=' found in CMDLINE; not a Rockchip parameter partition table. CMDLINE 中未找到 mtdparts=。");
        }

        var mtd = cmdline[(mtdIdx + "mtdparts=".Length)..];

        // The device name precedes the first ':' (e.g. "rk29xxnand:..."). Everything after
        // the first ':' holds the comma-separated entries; a trailing ":gpt" (or ":raw")
        // suffix is dropped.
        // 设备名位于首个 ':' 之前（如 "rk29xxnand:..."）。首个 ':' 之后为逗号分隔的条目；
        // 末尾的 ":gpt"（或 ":raw"）后缀被丢弃。
        var colonIdx = mtd.IndexOf(':');
        var entriesPart = colonIdx >= 0 ? mtd[(colonIdx + 1)..] : mtd;
        var lastColon = entriesPart.LastIndexOf(':');
        if (lastColon >= 0)
        {
            entriesPart = entriesPart[..lastColon];
        }

        var partitions = new List<RockchipPartition>();
        foreach (var rawSegment in entriesPart.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            partitions.Add(ParseEntry(segment));
        }

        return partitions;
    }

    /// <summary>
    /// <para>解析单个 <c>size@offset(name)</c> 条目。</para>
    /// Parses a single <c>size@offset(name)</c> entry.
    /// </summary>
    private static RockchipPartition ParseEntry(string entry)
    {
        var atIdx = entry.IndexOf('@');
        if (atIdx <= 0)
        {
            throw new InvalidDataException($"Invalid Rockchip partition entry '{entry}': expected size@offset(name). 分区条目格式无效。");
        }

        var sizeText = entry[..atIdx].Trim();
        var rest = entry[(atIdx + 1)..].Trim();
        var openIdx = rest.IndexOf('(');
        if (openIdx < 0)
        {
            throw new InvalidDataException($"Invalid Rockchip partition entry '{entry}': missing '('. 分区条目缺少 '('。");
        }

        var offsetText = rest[..openIdx].Trim();
        var closeIdx = rest.IndexOf(')', openIdx);
        var name = (closeIdx < 0 ? rest[(openIdx + 1)..] : rest[(openIdx + 1)..closeIdx]).Trim();
        return name.Length == 0
            ? throw new InvalidDataException($"Invalid Rockchip partition entry '{entry}': empty partition name. 分区条目名称为空。")
            : new RockchipPartition(name, ParseSectorValue(sizeText), ParseSectorValue(offsetText));
    }

    /// <summary>
    /// <para>解析大小/偏移值：支持 0x 十六进制、十进制与 K/M/G/T 后缀（后缀先换算为字节再
    /// 换算为 512 字节扇区；无后缀时按扇区处理，与 rkdeveloptool 一致）。</para>
    /// Parses a size/offset value: 0x-hex, decimal and K/M/G/T suffixes are supported
    /// (suffixed values are converted to bytes then to 512-byte sectors; bare values are
    /// already treated as sectors, matching rkdeveloptool).
    /// </summary>
    private static long ParseSectorValue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("Empty Rockchip partition size/offset value. 分区大小/偏移值为空。");
        }

        var value = text.Trim();
        long multiplier = 1;
        var last = char.ToUpperInvariant(value[^1]);
        if (last is 'K' or 'M' or 'G' or 'T')
        {
            multiplier = last switch
            {
                'K' => 1024L,
                'M' => 1024L * 1024,
                'G' => 1024L * 1024 * 1024,
                _ => 1024L * 1024 * 1024 * 1024
            };
            value = value[..^1].Trim();
        }

        long parsed;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
            {
                throw new InvalidDataException($"Invalid hex partition value: '{value}'. 十六进制分区值无效。");
            }
        }
        else if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            throw new InvalidDataException($"Invalid partition value: '{value}'. 分区值无效。");
        }

        if (parsed < 0)
        {
            throw new InvalidDataException($"Negative partition value: '{value}'. 分区值为负。");
        }

        if (multiplier == 1)
        {
            return parsed;
        }

        try
        {
            var bytes = checked(parsed * multiplier);
            return (bytes + SectorSize - 1) / SectorSize;
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"Partition value too large: '{value}'. 分区值过大。", ex);
        }
    }
}
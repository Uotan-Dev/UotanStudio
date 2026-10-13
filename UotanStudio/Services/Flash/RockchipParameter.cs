using System.Globalization;

namespace UotanStudio.Services.Flash;

/// <summary>
/// A single partition entry from a Rockchip parameter file.
/// <para>Rockchip parameter 文件中的单个分区条目。</para>
/// </summary>
public sealed class RockchipPartition
{
    /// <summary>
    /// Initializes a partition entry.
    /// <para>初始化分区条目。</para>
    /// </summary>
    /// <param name="name">Partition name, for example boot. <para>分区名，例如 boot。</para></param>
    /// <param name="sizeSectors">Partition size in 512-byte sectors. <para>分区大小，以 512 字节扇区计。</para></param>
    /// <param name="offsetSectors">Partition start LBA in 512-byte sectors.
    /// <para>分区起始 LBA，以 512 字节扇区计。</para></param>
    public RockchipPartition(string name, long sizeSectors, long offsetSectors)
    {
        Name = name;
        SizeSectors = sizeSectors;
        OffsetSectors = offsetSectors;
    }

    /// <summary>
    /// Gets the partition name.
    /// <para>获取分区名。</para>
    /// </summary>
    /// <value>The name recorded in the parameter entry. <para>parameter 条目中记录的分区名。</para></value>
    public string Name { get; }

    /// <summary>
    /// Gets the partition size in 512-byte sectors.
    /// <para>获取分区大小（512 字节扇区）。</para>
    /// </summary>
    /// <value>The size counted in 512-byte sectors. <para>以 512 字节扇区计的大小。</para></value>
    public long SizeSectors { get; }

    /// <summary>
    /// Gets the partition start LBA in 512-byte sectors.
    /// <para>获取分区起始 LBA（512 字节扇区）。</para>
    /// </summary>
    /// <value>The start LBA counted in 512-byte sectors. <para>以 512 字节扇区计的起始 LBA。</para></value>
    public long OffsetSectors { get; }

    /// <summary>
    /// Returns a string representation of the partition entry.
    /// <para>返回分区条目的字符串表示。</para>
    /// </summary>
    /// <returns>The name, start LBA and sector count. <para>名称、起始 LBA 与扇区数。</para></returns>
    public override string ToString() => $"RockchipPartition[{Name}, LBA 0x{OffsetSectors:X}, {SizeSectors} sectors]";
}

/// <summary>
/// Parses the partition table from a Rockchip firmware parameter file.
/// <para>解析 Rockchip 固件 parameter 文件中的分区表。</para>
/// </summary>
public static class RockchipParameterParser
{
    /// <summary>Rockchip sector size in bytes. <para>Rockchip 扇区大小（字节）。</para></summary>
    public const int SectorSize = 512;

    /// <summary>
    /// Parses a parameter file and returns its partition table.
    /// <para>解析 parameter 文件并返回其分区表。</para>
    /// </summary>
    /// <param name="filePath">Path to the parameter file. <para>parameter 文件路径。</para></param>
    /// <returns>The parsed partitions, in file order. <para>解析出的分区列表，保持文件中的顺序。</para></returns>
    /// <exception cref="InvalidDataException">The file has no <c>CMDLINE</c> line or an entry is malformed.
    /// <para>文件没有 <c>CMDLINE</c> 行或某个条目格式无效。</para></exception>
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
    /// Parses the partition table from a <c>CMDLINE</c> value.
    /// <para>从 <c>CMDLINE</c> 值解析分区表。</para>
    /// </summary>
    /// <param name="cmdline">The CMDLINE value. <para>CMDLINE 的值。</para></param>
    /// <returns>The parsed partitions. <para>解析出的分区列表。</para></returns>
    /// <exception cref="InvalidDataException">The value has no <c>mtdparts=</c> marker or an entry is malformed.
    /// <para>值中没有 <c>mtdparts=</c> 标记或某个条目格式无效。</para></exception>
    public static IReadOnlyList<RockchipPartition> ParseCommandLine(string cmdline)
    {
        var mtdIdx = cmdline.IndexOf("mtdparts=", StringComparison.OrdinalIgnoreCase);
        if (mtdIdx < 0)
        {
            throw new InvalidDataException("No 'mtdparts=' found in CMDLINE; not a Rockchip parameter partition table. CMDLINE 中未找到 mtdparts=。");
        }

        var mtd = cmdline[(mtdIdx + "mtdparts=".Length)..];

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
    /// Parses a single <c>size@offset(name)</c> entry.
    /// <para>解析单个 <c>size@offset(name)</c> 条目。</para>
    /// </summary>
    /// <param name="entry">The raw entry segment. <para>原始条目片段。</para></param>
    /// <returns>The parsed partition entry. <para>解析出的分区条目。</para></returns>
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
    /// Parses a size or offset value with 0x-prefixed hex, decimal, and K/M/G/T suffixes. Suffixed values are
    /// converted to bytes and then to 512-byte sectors; bare values are treated as sectors.
    /// <para>解析大小或偏移值，支持 0x 十六进制、十进制与 K/M/G/T 后缀；带后缀的值先换算为字节再换算为
    /// 512 字节扇区，无后缀的值按扇区处理。</para>
    /// </summary>
    /// <param name="text">The raw size or offset token. <para>原始大小或偏移文本。</para></param>
    /// <returns>The value in 512-byte sectors. <para>以 512 字节扇区计的值。</para></returns>
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
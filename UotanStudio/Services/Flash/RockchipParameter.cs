using System.Globalization;
using System.Text;

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
        : this(name, sizeSectors, offsetSectors, null)
    {
    }

    /// <summary>
    /// Initializes a partition entry with an optional unique GUID.
    /// <para>初始化带可选唯一 GUID 的分区条目。</para>
    /// </summary>
    /// <param name="name">Partition name, for example boot. <para>分区名，例如 boot。</para></param>
    /// <param name="sizeSectors">Partition size in 512-byte sectors. <para>分区大小，以 512 字节扇区计。</para></param>
    /// <param name="offsetSectors">Partition start LBA in 512-byte sectors.
    /// <para>分区起始 LBA，以 512 字节扇区计。</para></param>
    /// <param name="uniqueGuid">The GUID a <c>uuid:</c> record declares, or null.
    /// <para><c>uuid:</c> 记录声明的 GUID，或 null。</para></param>
    public RockchipPartition(string name, long sizeSectors, long offsetSectors, Guid? uniqueGuid)
    {
        Name = name;
        SizeSectors = sizeSectors;
        OffsetSectors = offsetSectors;
        UniqueGuid = uniqueGuid;
    }

    /// <summary>
    /// Gets the partition name as written in the parameter entry, including a trailing <c>:grow</c> marker.
    /// <para>获取 parameter 条目中记录的分区名，包含尾部的 <c>:grow</c> 标记。</para>
    /// </summary>
    /// <value>The name recorded in the parameter entry. <para>parameter 条目中记录的分区名。</para></value>
    public string Name { get; }

    /// <summary>
    /// Gets the partition name without the trailing <c>:grow</c> marker the vendor GPT writer drops.
    /// <para>获取去掉尾部 <c>:grow</c> 标记（厂商 GPT 写入器会丢弃该标记）后的分区名。</para>
    /// </summary>
    /// <value>The bare partition name. <para>不带标记的分区名。</para></value>
    public string BareName
    {
        get
        {
            var colon = Name.IndexOf(':');
            return colon < 0 ? Name : Name[..colon];
        }
    }

    /// <summary>
    /// Gets a value indicating whether the entry declares the grow marker, meaning it expands to the end of storage.
    /// <para>获取一个值，该值指示条目是否声明了 grow 标记，即扩展到存储末尾。</para>
    /// </summary>
    /// <value><see langword="true"/> when the name carries the grow marker or the size is the grow sentinel.
    /// <para>名称携带 grow 标记或大小为 grow 哨兵值时为 <see langword="true"/>。</para></value>
    public bool IsGrow => Name.EndsWith(":grow", StringComparison.OrdinalIgnoreCase)
        || SizeSectors == RockchipParameterParser.GrowSectorCount;

    /// <summary>
    /// Gets the partition size in 512-byte sectors; a grow entry reports the all-ones sentinel
    /// <see cref="GrowSectorCount"/> the vendor parsers use.
    /// <para>获取以 512 字节扇区计的分区大小；grow 条目报告厂商解析器使用的全一哨兵值
    /// <see cref="GrowSectorCount"/>。</para>
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
    /// Gets the unique GUID the matching <c>uuid:</c> parameter record declares, or null when the table names
    /// none for this partition.
    /// <para>获取匹配的 <c>uuid:</c> parameter 记录声明的唯一 GUID；表中未为该分区声明时为 null。</para>
    /// </summary>
    /// <value>The partition GUID, or null. <para>分区 GUID，或 null。</para></value>
    public Guid? UniqueGuid { get; }

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
/// <remarks>
/// The parser follows the vendor line-oriented behavior: a <c>CMDLINE:</c> line carries the
/// <c>mtdparts=</c> value, but a legacy parameter without the prefix still parses when any line contains an
/// <c>mtdparts=</c> marker; <c>#</c> lines are comments; a trailing <c>-</c> size is the grow marker; and
/// <c>uuid:</c> lines are independent records, not extensions of the command line.
/// <para>解析器遵循厂商的逐行行为：<c>CMDLINE:</c> 行携带 <c>mtdparts=</c> 值，但没有该前缀的旧式 parameter
/// 在任一行包含 <c>mtdparts=</c> 标记时仍可解析；<c>#</c> 行是注释；尾部的 <c>-</c> 大小是 grow 标记；
/// <c>uuid:</c> 行是独立记录，不是命令行的延伸。</para>
/// </remarks>
public static class RockchipParameterParser
{
    /// <summary>Rockchip sector size in bytes. <para>Rockchip 扇区大小（字节）。</para></summary>
    public const int SectorSize = 512;

    /// <summary>
    /// The grow-size sentinel the vendor parsers store for a trailing <c>-</c> entry.
    /// <para>厂商解析器为尾随 <c>-</c> 条目存储的 grow 大小哨兵值。</para>
    /// </summary>
    public const long GrowSectorCount = 0xFFFFFFFF;

    /// <summary>
    /// Parses a parameter file and returns its partition table. A file wrapped in the vendor
    /// <c>PARM</c> container is unwrapped first.
    /// <para>解析 parameter 文件并返回其分区表。以厂商 <c>PARM</c> 容器封装的文件会先解包。</para>
    /// </summary>
    /// <param name="filePath">Path to the parameter file. <para>parameter 文件路径。</para></param>
    /// <returns>The parsed partitions, in file order. <para>解析出的分区列表，保持文件中的顺序。</para></returns>
    /// <exception cref="InvalidDataException">The file has no <c>mtdparts</c> marker or an entry is malformed.
    /// <para>文件没有 <c>mtdparts</c> 标记或某个条目格式无效。</para></exception>
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

        using var stream = File.OpenRead(filePath);
        var length = (int)Math.Min(stream.Length, RockchipParmContainer.MaxParameterSectors * SectorSize);
        var bytes = new byte[length];
        var read = 0;
        while (read < length)
        {
            var chunk = stream.Read(bytes, read, length - read);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        return ParseBytes(bytes.AsSpan(0, read));
    }

    /// <summary>
    /// Parses parameter bytes, unwrapping a leading vendor <c>PARM</c> container when present.
    /// <para>解析 parameter 字节；存在厂商 <c>PARM</c> 容器头时先解包。</para>
    /// </summary>
    /// <param name="bytes">The raw file or device-read bytes.<para>原始文件或设备读出的字节。</para></param>
    /// <returns>The parsed partitions. <para>解析出的分区列表。</para></returns>
    /// <exception cref="InvalidDataException">A present <c>PARM</c> container is invalid, or the text carries no
    /// <c>mtdparts</c> marker.<para>存在的 <c>PARM</c> 容器无效，或文本没有 <c>mtdparts</c> 标记。</para></exception>
    public static IReadOnlyList<RockchipPartition> ParseBytes(ReadOnlySpan<byte> bytes)
    {
        if (RockchipParmContainer.HasMagic(bytes))
        {
            bytes = RockchipParmContainer.Unwrap(bytes);
        }

        return ParseText(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// Parses the partition table from a parameter text.
    /// <para>从 parameter 文本解析分区表。</para>
    /// </summary>
    /// <param name="text">The parameter text.<para>parameter 文本。</para></param>
    /// <returns>The parsed partitions. <para>解析出的分区列表。</para></returns>
    /// <exception cref="InvalidDataException">The text carries no <c>mtdparts</c> marker or an entry is malformed.
    /// <para>文本没有 <c>mtdparts</c> 标记或某个条目格式无效。</para></exception>
    public static IReadOnlyList<RockchipPartition> ParseText(string text)
    {
        string? cmdline = null;
        var guids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n', '\0']))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
            {
                var (name, guid) = ParseUuidRecord(line["uuid:".Length..]);
                guids[name] = guid;
                continue;
            }

            if (line.StartsWith("CMDLINE:", StringComparison.OrdinalIgnoreCase))
            {
                if (cmdline is null)
                {
                    cmdline = line["CMDLINE:".Length..].Trim();
                }

                continue;
            }

            if (line.Contains("mtdparts=", StringComparison.OrdinalIgnoreCase) && cmdline is null)
            {
                cmdline = line;
            }
        }

        return cmdline is null
            ? throw new InvalidDataException("No 'CMDLINE:' line or mtdparts= marker found in the parameter text. parameter 文本中未找到 CMDLINE 行或 mtdparts= 标记。")
            : ParseCommandLine(cmdline, guids);
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
        => ParseCommandLine(cmdline, new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Parses the partition table from a <c>CMDLINE</c> value, attaching the GUIDs the
    /// <c>uuid:</c> records declared for partition names.
    /// <para>从 <c>CMDLINE</c> 值解析分区表，并附加 <c>uuid:</c> 记录为分区名声明的 GUID。</para>
    /// </summary>
    /// <param name="cmdline">The CMDLINE value. <para>CMDLINE 的值。</para></param>
    /// <param name="guids">Name-to-GUID records parsed from <c>uuid:</c> lines.
    /// <para>从 <c>uuid:</c> 行解析的名称到 GUID 记录。</para></param>
    /// <returns>The parsed partitions. <para>解析出的分区列表。</para></returns>
    /// <exception cref="InvalidDataException">The value has no <c>mtdparts=</c> marker or an entry is malformed.
    /// <para>值中没有 <c>mtdparts=</c> 标记或某个条目格式无效。</para></exception>
    private static IReadOnlyList<RockchipPartition> ParseCommandLine(string cmdline, Dictionary<string, Guid> guids)
    {
        var mtdIdx = cmdline.IndexOf("mtdparts=", StringComparison.OrdinalIgnoreCase);
        if (mtdIdx < 0)
        {
            throw new InvalidDataException("No 'mtdparts=' found in CMDLINE; not a Rockchip parameter partition table. CMDLINE 中未找到 mtdparts=。");
        }

        var mtd = cmdline[(mtdIdx + "mtdparts=".Length)..];

        var colonIdx = mtd.IndexOf(':');
        var entriesPart = colonIdx >= 0 ? mtd[(colonIdx + 1)..] : mtd;
        var closeIdx = entriesPart.LastIndexOf(')');
        var lastColon = entriesPart.LastIndexOf(':');
        if (closeIdx >= 0 && lastColon > closeIdx)
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

            var entry = ParseEntry(segment);
            partitions.Add(guids.TryGetValue(entry.BareName, out var guid)
                ? new RockchipPartition(entry.Name, entry.SizeSectors, entry.OffsetSectors, guid)
                : entry);
        }

        return partitions;
    }

    /// <summary>
    /// Parses one <c>uuid:name=value</c> record: the name is the partition name as written in the table and the
    /// value is a textual UUID whose first three fields GPT stores little-endian.
    /// <para>解析一条 <c>uuid:name=value</c> 记录：名称是表内记录的分区名，值是文本 UUID，其前三个字段按 GPT
    /// 的小端字节存储。</para>
    /// </summary>
    /// <param name="record">The record text after the <c>uuid:</c> prefix. <para><c>uuid:</c> 前缀后的记录文本。</para></param>
    /// <returns>The partition name and its GUID. <para>分区名及其 GUID。</para></returns>
    /// <exception cref="InvalidDataException">The record has no <c>=</c>, an empty name, or the value is not a
    /// well-formed UUID.<para>记录没有 <c>=</c>、名称为空，或值不是格式正确的 UUID。</para></exception>
    private static (string Name, Guid Guid) ParseUuidRecord(string record)
    {
        var eqIdx = record.IndexOf('=');
        if (eqIdx <= 0)
        {
            throw new InvalidDataException($"Invalid Rockchip uuid record: '{record.Trim()}'. uuid 记录格式无效。");
        }

        var name = record[..eqIdx].Trim();
        if (name.Length == 0)
        {
            throw new InvalidDataException($"Invalid Rockchip uuid record: '{record.Trim()}' names no partition. uuid 记录未指明分区名。");
        }

        var value = record[(eqIdx + 1)..].Trim();
        if (!Guid.TryParseExact(value, "D", out var guid))
        {
            throw new InvalidDataException($"Invalid Rockchip partition UUID: '{value}'. 分区 UUID 格式无效。");
        }

        return (name, guid);
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
    /// converted to bytes and then to 512-byte sectors; bare values are treated as sectors. A lone <c>-</c> is
    /// the vendor grow marker and yields <see cref="GrowSectorCount"/>.
    /// <para>解析大小或偏移值，支持 0x 十六进制、十进制与 K/M/G/T 后缀；带后缀的值先换算为字节再换算为
    /// 512 字节扇区，无后缀的值按扇区处理。单独的 <c>-</c> 是厂商的 grow 标记，得到
    /// <see cref="GrowSectorCount"/>。</para>
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
        if (value == "-")
        {
            return GrowSectorCount;
        }

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
            throw new InvalidDataException($"Negative partition value: '{text.Trim()}'. 分区值为负。");
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
            throw new InvalidDataException($"Partition value too large: '{text.Trim()}'. 分区值过大。", ex);
        }
    }
}
using System.Globalization;

namespace UotanStudio.Models;

/// <summary>
/// Kind of a partition-table data source.
/// <para>分区表数据源类型。</para>
/// </summary>
public enum PartitionTableSource
{
    /// <summary>
    /// Android Logical Partition (LP) super metadata.
    /// <para>Android Logical Partition（LP）super 元数据。</para>
    /// </summary>
    Lp = 0,

    /// <summary>
    /// Rockchip parameter file whose CMDLINE carries an <c>mtdparts</c> partition table.
    /// <para>CMDLINE 携带 <c>mtdparts</c> 分区表的 Rockchip parameter 文件。</para>
    /// </summary>
    Rockchip = 1,

    /// <summary>
    /// UEFI GPT partition table (protective MBR + <c>EFI PART</c> header + partition entries).
    /// <para>UEFI GPT 分区表（保护性 MBR + <c>EFI PART</c> 头 + 分区表项）。</para>
    /// </summary>
    Gpt = 2,

    /// <summary>
    /// Classic MBR partition table (0x55AA signature + 4 primary slots + extended EBR chain).
    /// <para>经典 MBR 分区表（0x55AA 签名 + 4 主分区项 + 扩展分区 EBR 链）。</para>
    /// </summary>
    Mbr = 3,

    /// <summary>
    /// Amlogic vendor partition table (custom MBR-style layout + name table).
    /// <para>Amlogic 厂商私有分区表（自定义 MBR 风格布局 + 名称表）。</para>
    /// </summary>
    Amlogic = 4
}

/// <summary>
/// A normalized partition-table entry.
/// <para>归一化的分区表条目。</para>
/// </summary>
public sealed class PartitionEntry
{
    /// <summary>
    /// Partition name.
    /// <para>分区名称。</para>
    /// </summary>
    /// <value>The partition name.<para>分区名称。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Partition start sector (512-byte sectors).
    /// <para>分区起始扇区（512 字节扇区）。</para>
    /// </summary>
    /// <value>The first sector of the partition.<para>分区的起始扇区。</para></value>
    public ulong StartSector { get; set; }

    /// <summary>
    /// Partition size in 512-byte sectors.
    /// <para>分区大小（512 字节扇区）。</para>
    /// </summary>
    /// <value>The partition size counted in sectors.<para>分区大小，以扇区计数。</para></value>
    public ulong SizeSectors { get; set; }

    /// <summary>
    /// Partition type description: for LP the first extent's target type (<c>linear</c>/<c>zero</c>),
    /// for Rockchip <c>mtd</c>.
    /// <para>分区类型描述：LP 为第一个 extent 的目标类型（<c>linear</c>/<c>zero</c>），
    /// Rockchip 为 <c>mtd</c>。</para>
    /// </summary>
    /// <value>The type description of the partition.<para>分区的类型描述。</para></value>
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Partition sector value parse/format helpers (matching the Rockchip parameter syntax: 0x-hex,
/// decimal and K/M/G/T suffixes are supported).
/// <para>分区扇区值解析与格式化助手（与 Rockchip parameter 语法一致：
/// 支持 0x 十六进制、十进制与 K/M/G/T 后缀）。</para>
/// </summary>
public static class PartitionTableFormat
{
    /// <summary>
    /// Sector size in bytes.
    /// <para>扇区大小（字节）。</para>
    /// </summary>
    public const int SectorSize = 512;

    /// <summary>
    /// Parses a sector value accepting 0x-hex, decimal, and K/M/G/T suffixes. Suffixed values are
    /// converted from bytes to 512-byte sectors; bare values are treated as sectors.
    /// <para>解析扇区值，支持 0x 十六进制、十进制与 K/M/G/T 后缀。带后缀的值先按字节换算为 512 字节扇区，无后缀的值按扇区处理。</para>
    /// </summary>
    /// <param name="text">The text to parse. <para>要解析的文本。</para></param>
    /// <param name="sectors">The parsed sector count, zero on failure. <para>解析出的扇区数，失败时为 0。</para></param>
    /// <returns>True when the value parsed; otherwise, false.<para>解析成功时为 true，否则为 false。</para></returns>
    public static bool TryParseSectors(string? text, out ulong sectors)
    {
        sectors = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        ulong multiplier = 1;
        var last = char.ToUpperInvariant(value[^1]);
        if (last is 'K' or 'M' or 'G' or 'T')
        {
            multiplier = last switch
            {
                'K' => 1024UL,
                'M' => 1024UL * 1024,
                'G' => 1024UL * 1024 * 1024,
                _ => 1024UL * 1024 * 1024 * 1024
            };
            value = value[..^1].Trim();
        }

        ulong parsed;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }
        }
        else if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            return false;
        }

        if (multiplier == 1)
        {
            sectors = parsed;
            return true;
        }

        try
        {
            var bytes = checked(parsed * multiplier);
            sectors = (bytes + SectorSize - 1) / SectorSize;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Formats a sector count as unsigned 0x-hex.
    /// <para>以 0x 十六进制格式化扇区数（无符号）。</para>
    /// </summary>
    /// <param name="sectors">The sector count to format. <para>要格式化的扇区数。</para></param>
    /// <returns>The 0x-prefixed hexadecimal text.<para>带 0x 前缀的十六进制文本。</para></returns>
    public static string FormatSectors(ulong sectors) => "0x" + sectors.ToString("X", CultureInfo.InvariantCulture);
}
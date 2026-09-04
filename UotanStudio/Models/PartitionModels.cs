using System.Globalization;

namespace UotanStudio.Models;

/// <summary>
/// <para>分区表数据源类型，供分区表编辑器与打开方式判定共享。</para>
/// Kind of a partition-table data source, shared by the partition-table editor and the open-mode routing.
/// </summary>
public enum PartitionTableSource
{
    /// <summary>
    /// <para>Android Logical Partition（LP）super 元数据。</para>
    /// Android Logical Partition (LP) super metadata.
    /// </summary>
    Lp = 0,

    /// <summary>
    /// <para>Rockchip parameter 文件（<c>CMDLINE: mtdparts=...</c> 分区表）。</para>
    /// Rockchip parameter file (<c>CMDLINE: mtdparts=...</c> partition table).
    /// </summary>
    Rockchip = 1,

    /// <summary>
    /// <para>UEFI GPT 分区表（保护性 MBR + <c>EFI PART</c> 头 + 分区表项）。</para>
    /// UEFI GPT partition table (protective MBR + <c>EFI PART</c> header + partition entries).
    /// </summary>
    Gpt = 2,

    /// <summary>
    /// <para>经典 MBR 分区表（0x55AA 签名 + 4 主分区项 + 扩展分区 EBR 链）。</para>
    /// Classic MBR partition table (0x55AA signature + 4 primary slots + extended EBR chain).
    /// </summary>
    Mbr = 3,

    /// <summary>
    /// <para>Amlogic 厂商私有分区表（自定义 MBR 风格布局 + 名称表）。</para>
    /// Amlogic vendor partition table (custom MBR-style layout + name table).
    /// </summary>
    Amlogic = 4
}

/// <summary>
/// <para>归一化的分区表条目，供编辑器视图模型与打开方式判定共享。</para>
/// A normalized partition-table entry, shared by the editor view model and the open-mode routing.
/// </summary>
public sealed class PartitionEntry
{
    /// <summary>
    /// <para>分区名称。</para>
    /// Partition name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <para>分区起始扇区（512 字节扇区）。</para>
    /// Partition start sector (512-byte sectors).
    /// </summary>
    public ulong StartSector { get; set; }

    /// <summary>
    /// <para>分区大小（512 字节扇区）。</para>
    /// Partition size in 512-byte sectors.
    /// </summary>
    public ulong SizeSectors { get; set; }

    /// <summary>
    /// <para>分区类型描述：LP 为第一个 extent 的目标类型（<c>linear</c>/<c>zero</c>），
    /// Rockchip 为 <c>mtd</c>。</para>
    /// Partition type description: for LP the first extent's target type (<c>linear</c>/<c>zero</c>),
    /// for Rockchip <c>mtd</c>.
    /// </summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// <para>分区扇区值解析与格式化助手（与 Rockchip parameter 语法一致：
/// 支持 0x 十六进制、十进制与 K/M/G/T 后缀）。</para>
/// Partition sector value parse/format helpers (matching the Rockchip parameter syntax: 0x-hex,
/// decimal and K/M/G/T suffixes are supported).
/// </summary>
public static class PartitionTableFormat
{
    /// <summary>
    /// <para>扇区大小（字节）。</para>
    /// Sector size in bytes.
    /// </summary>
    public const int SectorSize = 512;

    /// <summary>
    /// <para>解析扇区值：支持 0x 十六进制、十进制与 K/M/G/T 后缀（后缀先换算为字节再换算为
    /// 512 字节扇区；无后缀时按扇区处理，与 rkdeveloptool 一致）。解析失败返回 <c>false</c>。</para>
    /// Parses a sector value: 0x-hex, decimal and K/M/G/T suffixes are supported (suffixed values are
    /// converted to bytes then to 512-byte sectors; bare values are already treated as sectors,
    /// matching rkdeveloptool). Returns <c>false</c> when the value cannot be parsed.
    /// </summary>
    /// <param name="text">要解析的文本。 The text to parse.</param>
    /// <param name="sectors">解析出的扇区数（失败时为 0）。 The parsed sector count (0 on failure).</param>
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
    /// <para>以 0x 十六进制格式化扇区数（无符号）。</para>
    /// Formats a sector count as unsigned 0x-hex.
    /// </summary>
    public static string FormatSectors(ulong sectors) => "0x" + sectors.ToString("X", CultureInfo.InvariantCulture);
}
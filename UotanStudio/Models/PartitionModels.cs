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

    /// <summary>
    /// Parses a byte count accepting a bare number (bytes), 0x-hex, decimals and B/K/KB/KiB/M/MB/MiB/G/GB/GiB/T/TB/TiB
    /// suffixes (the "i" spellings are accepted on input but never produced on output).
    /// Unlike <see cref="TryParseSectors"/>, a bare value is a byte count and is not divided by the sector size.
    /// <para>解析字节数，支持纯数字（字节）、0x 十六进制、小数以及 B/K/KB/KiB/M/MB/MiB/G/GB/GiB/T/TB/TiB 后缀。
    /// 与 <see cref="TryParseSectors"/> 不同，纯数字按字节计，不再除以扇区大小。</para>
    /// </summary>
    /// <param name="text">The text to parse. <para>要解析的文本。</para></param>
    /// <param name="bytes">The parsed byte count, zero on failure. <para>解析出的字节数，失败时为 0。</para></param>
    /// <returns>True when the value parsed; otherwise, false.<para>解析成功时为 true，否则为 false。</para></returns>
    public static bool TryParseBytes(string? text, out ulong bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var digits = 2;
            while (digits < value.Length && Uri.IsHexDigit(value[digits]))
            {
                digits++;
            }

            if (digits == 2
                || !ulong.TryParse(value[2..digits], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                || !TryGetUnitMultiplier(value[digits..], out var hexMultiplier))
            {
                return false;
            }

            return TryScale(hex, hexMultiplier, out bytes);
        }

        var length = 0;
        while (length < value.Length && (char.IsAsciiDigit(value[length]) || value[length] == '.'))
        {
            length++;
        }

        if (length == 0
            || !decimal.TryParse(value[..length], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            || !TryGetUnitMultiplier(value[length..], out var multiplier))
        {
            return false;
        }

        var scaled = number * multiplier;
        if (scaled < 0 || scaled > ulong.MaxValue)
        {
            return false;
        }

        bytes = (ulong)decimal.Ceiling(scaled);
        return true;
    }

    /// <summary>
    /// Formats a byte count with a short unit suffix ("512 B", "1.5 GB"); the result parses back through
    /// <see cref="TryParseBytes"/>.
    /// <para>以短单位后缀格式化字节数（"512 B"、"1.5 GB"）；结果可被 <see cref="TryParseBytes"/> 原样解析回来。</para>
    /// </summary>
    /// <param name="bytes">The byte count to format. <para>要格式化的字节数。</para></param>
    /// <returns>The value followed by its unit.<para>数值加单位。</para></returns>
    public static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{value:0} {units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }

    /// <summary>
    /// Converts a byte count to the sector count that covers it (rounding up).
    /// <para>把字节数换算为覆盖它的扇区数（向上取整）。</para>
    /// </summary>
    /// <param name="bytes">The byte count. <para>字节数。</para></param>
    /// <returns>The covering sector count.<para>覆盖该字节数的扇区数。</para></returns>
    public static ulong BytesToSectors(ulong bytes) => (bytes + SectorSize - 1) / SectorSize;

    private static bool TryGetUnitMultiplier(string suffix, out ulong multiplier)
    {
        multiplier = suffix.Trim().ToUpperInvariant() switch
        {
            "" or "B" => 1UL,
            "K" or "KB" or "KIB" => 1024UL,
            "M" or "MB" or "MIB" => 1024UL * 1024,
            "G" or "GB" or "GIB" => 1024UL * 1024 * 1024,
            "T" or "TB" or "TIB" => 1024UL * 1024 * 1024 * 1024,
            _ => 0UL
        };
        return multiplier != 0;
    }

    private static bool TryScale(ulong value, ulong multiplier, out ulong bytes)
    {
        bytes = 0;
        if (multiplier != 1 && value > ulong.MaxValue / multiplier)
        {
            return false;
        }

        bytes = value * multiplier;
        return true;
    }
}
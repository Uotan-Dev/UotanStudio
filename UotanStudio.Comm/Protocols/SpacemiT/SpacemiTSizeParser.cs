using System.Diagnostics;

namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Parses SpacemiT partition-table and package size strings ("640K", "4M", "2G",
/// plain byte counts) into byte sizes; "-" means "until end of storage".
/// <para>把 SpacemiT 分区表与烧录包中的尺寸字符串（"640K"、"4M"、"2G"、纯字节数）
/// 解析为字节大小；"-" 表示“直到存储末尾”。</para>
/// </summary>
public static class SpacemiTSizeParser
{
    /// <summary>
    /// Parses a size string into bytes.
    /// <para>把尺寸字符串解析为字节数。</para>
    /// </summary>
    /// <param name="value">Size text from an untrusted package file; leading/trailing whitespace is ignored. <para>来自不可信包文件的尺寸文本；忽略首尾空白。</para></param>
    /// <returns>The size in bytes, or -1 when the value is "-" (until end of storage). <para>以字节计的大小；值为 "-" 时返回 -1（直到存储末尾）。</para></returns>
    /// <exception cref="FormatException">The value is empty, exceeds 15 characters, or is not a supported size form. <para>值为空、超过 15 个字符或不是受支持的尺寸形式。</para></exception>
    public static long Parse(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new FormatException("Empty size string.");
        }

        if (trimmed.Length > 15)
        {
            throw new FormatException($"Size string too long: {trimmed.Length} characters.");
        }

        if (trimmed == "-")
        {
            return -1;
        }

        if (char.IsAsciiDigit(trimmed[^1]))
        {
            return long.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var plain)
                && plain >= 0
                ? plain
                : throw new FormatException($"Invalid size string '{trimmed}'.");
        }

        var multiplier = trimmed[^1] switch
        {
            'K' or 'k' => 1024L,
            'M' or 'm' => 1024L * 1024,
            'G' or 'g' => 1024L * 1024 * 1024,
            _ => 0L,
        };
        if (multiplier == 0)
        {
            throw new FormatException($"Unsupported size unit in '{trimmed}'.");
        }

        var numeric = trimmed[..^1].Trim();
        if (numeric.Length == 0 ||
            !long.TryParse(numeric, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) ||
            amount < 0)
        {
            throw new FormatException($"Invalid size string '{trimmed}'.");
        }

        var result = checked(amount * multiplier);
        Debug.Assert(result >= 0);
        return result;
    }
}
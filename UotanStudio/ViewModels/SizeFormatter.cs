namespace UotanStudio.ViewModels;

/// <summary>
/// Formats a byte count as a short human-readable size string: base-1024 units up to TB with
/// two-decimal precision; zero or negative values render as "0 B".
/// <para>把字节数格式化为简短的可读大小：以 1024 为底、单位至 TB、两位小数，零或负值显示为 "0 B"。</para>
/// </summary>
internal static class SizeFormatter
{
    /// <summary>Formats a byte count as a human-readable size.<para>将字节数格式化为可读大小。</para></summary>
    /// <param name="bytes">The byte count to render.<para>要显示的字节数。</para></param>
    /// <returns>The formatted size string.<para>格式化后的大小字符串。</para></returns>
    internal static string Format(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var abs = Math.Abs(bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        var value = (double)abs;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : $"{value:0.##} {units[unit]}";
    }
}
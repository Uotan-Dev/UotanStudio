namespace UotanStudio.ViewModels;

/// <summary>
/// Formats a byte count as a short human-readable size string for list and detail views:
/// base-1024 units up to TB, two-decimal precision, zero or negative rendered as "0 B".
/// Distinct from <see cref="Services.ProgressMeter.FormatSize"/>, which caps at TB with one decimal
/// and shows an em-dash for invalid progress values.
/// <para>为列表与详情视图把字节数格式化为简短的可读大小：以 1024 为底、单位至 TB、两位小数，
/// 零或负值显示为 "0 B"。与 <see cref="Services.ProgressMeter.FormatSize"/> 不同——后者用于进度显示，
/// 上限 TB、一位小数且无效值显示破折号。</para>
/// </summary>
internal static class SizeFormatter
{
    /// <summary>Formats <paramref name="bytes"/> as a human-readable size. 将字节数格式化为可读大小。</summary>
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
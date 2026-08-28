using System.Globalization;
using Avalonia.Data.Converters;
using UotanStudio.Services;

namespace UotanStudio.Converters;

/// <summary>
/// <para>将日志筛选下拉框的置空项（null 或空字符串）显示为本地化的"全部"文本，
/// 其余值原样显示（日志级别枚举名或组件名）。</para>
/// Displays the log-filter combo box's "all" placeholder (null or empty string) as the localized
/// "All" text; other values pass through unchanged (log level enum name or source name).
/// </summary>
public class LogFilterDisplayConverter : IValueConverter
{
    /// <summary>
    /// <para>null 或空字符串返回本地化"全部"，其余返回原值。</para>
    /// Returns the localized "All" for null or empty strings; passes other values through.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return LanguageService.Instance.GetString("Log_FilterAll");
        }

        if (value is string str)
        {
            return string.IsNullOrEmpty(str)
                ? LanguageService.Instance.GetString("Log_FilterAll")
                : str;
        }

        return value;
    }

    /// <summary>
    /// <para>不支持反向转换。</para>
    /// Reverse conversion is not supported.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.AvaloniaProperty.UnsetValue;
    }
}
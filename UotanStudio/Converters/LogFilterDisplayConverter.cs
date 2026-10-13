using System.Globalization;
using Avalonia.Data.Converters;
using UotanStudio.Services;

namespace UotanStudio.Converters;

/// <summary>
/// Displays the log filter's empty selection as the localized "All" text and passes other values
/// through unchanged.
/// <para>把日志筛选的置空选项显示为本地化的“全部”文本，其余值原样呈现。</para>
/// </summary>
public class LogFilterDisplayConverter : IValueConverter
{
    /// <summary>
    /// Returns the localized "All" text for null or empty strings and the value itself otherwise.
    /// <para>null 或空字符串返回本地化的“全部”文本，其余情况返回原值。</para>
    /// </summary>
    /// <param name="value">The raw filter selection. <para>原始的筛选选项值。</para></param>
    /// <param name="targetType">The target type of the binding, unused. <para>绑定的目标类型，未使用。</para></param>
    /// <param name="parameter">The converter parameter, unused. <para>转换参数，未使用。</para></param>
    /// <param name="culture">The culture information, unused. <para>区域信息，未使用。</para></param>
    /// <returns>The localized placeholder text or the untouched value. <para>本地化占位文本，或未经改动的原值。</para></returns>
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
    /// Reverse conversion is not supported.
    /// <para>不支持反向转换。</para>
    /// </summary>
    /// <param name="value">The converted value, ignored. <para>已转换的值，忽略。</para></param>
    /// <param name="targetType">The target type of the binding, ignored. <para>绑定的目标类型，忽略。</para></param>
    /// <param name="parameter">The converter parameter, ignored. <para>转换参数，忽略。</para></param>
    /// <param name="culture">The culture information, ignored. <para>区域信息，忽略。</para></param>
    /// <returns>An unset marker leaving the target value unchanged. <para>使目标值保持不变的未设置标记。</para></returns>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.AvaloniaProperty.UnsetValue;
    }
}
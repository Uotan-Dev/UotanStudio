using System.Globalization;
using Avalonia.Data.Converters;

namespace UotanStudio.Converters;

/// <summary>
/// Determines whether a string is present, returning true when it is neither null nor empty.
/// <para>判断字符串是否存有内容，既非 null 也非空字符串时返回 true。</para>
/// </summary>
public class StringNotNullOrEmptyConverter : IValueConverter
{
    /// <summary>
    /// Returns true when the value is a string with content and false for null or empty strings.
    /// <para>值是有内容的字符串时返回 true，为 null 或空字符串时返回 false。</para>
    /// </summary>
    /// <param name="value">The value to inspect. <para>要检查的值。</para></param>
    /// <param name="targetType">The target type of the binding, unused. <para>绑定的目标类型，未使用。</para></param>
    /// <param name="parameter">The converter parameter, unused. <para>转换参数，未使用。</para></param>
    /// <param name="culture">The culture information, unused. <para>区域信息，未使用。</para></param>
    /// <returns>True when the value is a non-empty string. <para>值为非空字符串时为 true。</para></returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string str && !string.IsNullOrEmpty(str);
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
using System.Globalization;
using Avalonia.Data.Converters;

namespace UotanStudio.Converters;

/// <summary>
/// Compares an integer value with the conversion parameter and reports whether they are equal.
/// <para>将整数值与转换参数比较，给出两者是否相等。</para>
/// </summary>
public class IntEqualConverter : IValueConverter
{
    /// <summary>
    /// Compares the integer value with the parameter, accepting the parameter as text or as an
    /// integer, and returns true when both denote the same number.
    /// <para>将整数值与参数比较，参数可为文本或整数，两者表示同一数值时返回 true。</para>
    /// </summary>
    /// <param name="value">The value carrying the integer to test. <para>携带待比较整数的值。</para></param>
    /// <param name="targetType">The target type of the binding, unused. <para>绑定的目标类型，未使用。</para></param>
    /// <param name="parameter">The integer to compare against, as text or as an integer. <para>作为文本或整数给出的比较目标值。</para></param>
    /// <param name="culture">The culture information, unused. <para>区域信息，未使用。</para></param>
    /// <returns>True when both sides denote the same integer. <para>两侧表示同一整数时为 true。</para></returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int paramValue))
        {
            return intValue == paramValue;
        }

        return value is int intVal && parameter is int paramInt ? intVal == paramInt : false;
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
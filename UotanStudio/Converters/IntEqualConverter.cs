using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace UotanStudio.Converters;

/// <summary>
/// <para>将整数值与参数进行比较，相等时返回 true。用于 RadioButton 的 IsChecked 绑定。</para>
/// Compares an integer value with the parameter, returning true when equal. Used for RadioButton IsChecked binding.
/// </summary>
public class IntEqualConverter : IValueConverter
{
    /// <summary>
    /// <para>将整数值与 ConverterParameter 比较，相等返回 true。</para>
    /// Compares the integer value with ConverterParameter, returning true if equal.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int paramValue))
        {
            return intValue == paramValue;
        }

        if (value is int intVal && parameter is int paramInt)
        {
            return intVal == paramInt;
        }

        return false;
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

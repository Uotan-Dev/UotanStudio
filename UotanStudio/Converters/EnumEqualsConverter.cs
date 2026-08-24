using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace UotanStudio.Converters;

/// <summary>
/// Converts an enum value to a boolean by comparing against the ConverterParameter string.
/// <para>将枚举值转换为布尔值：与 ConverterParameter 字符串比较。</para>
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string expected)
        {
            return false;
        }

        return string.Equals(value.ToString(), expected, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
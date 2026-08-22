using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace UotanStudio.Converters;

/// <summary>
/// <para>判断字符串是否非空，非空时返回 true。用于控制元素的可见性。</para>
/// Determines whether a string is not null or empty, returning true when not empty. Used to control element visibility.
/// </summary>
public class StringNotNullOrEmptyConverter : IValueConverter
{
    /// <summary>
    /// <para>字符串非空时返回 true，否则返回 false。</para>
    /// Returns true when the string is not null or empty, otherwise false.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string str && !string.IsNullOrEmpty(str);

    /// <summary>
    /// <para>不支持反向转换。</para>
    /// Reverse conversion is not supported.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Avalonia.AvaloniaProperty.UnsetValue;
}

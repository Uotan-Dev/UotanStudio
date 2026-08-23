using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace UotanStudio.Converters;

/// <summary>
/// <para>判断引用是否非空：null 时返回 false，非空返回 true。</para>
/// Determines whether a reference is non-null: false for null, true otherwise.
/// </summary>
/// <remarks>
/// <para>用于控制仅在有选中项时显示内容的元素可见性。</para>
/// Used to control visibility of elements that only make sense when a selection exists.
/// </remarks>
public class NotNullConverter : IValueConverter
{
    /// <summary>
    /// <para>非空引用返回 true，否则 false。</para>
    /// Returns true for a non-null reference, otherwise false.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    /// <summary>
    /// <para>不支持反向转换。</para>
    /// Reverse conversion is not supported.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Avalonia.AvaloniaProperty.UnsetValue;
}

/// <summary>
/// <para>判断引用是否为空：null 时返回 true，非空返回 false。</para>
/// Determines whether a reference is null: true for null, false otherwise.
/// </summary>
public class NullConverter : IValueConverter
{
    /// <summary>
    /// <para>null 引用返回 true，否则 false。</para>
    /// Returns true for a null reference, otherwise false.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is null;

    /// <summary>
    /// <para>不支持反向转换。</para>
    /// Reverse conversion is not supported.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Avalonia.AvaloniaProperty.UnsetValue;
}
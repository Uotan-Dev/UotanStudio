using System.Globalization;
using Avalonia.Data.Converters;

namespace UotanStudio.Converters;

/// <summary>
/// Compares by reference identity, returning true when the value is the same instance as the
/// conversion parameter.
/// <para>按引用标识比较，值与转换参数为同一实例时返回 true。</para>
/// </summary>
public class ReferenceEqualConverter : IValueConverter
{
    /// <summary>
    /// Shared instance that markup can reference directly with <c>{x:Static}</c>.
    /// <para>共享实例，可在标记中以 <c>{x:Static}</c> 直接引用。</para>
    /// </summary>
    /// <value>The single converter instance.<para>唯一的转换器实例。</para></value>
    public static ReferenceEqualConverter Instance { get; } = new();

    /// <summary>
    /// Returns true when the value and the parameter are the same instance, or, when the value is a
    /// two-element array, when its two elements are the same instance.
    /// <para>值与参数为同一实例时返回 true；值为二元数组时比较其两个元素。</para>
    /// </summary>
    /// <param name="value">The bound value, possibly a two-element array. <para>绑定的值，可能是二元数组。</para></param>
    /// <param name="targetType">The target type of the binding, unused. <para>绑定的目标类型，未使用。</para></param>
    /// <param name="parameter">The instance compared against a single value. <para>与单个值比较的目标实例。</para></param>
    /// <param name="culture">The culture information, unused. <para>区域信息，未使用。</para></param>
    /// <returns>True when the compared references are identical. <para>被比较的引用指向同一实例时为 true。</para></returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is object?[] { Length: 2 } values)
        {
            return ReferenceEquals(values[0], values[1]);
        }

        return ReferenceEquals(value, parameter);
    }

    /// <summary>
    /// Reverse conversion is not supported.
    /// <para>不支持反向转换。</para>
    /// </summary>
    /// <param name="value">The converted value, unused. <para>已转换的值，未使用。</para></param>
    /// <param name="targetType">The target type of the binding, unused. <para>绑定的目标类型，未使用。</para></param>
    /// <param name="parameter">The converter parameter, unused. <para>转换参数，未使用。</para></param>
    /// <param name="culture">The culture information, unused. <para>区域信息，未使用。</para></param>
    /// <returns>No value is produced because the method always throws. <para>方法始终抛出异常，因此不会产出结果。</para></returns>
    /// <exception cref="NotSupportedException">Reverse conversion is not implemented. <para>未实现反向转换。</para></exception>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
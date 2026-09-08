using System.Globalization;
using Avalonia.Data.Converters;

namespace UotanStudio.Converters;

/// <summary>
/// <para>引用相等比较转换器：值与 ConverterParameter 为同一实例（引用相等）时返回 true。
/// 用于设备列表项与当前选中设备（<see cref="ViewModels.Devices.DeviceInfo"/>）的选中打勾绑定——
/// DeviceInfo 未重写 Equals，按实例比较可避免序列号相同但状态变化重建实例后的误判。</para>
/// Reference-equality converter: returns true when the value is the same instance as
/// ConverterParameter. Used for the checked state of device list items against the currently
/// selected <see cref="ViewModels.Devices.DeviceInfo"/> — DeviceInfo does not override Equals,
/// so instance comparison avoids false positives after instances are rebuilt on state change.
/// </summary>
public class ReferenceEqualConverter : IValueConverter
{
    /// <summary>共享实例，供 XAML 以 <c>{x:Static}</c> 引用。<para>Shared instance for XAML <c>{x:Static}</c> use.</para></summary>
    public static ReferenceEqualConverter Instance { get; } = new();

    /// <summary>
    /// <para>值与参数引用相等时返回 true，否则 false；同时支持双值 MultiBinding
    /// （值为长度 2 的数组时比较两个元素）。</para>
    /// Returns true when the value and the parameter are the same instance; otherwise false.
    /// Also supports a two-value MultiBinding (compares the two elements when the value is
    /// an array of length 2).
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is object?[] { Length: 2 } values)
        {
            return ReferenceEquals(values[0], values[1]);
        }

        return ReferenceEquals(value, parameter);
    }

    /// <summary>不支持反向转换。 Reverse conversion is not supported.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

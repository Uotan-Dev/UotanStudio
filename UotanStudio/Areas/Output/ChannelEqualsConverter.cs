using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace UotanStudio.Areas.Output;

/// <summary>
/// <para>将 <see cref="OutputChannel"/> 与 bool 双向转换：等于指定通道时返回 true（用于 RadioButton）。
/// </para>
/// Converts <see cref="OutputChannel"/> to/from bool: returns true when the value equals the
/// bound channel (for RadioButton usage).
/// </summary>
/// <remarks>
/// <para>初始化绑定到指定通道的转换器。</para>
/// Initializes a converter bound to the given channel.
/// </remarks>
/// <param name="target"><para>目标通道。</para>Target channel.</param>
public sealed class ChannelEqualsConverter(OutputChannel target) : IValueConverter
{
    /// <summary>绑定"全部"通道的转换器实例。 Instance bound to the All channel.</summary>
    public static ChannelEqualsConverter All { get; } = new(OutputChannel.All);
    /// <summary>绑定"构建"通道的转换器实例。 Instance bound to the Build channel.</summary>
    public static ChannelEqualsConverter Build { get; } = new(OutputChannel.Build);
    /// <summary>绑定"烧录"通道的转换器实例。 Instance bound to the Flash channel.</summary>
    public static ChannelEqualsConverter Flash { get; } = new(OutputChannel.Flash);
    /// <summary>绑定"设备"通道的转换器实例。 Instance bound to the Device channel.</summary>
    public static ChannelEqualsConverter Device { get; } = new(OutputChannel.Device);

    private readonly OutputChannel _target = target;

    /// <summary>
    /// <para>值等于目标通道时返回 true。</para>
    /// Returns true when the value equals the target channel.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is OutputChannel channel && channel == _target;

    /// <summary>
    /// <para>bool 为 true 时返回目标通道。</para>
    /// Returns the target channel when the bool is true.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? _target : BindingOperations.DoNothing;
}
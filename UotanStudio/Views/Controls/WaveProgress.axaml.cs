using Avalonia;
using Avalonia.Controls;

namespace UotanStudio.Views.Controls;

/// <summary>
/// <para>水波进度控件：圆形仪表内按百分比升降的水面，用于电量等单值展示。</para>
/// Wave progress control: a circular gauge whose water level follows a percentage, used for values such as battery level.
/// </summary>
public partial class WaveProgress : UserControl
{
    /// <summary><para><see cref="Value"/> 的样式属性。</para>Styled property for <see cref="Value"/>.</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<WaveProgress, double>(nameof(Value), defaultValue: 50d);

    /// <summary><para><see cref="IsTextVisible"/> 的样式属性。</para>Styled property for <see cref="IsTextVisible"/>.</summary>
    public static readonly StyledProperty<bool> IsTextVisibleProperty =
        AvaloniaProperty.Register<WaveProgress, bool>(nameof(IsTextVisible), defaultValue: true);

    /// <summary><para>创建水波进度控件。</para>Creates the wave progress control.</summary>
    public WaveProgress() => InitializeComponent();

    /// <summary>
    /// <para>水位百分比（0–100）；超出范围的值会被忽略，避免水面跑到圆形之外。</para>
    /// Water level percentage (0-100); out-of-range values are ignored so the surface never leaves the circle.
    /// </summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set
        {
            if (value is >= 0 and <= 100)
            {
                SetValue(ValueProperty, value);
            }
        }
    }

    /// <summary><para>是否显示居中百分比文字。</para>Whether the centred percentage label is shown.</summary>
    public bool IsTextVisible
    {
        get => GetValue(IsTextVisibleProperty);
        set => SetValue(IsTextVisibleProperty, value);
    }
}
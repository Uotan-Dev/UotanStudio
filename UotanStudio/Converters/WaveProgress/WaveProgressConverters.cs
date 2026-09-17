using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace UotanStudio.Converters.WaveProgress;

/// <summary>
/// <para>把 <see cref="Views.Controls.WaveProgress"/> 的百分比值换算成水面画布上边距。</para>
/// Maps the <see cref="Views.Controls.WaveProgress"/> percentage onto the water surface's canvas top offset.
/// </summary>
public sealed class WaveProgressValueConverter : IValueConverter
{
    /// <summary><para>共享实例，供控件模板直接引用。</para>Shared instance referenced directly by the control template.</summary>
    public static readonly WaveProgressValueConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double percent)
        {
            return 0d;
        }

        // 画布高 200：0% 时水面在上沿附近，100% 时完全没过底部。
        // Canvas height is 200: the surface sits near the top at 0% and fully covers the bottom at 100%.
        return 155 - (percent * 2.1);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <para>把百分比值格式化成「NN%」文本。</para>
/// Formats the percentage as a "NN%" label.
/// </summary>
public sealed class WaveProgressValueTextConverter : IValueConverter
{
    /// <summary><para>共享实例。</para>Shared instance.</summary>
    public static readonly WaveProgressValueTextConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not double percent ? "0%" : $"{percent:#0}%";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <para>按百分比选取水面渐变：低电量时露出更多透明区，高电量时水面整体染色。</para>
/// Picks the water gradient by percentage: low levels keep more transparency, high levels tint the whole surface.
/// </summary>
public sealed class WaveProgressGradientConverter : IValueConverter
{
    /// <summary><para>共享实例。</para>Shared instance.</summary>
    public static readonly WaveProgressGradientConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double percent)
        {
            return Brushes.Transparent;
        }

        var normalized = Math.Clamp((percent / 100d) + 0.2d, 0d, 1d);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(WaveProgressPalette.Accent, 0),
                new GradientStop(Colors.Transparent, normalized)
            ]
        };
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <para>按百分比选取百分比文字的前景色：水位没过文字时改用浅色，否则跟随主题文本色。</para>
/// Picks the percentage label's foreground: light once the water covers the text, otherwise the themed text colour.
/// </summary>
public sealed class WaveProgressValueColorConverter : IValueConverter
{
    /// <summary><para>共享实例。</para>Shared instance.</summary>
    public static readonly WaveProgressValueColorConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double percent)
        {
            return Brushes.Black;
        }

        // 水位过半后文字落在主色水面上，浅色前景在浅色和深色主题下都保持可读。
        // Past half the label sits on the accent-coloured water, where a light foreground stays legible in both themes.
        return percent > 50 || Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? Brushes.GhostWhite
            : Brushes.Black;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <para>波浪控件使用的主题色解析：读取当前主题的 <c>AccentColor</c>，缺失时回退到主题色常量。</para>
/// Theme-accent resolution for the wave control: reads the current theme's <c>AccentColor</c> and falls back to
/// the accent constant when the resource is unavailable.
/// </summary>
internal static class WaveProgressPalette
{
    /// <summary><para>当前主题的强调色。</para>The accent colour of the current theme.</summary>
    internal static Color Accent
    {
        get
        {
            if (Application.Current is { } app
                && app.TryGetResource("AccentColor", app.ActualThemeVariant, out var value)
                && value is Color color)
            {
                return color;
            }

            // 兜底值与 Themes/AccentColors.axaml 的浅色 AccentColor 保持一致。
            // Fallback kept in sync with the light AccentColor in Themes/AccentColors.axaml.
            return Color.Parse("#D03A2F");
        }
    }
}
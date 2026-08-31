using Avalonia;
using Avalonia.Controls;

namespace UotanStudio.Views.Controls;

/// <summary>
/// <para>按最小项宽度自动换行，并让每一行的子项等宽填满可用宽度。</para>
/// Wraps children according to a minimum item width and justifies every line across the available width.
/// </summary>
public sealed class JustifiedWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(MinItemWidth), 110d);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(ItemSpacing), 6d);

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(LineSpacing), 6d);

    static JustifiedWrapPanel()
    {
        AffectsMeasure<JustifiedWrapPanel>(MinItemWidthProperty, ItemSpacingProperty, LineSpacingProperty);
        AffectsArrange<JustifiedWrapPanel>(MinItemWidthProperty, ItemSpacingProperty, LineSpacingProperty);
    }

    /// <summary>
    /// <para>获取或设置每个子项的最小宽度。</para>
    /// Gets or sets the minimum width of each child.
    /// </summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>
    /// <para>获取或设置同一行子项之间的距离。</para>
    /// Gets or sets the spacing between children on the same line.
    /// </summary>
    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>
    /// <para>获取或设置相邻行之间的距离。</para>
    /// Gets or sets the spacing between adjacent lines.
    /// </summary>
    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return default;
        }

        var itemSpacing = Math.Max(0, ItemSpacing);
        var lineSpacing = Math.Max(0, LineSpacing);
        var minItemWidth = Math.Max(1, MinItemWidth);
        var hasFiniteWidth = double.IsFinite(availableSize.Width);
        var availableWidth = hasFiniteWidth
            ? Math.Max(minItemWidth, availableSize.Width)
            : Children.Count * minItemWidth + (Children.Count - 1) * itemSpacing;
        var columns = CalculateColumnCount(availableWidth, minItemWidth, itemSpacing);

        var totalHeight = 0d;
        var index = 0;
        var line = 0;
        while (index < Children.Count)
        {
            var count = Math.Min(columns, Children.Count - index);
            var itemWidth = CalculateItemWidth(availableWidth, count, itemSpacing);
            var lineHeight = 0d;

            for (var column = 0; column < count; column++)
            {
                var child = Children[index + column];
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
            }

            if (line > 0)
            {
                totalHeight += lineSpacing;
            }

            totalHeight += lineHeight;
            index += count;
            line++;
        }

        return new Size(availableWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }

        var itemSpacing = Math.Max(0, ItemSpacing);
        var lineSpacing = Math.Max(0, LineSpacing);
        var minItemWidth = Math.Max(1, MinItemWidth);
        var availableWidth = Math.Max(minItemWidth, finalSize.Width);
        var columns = CalculateColumnCount(availableWidth, minItemWidth, itemSpacing);

        var y = 0d;
        var index = 0;
        while (index < Children.Count)
        {
            var count = Math.Min(columns, Children.Count - index);
            var itemWidth = CalculateItemWidth(availableWidth, count, itemSpacing);
            var lineHeight = 0d;

            for (var column = 0; column < count; column++)
            {
                lineHeight = Math.Max(lineHeight, Children[index + column].DesiredSize.Height);
            }

            for (var column = 0; column < count; column++)
            {
                var x = column * (itemWidth + itemSpacing);
                Children[index + column].Arrange(new Rect(x, y, itemWidth, lineHeight));
            }

            y += lineHeight + lineSpacing;
            index += count;
        }

        return finalSize;
    }

    private int CalculateColumnCount(double availableWidth, double minItemWidth, double itemSpacing)
    {
        var count = (int)Math.Floor((availableWidth + itemSpacing) / (minItemWidth + itemSpacing));
        return Math.Clamp(count, 1, Children.Count);
    }

    private static double CalculateItemWidth(double availableWidth, int count, double itemSpacing)
    {
        return Math.Max(0, (availableWidth - (count - 1) * itemSpacing) / count);
    }
}

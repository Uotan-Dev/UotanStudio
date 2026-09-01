using Avalonia;
using Avalonia.Controls;

namespace UotanStudio.Views.Controls;

/// <summary>
/// <para>按子项的最小宽度和实际内容宽度自动换行，并让每一行填满可用宽度。</para>
/// Wraps children according to both their minimum and content widths, then justifies every line.
/// </summary>
public sealed class JustifiedWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(MinItemWidth), 110d);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(ItemSpacing), 6d);

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<JustifiedWrapPanel, double>(nameof(LineSpacing), 6d);

    private readonly List<RowLayout> _rows = [];
    private double[] _requiredWidths = [];

    static JustifiedWrapPanel()
    {
        AffectsMeasure<JustifiedWrapPanel>(MinItemWidthProperty, ItemSpacingProperty, LineSpacingProperty);
        AffectsArrange<JustifiedWrapPanel>(MinItemWidthProperty, ItemSpacingProperty, LineSpacingProperty);
    }

    /// <summary>
    /// <para>获取或设置每个子项的基础最小宽度；内容需要更宽时以内容宽度为准。</para>
    /// Gets or sets the base minimum width; wider content raises the effective minimum.
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
        _rows.Clear();

        if (Children.Count == 0)
        {
            return default;
        }

        var itemSpacing = Math.Max(0, ItemSpacing);
        var lineSpacing = Math.Max(0, LineSpacing);
        var minItemWidth = Math.Max(1, MinItemWidth);
        EnsureRequiredWidthCapacity();

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _requiredWidths[index] = Math.Max(minItemWidth, child.DesiredSize.Width);
        }

        if (!double.IsFinite(availableSize.Width))
        {
            var widths = _requiredWidths[..Children.Count];
            var rowHeight = Children.Max(child => child.DesiredSize.Height);
            _rows.Add(new RowLayout(0, widths, rowHeight));
            return new Size(widths.Sum() + itemSpacing * Math.Max(0, widths.Length - 1), rowHeight);
        }

        var availableWidth = Math.Max(0, availableSize.Width);
        var totalHeight = BuildMeasuredRows(availableWidth, itemSpacing, lineSpacing);
        return new Size(availableWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemSpacing = Math.Max(0, ItemSpacing);
        var lineSpacing = Math.Max(0, LineSpacing);
        var y = 0d;

        foreach (var row in _rows)
        {
            var x = 0d;
            for (var column = 0; column < row.Widths.Length; column++)
            {
                Children[row.StartIndex + column].Arrange(new Rect(x, y, row.Widths[column], row.Height));
                x += row.Widths[column] + itemSpacing;
            }

            y += row.Height + lineSpacing;
        }

        return finalSize;
    }

    private double BuildMeasuredRows(double availableWidth, double itemSpacing, double lineSpacing)
    {
        var totalHeight = 0d;
        var rowStart = 0;
        var rowWidth = 0d;

        for (var index = 0; index < Children.Count; index++)
        {
            var requiredWidth = _requiredWidths[index];
            var proposedWidth = index == rowStart
                ? requiredWidth
                : rowWidth + itemSpacing + requiredWidth;

            if (index > rowStart && proposedWidth > availableWidth)
            {
                totalHeight = AddMeasuredRow(
                    rowStart,
                    index - rowStart,
                    availableWidth,
                    itemSpacing,
                    lineSpacing,
                    totalHeight);
                rowStart = index;
                rowWidth = requiredWidth;
            }
            else
            {
                rowWidth = proposedWidth;
            }
        }

        return AddMeasuredRow(
            rowStart,
            Children.Count - rowStart,
            availableWidth,
            itemSpacing,
            lineSpacing,
            totalHeight);
    }

    private double AddMeasuredRow(
        int startIndex,
        int count,
        double availableWidth,
        double itemSpacing,
        double lineSpacing,
        double totalHeight)
    {
        var widths = new double[count];
        var gapWidth = itemSpacing * Math.Max(0, count - 1);
        var contentWidth = Math.Max(0, availableWidth - gapWidth);
        var requiredWidth = 0d;

        for (var column = 0; column < count; column++)
        {
            requiredWidth += _requiredWidths[startIndex + column];
        }

        if (requiredWidth <= contentWidth)
        {
            var extraWidth = (contentWidth - requiredWidth) / count;
            for (var column = 0; column < count; column++)
            {
                widths[column] = _requiredWidths[startIndex + column] + extraWidth;
            }
        }
        else if (count == 1)
        {
            widths[0] = contentWidth;
        }
        else
        {
            var scale = requiredWidth > 0 ? contentWidth / requiredWidth : 0;
            for (var column = 0; column < count; column++)
            {
                widths[column] = _requiredWidths[startIndex + column] * scale;
            }
        }

        var rowHeight = 0d;
        for (var column = 0; column < count; column++)
        {
            var child = Children[startIndex + column];
            child.Measure(new Size(widths[column], double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        if (_rows.Count > 0)
        {
            totalHeight += lineSpacing;
        }

        _rows.Add(new RowLayout(startIndex, widths, rowHeight));
        return totalHeight + rowHeight;
    }

    private void EnsureRequiredWidthCapacity()
    {
        if (_requiredWidths.Length != Children.Count)
        {
            _requiredWidths = new double[Children.Count];
        }
    }

    private sealed record RowLayout(int StartIndex, double[] Widths, double Height);
}

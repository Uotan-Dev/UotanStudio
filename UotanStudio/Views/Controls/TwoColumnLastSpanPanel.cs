using Avalonia;
using Avalonia.Controls;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Arranges children into two equal-width columns per row and stretches the last child across the full row when the
/// visible item count is odd.
/// <para>将子项按每行等宽两列排布；当可见子项数量为奇数时，最后一项自动占据整行。</para>
/// </summary>
public sealed partial class TwoColumnLastSpanPanel : Panel
{
    private readonly List<Control> _visibleChildren = [];

    static TwoColumnLastSpanPanel()
    {
        AffectsMeasure<TwoColumnLastSpanPanel>(ColumnSpacingProperty, RowSpacingProperty);
        AffectsArrange<TwoColumnLastSpanPanel>(ColumnSpacingProperty, RowSpacingProperty);
    }

    /// <summary>
    /// Gets or sets the horizontal spacing between the two columns.
    /// <para>获取或设置两列之间的水平间距。</para>
    /// </summary>
    [GeneratedStyledProperty(DefaultValue = 10d)]
    public partial double ColumnSpacing { get; set; }

    /// <summary>
    /// Gets or sets the vertical spacing between adjacent rows.
    /// <para>获取或设置相邻行之间的垂直间距。</para>
    /// </summary>
    [GeneratedStyledProperty(DefaultValue = 10d)]
    public partial double RowSpacing { get; set; }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _visibleChildren.Clear();
        foreach (var child in Children)
        {
            if (child.IsVisible)
            {
                _visibleChildren.Add(child);
            }
        }

        var count = _visibleChildren.Count;
        if (count == 0)
        {
            return default;
        }

        var hasFiniteWidth = !double.IsInfinity(availableSize.Width);
        var colSpacing = Math.Max(0, ColumnSpacing);
        var rowSpacing = Math.Max(0, RowSpacing);
        var columnWidth = hasFiniteWidth
            ? Math.Max(0, (availableSize.Width - colSpacing) / 2)
            : double.PositiveInfinity;

        var totalHeight = 0d;
        var maxRowWidth = 0d;
        var pairCount = count / 2;

        for (var i = 0; i < pairCount * 2; i += 2)
        {
            var left = _visibleChildren[i];
            var right = _visibleChildren[i + 1];
            left.Measure(new Size(columnWidth, double.PositiveInfinity));
            right.Measure(new Size(columnWidth, double.PositiveInfinity));

            var rowHeight = Math.Max(left.DesiredSize.Height, right.DesiredSize.Height);
            if (i > 0)
            {
                totalHeight += rowSpacing;
            }

            totalHeight += rowHeight;
            maxRowWidth = Math.Max(maxRowWidth, left.DesiredSize.Width + colSpacing + right.DesiredSize.Width);
        }

        if (count % 2 == 1)
        {
            var last = _visibleChildren[count - 1];
            last.Measure(new Size(hasFiniteWidth ? availableSize.Width : double.PositiveInfinity, double.PositiveInfinity));
            if (pairCount > 0)
            {
                totalHeight += rowSpacing;
            }

            totalHeight += last.DesiredSize.Height;
            maxRowWidth = Math.Max(maxRowWidth, last.DesiredSize.Width);
        }

        return new Size(hasFiniteWidth ? availableSize.Width : maxRowWidth, totalHeight);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = _visibleChildren.Count;
        if (count == 0)
        {
            return finalSize;
        }

        var colSpacing = Math.Max(0, ColumnSpacing);
        var rowSpacing = Math.Max(0, RowSpacing);
        var columnWidth = Math.Max(0, (finalSize.Width - colSpacing) / 2);
        var y = 0d;
        var pairCount = count / 2;

        for (var i = 0; i < pairCount * 2; i += 2)
        {
            var left = _visibleChildren[i];
            var right = _visibleChildren[i + 1];
            var rowHeight = Math.Max(left.DesiredSize.Height, right.DesiredSize.Height);

            left.Arrange(new Rect(0, y, columnWidth, rowHeight));
            right.Arrange(new Rect(columnWidth + colSpacing, y, columnWidth, rowHeight));

            y += rowHeight + rowSpacing;
        }

        if (count % 2 == 1)
        {
            var last = _visibleChildren[count - 1];
            last.Arrange(new Rect(0, y, finalSize.Width, last.DesiredSize.Height));
        }

        return finalSize;
    }
}
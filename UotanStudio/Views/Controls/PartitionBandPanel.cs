using Avalonia;
using Avalonia.Controls;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Band segment geometry contract: the visual partition band lays children out sequentially by width
/// ratio, with gap children allowed to shrink smaller than real segments.
/// <para>分区段几何契约：可视化分区段布局按各段的宽度占比顺序排布，间隙段允许收缩到更小的宽度。</para>
/// </summary>
public interface IBandSegmentLayout
{
    /// <summary>
    /// The fraction of the available width this segment occupies (0..1).
    /// <para>段宽度占可用总宽的比例（0..1）。</para>
    /// </summary>
    double BandWidthRatio { get; }

    /// <summary>
    /// Whether this segment is an unallocated gap.
    /// <para>是否为未分配间隙段。</para>
    /// </summary>
    bool BandIsGap { get; }
}

/// <summary>
/// A GNOME Disks style visual partition-band panel: children receive widths in order of their
/// <see cref="IBandSegmentLayout.BandWidthRatio"/>; real segments keep a minimum width and everything
/// is scaled down proportionally (with clipped labels) when the space is insufficient.
/// <para>GNOME Disks 风格的可视化分区段面板：子项按 <see cref="IBandSegmentLayout.BandWidthRatio"/>
/// 顺序分配宽度；非间隙段保证最小宽度，空间不足时整体等比压缩并裁剪文本。</para>
/// </summary>
public sealed class PartitionBandPanel : Panel
{
    /// <summary>
    /// Spacing between adjacent segments.
    /// <para>相邻段之间的间距。</para>
    /// </summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<PartitionBandPanel, double>(nameof(Spacing), 2);

    /// <summary>
    /// Minimum width of a real (non-gap) segment.
    /// <para>非间隙段的最小宽度。</para>
    /// </summary>
    public static readonly StyledProperty<double> MinSegmentWidthProperty =
        AvaloniaProperty.Register<PartitionBandPanel, double>(nameof(MinSegmentWidth), 26);

    /// <summary>
    /// Minimum width of a gap segment.
    /// <para>间隙段的最小宽度。</para>
    /// </summary>
    public static readonly StyledProperty<double> MinGapWidthProperty =
        AvaloniaProperty.Register<PartitionBandPanel, double>(nameof(MinGapWidth), 4);

    static PartitionBandPanel()
    {
        AffectsMeasure<PartitionBandPanel>(SpacingProperty);
        AffectsMeasure<PartitionBandPanel>(MinSegmentWidthProperty);
        AffectsMeasure<PartitionBandPanel>(MinGapWidthProperty);
    }

    /// <summary>
    /// Gets or sets the spacing between adjacent segments.
    /// <para>获取或设置相邻段之间的间距。</para>
    /// </summary>
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>
    /// Gets or sets the minimum width of a real (non-gap) segment.
    /// <para>获取或设置非间隙段的最小宽度。</para>
    /// </summary>
    public double MinSegmentWidth { get => GetValue(MinSegmentWidthProperty); set => SetValue(MinSegmentWidthProperty, value); }

    /// <summary>
    /// Gets or sets the minimum width of a gap segment.
    /// <para>获取或设置间隙段的最小宽度。</para>
    /// </summary>
    public double MinGapWidth { get => GetValue(MinGapWidthProperty); set => SetValue(MinGapWidthProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = ConstraintWidth(availableSize.Width);
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : double.PositiveInfinity;
        var widths = ComputeWidths(width);
        var desiredHeight = 0.0;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(widths[i], height));
            desiredHeight = Math.Max(desiredHeight, Children[i].DesiredSize.Height);
        }

        var total = Sum(widths) + Spacing * Math.Max(0, VisibleCount(widths) - 1);
        return new Size(total, double.IsFinite(desiredHeight) ? desiredHeight : 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = ComputeWidths(finalSize.Width);
        var x = 0.0;
        var placed = false;
        for (var i = 0; i < Children.Count; i++)
        {
            var width = widths[i];
            if (width <= 0)
            {
                continue;
            }

            if (placed)
            {
                x += Spacing;
            }

            placed = true;
            Children[i].Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }

    private double ConstraintWidth(double available)
    {
        if (double.IsFinite(available))
        {
            return Math.Max(0, available);
        }

        var total = 0.0;
        foreach (var child in Children)
        {
            total += child.DataContext is IBandSegmentLayout { BandIsGap: true } ? MinGapWidth : MinSegmentWidth;
        }

        return total + Spacing * Math.Max(0, Children.Count - 1);
    }

    private double[] ComputeWidths(double available)
    {
        var count = Children.Count;
        if (count == 0)
        {
            return [];
        }

        var widths = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (Children[i].DataContext is not IBandSegmentLayout layout)
            {
                widths[i] = MinSegmentWidth;
                continue;
            }

            var raw = Math.Clamp(layout.BandWidthRatio, 0, 1) * available;
            widths[i] = layout.BandIsGap
                ? layout.BandWidthRatio > 0 ? Math.Max(raw, MinGapWidth) : 0
                : Math.Max(raw, MinSegmentWidth);
        }

        var budget = Math.Max(0, available - Spacing * Math.Max(0, VisibleCount(widths) - 1));
        var sum = Sum(widths);
        if (sum > budget && sum > 0)
        {
            var scale = budget / sum;
            for (var i = 0; i < count; i++)
            {
                widths[i] *= scale;
            }
        }

        return widths;
    }

    private static int VisibleCount(double[] widths)
    {
        var count = 0;
        foreach (var width in widths)
        {
            if (width > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static double Sum(double[] widths)
    {
        var total = 0.0;
        foreach (var value in widths)
        {
            total += value;
        }

        return total;
    }
}
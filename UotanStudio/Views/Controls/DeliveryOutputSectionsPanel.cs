using Avalonia;
using Avalonia.Controls;

namespace UotanStudio.Views.Controls;

/// <summary><para>按内容排列输出镜像与脚本设置，并将可滚动内容限制在可用高度内。</para>
/// Arranges output images and script settings at their content heights, bounded by available space.</summary>
public sealed partial class DeliveryOutputSectionsPanel : Panel
{
    static DeliveryOutputSectionsPanel() => AffectsMeasure<DeliveryOutputSectionsPanel>(SpacingProperty);

    /// <summary>Gets or sets the spacing between sections. <para>获取或设置两部分的间距。</para></summary>
    [GeneratedStyledProperty(DefaultValue = 10d)]
    public partial double Spacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return base.MeasureOverride(availableSize);
        var images = Children[0];
        var scripts = Children[1];
        var height = Math.Max(0, availableSize.Height - Spacing);

        scripts.Measure(new Size(availableSize.Width, height / 2));
        images.Measure(new Size(availableSize.Width, Math.Max(0, height - scripts.DesiredSize.Height)));
        scripts.Measure(new Size(availableSize.Width, Math.Max(0, height - images.DesiredSize.Height)));
        return new Size(Math.Max(images.DesiredSize.Width, scripts.DesiredSize.Width),
            images.DesiredSize.Height + Spacing + scripts.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return base.ArrangeOverride(finalSize);
        var images = Children[0];
        var scripts = Children[1];
        var height = Math.Max(0, finalSize.Height - Spacing);
        var imagesHeight = Math.Min(images.DesiredSize.Height, Math.Max(0, height - scripts.DesiredSize.Height));
        images.Arrange(new Rect(0, 0, finalSize.Width, imagesHeight));
        scripts.Arrange(new Rect(0, imagesHeight + Spacing, finalSize.Width,
            Math.Min(scripts.DesiredSize.Height, Math.Max(0, height - imagesHeight))));
        return finalSize;
    }
}
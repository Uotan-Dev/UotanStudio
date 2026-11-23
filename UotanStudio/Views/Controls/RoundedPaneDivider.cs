using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UotanStudio.Views.Controls;

/// <summary>A pane edge with rounded returns and a shadow on one side. <para>带圆角端部和单侧投影的面板分隔边缘。</para></summary>
public sealed partial class RoundedPaneDivider : Control
{
    private const double EdgeThickness = 0.5;
    private const double CornerSize = 6;
    private const double ShadowStep = 0.75;
    private static readonly Pen[] ShadowPens =
    [
        new(0x1E000000u, ShadowStep), new(0x12000000u, ShadowStep),
        new(0x08000000u, ShadowStep), new(0x02000000u, ShadowStep)
    ];

    /// <summary>The divider brush. <para>分隔线画刷。</para></summary>
    [GeneratedStyledProperty]
    public partial IBrush? EdgeBrush { get; set; }

    /// <summary>Whether to draw a vertical edge. <para>是否绘制竖向边缘。</para></summary>
    [GeneratedStyledProperty(DefaultValue = true)]
    public partial bool IsVertical { get; set; }

    /// <summary>Whether the horizontal edge casts its shadow upward. <para>横向边缘是否向上投影。</para></summary>
    [GeneratedStyledProperty]
    public partial bool ShadowUp { get; set; }

    /// <summary>Whether the vertical edge casts its shadow leftward. <para>竖向边缘是否向左投影。</para></summary>
    [GeneratedStyledProperty]
    public partial bool ShadowLeft { get; set; }

    static RoundedPaneDivider() =>
        AffectsRender<RoundedPaneDivider>(EdgeBrushProperty, IsVerticalProperty, ShadowUpProperty, ShadowLeftProperty);

    /// <summary>Draws the rounded edge and its 3 DIP fading shadow on one side. <para>绘制圆角边缘及其 3 DIP 单侧渐隐投影。</para></summary>
    public override void Render(DrawingContext context)
    {
        var length = IsVertical ? Bounds.Height : Bounds.Width;
        if (length <= EdgeThickness || EdgeBrush is null) return;
        var first = EdgeThickness / 2;
        var last = length - first;
        var radius = Math.Min(CornerSize, (last - first) / 2);
        var axis = (IsVertical ? Bounds.Width : Bounds.Height) / 2;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var arc = new Size(radius, radius);
            if (IsVertical)
            {
                var returnOffset = ShadowLeft ? radius : -radius;
                var sweep = ShadowLeft ? SweepDirection.CounterClockwise : SweepDirection.Clockwise;
                path.BeginFigure(new Point(axis + returnOffset, first), false);
                path.ArcTo(new Point(axis, first + radius), arc, 0, false, sweep);
                path.LineTo(new Point(axis, last - radius));
                path.ArcTo(new Point(axis + returnOffset, last), arc, 0, false, sweep);
            }
            else
            {
                var returnOffset = ShadowUp ? radius : -radius;
                var sweep = ShadowUp ? SweepDirection.Clockwise : SweepDirection.CounterClockwise;
                path.BeginFigure(new Point(first, axis + returnOffset), false);
                path.ArcTo(new Point(first + radius, axis), arc, 0, false, sweep);
                path.LineTo(new Point(last - radius, axis));
                path.ArcTo(new Point(last, axis + returnOffset), arc, 0, false, sweep);
            }
            path.EndFigure(false);
        }

        for (var i = 0; i < ShadowPens.Length; i++)
        {
            var offset = EdgeThickness / 2 + (i + 0.5) * ShadowStep;
            var translation = IsVertical ? Matrix.CreateTranslation(ShadowLeft ? -offset : offset, 0)
                : Matrix.CreateTranslation(0, ShadowUp ? -offset : offset);
            using (context.PushTransform(translation))
                context.DrawGeometry(null, ShadowPens[i], geometry);
        }
        context.DrawGeometry(null, new Pen(EdgeBrush, EdgeThickness), geometry);
    }
}
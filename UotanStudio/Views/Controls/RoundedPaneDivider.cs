using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UotanStudio.Views.Controls;

/// <summary><para>带圆角端部和单侧投影的面板分隔边缘。</para>A pane edge with rounded returns and a shadow on one side.</summary>
public sealed class RoundedPaneDivider : Control
{
    private const double EdgeThickness = 0.5;
    private const double CornerSize = 6;
    private const double ShadowStep = 0.75;
    private static readonly Pen[] ShadowPens =
    [
        new(0x1E000000u, ShadowStep), new(0x12000000u, ShadowStep),
        new(0x08000000u, ShadowStep), new(0x02000000u, ShadowStep)
    ];

    /// <summary><para>分隔线画刷属性。</para>The divider brush property.</summary>
    public static readonly StyledProperty<IBrush?> EdgeBrushProperty =
        AvaloniaProperty.Register<RoundedPaneDivider, IBrush?>(nameof(EdgeBrush));
    /// <summary><para>是否为竖向边缘的属性。</para>The vertical-edge property.</summary>
    public static readonly StyledProperty<bool> IsVerticalProperty =
        AvaloniaProperty.Register<RoundedPaneDivider, bool>(nameof(IsVertical), true);
    /// <summary><para>横向边缘是否向上投影的属性。</para>Whether a horizontal edge casts its shadow upward.</summary>
    public static readonly StyledProperty<bool> ShadowUpProperty =
        AvaloniaProperty.Register<RoundedPaneDivider, bool>(nameof(ShadowUp));

    /// <summary><para>分隔线画刷。</para>The divider brush.</summary>
    public IBrush? EdgeBrush { get => GetValue(EdgeBrushProperty); set => SetValue(EdgeBrushProperty, value); }
    /// <summary><para>是否绘制竖向边缘。</para>Whether to draw a vertical edge.</summary>
    public bool IsVertical { get => GetValue(IsVerticalProperty); set => SetValue(IsVerticalProperty, value); }
    /// <summary><para>横向边缘是否向上投影。</para>Whether the horizontal edge casts its shadow upward.</summary>
    public bool ShadowUp { get => GetValue(ShadowUpProperty); set => SetValue(ShadowUpProperty, value); }

    static RoundedPaneDivider() =>
        AffectsRender<RoundedPaneDivider>(EdgeBrushProperty, IsVerticalProperty, ShadowUpProperty);

    /// <summary><para>绘制圆角边缘及其 3 DIP 单侧渐隐投影。</para>Draws the rounded edge and its 3 DIP fading shadow on one side.</summary>
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
                path.BeginFigure(new Point(axis - radius, first), false);
                path.ArcTo(new Point(axis, first + radius), arc, 0, false, SweepDirection.Clockwise);
                path.LineTo(new Point(axis, last - radius));
                path.ArcTo(new Point(axis - radius, last), arc, 0, false, SweepDirection.Clockwise);
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

        // 投影沿同一弧线朝接收侧平移，限制在边缘外 3 DIP 内。
        // Translate the same curve toward the receiving pane, staying within 3 DIP of its edge.
        for (var i = 0; i < ShadowPens.Length; i++)
        {
            var offset = EdgeThickness / 2 + (i + 0.5) * ShadowStep;
            var translation = IsVertical ? Matrix.CreateTranslation(offset, 0)
                : Matrix.CreateTranslation(0, ShadowUp ? -offset : offset);
            using (context.PushTransform(translation))
                context.DrawGeometry(null, ShadowPens[i], geometry);
        }
        context.DrawGeometry(null, new Pen(EdgeBrush, EdgeThickness), geometry);
    }
}

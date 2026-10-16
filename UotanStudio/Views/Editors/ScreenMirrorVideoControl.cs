using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// Renders YUV planes through Skia's active GPU backend, with the existing bitmap fallback.
/// <para>经 Skia 当前 GPU 后端渲染 YUV 平面，并保留既有位图回退。</para>
/// </summary>
public sealed class ScreenMirrorVideoControl : Control
{
    /// <summary>
    /// Attaches the hosted composition surface for the bound presenter when this control enters the visual tree.
    /// <para>当该控件进入可视树时，为绑定的呈现器挂载托管的合成表面。</para>
    /// </summary>
    /// <param name="e">
    /// The visual-tree attachment event data.
    /// <para>可视树挂载事件数据。</para>
    /// </param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ScreenMirrorViewModel model) _ = model.GpuPresenter.AttachCompositionVisualAsync(this);
        InvalidateVisual();
    }

    /// <summary>
    /// Detaches the hosted composition surface for the bound presenter when this control leaves the visual tree.
    /// <para>当该控件离开可视树时，为绑定的呈现器分离托管的合成表面。</para>
    /// </summary>
    /// <param name="e">
    /// The visual-tree detachment event data.
    /// <para>可视树分离事件数据。</para>
    /// </param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is ScreenMirrorViewModel model) model.GpuPresenter.DetachCompositionVisual();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Re-letterboxes the composition surface when the bounds of this control change.
    /// <para>当该控件的边界变化时重新按信箱模式布局合成表面。</para>
    /// </summary>
    /// <param name="change">
    /// The property-change event data.
    /// <para>属性变更事件数据。</para>
    /// </param>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && DataContext is ScreenMirrorViewModel model)
            model.GpuPresenter.UpdateCompositionLayout();
    }

    /// <summary>
    /// Draws the latest bitmap frame when no GPU path is active and registers the custom video draw operation.
    /// <para>无 GPU 路径激活时绘制最新位图帧，并注册自定义视频绘制操作。</para>
    /// </summary>
    /// <param name="context">
    /// The drawing context receiving the frame.
    /// <para>接收该帧的绘制上下文。</para>
    /// </param>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not ScreenMirrorViewModel model) return;
        var bounds = new Rect(Bounds.Size);
        if (!model.GpuPresenter.UsesCompositionSurface && !model.GpuPresenter.IsAvailable && model.Frame is { } bitmap)
        {
            var size = bitmap.Size;
            var scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
            var destination = new Rect((bounds.Width - size.Width * scale) / 2, (bounds.Height - size.Height * scale) / 2, size.Width * scale, size.Height * scale);
            context.DrawImage(bitmap, new Rect(size), destination);
        }
        context.Custom(model.GpuPresenter.CreateOperation(bounds));
    }
}
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>Renders YUV planes through Skia's active GPU backend, with the existing bitmap fallback.</summary>
/// <summary>经 Skia 当前 GPU 后端渲染 YUV 平面，并保留既有位图回退。</summary>
/// <remarks>
/// <para>
/// While composition-owned frames cover the control (GPU zero-copy export), the registered draw
/// operation stays in the scene so the render-thread recycle queue keeps draining, but it draws nothing;
/// the picture itself is the hosted composition surface visual.
/// </para>
/// <para>
/// 当合成器持有的帧覆盖控件时（GPU 零拷贝导出），已注册的绘制操作仍留在场景中以持续排空渲染线程回收队列，
/// 但不再绘制任何内容；画面本身由托管的合成表面视觉呈现。
/// </para>
/// </remarks>
public sealed class ScreenMirrorVideoControl : Control
{
    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ScreenMirrorViewModel model) _ = model.GpuPresenter.AttachCompositionVisualAsync(this);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is ScreenMirrorViewModel model) model.GpuPresenter.DetachCompositionVisual();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && DataContext is ScreenMirrorViewModel model)
            model.GpuPresenter.UpdateCompositionLayout();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not ScreenMirrorViewModel model) return;
        var bounds = new Rect(Bounds.Size);
        if (!model.GpuPresenter.UsesCompositionSurface && !model.GpuPresenter.IsAvailable && model.Frame is { } bitmap)
        {
            var size = bitmap.Size;
            double scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
            var destination = new Rect((bounds.Width - size.Width * scale) / 2, (bounds.Height - size.Height * scale) / 2, size.Width * scale, size.Height * scale);
            context.DrawImage(bitmap, new Rect(size), destination);
        }
        context.Custom(model.GpuPresenter.CreateOperation(bounds));
    }
}
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
    private ScreenMirrorViewModel? _boundModel;

    /// <summary>
    /// Creates the video control and attaches the composition surface when its view-model arrives
    /// after the visual tree attachment, as happens when a document view is rebuilt for a language
    /// or theme change.
    /// <para>创建视频控件；当视图模型在可视树挂载之后才到达时（例如语言或主题切换重建文档视图），
    /// 同样挂载合成表面。</para>
    /// </summary>
    public ScreenMirrorVideoControl()
    {
        DataContextChanged += (_, _) => BindPresentationHost();
    }

    /// <summary>
    /// Attaches the hosted composition surface for the bound presenter when this control enters the visual tree;
    /// a reattach after a dock float or drag reuses the surface when the same control comes back quickly.
    /// <para>当该控件进入可视树时，为绑定的呈现器挂载托管的合成表面；停靠浮动或拖拽后同一控件快速
    /// 回到可视树时会复用原表面。</para>
    /// </summary>
    /// <param name="e">
    /// The visual-tree attachment event data.
    /// <para>可视树挂载事件数据。</para>
    /// </param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindPresentationHost();
        InvalidateVisual();
    }

    /// <summary>
    /// Defers the hosted composition surface release when this control leaves the visual tree; a real close still
    /// releases it after the presenter's short debounce.
    /// <para>当该控件离开可视树时延迟释放托管的合成表面；真正关闭时呈现器仍会在短暂去抖后释放它。</para>
    /// </summary>
    /// <param name="e">
    /// The visual-tree detachment event data.
    /// <para>可视树分离事件数据。</para>
    /// </param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _boundModel?.GpuPresenter.ScheduleDetachCompositionVisual(this);
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
        if (change.Property == BoundsProperty && _boundModel is { } model)
            model.GpuPresenter.UpdateCompositionLayout();
    }

    /// <summary>
    /// Draws the latest bitmap frame until composition frames cover the video area and registers the custom
    /// video draw operation; a rebuilt surface stays transparent behind the bitmap until its first frame.
    /// <para>在合成帧覆盖视频区域之前绘制最新位图帧，并注册自定义视频绘制操作；重建后的表面在首帧到达
    /// 前保持透明，位图回退始终可见。</para>
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
        var presenter = model.GpuPresenter;
        var compositionCovering = presenter.UsesCompositionSurface && presenter.CompositionFramePresented;
        if (!compositionCovering && model.Frame is { } bitmap)
        {
            var size = bitmap.Size;
            var scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
            var destination = new Rect((bounds.Width - size.Width * scale) / 2, (bounds.Height - size.Height * scale) / 2, size.Width * scale, size.Height * scale);
            context.DrawImage(bitmap, new Rect(size), destination);
            return;
        }

        context.Custom(presenter.CreateOperation(bounds));
    }

    private void BindPresentationHost()
    {
        var next = DataContext as ScreenMirrorViewModel;
        if (!ReferenceEquals(next, _boundModel))
        {
            var previous = _boundModel;
            _boundModel = next;
            previous?.GpuPresenter.DetachCompositionVisual(this);
        }

        if (next is null || VisualRoot is null) return;
        _ = next.GpuPresenter.AttachCompositionVisualAsync(this);
        InvalidateVisual();
    }
}
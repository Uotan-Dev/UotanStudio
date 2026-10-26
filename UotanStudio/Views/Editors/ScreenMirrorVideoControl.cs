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
    private ScreenMirrorGpuPresenter? _boundPresenter;
    private WindowBase? _hookedTopLevel;
    private Window? _hookedWindow;
    private int _renderLogged;

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
    /// Attaches the hosted composition surface for the bound presenter when this control enters the visual tree and
    /// re-establishes presentation, because a dock document tab switch detaches and reattaches the cached view.
    /// <para>当该控件进入可视树时为绑定的呈现器挂载托管的合成表面并重建呈现，因为停靠文档切换标签会先分离再
    /// 重新挂载缓存视图。</para>
    /// </summary>
    /// <param name="e">
    /// The visual-tree attachment event data.
    /// <para>可视树挂载事件数据。</para>
    /// </param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MirrorTrace.Write($"control attach bound={Describe(_boundModel)} same={ReferenceEquals(DataContext, _boundModel)} presenter={Describe(_boundPresenter)} root={VisualRoot is not null}");
        BindPresentationHost();
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
        MirrorTrace.Write($"control detach bound={Describe(_boundModel)} presenter={Describe(_boundPresenter)}");
        UnhookTopLevel();
        _boundPresenter?.ScheduleDetachCompositionVisual(this);
        base.OnDetachedFromVisualTree(e);
    }

    private static string Describe(ScreenMirrorViewModel? model)
        => model is null ? "null" : model.GetHashCode().ToString("x8");

    private static string Describe(ScreenMirrorGpuPresenter? presenter)
        => presenter is null ? "null" : presenter.GetHashCode().ToString("x8");

    /// <summary>
    /// Re-letterboxes the composition surface when the bounds of this control change and re-establishes presentation
    /// when the control is shown again.
    /// <para>当该控件的边界变化时重新按信箱模式布局合成表面；控件重新可见时重建呈现。</para>
    /// </summary>
    /// <param name="change">
    /// The property-change event data.
    /// <para>属性变更事件数据。</para>
    /// </param>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            _boundModel?.GpuPresenter.UpdateCompositionLayout();
            return;
        }

        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            MirrorTrace.Write("control visible");
            RecoverPresentation();
        }
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
        var compositionCovering = presenter.CompositionCoversVideo;
        if (_renderLogged++ % 120 == 0)
            MirrorTrace.Write($"render covering={compositionCovering} frame={model.Frame is not null} bounds={bounds.Width:F0}x{bounds.Height:F0}");
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

    /// <summary>
    /// Rebinds the hosted composition surface when the view model or its presenter instance changed; the dock calls
    /// this through the document view on every presented frame, and applying mirror settings replaces the presenter.
    /// <para>视图模型或其呈现器实例变化时重新绑定托管的合成表面；停靠文档视图在每次呈现帧时调用，应用投屏设置
    /// 会替换呈现器。</para>
    /// </summary>
    internal void EnsurePresentationHost()
    {
        if (!ReferenceEquals(DataContext as ScreenMirrorViewModel, _boundModel)
            || !ReferenceEquals(_boundModel?.GpuPresenter, _boundPresenter))
        {
            BindPresentationHost();
        }
    }

    private void BindPresentationHost()
    {
        var next = DataContext as ScreenMirrorViewModel;
        var nextPresenter = next?.GpuPresenter;
        MirrorTrace.Write($"bind next={Describe(next)} presenter={Describe(nextPresenter)} bound={Describe(_boundModel)} boundPresenter={Describe(_boundPresenter)} root={VisualRoot is not null}");
        if (!ReferenceEquals(next, _boundModel))
        {
            var previous = _boundModel;
            _boundModel = next;
            previous?.GpuPresenter.DetachCompositionVisual(this);
        }
        else if (!ReferenceEquals(nextPresenter, _boundPresenter))
        {
            _boundPresenter?.DetachCompositionVisual(this);
        }

        _boundPresenter = nextPresenter;
        if (next is null || VisualRoot is null)
        {
            UnhookTopLevel();
            return;
        }

        HookTopLevel();
        _ = next.GpuPresenter.AttachCompositionVisualAsync(this);
        RecoverPresentation();
    }

    /// <summary>
    /// Re-layouts the composition surface, asks the device for a fresh key frame and redraws, so a picture that was
    /// covered or rebuilt resumes instead of staying on a stale frame.
    /// <para>重新布局合成表面、向设备请求新的关键帧并重绘，使被遮盖或重建过的画面恢复，而不是停留在旧帧。</para>
    /// </summary>
    private void RecoverPresentation()
    {
        if (_boundModel is not { } model || VisualRoot is null)
        {
            MirrorTrace.Write($"recover skipped model={Describe(_boundModel)} root={VisualRoot is not null}");
            return;
        }

        MirrorTrace.Write($"recover model={Describe(model)} covering={model.GpuPresenter.CompositionCoversVideo}");
        model.RecoverPresentation();
        InvalidateVisual();
    }

    /// <summary>
    /// Watches window activation and window state, because a covered or minimized window can come back with a
    /// composition surface whose last frame is stale and no new frame until the device screen changes.
    /// <para>监听窗口激活与窗口状态：被其它窗口遮盖或最小化后再回到前台时，合成表面可能仍停留在旧帧，而设备画面
    /// 不变就不会有新帧。</para>
    /// </summary>
    private void HookTopLevel()
    {
        var topLevel = TopLevel.GetTopLevel(this) as WindowBase;
        var window = topLevel as Window;
        if (ReferenceEquals(topLevel, _hookedTopLevel) && ReferenceEquals(window, _hookedWindow))
        {
            return;
        }

        UnhookTopLevel();
        _hookedTopLevel = topLevel;
        _hookedWindow = window;
        if (topLevel is not null)
        {
            topLevel.Activated += OnHostActivated;
        }

        if (window is not null)
        {
            window.PropertyChanged += OnHostWindowPropertyChanged;
        }
    }

    private void UnhookTopLevel()
    {
        if (_hookedTopLevel is { } topLevel)
        {
            topLevel.Activated -= OnHostActivated;
        }

        if (_hookedWindow is { } window)
        {
            window.PropertyChanged -= OnHostWindowPropertyChanged;
        }

        _hookedTopLevel = null;
        _hookedWindow = null;
    }

    private void OnHostActivated(object? sender, EventArgs e) => RecoverPresentation();

    /// <summary>
    /// Recovers the picture when the host window leaves the minimized state, which does not always raise activation
    /// first.
    /// <para>宿主窗口离开最小化状态时恢复画面；该路径不一定会先触发激活事件。</para>
    /// </summary>
    /// <param name="sender">
    /// The host window.
    /// <para>宿主窗口。</para>
    /// </param>
    /// <param name="e">
    /// The window property-change data.
    /// <para>窗口属性变更数据。</para>
    /// </param>
    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && e.GetNewValue<WindowState>() != WindowState.Minimized)
        {
            RecoverPresentation();
        }
    }
}
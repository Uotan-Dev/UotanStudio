using Avalonia.Input;
using Avalonia.Controls;
using UotanStudio.Comm.Protocols.Scrcpy;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>scrcpy 投屏文档视图：显示解码后的设备画面，并把鼠标/滚轮/键盘事件转发给设备。</para>
/// scrcpy screen-mirror document view: shows the decoded device frame and
/// forwards mouse / wheel / key events to the device.
/// </summary>
public partial class ScreenMirrorView : UserControl
{
    /// <summary>
    /// <para>构造投屏视图。</para>
    /// Creates the screen-mirror view.
    /// </summary>
    public ScreenMirrorView()
    {
        InitializeComponent();
    }

    private ScreenMirrorViewModel? ViewModel => DataContext as ScreenMirrorViewModel;

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TranslateTouch(e.GetCurrentPoint(this), out var x, out var y, out var width, out var height) && ViewModel is not null)
        {
            var buttons = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? ScrcpyConstants.ButtonLeft
                : e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed ? ScrcpyConstants.ButtonMiddle
                : e.GetCurrentPoint(this).Properties.IsRightButtonPressed ? ScrcpyConstants.ButtonRight : 0u;
            ViewModel.InjectTouch(ScrcpyConstants.ActionDown, ScrcpyConstants.PointerIdMouse, x, y, width, height, buttons);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (TranslateTouch(e.GetCurrentPoint(this), out var x, out var y, out var width, out var height)
            && ViewModel is not null
            && e.Pointer.Captured == this)
        {
            ViewModel.InjectTouch(ScrcpyConstants.ActionMove, ScrcpyConstants.PointerIdMouse, x, y, width, height, ScrcpyConstants.ButtonLeft);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (TranslateTouch(e.GetCurrentPoint(this), out var x, out var y, out var width, out var height) && ViewModel is not null)
        {
            ViewModel.InjectTouch(ScrcpyConstants.ActionUp, ScrcpyConstants.PointerIdMouse, x, y, width, height, 0);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (TranslateTouch(e.GetCurrentPoint(this), out var x, out var y, out var width, out var height) && ViewModel is not null)
        {
            // Avalonia: positive Delta.Y scrolls up, matching the wire semantics.
            // Avalonia 的正向 Delta.Y 对应向上滚动，与线路语义一致。
            ViewModel.InjectScroll(x, y, width, height, (float)e.Delta.Y);
            e.Handled = true;
        }
    }

    private bool TranslateTouch(PointerPoint point, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (ViewModel?.Frame is not { } frame)
        {
            return false;
        }

        width = frame.PixelSize.Width;
        height = frame.PixelSize.Height;
        // The Image is Stretch=Uniform and centered; convert via the Image
        // bounds relative to the rendered source rectangle.
        // Image 为 Stretch=Uniform 居中显示；按 Image 相对渲染源矩形转换。
        if (this.GetControl<Image>("FrameImage") is { } image && image.Bounds.Width > 0 && image.Bounds.Height > 0)
        {
            var scale = Math.Min(image.Bounds.Width / width, image.Bounds.Height / height);
            var offsetX = (image.Bounds.Width - width * scale) / 2;
            var offsetY = (image.Bounds.Height - height * scale) / 2;
            var local = point.Position;
            x = (int)((local.X - offsetX) / scale);
            y = (int)((local.Y - offsetY) / scale);
        }
        else
        {
            return false;
        }

        return x >= 0 && y >= 0 && x < width && y < height;
    }
}

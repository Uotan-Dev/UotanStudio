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
    private ScreenMirrorViewModel? _subscribed;
    private IPointer? _pressedPointer;
    private uint _pressedButton;
    private (int X, int Y, int Width, int Height) _lastTouch;
    /// <summary>
    /// <para>构造投屏视图。</para>
    /// Creates the screen-mirror view.
    /// </summary>
    public ScreenMirrorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SubscribeToFrames();
        AttachedToVisualTree += (_, _) => SubscribeToFrames();
        DetachedFromVisualTree += (_, _) => UnsubscribeFromFrames();
    }

    private void SubscribeToFrames()
    {
        UnsubscribeFromFrames();
        if (VisualRoot is not null && ViewModel is { } viewModel)
        {
            _subscribed = viewModel;
            viewModel.FramePresented += OnFramePresented;
        }
    }

    private void UnsubscribeFromFrames()
    {
        EndGesture(cancel: true);
        if (_subscribed is { } previous) previous.FramePresented -= OnFramePresented;
        _subscribed = null;
    }

    private void OnFramePresented(object? sender, EventArgs e)
        => FrameImage.InvalidateVisual();

    private ScreenMirrorViewModel? ViewModel => DataContext as ScreenMirrorViewModel;

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ViewModel is { } selected)
            Services.Workspace.DocumentNotifications.Publish(selected);
        if (_pressedPointer is not null || _subscribed is null) return;
        uint button = ActionButton(e.GetCurrentPoint(FrameImage).Properties.PointerUpdateKind);
        if (button == 0 || !TranslateTouch(e, false, out var point)) return;
        _lastTouch = point;
        _pressedButton = button;
        _pressedPointer = e.Pointer;
        e.Pointer.Capture(this);
        _subscribed.InjectTouch(ScrcpyConstants.ActionDown, ScrcpyConstants.PointerIdMouse,
            point.X, point.Y, point.Width, point.Height, button, button);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer != _pressedPointer || _subscribed is null) return;
        if (TranslateTouch(e, true, out var point))
        {
            if (point.Width != _lastTouch.Width || point.Height != _lastTouch.Height)
            {
                EndGesture(cancel: true);
                return;
            }
            _lastTouch = point;
            _subscribed.InjectTouch(ScrcpyConstants.ActionMove, ScrcpyConstants.PointerIdMouse,
                point.X, point.Y, point.Width, point.Height, _pressedButton);
        }
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer != _pressedPointer
            || ActionButton(e.GetCurrentPoint(FrameImage).Properties.PointerUpdateKind) != _pressedButton) return;
        if (TranslateTouch(e, true, out var point)) _lastTouch = point;
        EndGesture(cancel: false);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (e.Pointer == _pressedPointer) EndGesture(cancel: true);
    }

    private void EndGesture(bool cancel)
    {
        var pointer = _pressedPointer;
        if (pointer is null) return;
        _pressedPointer = null; // Clear first: releasing capture may synchronously raise CaptureLost.
        var point = _lastTouch;
        _subscribed?.InjectTouch(cancel ? (byte)3 : ScrcpyConstants.ActionUp, ScrcpyConstants.PointerIdMouse,
            point.X, point.Y, point.Width, point.Height, 0, cancel ? 0 : _pressedButton);
        _pressedButton = 0;
        if (pointer.Captured == this) pointer.Capture(null);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (TranslateTouch(e, false, out var point) && ViewModel is { } viewModel)
        {
            // InjectScroll accepts positive-down; Avalonia Delta.Y is positive-up.
            viewModel.InjectScroll(point.X, point.Y, point.Width, point.Height, -(float)e.Delta.Y);
            e.Handled = true;
        }
    }

    private static uint ActionButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => ScrcpyConstants.ButtonLeft,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => ScrcpyConstants.ButtonRight,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => ScrcpyConstants.ButtonMiddle,
        _ => 0,
    };

    private bool TranslateTouch(PointerEventArgs e, bool clamp, out (int X, int Y, int Width, int Height) point)
    {
        point = default;
        if (ViewModel?.Frame is not { } frame) return false;
        int width = frame.PixelSize.Width, height = frame.PixelSize.Height;
        // GetPosition applies the complete visual transform, including status row,
        // dock offsets and DPI. Both inputs below are image-local logical units.
        if (!ScreenMirrorCoordinates.TryMap(e.GetPosition(FrameImage), FrameImage.Bounds.Size,
            width, height, clamp, out int x, out int y)) return false;
        point = (x, y, width, height);
        return true;
    }
}

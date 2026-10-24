using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UotanStudio.Mirroring;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// scrcpy screen-mirror document view: shows the decoded device frame and
/// forwards mouse / wheel / key events to the device.
/// <para>scrcpy 投屏文档视图：显示解码后的设备画面，并把鼠标/滚轮/键盘事件转发给设备。</para>
/// </summary>
public partial class ScreenMirrorView : UserControl
{
    private ScreenMirrorViewModel? _subscribed;
    private IPointer? _pressedPointer;
    private uint _pressedButton;
    private (int X, int Y, int Width, int Height) _lastTouch;
    /// <summary>
    /// Creates the screen-mirror view.
    /// <para>构造投屏视图。</para>
    /// </summary>
    public ScreenMirrorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SubscribeToFrames();
        AttachedToVisualTree += (_, _) =>
        {
            SubscribeToFrames();
            ViewModel?.RecoverPresentation();
            InvalidateVisual();
        };
        DetachedFromVisualTree += (_, _) => UnsubscribeFromFrames();
    }

    private void SubscribeToFrames()
    {
        UnsubscribeFromFrames();
        if (VisualRoot is null || ViewModel is not { } viewModel) return;
        _subscribed = viewModel;
        viewModel.FramePresented += OnFramePresented;
        viewModel.DeviceClipboardReceived += OnDeviceClipboardReceived;
        OnFramePresented(viewModel, EventArgs.Empty);
    }

    private void UnsubscribeFromFrames()
    {
        EndGesture(cancel: true);
        if (_subscribed is { } previous)
        {
            previous.FramePresented -= OnFramePresented;
            previous.DeviceClipboardReceived -= OnDeviceClipboardReceived;
        }
        _subscribed = null;
    }

    private void OnDeviceClipboardReceived(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch
            {

            }
        });
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => ViewModel?.SendBack();

    private void OnHomeClick(object? sender, RoutedEventArgs e) => ViewModel?.SendHome();

    private void OnRecentsClick(object? sender, RoutedEventArgs e) => ViewModel?.SendRecents();

    private void OnVolumeDownClick(object? sender, RoutedEventArgs e) => ViewModel?.AdjustVolume(up: false);

    private void OnVolumeUpClick(object? sender, RoutedEventArgs e) => ViewModel?.AdjustVolume(up: true);

    private void OnPowerClick(object? sender, RoutedEventArgs e) => ViewModel?.ToggleDisplayPower();

    private void OnPullClipboardClick(object? sender, RoutedEventArgs e) => ViewModel?.RequestClipboard();

    private async void OnPushClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            viewModel.SendClipboard(text, paste: false);
        }
    }

    /// <summary>
    /// Maps physical key presses onto injected Android key codes; unmapped keys stay unhandled so host
    /// shortcuts keep working.
    /// <para>把物理键盘按键映射为 Android 键码注入；未知键不拦截，保留宿主快捷键语义。</para>
    /// </summary>
    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ViewModel is { } viewModel && TryMapAndroidKey(e.Key, out var keyCode))
        {
            viewModel.InjectKeyCode(ScrcpyConstants.ActionDown, keyCode, MapMetaState(e.KeyModifiers));
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (ViewModel is { } viewModel && TryMapAndroidKey(e.Key, out var keyCode))
        {
            viewModel.InjectKeyCode(ScrcpyConstants.ActionUp, keyCode, MapMetaState(e.KeyModifiers));
            e.Handled = true;
        }
    }

    /// <summary>
    /// Forwards typed text as scrcpy INJECT_TEXT instead of per-key codes so layout differences stay on the device.
    /// <para>把文本输入转发为 scrcpy INJECT_TEXT，避免逐键映射布局差异。</para>
    /// </summary>
    /// <inheritdoc />
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (ViewModel is { } viewModel && e.Text is { Length: > 0 } text)
        {
            viewModel.InjectText(text);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Maps Avalonia keys onto Android KeyEvent key codes.
    /// <para>Avalonia 按键到 Android KeyEvent 键码的映射表。</para>
    /// </summary>
    private static bool TryMapAndroidKey(Key key, out int keyCode)
    {
        (Key Key, int Code)[] table =
        [
            (Key.Escape, 111), (Key.Home, 3), (Key.Back, 67), (Key.Tab, 61), (Key.Enter, 66),
            (Key.Space, 62), (Key.Up, 19), (Key.Down, 20), (Key.Left, 21), (Key.Right, 22),
            (Key.D0, 7), (Key.D1, 8), (Key.D2, 9), (Key.D3, 10), (Key.D4, 11), (Key.D5, 12),
            (Key.D6, 13), (Key.D7, 14), (Key.D8, 15), (Key.D9, 16),
        ];
        foreach (var (candidate, code) in table)
        {
            if (key == candidate)
            {
                keyCode = code;
                return true;
            }
        }

        if (key is >= Key.A and <= Key.Z)
        {
            keyCode = 29 + (key - Key.A);
            return true;
        }

        keyCode = 0;
        return false;
    }

    private static int MapMetaState(KeyModifiers modifiers)
    {
        var meta = 0;
        if (modifiers.HasFlag(KeyModifiers.Shift)) meta |= 1;
        if (modifiers.HasFlag(KeyModifiers.Alt)) meta |= 2;
        if (modifiers.HasFlag(KeyModifiers.Control)) meta |= 4096;
        return meta;
    }

    private void OnFramePresented(object? sender, EventArgs e)
    {
        WaitingLabel.IsVisible = ViewModel?.HasPresentedVideo != true;
        FrameImage.EnsurePresentationHost();
        FrameImage.InvalidateVisual();
    }

    private ScreenMirrorViewModel? ViewModel => DataContext as ScreenMirrorViewModel;

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ViewModel is { } selected)
            Services.Ui.Workspace.DocumentNotifications.Publish(selected);
        if (_pressedPointer is not null || _subscribed is null) return;
        var button = ActionButton(e.GetCurrentPoint(FrameImage).Properties.PointerUpdateKind);
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
        _pressedPointer = null;
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
        if (ViewModel?.InjectionVideoSize is not { Width: > 0, Height: > 0 } size) return false;
        int width = size.Width, height = size.Height;

        if (!ScreenMirrorCoordinates.TryMap(e.GetPosition(FrameImage), FrameImage.Bounds.Size,
            width, height, clamp, out var x, out var y)) return false;
        point = (x, y, width, height);
        return true;
    }
}
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace UotanStudio.Views.Behaviors;

/// <summary>
/// Restores title bar dragging and double-click-to-maximise on platforms where the window manager no longer
/// handles those gestures.
/// <para>在窗口管理器不再处理这些手势的平台上，为标题栏补回拖动与双击最大化。</para>
/// </summary>
/// <remarks>
/// On Windows and macOS the system owns the title bar, so pressing it drags the window and double-clicking it
/// maximises, both for free. On Linux the decorations are drawn by Avalonia (<c>WindowDecorations=Full</c> plus
/// <c>EnableDrawnDecorations</c>), and the X11 backend converts a press on an element marked
/// <c>ElementRole=TitleBar</c> into <c>_NET_WM_MOVERESIZE_MOVE</c> before the managed pipeline sees it
/// (<c>X11Window.ScheduleInput</c>). That covers dragging, but it also means no managed code ever observes the
/// press, so the double-click gesture is lost.
/// <para>
/// Handling the press on the application's own title bar control sidesteps the interception, but then dragging
/// has to be restored as well, because the press no longer falls through to the decoration title bar. Dragging
/// cannot start on press directly: <see cref="Window.BeginMoveDrag"/> hands the pointer to the window manager,
/// which grabs it, so a second press never arrives and a double-click could not be detected. Instead the press
/// is remembered and the drag starts only once the pointer actually moves past a small threshold; a press
/// released without moving is left to the click counter, which turns a second press into a maximise.
/// </para>
/// <para>Windows 与 macOS 上标题栏归系统所有，按下即可拖动、双击即可最大化；Linux 上装饰由 Avalonia 自绘，
/// X11 后端会在事件进入托管层之前把 <c>ElementRole=TitleBar</c> 上的按下转成 <c>_NET_WM_MOVERESIZE_MOVE</c>，
/// 拖动因此可用，但应用代码收不到按下，双击手势丢失。把按下交给应用自己的标题栏控件可以绕开该拦截，
/// 代价是拖动也要自己补回。拖动不能在按下时立即启动：<see cref="Window.BeginMoveDrag"/> 会把指针交给窗口
/// 管理器并抓取，第二次按下就收不到，双击无法识别。因此这里先记住按下，等指针实际移动超过一个小阈值再
/// 开始拖动；未移动就抬起的按下交给点击计数器处理，第二次按下即触发最大化。</para>
/// </remarks>
public static class WindowTitleBarBehavior
{
    /// <summary>
    /// How far the pointer must travel before a press turns into a window drag, in device-independent pixels.
    /// Keeps a shaky double-click from being swallowed by the drag path.
    /// <para>按下后指针需移动多远才转为拖动窗口（设备无关像素），避免手抖把双击误判成拖动。</para>
    /// </summary>
    private const double DragThreshold = 5;

    private static readonly ConditionalWeakTable<Control, Tracker> Trackers = new();

    /// <summary>
    /// Enables dragging and double-click-to-maximise on <paramref name="titleBar"/> for <paramref name="window"/>.
    /// <para>为 <paramref name="window"/> 在 <paramref name="titleBar"/> 上启用拖动与双击最大化。</para>
    /// </summary>
    /// <param name="window">The window to move and maximise.<para>要移动与最大化的窗口。</para></param>
    /// <param name="titleBar">The title bar control that receives the gestures.<para>接收这些手势的标题栏控件。</para></param>
    public static void Attach(Window window, Control titleBar) =>
        Trackers.GetValue(titleBar, _ => new Tracker(window, titleBar));

    private sealed class Tracker
    {
        private readonly Window _window;
        private readonly Control _titleBar;

        // 按下但尚未转为拖动的状态，用于判断是否越过拖动阈值。
        // Press state that has not yet become a drag, used to test the drag threshold.
        private PointerPressedEventArgs? _pendingPress;
        private Point _pressOrigin;

        internal Tracker(Window window, Control titleBar)
        {
            _window = window;
            _titleBar = titleBar;

            // 使用冒泡阶段（Bubble）且不监听已处理事件：标题栏内的菜单、按钮等交互子控件优先消费
            // 点击与拖动，只有落在空白区域且未被子控件处理的事件才会触发拖动与双击最大化。
            // Use Bubble routing without handledEventsToo so interactive children (menus, buttons)
            // consume their own clicks and drags; only unhandled events on empty title bar space reach here.
            _titleBar.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
            _titleBar.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble);
            _titleBar.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble);
            _titleBar.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble);
            _titleBar.DetachedFromVisualTree += OnDetached;
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_titleBar).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // 全屏时标题栏本就隐藏，拖动与最大化都无意义。
            // The title bar is hidden in fullscreen, where neither dragging nor maximising applies.
            if (_window.WindowState == WindowState.FullScreen)
            {
                return;
            }

            if (e.ClickCount >= 2)
            {
                // 双击：仅在窗口允许缩放与最大化时切换，并丢弃待处理的按下，避免随后又触发一次拖动。
                // Double-click: toggle between normal and maximised only when permitted, dropping any
                // pending press so the gesture does not also start a drag.
                _pendingPress = null;
                if (_window.CanResize && _window.CanMaximize)
                {
                    _window.WindowState = _window.WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized;
                    e.Handled = true;
                }

                return;
            }

            // 单击：先记住，等指针移动后再决定是不是拖动。
            // Single click: remember it and decide whether it becomes a drag once the pointer moves.
            _pendingPress = e;
            _pressOrigin = e.GetPosition(_window);
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_pendingPress is not { } pressed)
            {
                return;
            }

            // 若指针已在窗口外或菜单弹层中被释放，立即丢弃状态，避免后续悬停误触发拖动。
            // Drop stale state if the button is no longer pressed (e.g. released outside or by a popup).
            if (!e.GetCurrentPoint(_titleBar).Properties.IsLeftButtonPressed)
            {
                _pendingPress = null;
                return;
            }

            var current = e.GetPosition(_window);
            if (Math.Abs(current.X - _pressOrigin.X) < DragThreshold
                && Math.Abs(current.Y - _pressOrigin.Y) < DragThreshold)
            {
                return;
            }

            // 越过阈值：把手势交给窗口管理器，由它接管后续移动。
            // Crossed the threshold: hand the gesture to the window manager, which takes over the move.
            _pendingPress = null;
            _window.BeginMoveDrag(pressed);
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _pendingPress = null;

        private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _pendingPress = null;

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _pendingPress = null;
            _titleBar.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            _titleBar.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
            _titleBar.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
            _titleBar.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost);
            _titleBar.DetachedFromVisualTree -= OnDetached;
        }
    }
}
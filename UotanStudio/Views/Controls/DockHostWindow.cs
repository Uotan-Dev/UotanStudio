using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Dock.Settings;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Application floating dock host window that adds X11 pointer-captured dragging (so releasing the
/// mouse commits dock targets instead of being swallowed by <c>_NET_WM_MOVERESIZE</c>) and clears
/// stuck <c>:dragging</c> translucent states on Linux when a compositor consumes pointer release.
/// <para>应用级浮动停靠宿主窗口：在 X11 下通过指针捕获移动窗口（避免松手事件被
/// <c>_NET_WM_MOVERESIZE</c> 吞掉而无法停靠回主窗口），并在 Linux 合成器吞掉松手事件时清理卡死的
/// <c>:dragging</c> 半透明状态。</para>
/// </summary>
public class DockHostWindow : HostWindow
{
    private bool _pendingX11Drag;
    private bool _x11Dragging;
    private PointerPressedEventArgs? _pendingPressArgs;
    private IPointer? _x11CapturedPointer;
    private Point _pendingStartClient;
    private PixelPoint _x11DragAnchorOffset;
    private WindowDragDockScope _pendingDockScope;

    /// <summary>
    /// Uses the standard <see cref="HostWindow"/> control theme and selectors.
    /// <para>沿用标准的 <see cref="HostWindow"/> 控件主题与样式选择器。</para>
    /// </summary>
    protected override Type StyleKeyOverride => typeof(HostWindow);

    /// <summary>
    /// Initializes a new floating dock host window with default options.
    /// <para>使用默认选项初始化新的浮动停靠宿主窗口。</para>
    /// </summary>
    public DockHostWindow() : this(new DockManagerOptions { IsDockingEnabled = true })
    {
    }

    /// <summary>
    /// Initializes a new floating dock host window with the supplied dock manager options.
    /// <para>使用给定的停靠管理器选项初始化新的浮动停靠宿主窗口。</para>
    /// </summary>
    /// <param name="options">Shared dock manager options.<para>共享的停靠管理器选项。</para></param>
    public DockHostWindow(DockManagerOptions options) : base(options)
    {
        Workspace.DockMenuIcons.EnsureWired();
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        AddHandler(PointerPressedEvent, OnLinuxTunnelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: false);
        AddHandler(PointerMovedEvent, OnLinuxPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnLinuxPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnLinuxPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerEnteredEvent, OnLinuxPointerEntered, RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += OnLinuxDeactivated;
    }

    private bool IsX11Window => OperatingSystem.IsLinux() && PlatformImpl is IX11OptionsToplevelImplFeature;

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        if (OperatingSystem.IsLinux())
        {
            ResetX11DragState();
            Deactivated -= OnLinuxDeactivated;
        }

        base.OnClosed(e);
    }

    private void OnLinuxTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsX11Window)
        {
            if (HasStuckDraggingState())
            {
                ClearStuckDraggingState();
            }

            return;
        }

        if (WindowState != WindowState.Normal
            || e.ClickCount != 1
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!TryResolveX11DragScope(e.Source as Control, out var dockScope))
        {
            return;
        }

        _pendingX11Drag = true;
        _pendingPressArgs = e;
        _pendingStartClient = e.GetPosition(this);
        _pendingDockScope = dockScope;
        _x11CapturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnLinuxPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_x11Dragging)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                FinishX11Drag(e);
                return;
            }

            MoveWindowToPointer(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (_pendingX11Drag)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _pendingPressArgs is null)
            {
                ResetX11DragState();
                return;
            }

            var currentClient = e.GetPosition(this);
            var delta = currentClient - _pendingStartClient;
            if (Math.Abs(delta.X) <= DockSettings.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) <= DockSettings.MinimumVerticalDragDistance)
            {
                return;
            }

            var pressArgs = _pendingPressArgs;
            var scope = _pendingDockScope;
            _pendingX11Drag = false;
            _pendingPressArgs = null;

            if (!InvokeTryBeginWindowDrag(this, pressArgs, scope, ownsPointerRelease: true))
            {
                ReleaseX11Capture();
                return;
            }

            _x11Dragging = true;
            _x11DragAnchorOffset = this.PointToScreen(_pendingStartClient) - Position;
            MoveWindowToPointer(currentClient);
            e.Handled = true;
            return;
        }

        if (HasStuckDraggingState() && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ClearStuckDraggingState();
        }
    }

    private void OnLinuxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_x11Dragging)
        {
            FinishX11Drag(e);
            e.Handled = true;
            return;
        }

        if (_pendingX11Drag)
        {
            ResetX11DragState();
            e.Handled = true;
        }
    }

    private void OnLinuxPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_x11Dragging)
        {
            CancelX11Drag();
            return;
        }

        if (_pendingX11Drag)
        {
            ResetX11DragState();
        }
    }

    private void OnLinuxPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_x11Dragging
            && !_pendingX11Drag
            && HasStuckDraggingState()
            && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ClearStuckDraggingState();
        }
    }

    private void OnLinuxDeactivated(object? sender, EventArgs e)
    {
        if (_x11Dragging)
        {
            CancelX11Drag();
            return;
        }

        if (_pendingX11Drag)
        {
            ResetX11DragState();
            return;
        }

        if (HasStuckDraggingState())
        {
            ClearStuckDraggingState();
        }
    }

    private void MoveWindowToPointer(Point clientPoint)
    {
        var screenPoint = this.PointToScreen(clientPoint);
        var targetPosition = screenPoint - _x11DragAnchorOffset;
        if (targetPosition != Position)
        {
            Position = targetPosition;
        }
    }

    private void FinishX11Drag(PointerEventArgs e)
    {
        _x11Dragging = false;
        _pendingX11Drag = false;
        _pendingPressArgs = null;
        ReleaseX11Capture();
        InvokeEndWindowDrag(this, e);
    }

    private void CancelX11Drag()
    {
        _x11Dragging = false;
        _pendingX11Drag = false;
        _pendingPressArgs = null;
        ReleaseX11Capture();
        InvokeCancelExternalWindowDrag(this);
    }

    private void ResetX11DragState()
    {
        _x11Dragging = false;
        _pendingX11Drag = false;
        _pendingPressArgs = null;
        ReleaseX11Capture();
    }

    private void ReleaseX11Capture()
    {
        if (_x11CapturedPointer is { } pointer)
        {
            _x11CapturedPointer = null;
            if (ReferenceEquals(pointer.Captured, this))
            {
                pointer.Capture(null);
            }
        }
    }

    private bool HasStuckDraggingState()
        => GetMouseDownField(this) || Classes.Contains(":dragging");

    private void ClearStuckDraggingState()
        => InvokeCancelExternalWindowDrag(this);

    private bool TryResolveX11DragScope(Control? source, out WindowDragDockScope dockScope)
    {
        dockScope = WindowDragDockScope.WindowChrome;
        if (source is null)
        {
            return false;
        }

        var insideToolGrip = false;
        for (Visual? current = source; current is not null && !ReferenceEquals(current, this); current = current.GetVisualParent())
        {
            if (current is Button or TextBox or ComboBox or MenuBase or MenuItem or DocumentTabStripItem or ToolTabStripItem)
            {
                return false;
            }

            if (current is Control { Name: "PART_Grip" })
            {
                insideToolGrip = true;
            }

            if (current is ToolChromeControl)
            {
                if (ToolChromeControlsWholeWindow && insideToolGrip)
                {
                    dockScope = WindowDragDockScope.WindowChrome;
                    return true;
                }

                return false;
            }

            if (current is DocumentTabStrip tabStrip)
            {
                if (tabStrip.EnableWindowDrag)
                {
                    dockScope = DockSettings.DocumentWindowDragScope;
                    return true;
                }

                return false;
            }

            if (current is HostWindowTitleBar)
            {
                dockScope = WindowDragDockScope.WindowChrome;
                return true;
            }
        }

        return false;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_mouseDown")]
    private static extern ref bool GetMouseDownField(HostWindow window);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryBeginWindowDrag")]
    private static extern bool InvokeTryBeginWindowDrag(
        HostWindow window,
        PointerPressedEventArgs e,
        WindowDragDockScope dockScope,
        bool ownsPointerRelease);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EndWindowDrag")]
    private static extern void InvokeEndWindowDrag(HostWindow window, PointerEventArgs e);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CancelExternalWindowDrag")]
    private static extern void InvokeCancelExternalWindowDrag(HostWindow window);
}
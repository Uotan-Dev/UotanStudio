using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Nodes.EditorCommands;

namespace UotanStudio.Views.Nodes;

partial class NodeEditorView
{
    /// <summary>
    /// Pointer travel in screen pixels before a left press becomes a node drag or a box selection, so a plain click
    /// still selects without nudging a node.
    /// <para>Pointer travel in screen pixels before a left press becomes a node drag or a box selection, so a plain click
    /// still selects without nudging a node.</para>
    /// </summary>
    private const double CanvasGestureThreshold = 4;

    /// <summary>
    /// Screen-space distance in pixels scrolled per notch of the mouse wheel.
    /// <para>鼠标滚轮每格在屏幕空间中滚动的像素距离。</para>
    /// </summary>
    private const double CanvasWheelScrollDistance = 60;

    /// <summary>
    /// Height of the grip strip along the bottom edge of the editor where a press is left for the alignment guide's
    /// split drag instead of the canvas.
    /// <para>Height of the grip strip along the bottom edge of the editor where a press is left for the alignment guide's
    /// split drag instead of the canvas.</para>
    /// </summary>
    private const double AlignmentGuideGripThickness = 8;

    private CanvasGesture _canvasGesture;
    private IPointer? _canvasGesturePointer;
    private Point _canvasGestureOrigin;
    private NodifyM.Avalonia.Controls.BaseNode? _canvasGestureNode;
    private PointerPressedEventArgs? _canvasGestureTrigger;
    private double _panStartPointerX;
    private double _panStartPointerY;
    private double _panStartOffsetX;
    private double _panStartOffsetY;
    private double _panStartZoom = 1;

    private enum CanvasGesture
    {
        None,
        Pending,
        Panning,
        Selecting,
        DraggingNode
    }

    /// <summary>
    /// Routes every canvas press: the right button pans, a left press on a connector is left to NodifyM so connector
    /// dragging still starts, and any other left press is taken over by the canvas gesture before NodifyM sees it.
    /// The press, move, release and capture-loss handlers sit in the tunnel phase with handled events included, so
    /// the gesture state machine sees the whole gesture regardless of what the controls inside mark as handled.
    /// <para>Routes every canvas press: the right button pans, a left press on a connector is left to NodifyM so connector
    /// dragging still starts, and any other left press is taken over by the canvas gesture before NodifyM sees it.</para>
    /// </summary>
    private void AttachCanvasGestureHandlers()
    {
        Editor.AddHandler(PointerPressedEvent, OnCanvasGesturePressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Editor.AddHandler(PointerMovedEvent, OnCanvasGestureMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Editor.AddHandler(PointerReleasedEvent, OnCanvasGestureReleased,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Editor.AddHandler(PointerCaptureLostEvent, OnCanvasGestureCaptureLost,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Editor.AddHandler(PointerWheelChangedEvent, OnCanvasPointerWheelChanged,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Decides who owns a canvas press. Marking the event handled here stops NodifyM's own node and connection selection
    /// clearing, its left-drag panning and its left box selection for exactly this gesture; connectors are deliberately
    /// left unhandled so a pending connection still starts. A right press is handled even when panning is disabled,
    /// because NodifyM would otherwise clear the selection on a press that the status-bar hint promises is harmless.
    /// <para>决定画布按下由谁处理：右键平移画布，连接器按下交还给 NodifyM，其余左键由画布手势接管。</para>
    /// </summary>
    /// <param name="sender">The editor that routed the event.</param>
    /// <param name="e">The press arguments.</param>
    private void OnCanvasGesturePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_canvasGesture != CanvasGesture.None || !IsPointerOnSurface(e))
        {
            return;
        }

        var editable = DataContext is NodeEditorViewModel { CanEditGraph: true } && Editor.IsEnabled;
        var onCanvas = IsPointerOnCanvas(e) && !IsAlignmentGuideGrip(e);
        var captureTarget = e.Source as IInputElement ?? Editor;
        if (e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed)
        {
            if (editable && onCanvas)
            {
                BeginPan(e, captureTarget);
            }

            e.Handled = true;
            return;
        }


        if (!e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed || FindConnectorAt(e.Source) is not null)
        {
            return;
        }

        Editor.Focus();
        if (onCanvas && FindConnectionAt(e) is { DataContext: NodeConnectionViewModel connection })
        {
            if (editable && !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && DataContext is NodeEditorViewModel vm)
            {
                Editor.SelectedItems = new List<FlowNodeViewModel>();
                vm.SelectConnection(connection);
            }

            e.Handled = true;
            return;
        }

        if (!editable || !onCanvas)
        {
            return;
        }

        _canvasGesture = CanvasGesture.Pending;
        _canvasGesturePointer = e.Pointer;
        _canvasGestureTrigger = e;
        _canvasGestureOrigin = e.GetPosition(Editor);
        _canvasGestureNode = FindNodeAt(e.Source);
        e.Pointer.Capture(captureTarget);
        e.Handled = true;
    }

    private void BeginPan(PointerPressedEventArgs e, IInputElement captureTarget)
    {
        _canvasGesture = CanvasGesture.Panning;
        _canvasGesturePointer = e.Pointer;
        _canvasGestureOrigin = e.GetPosition(EditorSurface);
        _panStartPointerX = _canvasGestureOrigin.X;
        _panStartPointerY = _canvasGestureOrigin.Y;
        _panStartOffsetX = Editor.OffsetX;
        _panStartOffsetY = Editor.OffsetY;
        _panStartZoom = Editor.Zoom > 0 ? Editor.Zoom : 1;
        e.Pointer.Capture(captureTarget);
        e.Handled = true;
    }

    /// <summary>
    /// Pans from screen-space travel: the viewport offset and the view translate transform move by the pointer delta
    /// divided by the zoom, so the content follows the pointer at any zoom level. Node selection is never touched.
    /// <para>Pans from screen-space travel: the viewport offset and the view translate transform move by the pointer delta
    /// divided by the zoom, so the content follows the pointer at any zoom level. Node selection is never touched.</para>
    /// </summary>
    /// <param name="position">Pointer position in surface coordinates.</param>
    internal void PanCanvas(Point position)
    {
        var zoom = _panStartZoom > 0 ? _panStartZoom : 1;
        var offsetX = _panStartOffsetX + ((position.X - _panStartPointerX) / zoom);
        var offsetY = _panStartOffsetY + ((position.Y - _panStartPointerY) / zoom);
        SetCanvasOffset(offsetX, offsetY);
    }

    private void SetCanvasOffset(double offsetX, double offsetY)
    {
        Editor.OffsetX = offsetX;
        Editor.OffsetY = offsetY;
        Editor.ViewTranslateTransform.X = offsetX;
        Editor.ViewTranslateTransform.Y = offsetY;
    }

    /// <summary>
    /// Scrolls the canvas by screen-space units along the horizontal and vertical axes, taking zoom into account.
    /// <para>按屏幕空间单位沿水平和垂直轴滚动画布，已考虑缩放比例。</para>
    /// </summary>
    /// <param name="horizontalUnits">Horizontal wheel units (positive scrolls left, negative scrolls right).<para>水平滚轮单位（正值向左滚，负值向右滚）。</para></param>
    /// <param name="verticalUnits">Vertical wheel units (positive scrolls up, negative scrolls down).<para>垂直滚轮单位（正值向上滚，负值向下滚）。</para></param>
    internal void ScrollCanvas(double horizontalUnits, double verticalUnits)
    {
        var zoom = Editor.Zoom > 0 ? Editor.Zoom : 1;
        var deltaX = (horizontalUnits * CanvasWheelScrollDistance) / zoom;
        var deltaY = (verticalUnits * CanvasWheelScrollDistance) / zoom;
        SetCanvasOffset(Editor.OffsetX + deltaX, Editor.OffsetY + deltaY);
    }

    /// <summary>
    /// Handles mouse wheel scrolling and zooming on the node editor canvas: standard wheel scrolls vertically,
    /// holding Shift (or horizontal wheel) scrolls horizontally, and holding Ctrl (or Meta on macOS) zooms in/out.
    /// <para>处理节点编辑器画布上的滚轮滚动与缩放：普通滚轮上下滚动，按住 Shift（或横向滚轮）左右滚动，按住 Ctrl（macOS 为 Command）放大缩小。</para>
    /// </summary>
    /// <param name="sender">The editor that routed the event.<para>路由该事件的编辑器。</para></param>
    /// <param name="e">The wheel arguments.<para>滚轮事件参数。</para></param>
    internal void OnCanvasPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!IsPointerOnSurface(e) || DataContext is not NodeEditorViewModel || !Editor.IsEnabled)
        {
            return;
        }

        var zoom = Editor.Zoom > 0 ? Editor.Zoom : 1;
        if (!CalculateWheelAction(e.Delta, e.KeyModifiers, zoom, out var isZoom, out var deltaX, out var deltaY))
        {
            return;
        }

        if (isZoom)
        {
            // Leave event unhandled so NodifyEditor executes its built-in pointer-centered zoom.
            return;
        }

        SetCanvasOffset(Editor.OffsetX + deltaX, Editor.OffsetY + deltaY);
        e.Handled = true;
    }

    /// <summary>
    /// Calculates the canvas offset delta and zoom decision for a mouse wheel event based on modifiers and wheel delta.
    /// <para>根据修饰键和滚轮增量计算画布偏移增量和缩放决策。</para>
    /// </summary>
    /// <param name="delta">The pointer wheel delta vector.<para>滚轮增量向量。</para></param>
    /// <param name="keyModifiers">The active keyboard modifiers.<para>当前生效的键盘修饰键。</para></param>
    /// <param name="zoom">Current canvas zoom level.<para>当前画布缩放比例。</para></param>
    /// <param name="isZoom">Whether this wheel event should be treated as a zoom operation.<para>该滚轮事件是否应视为缩放操作。</para></param>
    /// <param name="deltaOffsetX">The computed delta to apply to the X offset.<para>计算出的 X 偏移增量。</para></param>
    /// <param name="deltaOffsetY">The computed delta to apply to the Y offset.<para>计算出的 Y 偏移增量。</para></param>
    /// <returns>True if the event represents an active scroll or zoom; false if no movement occurred.<para>若该事件表示有效滚动或缩放则为 true；无位移则为 false。</para></returns>
    internal static bool CalculateWheelAction(
        Vector delta,
        KeyModifiers keyModifiers,
        double zoom,
        out bool isZoom,
        out double deltaOffsetX,
        out double deltaOffsetY)
    {
        isZoom = false;
        deltaOffsetX = 0;
        deltaOffsetY = 0;

        var isZoomModifier = keyModifiers.HasFlag(KeyModifiers.Control)
            || (OperatingSystem.IsMacOS() && keyModifiers.HasFlag(KeyModifiers.Meta));

        if (isZoomModifier)
        {
            if (Math.Abs(delta.Y) > double.Epsilon)
            {
                isZoom = true;
                return true;
            }

            return false;
        }

        double horizontalUnits;
        double verticalUnits;

        if (keyModifiers.HasFlag(KeyModifiers.Shift))
        {
            horizontalUnits = Math.Abs(delta.X) > double.Epsilon ? delta.X : delta.Y;
            verticalUnits = 0;
        }
        else
        {
            horizontalUnits = delta.X;
            verticalUnits = delta.Y;
        }

        if (Math.Abs(horizontalUnits) < double.Epsilon && Math.Abs(verticalUnits) < double.Epsilon)
        {
            return false;
        }

        var effectiveZoom = zoom > 0 ? zoom : 1;
        deltaOffsetX = (horizontalUnits * CanvasWheelScrollDistance) / effectiveZoom;
        deltaOffsetY = (verticalUnits * CanvasWheelScrollDistance) / effectiveZoom;
        return true;
    }

    private void OnCanvasGestureMoved(object? sender, PointerEventArgs e)
    {
        if (_canvasGesturePointer is null || e.Pointer != _canvasGesturePointer)
        {
            return;
        }

        if (DataContext is not NodeEditorViewModel { CanEditGraph: true } || !Editor.IsEnabled)
        {
            EndCanvasGesture(e.Pointer);
            return;
        }


        switch (_canvasGesture)
        {
            case CanvasGesture.Panning:
                if (!e.GetCurrentPoint(EditorSurface).Properties.IsRightButtonPressed)
                {
                    EndCanvasGesture(e.Pointer);
                    return;
                }

                PanCanvas(e.GetPosition(EditorSurface));
                e.Handled = true;
                return;
            case CanvasGesture.Pending:
                PromotePendingGesture(e);
                e.Handled = true;
                return;
            case CanvasGesture.Selecting:
                UpdateSelectionBox(_selectionBoxStart, e.GetPosition(EditorSurface));
                e.Handled = true;
                return;
            case CanvasGesture.DraggingNode:
                Editor.NodeDragging(e);
                e.Handled = true;
                return;
            default:
                return;
        }
    }

    private void PromotePendingGesture(PointerEventArgs e)
    {
        var delta = e.GetPosition(Editor) - _canvasGestureOrigin;
        if (Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y)) < CanvasGestureThreshold)
        {
            return;
        }

        if (DataContext is not NodeEditorViewModel vm)
        {
            EndCanvasGesture(e.Pointer);
            return;
        }

        if (_canvasGestureNode is { } node && node.DataContext is FlowNodeViewModel clicked
            && _canvasGestureTrigger is { } trigger)
        {
            _canvasGesture = CanvasGesture.DraggingNode;
            vm.SelectConnection(null);
            if (!clicked.IsSelected)
            {
                Editor.SelectedItems = new List<FlowNodeViewModel> { clicked };
                vm.SetSelection([clicked]);
            }

            _nodeDragStartLocations = Editor.SelectedItems?
                .OfType<FlowNodeViewModel>()
                .ToDictionary(item => item, item => item.Location);
            Editor.StartNodeDrag(trigger, node);
            Editor.NodeDragging(e);
            return;
        }

        _canvasGesture = CanvasGesture.Selecting;
        _selectionBoxDragging = true;
        _selectionBoxPointer = e.Pointer;
        _selectionBoxStart = _canvasGestureOrigin;
        _selectionBoxMoved = true;
        UpdateSelectionBox(_selectionBoxStart, _selectionBoxStart);
    }

    private void OnCanvasGestureReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_canvasGesturePointer is null || e.Pointer != _canvasGesturePointer)
        {
            return;
        }

        var gesture = _canvasGesture;
        var node = _canvasGestureNode;
        var moved = _selectionBoxMoved;
        if (gesture == CanvasGesture.DraggingNode)
        {
            Editor.StopNodeDrag(e);
        }

        EndCanvasGesture(e.Pointer);
        if (gesture == CanvasGesture.Selecting && moved)
        {
            SelectNodesInRectangle(_selectionBoxStart, e.GetPosition(EditorSurface));
        }
        else if (gesture == CanvasGesture.Pending && node?.DataContext is FlowNodeViewModel clicked
                 && DataContext is NodeEditorViewModel vm)
        {
            vm.SelectConnection(null);
            Editor.SelectedItems = new List<FlowNodeViewModel> { clicked };
            vm.SetSelection([clicked]);
        }

        e.Handled = true;
    }

    private void OnCanvasGestureCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_canvasGesturePointer is null || e.Pointer != _canvasGesturePointer)
        {
            return;
        }

        if (_canvasGesture == CanvasGesture.DraggingNode && _nodeDragStartLocations is { Count: > 0 } startLocations
            && DataContext is NodeEditorViewModel vm)
        {
            var command = new MoveNodeCommand();
            foreach (var (node, originalLocation) in startLocations)
            {
                command.AddTarget(node, originalLocation, node.Location);
            }
            _nodeDragStartLocations = null;
            if (command.HasTargets)
            {
                vm.Record(command);
            }
        }

        EndCanvasGesture(e.Pointer);
    }

    /// <summary>
    /// Clears the gesture state and the selection rectangle.
    /// <para>清除手势状态与框选矩形。</para>
    /// </summary>
    /// <param name="pointer">The pointer that owned the gesture.</param>
    private void EndCanvasGesture(IPointer pointer)
    {
        var wasDraggingNode = _canvasGesture == CanvasGesture.DraggingNode;
        _canvasGesture = CanvasGesture.None;
        _canvasGesturePointer = null;
        _canvasGestureNode = null;
        _canvasGestureTrigger = null;
        _selectionBoxMoved = false;
        if (_selectionBoxDragging)
        {
            EndSelectionBox(pointer);
        }

        if (wasDraggingNode && Editor.AlignmentLine.Count > 0)
        {
            Editor.ClearAlignmentLine();
        }

        if (pointer.Captured == Editor)
        {
            pointer.Capture(null);
        }
    }

    /// <summary>
    /// Tells whether the pointer landed inside the editor surface, so a right press over the minimap or the status
    /// overlay is still consumed here instead of reaching NodifyM and clearing the selection.
    /// <para>Tells whether the pointer landed inside the editor surface.</para>
    /// </summary>
    /// <param name="e">The press arguments.</param>
    private bool IsPointerOnSurface(PointerEventArgs e)
    {
        if (!EditorSurface.IsVisible)
        {
            return false;
        }

        var position = e.GetPosition(EditorSurface);
        return EditorSurface.Bounds.Width > 0
            && EditorSurface.Bounds.Height > 0
            && EditorSurface.Bounds.Contains(position);
    }

    /// <summary>
    /// Tells whether the pointer landed inside the zoomable canvas rather than on an overlay drawn over it.
    /// <para>Tells whether the pointer landed inside the zoomable canvas.</para>
    /// </summary>
    /// <param name="e">The press arguments.</param>
    private bool IsPointerOnCanvas(PointerEventArgs e)
    {
        if (!Editor.IsVisible || Editor.Bounds.Width <= 0 || Editor.Bounds.Height <= 0)
        {
            return false;
        }

        return Editor.Bounds.Contains(e.GetPosition(Editor));
    }

    /// <summary>
    /// The alignment guide draws a grab handle across the bottom edge of the editor; that strip keeps NodifyM's own
    /// split drag so the guide can still be resized.
    /// <para>The alignment guide draws a grab handle across the bottom edge of the editor; that strip keeps NodifyM's own
    /// split drag so the guide can still be resized.</para>
    /// </summary>
    /// <param name="e">The press arguments.</param>
    private bool IsAlignmentGuideGrip(PointerPressedEventArgs e)
    {
        var position = e.GetPosition(Editor);
        return Editor.Bounds.Height > AlignmentGuideGripThickness * 2
            && position.Y >= Editor.Bounds.Height - AlignmentGuideGripThickness;
    }

    /// <summary>
    /// Finds the node under the pointer whose content is a flow node, so only real graph nodes start a node drag
    /// rather than the pending-connection preview or other editor decorations.
    /// <para>Finds the node under the pointer whose content is a flow node, so only real graph nodes start a node drag.</para>
    /// </summary>
    /// <param name="source">The element the routed event was sent to.</param>
    private static NodifyM.Avalonia.Controls.BaseNode? FindNodeAt(object? source) =>
        source is Visual visual
            ? visual.GetSelfAndVisualAncestors()
                .OfType<NodifyM.Avalonia.Controls.BaseNode>()
                .FirstOrDefault(node => node.DataContext is FlowNodeViewModel)
            : null;

    /// <summary>
    /// Finds the connector under the pointer. The connector, including the socket drawn outside the node body, is
    /// what makes a press a connector drag rather than a node drag, so it must reach NodifyM untouched.
    /// <para>Finds the connector under the pointer. Its press must reach NodifyM untouched.</para>
    /// </summary>
    /// <param name="source">The element the routed event was sent to.</param>
    internal static NodifyM.Avalonia.Controls.Connector? FindConnectorAt(object? source) =>
        source is Visual visual
            ? visual.GetSelfAndVisualAncestors().OfType<NodifyM.Avalonia.Controls.Connector>().FirstOrDefault()
            : null;
}
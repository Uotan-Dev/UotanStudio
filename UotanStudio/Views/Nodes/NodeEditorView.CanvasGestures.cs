using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.ViewModels.NodeViewModels.EditorCommands;

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
    private bool IsPointerOnSurface(PointerPressedEventArgs e)
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
    private bool IsPointerOnCanvas(PointerPressedEventArgs e)
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
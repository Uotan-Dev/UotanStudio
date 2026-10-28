using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Projects;
using UotanStudio.Services.Ui;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.Views.Shell;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Nodes.EditorCommands;

namespace UotanStudio.Views.Nodes;

public partial class NodeEditorView : UserControl
{
    /// <summary>Pane minimums in pixels at normal widths. <para>三栏在正常宽度下的最小宽度（像素）。</para></summary>
    private const double LeftPaneMinimum = 200;
    private const double CenterPaneMinimum = 200;
    private const double RightPaneMinimum = 280;
    private const double ConnectionHitTestThickness = 10;

    /// <summary>
    /// Width below which the gesture hint leaves the status bar so the zoom, node and connection counts keep the
    /// whole line on a narrow editor.
    /// <para>低于该宽度时状态栏不显示手势提示，窄窗口下缩放与节点、连线计数仍能占满整行。</para>
    /// </summary>
    internal const double CanvasHintMinimumWidth = 860;

    /// <summary>Whether the status bar still has room for the canvas gesture hint. <para>状态栏是否仍有空间显示画布手势提示。</para></summary>
    internal static readonly DirectProperty<NodeEditorView, bool> CanvasHintVisibleProperty =
        AvaloniaProperty.RegisterDirect<NodeEditorView, bool>(nameof(CanvasHintVisible), view => view.CanvasHintVisible);

    private bool _canvasHintVisible;
    private bool _draggingLeft;
    private bool _draggingRight;
    private double _dragOriginX;
    private double _startLeft;
    private double _startCenter;
    private double _startRight;
    private const double ToolboxDragThreshold = 4;
    private PointerPressedEventArgs? _toolboxDragTrigger;
    private NodeToolboxItem? _toolboxDragItem;
    private IPointer? _toolboxDragPointer;
    private Point _toolboxDragOrigin;
    private bool _suppressToolboxClick;
    private Dictionary<FlowNodeViewModel, Point>? _nodeDragStartLocations;
    private bool _selectionBoxDragging;
    private IPointer? _selectionBoxPointer;
    private Point _selectionBoxStart;
    private bool _selectionBoxMoved;
    private string? _dropPreviewTypeKey;
    private FlowNodeViewModel? _dropPreviewNode;

    /// <summary>Gets or sets whether the canvas gesture hint is shown in the status bar. <para>获取或设置状态栏是否显示画布手势提示。</para></summary>
    internal bool CanvasHintVisible
    {
        get => _canvasHintVisible;
        set => SetAndRaise(CanvasHintVisibleProperty, ref _canvasHintVisible, value);
    }

    public NodeEditorView()
    {
        InitializeComponent();
        _executionLogPresenter = CreateExecutionLogPresenter();
        AddHandler(KeyDownEvent, OnLocalEditorKeyDown, RoutingStrategies.Bubble);

        Editor.SelectionChanged += OnEditorSelectionChanged;
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Bubble);
        PreservePendingConnectionVisual();
        AttachCanvasGestureHandlers();
        AttachCanvasViewportTracking();
        Editor.AddHandler(PointerReleasedEvent, OnEditorPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        PaneLayout.SizeChanged += (_, _) => ApplyPaneMinimums();
        CenterPane.SizeChanged += OnCenterPaneSizeChanged;
        UpdateExecutionLogPane();

        AddHandler(PointerPressedEvent, OnToolboxItemPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnToolboxItemPointerMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnToolboxItemPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnToolboxItemPointerCaptureLost,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Left splitter pressed: records the pointer origin and the current pane widths, and captures the pointer
    /// so the drag keeps following even outside the handle.
    /// <para>左分割线按下：记录起始指针位置与三栏当前宽度，并捕获指针以便拖出拖动条也能继续跟随。</para>
    /// </summary>
    private void OnLeftSplitterPressed(object? sender, PointerPressedEventArgs e) => BeginDrag(sender, e, left: true);

    /// <summary>Right splitter pressed. <para>右分割线按下。</para></summary>
    private void OnRightSplitterPressed(object? sender, PointerPressedEventArgs e) => BeginDrag(sender, e, left: false);

    private void BeginDrag(object? sender, PointerPressedEventArgs e, bool left)
    {
        if (PaneLayout.ColumnDefinitions.Count < 5 || !e.GetCurrentPoint(PaneLayout).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragOriginX = e.GetPosition(PaneLayout).X;
        _startLeft = PaneLayout.ColumnDefinitions[0].ActualWidth;
        _startCenter = PaneLayout.ColumnDefinitions[2].ActualWidth;
        _startRight = PaneLayout.ColumnDefinitions[4].ActualWidth;
        _draggingLeft = left;
        _draggingRight = !left;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    /// <summary>While dragging: turns the pointer offset into a width trade between the two adjacent panes. <para>拖动中：把指针位移换算成相邻两栏的宽度搬运。</para></summary>
    private void OnSplitterMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingLeft && !_draggingRight)
        {
            return;
        }

        var delta = e.GetPosition(PaneLayout).X - _dragOriginX;
        if (_draggingLeft)
        {
            ApplyDrag(delta, leftPair: true);
        }
        else
        {
            ApplyDrag(delta, leftPair: false);
        }

        e.Handled = true;
    }

    /// <summary>Pointer released: ends the drag. <para>松开指针：结束拖动。</para></summary>
    private void OnSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndDrag(e.Pointer);
        e.Handled = true;
    }

    /// <summary>Pointer capture lost (window deactivated and so on): ends the drag so no state is left behind. <para>指针捕获丢失（例如窗口失活）：结束拖动，避免残留拖动状态。</para></summary>
    private void OnSplitterCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndDrag(e.Pointer);
        e.Handled = true;
    }

    private void EndDrag(IPointer pointer)
    {
        if (!_draggingLeft && !_draggingRight)
        {
            return;
        }

        _draggingLeft = false;
        _draggingRight = false;
        pointer.Capture(null);
    }

    /// <summary>
    /// Trades width: the pair being dragged keeps its combined width, so the third pane's width and share are
    /// untouched, and each pane is clamped to the effective minimum so the drag simply stops at the limit.
    /// <para>搬运宽度：被拖动的两栏共享的总宽保持不变，因此第三栏的宽度与比例完全不受影响；
    /// 两栏各自不小于当前生效的最小宽度，所以拖到头就停。</para>
    /// </summary>
    private void ApplyDrag(double delta, bool leftPair)
    {
        var available = AvailablePaneWidth();
        var (minimumLeft, minimumCenter, minimumRight) = EffectiveMinimums(available);
        double left;
        double center;
        double right;
        if (leftPair)
        {
            var budget = _startLeft + _startCenter;
            left = Math.Clamp(_startLeft + delta, minimumLeft, Math.Max(minimumLeft, budget - minimumCenter));
            center = budget - left;
            right = _startRight;
        }
        else
        {
            var budget = _startCenter + _startRight;
            right = Math.Clamp(_startRight - delta, minimumRight, Math.Max(minimumRight, budget - minimumCenter));
            center = budget - right;
            left = _startLeft;
        }

        SetPaneWidths(left, center, right);
    }

    /// <summary>
    /// Writes the three pane widths back as star weights equal to those pixel values, so later window resizes
    /// keep scaling all three panes in the same proportion.
    /// <para>把三栏宽度写回列定义：写入的是与像素等值的星号权重，因此窗口后续变宽/变窄时三栏仍按同一比例缩放。</para>
    /// </summary>
    private void SetPaneWidths(double left, double center, double right)
    {
        var columns = PaneLayout.ColumnDefinitions;
        columns[0].Width = new GridLength(Math.Max(0, left), GridUnitType.Star);
        columns[2].Width = new GridLength(Math.Max(0, center), GridUnitType.Star);
        columns[4].Width = new GridLength(Math.Max(0, right), GridUnitType.Star);
    }

    /// <summary>
    /// <para>三栏可用宽度（扣除两条 1px 分隔线）。</para>Width available to the three panes (minus the two 1px dividers).</summary>
    private double AvailablePaneWidth()
    {
        var columns = PaneLayout.ColumnDefinitions;
        return columns.Count < 5
            ? PaneLayout.Bounds.Width
            : Math.Max(0, PaneLayout.Bounds.Width - columns[1].ActualWidth - columns[3].ActualWidth);
    }

    /// <summary>
    /// Scales the minimums down when the available width cannot hold all three, so every size fits and the last
    /// pane is never clipped.
    /// <para>最小宽度随可用宽度等比缩放：可用宽度不足以放下三个最小宽度时一起缩小，
    /// 这样任何尺寸下三栏都放得下，不会出现最后一栏被裁掉的情况。</para>
    /// </summary>
    private (double Left, double Center, double Right) EffectiveMinimums(double available)
    {
        var total = LeftPaneMinimum + CenterPaneMinimum + RightPaneMinimum;
        if (available <= 0 || available >= total)
        {
            return (LeftPaneMinimum, CenterPaneMinimum, RightPaneMinimum);
        }

        var scale = available / total;
        return (LeftPaneMinimum * scale, CenterPaneMinimum * scale, RightPaneMinimum * scale);
    }

    /// <summary>Applies the effective minimums to the column definitions (called when the pane size changes). <para>把当前生效的最小宽度应用到列定义（面板尺寸变化时调用）。</para></summary>
    private void ApplyPaneMinimums()
    {
        UpdateResponsivePanes();
        if (_isNarrowLayout)
        {
            return;
        }
        var columns = PaneLayout.ColumnDefinitions;
        if (columns.Count < 5)
        {
            return;
        }

        var (left, center, right) = EffectiveMinimums(AvailablePaneWidth());
        columns[0].MinWidth = left;
        columns[2].MinWidth = center;
        columns[4].MinWidth = right;
    }

    /// <summary>
    /// Applies the pane minimums and the status-bar hint once the editor has its final size, which is what decides
    /// whether the gesture hint fits next to the zoom and count labels.
    /// <para>编辑器尺寸确定后应用栏宽下限与状态栏提示的可见性。</para>
    /// </summary>
    /// <param name="e">The size changed arguments.</param>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyPaneMinimums();
    }

    /// <summary>
    /// When the canvas node selection changes, sync the first added node to the ViewModel to drive the properties panel.
    /// <para>画布节点选中变更时，将新增选中中的第一个节点同步到 ViewModel，驱动右侧属性面板。</para>
    /// </summary>
    /// <param name="sender">Event source. <para>事件源。</para></param>
    /// <param name="e">The selection changed args. <para>选中变更事件参数。</para></param>
    private void OnEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not NodeEditorViewModel vm) return;
        var selected = Editor.SelectedItems?
            .OfType<FlowNodeViewModel>()
            .ToList() ?? [];
        var primary = e.AddedItems.OfType<FlowNodeViewModel>().FirstOrDefault()
            ?? selected.FirstOrDefault();
        vm.SetSelection(selected, primary);
    }

    private IReadOnlyList<FlowNodeViewModel> NodesIntersectingSelection(Point first, Point second)
    {
        var selection = CreateSelectionRectangle(first, second);
        List<FlowNodeViewModel> result = [];
        if (DataContext is not NodeEditorViewModel vm) return result;

        foreach (var node in vm.Nodes)
        {
            if (Editor.ContainerFromItem(node) is not Control container) continue;
            var controls = container.GetVisualDescendants()
                .OfType<Control>()
                .Where(control => ReferenceEquals(control.DataContext, node))
                .Prepend(container);
            Rect? nodeBounds = null;
            foreach (var control in controls)
            {
                if (control.TranslatePoint(new Point(), EditorSurface) is not { } topLeft) continue;
                var bounds = new Rect(topLeft, control.Bounds.Size);
                nodeBounds = nodeBounds is { } existing ? existing.Union(bounds) : bounds;
            }

            if (nodeBounds is { } value && selection.Intersects(value)) result.Add(node);
        }

        return result;
    }

    internal int SelectNodesInRectangle(Point first, Point second)
    {
        var selected = NodesIntersectingSelection(first, second);
        Editor.SelectedItems = selected.ToList();
        if (DataContext is NodeEditorViewModel vm) vm.SetSelection(selected);
        return selected.Count;
    }

    /// <summary>
    /// Shows the box selection rectangle while the left drag is in progress.
    /// <para>Shows the box selection rectangle while the left drag is in progress.</para>
    /// </summary>
    /// <param name="first">Where the press started, in surface coordinates.</param>
    /// <param name="second">The current pointer position, in surface coordinates.</param>
    private void UpdateSelectionBox(Point first, Point second)
    {
        var rectangle = CreateSelectionRectangle(first, second);
        Canvas.SetLeft(SelectionBox, rectangle.X);
        Canvas.SetTop(SelectionBox, rectangle.Y);
        SelectionBox.Width = rectangle.Width;
        SelectionBox.Height = rectangle.Height;
        SelectionBox.IsVisible = _selectionBoxMoved;
    }

    /// <summary>
    /// Ends the box selection: hides the rectangle, drops the pointer capture and stops reporting an active box drag.
    /// <para>Ends the box selection: hides the rectangle, drops the pointer capture and stops reporting an active box drag.</para>
    /// </summary>
    /// <param name="pointer">The pointer that owned the box drag.</param>
    private void EndSelectionBox(IPointer pointer)
    {
        _selectionBoxDragging = false;
        _selectionBoxPointer = null;
        SelectionBox.IsVisible = false;
        if (pointer.Captured == Editor)
        {
            pointer.Capture(null);
        }
    }

    internal static Rect CreateSelectionRectangle(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(second.X - first.X),
        Math.Abs(second.Y - first.Y));

    private void OnToolboxItemClicked(object? sender, RoutedEventArgs e)
    {
        if (_suppressToolboxClick)
        {
            _suppressToolboxClick = false;
            return;
        }

        if (sender is Button button && button.DataContext is NodeToolboxItem item)
        {
            if (DataContext is NodeEditorViewModel { CanEditGraph: true } vm)
            {
                var node = item.CreateNode();
                vm.AddNode(node);
                vm.SetSelection([node]);
            }
        }
    }

    /// <summary>
    /// Left press that the canvas gesture state machine accepted records where the selected nodes were before a
    /// possible drag, so the release can record one undoable move.
    /// <para>被画布手势状态机接受的左键按下会记录选中节点的拖动前位置，以便松开时记录一次可撤销的移动。</para>
    /// </summary>
    /// <param name="sender">The editor that routed the event.</param>
    /// <param name="e">The press arguments.</param>
    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed
            || DataContext is not NodeEditorViewModel
            || _canvasGesture != CanvasGesture.Pending)
        {
            return;
        }

        _nodeDragStartLocations = Editor.SelectedItems?
            .OfType<FlowNodeViewModel>()
            .ToDictionary(node => node, node => node.Location);
    }

    /// <summary>
    /// Left release: records one undoable move for the nodes that ended somewhere other than where the drag started.
    /// The pre-drag locations are consumed here, after which a further release has nothing left to record.
    /// <para>Left release: records one undoable move for the nodes that ended somewhere other than where the drag started.</para>
    /// </summary>
    /// <param name="sender">The editor that routed the event.</param>
    /// <param name="e">The release arguments.</param>
    private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_nodeDragStartLocations is not { Count: > 0 } startLocations
            || DataContext is not NodeEditorViewModel vm)
        {
            _nodeDragStartLocations = null;
            return;
        }

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

    private NodifyM.Avalonia.Controls.Connection? FindConnectionAt(PointerEventArgs e)
    {
        foreach (var connection in Editor.GetVisualDescendants()
                     .OfType<NodifyM.Avalonia.Controls.Connection>()
                     .Reverse())
        {
            var tolerance = ConnectionHitTestThickness / (2 * Math.Max(Editor.Zoom, 0.01));
            if (IsConnectionHit(connection, e.GetPosition(connection), tolerance)) return connection;
        }

        return null;
    }

    internal static bool IsConnectionHit(
        NodifyM.Avalonia.Controls.Connection connection,
        Point point,
        double tolerance)
    {
        var direction = connection.Direction == NodifyM.Avalonia.Controls.ConnectionDirection.Forward ? 1d : -1d;
        var spacing = new Vector(connection.Spacing * direction, 0);
        var source = connection.Source;
        var target = connection.Target;
        var start = source + spacing;
        var end = target - spacing;
        var delta = target - source;
        var smooth = Math.Min(100, Math.Abs(delta.Y));
        var offset = Math.Max(smooth, Math.Abs(delta.X) / 2);
        offset = Math.Min(100 + Math.Sqrt(Math.Abs(delta.X) * 25), offset);
        var control = new Vector(offset * direction, 0);

        if (DistanceToSegment(point, source, start) <= tolerance
            || DistanceToSegment(point, end, target) <= tolerance)
        {
            return true;
        }

        var previous = start;
        for (var index = 1; index <= 32; index++)
        {
            var t = index / 32d;
            var current = CubicBezier(start, start + control, end - control, end, t);
            if (DistanceToSegment(point, previous, current) <= tolerance) return true;
            previous = current;
        }

        return false;
    }

    private static Point CubicBezier(Point start, Point control1, Point control2, Point end, double t)
    {
        var remaining = 1 - t;
        return new Point(
            (remaining * remaining * remaining * start.X)
            + (3 * remaining * remaining * t * control1.X)
            + (3 * remaining * t * t * control2.X)
            + (t * t * t * end.X),
            (remaining * remaining * remaining * start.Y)
            + (3 * remaining * remaining * t * control1.Y)
            + (3 * remaining * t * t * control2.Y)
            + (t * t * t * end.Y));
    }

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        var segment = end - start;
        var lengthSquared = segment.X * segment.X + segment.Y * segment.Y;
        if (lengthSquared <= double.Epsilon) return Distance(point, start);
        var projection = Math.Clamp(((point - start).X * segment.X + (point - start).Y * segment.Y) / lengthSquared, 0, 1);
        return Distance(point, start + segment * projection);

        static double Distance(Point first, Point second)
        {
            var x = first.X - second.X;
            var y = first.Y - second.Y;
            return Math.Sqrt(x * x + y * y);
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<TextBox>().Any()) return;
        if (DataContext is not NodeEditorViewModel vm) return;
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && vm.HasSelection)
        {
            vm.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.SelectedConnection is not null)
        {
            vm.SelectConnection(null);
            e.Handled = true;
        }
    }

    private void OnToolboxItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var button = e.Source is Visual source
            ? source.GetVisualAncestors().Prepend(source).OfType<Button>().FirstOrDefault()
            : null;
        if (button is null
            || button.Command is not null
            || button.Tag is "toolbox-preset"
            || button.DataContext is not NodeToolboxItem item
            || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _toolboxDragTrigger = e;
        _toolboxDragItem = item;
        _toolboxDragPointer = e.Pointer;
        _toolboxDragOrigin = e.GetPosition(this);
        _suppressToolboxClick = false;
        e.Pointer.Capture(button);
    }

    private async void OnToolboxItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_toolboxDragTrigger is null
            || _toolboxDragItem is null
            || e.Pointer != _toolboxDragPointer)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ClearToolboxDrag();
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _toolboxDragOrigin.X) < ToolboxDragThreshold
            && Math.Abs(position.Y - _toolboxDragOrigin.Y) < ToolboxDragThreshold)
        {
            return;
        }

        var trigger = _toolboxDragTrigger;
        var item = _toolboxDragItem;
        _suppressToolboxClick = true;
        ClearToolboxDrag();
        e.Handled = true;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText($"uotan-node:{item.TypeKey}"));
        try
        {
            await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Copy);
        }
        finally
        {

            Dispatcher.UIThread.Post(() => _suppressToolboxClick = false, DispatcherPriority.Input);
        }
    }

    private void OnToolboxItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer == _toolboxDragPointer)
        {
            ClearToolboxDrag();
        }
    }

    private void OnToolboxItemPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_suppressToolboxClick && e.Pointer == _toolboxDragPointer)
        {
            ClearToolboxDrag();
        }
    }

    private void ClearToolboxDrag()
    {
        var pointer = _toolboxDragPointer;
        _toolboxDragTrigger = null;
        _toolboxDragItem = null;
        _toolboxDragPointer = null;
        if (pointer?.Captured is not null)
        {
            pointer.Capture(null);
        }
    }

    private static string? ReadNodeDragPayload(IDataTransfer transfer)
    {
        var text = transfer.TryGetText();
        return text is not null && text.StartsWith("uotan-node:", StringComparison.Ordinal)
            ? text["uotan-node:".Length..]
            : null;
    }

    private static NodeToolboxItem? FindToolboxItem(NodeEditorViewModel vm, string typeKey) =>
        vm.ToolboxItems
            .FirstOrDefault(candidate => string.Equals(candidate.TypeKey, typeKey, StringComparison.OrdinalIgnoreCase));

    private void OnCanvasDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not NodeEditorViewModel vm
            || !vm.CanEditGraph
            || ReadNodeDragPayload(e.DataTransfer) is not { } typeKey
            || FindToolboxItem(vm, typeKey) is not { } item)
        {
            HideNodeDropPreview();
            e.DragEffects = DragDropEffects.None;
            return;
        }

        ShowNodeDropPreview(e.GetPosition(DropPreviewLayer), item);
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnCanvasDragLeave(object? sender, DragEventArgs e)
    {
        HideNodeDropPreview();
    }

    private void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        HideNodeDropPreview();
        if (DataContext is not NodeEditorViewModel vm
            || !vm.CanEditGraph
            || ReadNodeDragPayload(e.DataTransfer) is not { } typeKey)
        {
            return;
        }

        if (FindToolboxItem(vm, typeKey) is not { } item)
        {
            return;
        }

        var node = item.CreateNode();
        node.Location = ToNodeLocation(e.GetPosition(Editor), Editor.OffsetX, Editor.OffsetY);
        vm.AddNode(node, preserveLocation: true);
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    internal static Point ToNodeLocation(Point editorPosition, double offsetX, double offsetY) =>
        new(editorPosition.X - offsetX, editorPosition.Y - offsetY);

    internal void ShowNodeDropPreview(Point position, NodeToolboxItem item)
    {
        if (!string.Equals(_dropPreviewTypeKey, item.TypeKey, StringComparison.Ordinal))
        {
            _dropPreviewTypeKey = item.TypeKey;
            _dropPreviewNode = item.CreateNode();
            _dropPreviewNode.RefreshCanvasInputs((DataContext as NodeEditorViewModel)?.CompactNodes == true);
            NodeDropPreview.DataContext = _dropPreviewNode;
        }

        Canvas.SetLeft(NodeDropPreview, position.X);
        Canvas.SetTop(NodeDropPreview, position.Y);
        NodeDropPreview.IsVisible = true;
    }

    internal void HideNodeDropPreview()
    {
        NodeDropPreview.IsVisible = false;
    }

    /// <summary>
    /// Path-port browse button: opens a storage-provider file picker and writes the chosen path into the port's initial value.
    /// <para>路径端口「浏览」按钮：通过存储提供程序弹出文件选择器，把所选路径写入该端口的初始值。</para>
    /// </summary>
    /// <param name="sender">The browse button whose Tag carries the target connector.</param>
    /// <param name="e">Routed event args.</param>
    private async void OnBrowsePathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NodeConnectorViewModel connector }
            || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var storage = topLevel.StorageProvider;
        if (connector.IsDirectoryPath)
        {
            if (!storage.CanPickFolder)
            {
                SetPathPickerUnavailableStatus();
                return;
            }
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Services.LanguageService.Instance.GetString("Node_Patch_SelectFolder"),
                AllowMultiple = false
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } folderPath)
            {
                connector.InitialValue = folderPath;
            }
            return;
        }
        if (IsOutputFilePath(connector))
        {
            if (!storage.CanSave)
            {
                SetPathPickerUnavailableStatus();
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Services.LanguageService.Instance.GetString("Menu_SaveAs"),
                DefaultExtension = connector.OutputExtension,
                SuggestedFileName = connector.InitialValue is string initialPath
                    && !string.IsNullOrWhiteSpace(initialPath)
                    ? Path.GetFileName(initialPath)
                    : connector.SuggestedOutputName
            });
            if (file?.TryGetLocalPath() is { Length: > 0 } outputPath)
            {
                connector.InitialValue = outputPath;
            }
            return;
        }

        if (!storage.CanOpen)
        {
            SetPathPickerUnavailableStatus();
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Services.LanguageService.Instance.GetString("Dialog_SelectFile"),
            AllowMultiple = false
        });
        if (files is { Count: > 0 } && files[0].TryGetLocalPath() is { Length: > 0 } localPath)
        {
            connector.InitialValue = localPath;
        }
    }

    internal static bool IsOutputFilePath(NodeConnectorViewModel connector) =>
        connector.PortType == NodePortType.Path
        && connector.Title is "Output Path" or "Output File" or "Destination";

    private void SetPathPickerUnavailableStatus()
    {
        if (DataContext is NodeEditorViewModel vm)
        {
            vm.ExecutionStatus = Services.LanguageService.Instance.GetString("Node_FilePickerUnavailable");
        }
    }

    private async void OnImportTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodeEditorViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Services.LanguageService.Instance.GetString("Node_ImportTemplate"),
            AllowMultiple = false,
            FileTypeFilter = [VisualPluginFileType()]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            var package = await VisualPluginFile.LoadAsync(path);
            if (package.Kind == VisualPluginKind.CompositeNode)
            {
                if (VisualPluginBundle.EnumerateScopes(package).Any(scope => scope.Assets.Count > 0))
                {
                    package = (await VisualPluginManager.Instance.InstallAsync(path)).Package;
                }
                var node = new CompositeNodeViewModel(package);
                vm.AddNode(node);
                vm.SetSelection([node]);
            }
            else
            {
                vm.ImportTemplate(package);
            }
            vm.ExecutionStatus = Services.LanguageService.Instance.GetString("Node_ImportSucceeded");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            vm.ExecutionStatus = string.Format(Services.LanguageService.Instance.GetString("Node_OperationFailed"), Services.UserError.Describe(ex));
        }
    }

    private async void OnExportTemplateClick(object? sender, RoutedEventArgs e)
        => await ExportPackageAsync(VisualPluginKind.WorkflowTemplate);

    private async void OnExportCompositeClick(object? sender, RoutedEventArgs e)
        => await ExportPackageAsync(VisualPluginKind.CompositeNode);

    private async Task ExportPackageAsync(VisualPluginKind kind)
    {
        if (DataContext is not NodeEditorViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var composite = kind == VisualPluginKind.CompositeNode;
        var fallbackName = Services.LanguageService.Instance.GetString(
            composite ? "Node_DefaultCompositeName" : "Node_DefaultTemplateName");
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Services.LanguageService.Instance.GetString(
                composite ? "Node_ExportComposite" : "Node_ExportTemplate"),
            SuggestedFileName = VisualPluginPackageBuilder.NormalizeId(fallbackName) + ".uotanplugin.json",
            DefaultExtension = "json",
            FileTypeChoices = [VisualPluginFileType()]
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            var rawName = Path.GetFileName(path);
            var suffix = ".uotanplugin.json";
            var name = rawName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? rawName[..^suffix.Length]
                : Path.GetFileNameWithoutExtension(rawName);
            var package = vm.CreateVisualPluginPackage(name,
                VisualPluginPackageBuilder.NormalizeId(name), kind);
            await VisualPluginFile.SaveAsync(path, package);
            if (composite) await VisualPluginManager.Instance.InstallAsync(path);
            vm.ExecutionStatus = Services.LanguageService.Instance.GetString(
                composite ? "Node_CompositeExported" : "Node_TemplateExported");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            vm.ExecutionStatus = string.Format(Services.LanguageService.Instance.GetString("Node_OperationFailed"), Services.UserError.Describe(ex));
        }
    }

    private static FilePickerFileType VisualPluginFileType() => new("Uotan visual plugin JSON")
    {
        Patterns = ["*.uotanplugin", "*.uotanplugin.json", "*.json"]
    };

    /// <summary>
    /// Node project tab strip: clicking a tab makes that node project the active canvas.
    /// <para>节点项目标签条：点击标签把同名节点项目设为活动画布。</para>
    /// </summary>
    private async void OnNodeTabSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        await ProjectOperationsService.Instance.SetActiveNodeProjectAsync(name);
    }

    /// <summary>
    /// Node project tab strip: the close button discards the canvas document while the solution keeps the
    /// registration and the file on disk.
    /// <para>节点项目标签条：关闭按钮丢弃该画布文档，解决方案仍保留登记与磁盘文件。</para>
    /// </summary>
    private async void OnNodeTabClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        if (DataContext is not NodeEditorViewModel vm) return;
        if (vm.FindDocument(name) is { IsDirty: true })
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }
            var language = Services.LanguageService.Instance;
            var confirmed = await DialogService.ConfirmAsync(owner,
                language.GetString("Dialog_UnsavedTitle"),
                string.Format(language.GetString("Dialog_UnsavedMessage"), name),
                language.GetString("Dialog_DiscardChanges"));
            if (!confirmed)
            {
                return;
            }
        }
        await ProjectOperationsService.Instance.CloseNodeProjectAsync(name);
    }
}
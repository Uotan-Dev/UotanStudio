using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Compare;

namespace UotanStudio.Views.Compare;

/// <summary>
/// <para>对比视图：按文件列集合动态生成并排的列（列间带分割线与可拖动分隔条），每列显示目录树
/// （双击展开，导入后保持折叠）、单文件内容或对比结果单元格。视图层同时接线系统文件拖拽
/// （拖入的每个路径占用一个文件列）以及对比两列的同步滚动与选中联动。</para>
/// Compare view: generates side-by-side columns from the column collection (each gap holding a divider
/// and a draggable splitter), where a column shows a directory tree (double-click to expand, collapsed
/// right after import), a single file's content or comparison cells. The view also wires OS file
/// drag-and-drop (each dropped path takes one column) plus synchronised scrolling and selection
/// between the two compare columns.
/// </summary>
public partial class CompareView : UserControl
{
    /// <summary>
    /// <para>行模板 <c>MinHeight</c>：各行恒为 20px，用于按偏移估算行索引（实测失败时兜底）。</para>
    /// Row-template <c>MinHeight</c>: every row is a fixed 20 px, used to estimate the row index
    /// from a scroll offset (fallback when measuring fails).
    /// </summary>
    private const double RowMinHeight = 20.0;

    /// <summary>
    /// <para>列间隔宽度：1px 分割线落在中间，两侧留出可抓取的拖动区域。</para>
    /// Column gap width: the 1 px divider line sits in the middle with draggable space around it.
    /// </summary>
    private const double DividerWidth = 5.0;

    /// <summary>
    /// <para>单列最小宽度：写在列定义上，<see cref="GridSplitter"/> 拖动时才会遵守。</para>
    /// Minimum width of one column; it lives on the column definition so the <see cref="GridSplitter"/>
    /// honours it while dragging.
    /// </summary>
    private const double MinColumnWidth = 140.0;

    /// <summary>
    /// <para>开始拖拽所需的最小移动距离（像素）：小于它视为点击，避免误触开始拖拽。</para>
    /// Minimum pointer movement (in pixels) before a drag starts; anything smaller counts as a click.
    /// </summary>
    private const double DragThreshold = 4.0;

    private readonly List<ColumnVisuals> _columnVisuals = [];
    private readonly List<GridSplitter> _columnDividers = [];
    private readonly List<ColumnDefinition> _columnDefinitions = [];
    private CompareViewModel? _vm;
    private bool _syncing;
    private bool _syncingSelection;

    // 最近一次镜像写入的目标偏移（NaN=无标记）：抑制镜像回声——写入目标列后，目标列因布局/
    // 异步触发的 ScrollChanged 会把偏移按行索引回写给源列，拖动中改写源偏移会放大鼠标位移
    // （上拉过量）；记录写入值并在回声到达时忽略，可切断该回环。
    // Last mirrored write's target offset (NaN = none): suppresses mirror echoes — after writing the
    // target column, the target's layout/asynchronous ScrollChanged could mirror the offset back to
    // the source column by row index, and rewriting the source offset mid-drag amplifies the mouse
    // movement (up-drag overshoot); recording the written value and ignoring the echo breaks it.
    private double _mirroredLeftY = double.NaN;
    private double _mirroredRightY = double.NaN;

    /// <summary>
    /// <para>创建对比视图并初始化组件。</para>
    /// Creates the compare view and initializes its components.
    /// </summary>
    public CompareView()
    {
        InitializeComponent();

        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        DataContextChanged += OnDataContextChanged;
        // 兜底：只拦截「异常调用栈来自本视图」的未处理异常（日志 + 错误条），避免编辑器内部状态冲突直接终止进程；
        // 其它来源的未处理异常照旧向上抛出，不掩盖别处的问题。
        // Safety net: only unhandled exceptions whose stack comes from this view are turned into a log line plus an error banner entry,
        // so an internal editor state conflict cannot kill the process; exceptions from elsewhere still propagate as before.
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

        // 拖拽与跨行框选在隧道阶段捕获：行内的输入框/列表项会消费冒泡的指针事件，
        // 只有在预览阶段才能稳定拿到「从某一行开始的按下/移动」。
        // Drag and range selection are captured in the tunnel phase: editors and list items consume the bubbling pointer
        // events, so only the preview phase reliably sees a press/move that starts on a row.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPreviewPointerMoved, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// <para>一个文件列在视图层的运行时部件：列容器、本列实际显示的行列表与其滚动容器。</para>
    /// The runtime parts of one file column: its container, the row list actually shown, and that
    /// list's scroll container.
    /// </summary>
    private sealed class ColumnVisuals(CompareColumnViewModel column, ContentControl container)
    {
        /// <summary>本列视图模型。 The column view model.</summary>
        public CompareColumnViewModel Column { get; } = column;

        /// <summary>承载列模板的容器。 The container hosting the column template.</summary>
        public ContentControl Container { get; } = container;

        /// <summary>本列当前显示的行列表；目录树列没有行列表。 The row list shown by this column; tree columns have none.</summary>
        public ListBox? List { get; set; }

        /// <summary>行列表内部滚动容器。 The row list's inner scroll container.</summary>
        public ScrollViewer? Scroll { get; set; }

        /// <summary>浏览文本编辑器（文本文件）。 The browsing text editor (text files).</summary>
        public TextEditor? BrowseEditor { get; set; }

        /// <summary>文本对比编辑器。 The text-comparison editor.</summary>
        public TextEditor? CompareEditor { get; set; }

        /// <summary>浏览编辑器是否已接线（含列属性监听）。 Whether the browsing editor is wired up (including the column listener).</summary>
        public bool BrowseWired { get; set; }

        /// <summary>列属性变化处理器（用于退订）。 The column property-change handler (kept for unsubscribing).</summary>
        public System.ComponentModel.PropertyChangedEventHandler? ColumnHandler { get; set; }
    }

    // ===== 生命周期与列构建 =====

    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        OnDataContextChanged(sender, EventArgs.Empty);
        // 列模板内部的 ScrollViewer 随模板实例化后才存在：Loaded 时模板可能尚未应用
        //（数据未加载/页面未激活），故延迟到布局完成后挂接；数据加载完成后还会再挂接一次。
        // The inner ScrollViewer of a row list exists only after its template is realized: at Loaded
        // the template may not be applied yet (no data / inactive page), so attach after layout;
        // attaching again after data loads covers the template-realized case.
        Dispatcher.UIThread.Post(AttachScrollViewers, DispatcherPriority.Loaded);
        ScrollToSelected();
    }

    private void OnViewUnloaded(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandledException;
        DetachScrollViewers();
        UnsubscribeViewModel();
    }

    /// <summary>
    /// <para>未处理异常兜底：调用栈来自对比视图（含其编辑器）时记录日志、写错误条并标记已处理，避免直接崩退；
    /// 其它来源保持原行为。</para>
    /// Unhandled-exception safety net: when the stack comes from the compare view (or its editors) the failure is logged, shown in the
    /// error banner and marked handled so the app does not die; anything else keeps its original behaviour.
    /// </summary>
    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var stack = e.Exception.StackTrace ?? string.Empty;
        if (!stack.Contains("UotanStudio.Views.Compare", StringComparison.Ordinal))
        {
            return;
        }

        e.Handled = true;
        LogService.Instance.Write(LogLevel.Error, "Compare", e.Exception.ToString());
        if (DataContext is CompareViewModel vm)
        {
            vm.ReportError(e.Exception.Message);
        }
    }

    /// <summary>
    /// <para>DataContext 变更：切换属性通知与列集合订阅（防泄漏），并重建列。</para>
    /// Data-context change: switches the property-changed and column-collection subscriptions (avoids
    /// leaks) and rebuilds the columns.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeViewModel();

        _vm = DataContext as CompareViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Columns.CollectionChanged += OnColumnsCollectionChanged;
        }

        RebuildColumns();
    }

    private void UnsubscribeViewModel()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Columns.CollectionChanged -= OnColumnsCollectionChanged;
        _vm = null;
    }

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildColumns();

    /// <summary>
    /// <para>按列集合生成网格：每列一个 <c>*</c> 宽列（带最小宽度），列间插入 1px 分割线与可直接拖动的
    /// <see cref="GridSplitter"/>（必须与列格子同层，放进 <c>ContentControl</c> 模板里会定位不到自己的列）。
    /// 对比状态下拖动条被禁用（宽度不允许调整），只保留分割线。</para>
    /// Builds the grid from the column collection: one star-width column per file column (with a minimum width)
    /// and, in every gap, a 1px divider plus a directly draggable <see cref="GridSplitter"/> (it must share the
    /// grid cell, because inside a <c>ContentControl</c>'s template it cannot resolve its own column). While
    /// comparing the splitter is disabled (widths are locked) and only the divider stays.
    /// </summary>
    private void RebuildColumns()
    {
        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        DetachScrollViewers();
        ColumnGrid.Children.Clear();
        ColumnGrid.ColumnDefinitions.Clear();
        _columnVisuals.Clear();
        _columnDividers.Clear();
        _columnDefinitions.Clear();

        var columnTemplate = (DataTemplate)Resources["CompareColumnTemplate"]!;
        var dividerTemplate = (ControlTemplate)Resources["CompareDividerTemplate"]!;

        for (var index = 0; index < vm.Columns.Count; index++)
        {
            if (index > 0)
            {
                ColumnGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(DividerWidth)));
                var cell = ColumnGrid.ColumnDefinitions.Count - 1;

                var line = new ContentControl { Template = dividerTemplate, Focusable = false, IsHitTestVisible = false };
                Grid.SetColumn(line, cell);
                ColumnGrid.Children.Add(line);

                var splitter = new GridSplitter
                {
                    ResizeDirection = GridResizeDirection.Columns,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    Background = Avalonia.Media.Brushes.Transparent,
                    Cursor = new Cursor(StandardCursorType.SizeWestEast),
                };
                Grid.SetColumn(splitter, cell);
                ColumnGrid.Children.Add(splitter);
                _columnDividers.Add(splitter);
            }

            var columnDefinition = new ColumnDefinition(1, GridUnitType.Star)
            {
                MinWidth = MinColumnWidth
            };
            ColumnGrid.ColumnDefinitions.Add(columnDefinition);
            // 只记录真正的列定义：列间隔（分割线与拖动条所在的格子）不能被当成列一起重置宽度。
            // Only the real column definitions are recorded: the gap cells (divider plus splitter) must not be resized as columns.
            _columnDefinitions.Add(columnDefinition);

            var container = new ContentControl
            {
                Content = vm.Columns[index],
                ContentTemplate = columnTemplate,
                Focusable = false
            };
            // 新列的模板稍后才实例化，挂接滚动容器要等它进入可视树再补一次。
            // A new column's template is realised later, so the scroll container is attached again once it loads.
            container.Loaded += OnColumnContainerLoaded;
            Grid.SetColumn(container, ColumnGrid.ColumnDefinitions.Count - 1);
            ColumnGrid.Children.Add(container);
            _columnVisuals.Add(new ColumnVisuals(vm.Columns[index], container));
        }

        UpdateDividerInteractivity();
        Dispatcher.UIThread.Post(AttachColumnEditors, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// <para>对比状态下禁用拖动条（左右宽度锁定），多列文件浏览时保持可拖动。</para>
    /// Disables the splitters while comparing (the panes' widths are locked) and keeps them draggable while
    /// browsing multiple file columns.
    /// </summary>
    private void UpdateDividerInteractivity()
    {
        var draggable = DataContext is not CompareViewModel vm || !vm.IsComparing;
        foreach (var splitter in _columnDividers)
        {
            splitter.IsEnabled = draggable;
            splitter.IsHitTestVisible = draggable;
            splitter.Cursor = draggable ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        }
    }

    /// <summary>
    /// <para>把各列宽度恢复成等分：开始对比时两栏宽度被锁定，此时回到等分状态（用户此前手动拖动过的宽度不再保留）。</para>
    /// Resets every column to an equal share: entering a comparison locks the pane widths, and this puts them back to an equal split (a
    /// width the user dragged earlier is no longer kept).
    /// </summary>
    private void ResetColumnWidths()
    {
        // 只重置内容列：列间隔那几列是固定宽度（1px 分割线 + 可拖动条），把它们也设成星号会吃掉大块宽度，
        // 看起来就是"两列没有填满"。
        // Only the content columns are reset: the gap columns keep their fixed width (the divider line plus the draggable strip), otherwise
        // they would swallow a large share of the width and the panes would look like they do not fill.
        foreach (var definition in _columnDefinitions)
        {
            definition.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    // ===== 双列同步滚动 =====

    /// <summary>
    /// <para>解析每列实际显示的行列表并订阅其 <c>ScrollChanged</c>；只有参与对比的两列之间才做
    /// 滚动镜像。再次调用会先退订旧实例（防重复订阅）。</para>
    /// Resolves each column's effective row list and subscribes to its <c>ScrollChanged</c>; scrolling
    /// is only mirrored between the two compare columns. Calling again unsubscribes the old instances
    /// first (avoids duplicate subscriptions).
    /// </summary>
    private void AttachScrollViewers()
    {
        if (!IsLoaded)
        {
            return;
        }

        foreach (var visuals in _columnVisuals)
        {
            var list = ResolveRowList(visuals.Container, visuals.Column.UsesRightCells);
            if (ReferenceEquals(list, visuals.List))
            {
                continue;
            }

            if (visuals.Scroll is not null)
            {
                visuals.Scroll.ScrollChanged -= OnScrollChanged;
                visuals.Scroll = null;
            }

            visuals.List = list;
            if (list is null)
            {
                continue;
            }

            visuals.Scroll = ResolveListScrollViewer(list);
            if (visuals.Scroll is not null)
            {
                visuals.Scroll.ScrollChanged += OnScrollChanged;
            }
        }

        _mirroredLeftY = double.NaN;
        _mirroredRightY = double.NaN;
    }

    /// <summary>
    /// <para>列容器首次进入可视树时补挂滚动容器：新增第 3、4…列时其模板尚未应用，早先那次挂接会落空，
    /// 只有在这里再补一次，新列的滚动镜像/选中同步才生效。</para>
    /// Attaches the scroll container when a column's container is first realized: a newly added third, fourth … column
    /// has no template yet when the earlier attach runs, so this retry is what makes the new column's scroll mirroring and
    /// selection synchronisation work.
    /// </summary>
    private void OnColumnContainerLoaded(object? sender, RoutedEventArgs e) => AttachScrollViewers();

    /// <summary>懒解析某一列的列表与滚动容器（容器已实现但缓存还没跟上时补上）。</summary>
    private void EnsureColumnResolved(ColumnVisuals visuals)
    {
        if (visuals.List is not null)
        {
            return;
        }

        var list = ResolveRowList(visuals.Container, visuals.Column.UsesRightCells);
        if (list is null)
        {
            return;
        }

        visuals.List = list;
        visuals.Scroll = ResolveListScrollViewer(list);
        if (visuals.Scroll is not null)
        {
            visuals.Scroll.ScrollChanged += OnScrollChanged;
        }
    }

    /// <summary>
    /// <para>在列容器内查找本列应显示的行列表：对比右列取右侧单元格列表，其余取左侧单元格列表
    /// （浏览单文件内容同样落在左侧列表）。</para>
    /// Finds the row list a column should show inside its container: the compare right column takes the
    /// right-cell list and everything else the left-cell list (browsing a single file also lands in the
    /// left list).
    /// </summary>
    private static ListBox? ResolveRowList(ContentControl container, bool rightCells)
    {
        var name = rightCells ? "RightCellsList" : "LeftCellsList";
        foreach (var list in container.GetVisualDescendants().OfType<ListBox>())
        {
            if (string.Equals(list.Name, name, StringComparison.Ordinal))
            {
                return list;
            }
        }

        return null;
    }

    /// <summary>
    /// <para>精确解析行列表真正的内部滚动容器：优先取模板部件 <c>PART_ScrollViewer</c>
    /// （即 <see cref="ListBox.Scroll"/>），避免 <c>FindDescendantOfType</c> 命中外部/嵌套滚动容器；
    /// 模板尚未应用时退而求其次，在子树内找包含 ItemsPanelRoot 的最内层 ScrollViewer。</para>
    /// Precisely resolves the real inner scroll container of a row list: prefers the template part
    /// <c>PART_ScrollViewer</c> (exposed as <see cref="ListBox.Scroll"/>) so outer/nested scroll
    /// containers are not picked up; when the template is not applied yet it falls back to the
    /// innermost ScrollViewer containing the items panel root.
    /// </summary>
    private static ScrollViewer? ResolveListScrollViewer(ListBox list)
    {
        if (list.Scroll is ScrollViewer direct)
        {
            return direct;
        }

        var panelRoot = list.ItemsPanelRoot;
        if (panelRoot is null)
        {
            return list.FindDescendantOfType<ScrollViewer>();
        }

        ScrollViewer? innermost = null;
        var minDistance = int.MaxValue;
        foreach (var candidate in list.GetVisualDescendants().OfType<ScrollViewer>())
        {
            if (!candidate.IsVisualAncestorOf(panelRoot))
            {
                continue;
            }

            var distance = panelRoot.CalculateDistanceFromAncestor(candidate);
            if (distance >= 0 && distance < minDistance)
            {
                minDistance = distance;
                innermost = candidate;
            }
        }

        return innermost ?? list.FindDescendantOfType<ScrollViewer>();
    }

    /// <summary>卸载或重建列前退订所有滚动容器，防止悬挂引用与重复订阅。</summary>
    private void DetachScrollViewers()
    {
        foreach (var visuals in _columnVisuals)
        {
            if (visuals.Scroll is not null)
            {
                visuals.Scroll.ScrollChanged -= OnScrollChanged;
                visuals.Scroll = null;
            }

            visuals.List = null;
        }
    }

    /// <summary>
    /// <para>左右两列同步滚动：把发起滚动一列的**第一个可见行索引**换算成目标列的垂直偏移
    /// （索引 × 行高，并夹取到目标侧 Extent 内），使同一 Rows 行落在两侧同一水平高度。相比直接
    /// 镜像像素偏移，行索引对齐对子像素漂移与虚拟化下 Extent 估算偏差更稳健；只同步垂直方向，
    /// 水平各自独立。<c>_syncing</c> 防止回写再次触发本回调形成回环。</para>
    /// Synchronised scrolling: converts the scrolled column's **first visible row index** into the
    /// target column's vertical offset (index × row height, clamped to the target's Extent), keeping
    /// the same Rows row at the same height in both columns. Row-index alignment is more robust than
    /// mirroring raw pixel offsets against sub-pixel drift and virtualized Extent estimation; only the
    /// vertical axis is synced, horizontal stays independent. <c>_syncing</c> guards against the
    /// mirrored write re-entering this handler in a loop.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer source)
        {
            return;
        }

        var left = FindCompareVisuals(CompareColumnSide.Left);
        var right = FindCompareVisuals(CompareColumnSide.Right);
        if (left is null || right is null || left.Scroll is null || right.Scroll is null)
        {
            return;
        }

        var sourceIsLeft = ReferenceEquals(source, left.Scroll);
        if (!sourceIsLeft && !ReferenceEquals(source, right.Scroll))
        {
            return;
        }

        var target = sourceIsLeft ? right.Scroll : left.Scroll;
        var targetList = sourceIsLeft ? right.List : left.List;

        // 镜像回声抑制：若本次滚动正是我们最近一次写入该列的偏移（布局/异步延迟触发），
        // 视为自己的回写，忽略并清除标记——否则偏移会被按行索引回写给正在拖动的源列，
        // 拖动中改写源偏移会放大鼠标位移（上拉过量）。
        // Mirror-echo suppression: if this scroll is exactly the offset we last wrote to this column
        // (fired asynchronously by layout), treat it as our own echo, ignore it and clear the marker —
        // otherwise the offset would be mirrored back to the column being dragged by row index, and
        // rewriting the source offset mid-drag amplifies the mouse movement.
        var echo = sourceIsLeft ? _mirroredLeftY : _mirroredRightY;
        if (!double.IsNaN(echo) && Math.Abs(source.Offset.Y - echo) < 0.5)
        {
            if (sourceIsLeft)
            {
                _mirroredLeftY = double.NaN;
            }
            else
            {
                _mirroredRightY = double.NaN;
            }

            return;
        }

        if (_syncing || targetList is null)
        {
            return;
        }

        var sourceList = sourceIsLeft ? left.List : right.List;
        if (sourceList is null)
        {
            return;
        }

        var rowHeight = GetRowHeight(sourceList);
        if (rowHeight <= 0)
        {
            return;
        }

        var index = (int)Math.Floor(source.Offset.Y / rowHeight);

        _syncing = true;
        try
        {
            var before = target.Offset.Y;
            ScrollListToIndex(targetList, target, index);
            var after = target.Offset.Y;
            if (Math.Abs(after - before) >= 0.5)
            {
                // 记录写入的目标偏移供回声抑制：同步触发时由 _syncing 拦截，
                // 异步（布局）触发时靠该标记拦截。
                // Record the written target offset for echo suppression: synchronous echoes are caught
                // by _syncing, asynchronous (layout) echoes by this marker.
                if (sourceIsLeft)
                {
                    _mirroredRightY = after;
                }
                else
                {
                    _mirroredLeftY = after;
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// <para>把行列表滚到指定行索引：目标偏移 = 索引 × 行高（夹取到 Extent 内）。用偏移量
    /// 精确设置而非 <see cref="ItemsControl.ScrollIntoView"/>——后者的语义是「滚到该项可见」，
    /// 若该项已在视口内则不做任何滚动，两侧位置仍会不同步。</para>
    /// Scrolls a row list to the given row index: target offset = index × row height (clamped to the
    /// Extent). Uses an exact offset instead of <see cref="ItemsControl.ScrollIntoView"/>, whose
    /// semantics are "scroll until the item is visible" and which does nothing when the item is
    /// already in the viewport — leaving the columns misaligned.
    /// </summary>
    private static void ScrollListToIndex(ListBox list, ScrollViewer? scroll, int index)
    {
        if (list.ItemsSource is not IReadOnlyList<CompareRowViewModel> rows || rows.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, rows.Count - 1);

        if (scroll is null)
        {
            list.ScrollIntoView(index);
            return;
        }

        var rowHeight = GetRowHeight(list);
        if (rowHeight <= 0)
        {
            list.ScrollIntoView(index);
            return;
        }

        var maxVertical = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var y = Math.Clamp(index * rowHeight, 0, maxVertical);
        if (Math.Abs(y - scroll.Offset.Y) < 0.5)
        {
            return;
        }

        scroll.Offset = new Vector(scroll.Offset.X, y);
    }

    /// <summary>
    /// <para>取单行实际高度：从行列表已生成的容器中实测首个非零高度（行模板
    /// <c>MinHeight=20</c> 恒定，各列行高一致）；尚未布局出容器时回退到固定 20。</para>
    /// Returns the effective single-row height: measured from the first realized container with a
    /// non-zero height (the row template fixes <c>MinHeight=20</c>, so all columns share one row
    /// height); falls back to a fixed 20 when no container is realized yet.
    /// </summary>
    private static double GetRowHeight(ListBox list)
    {
        foreach (var container in list.GetRealizedContainers())
        {
            if (container.Bounds.Height > 0)
            {
                return container.Bounds.Height;
            }
        }

        return RowMinHeight;
    }

    // ===== 选中项联动 =====

    /// <summary>
    /// <para>行列表选中变化：更新文档的 <see cref="CompareViewModel.SelectedIndex"/>（复制/删除/
    /// 差异导航据此定位），并把同一行同步选中到另一侧对比列；浏览列（单文件内容）的选中不参与
    /// 对比联动。</para>
    /// Row-list selection changed: updates the document's
    /// <see cref="CompareViewModel.SelectedIndex"/> (used to target the copy/delete/diff-navigation
    /// commands) and selects the same row in the other compare column; selection inside a browsing
    /// column (a single file's content) does not take part in compare synchronisation.
    /// </summary>
    private void OnRowSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _vm is null || sender is not ListBox list)
        {
            return;
        }

        var visuals = _columnVisuals.FirstOrDefault(item => ReferenceEquals(item.List, list));
        if (visuals is null)
        {
            return;
        }

        // 文本对比按选中行集合复制（拖拽可选中多行），因此把索引集合写回文档。
        // A text comparison copies by the selected row set (dragging selects many rows), so the index set is written back
        // to the document.
        _vm.UpdateRowSelection(list.Selection?.SelectedIndexes ?? []);

        // 把选中行广播给属性面板（无选中时清空）。
        // Broadcasts the selected row to the properties panel (cleared when nothing is selected).
        PublishSelectionToProperties(list);

        if (!visuals.Column.IsCompareSide)
        {
            return;
        }

        // 行列表的 ItemsSource 就是文档的 Rows，索引即 Rows 索引。
        // The row list's ItemsSource is the document's Rows, so the list index is the Rows index.
        if (list.SelectedIndex >= 0)
        {
            _vm.SelectedIndex = list.SelectedIndex;
        }

        var otherSide = visuals.Column.Side == CompareColumnSide.Left
            ? CompareColumnSide.Right
            : CompareColumnSide.Left;
        var other = FindCompareVisuals(otherSide);
        if (other?.List is not { } otherList)
        {
            return;
        }

        // 选择集合完全一致（含同为空）时无需同步，避免每次点击都触发多余的清空/重选。
        // Skip when both selections already match (including both empty) so a click does not trigger a redundant clear/reselect.
        var sourceIndexes = list.Selection?.SelectedIndexes ?? [];
        var targetIndexes = otherList.Selection?.SelectedIndexes ?? [];
        if (sourceIndexes.Count == targetIndexes.Count
            && !sourceIndexes.Except(targetIndexes).Any())
        {
            return;
        }

        // Multiple 模式（文本对比）下只写 SelectedIndex 是"追加"语义，会残留之前的选中行；
        // 整组同步：先清空对侧选择再选中相同索引集合。
        // In Multiple mode (text comparison) writing SelectedIndex alone adds to the old set, leaving stale rows;
        // sync as a group: clear the other side first, then select the same index set.
        _syncingSelection = true;
        try
        {
            otherList.Selection?.Clear();
            foreach (var index in list.Selection?.SelectedIndexes ?? [])
            {
                otherList.Selection?.Select(index);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>
    /// <para>把当前选中行以绝对路径广播给属性面板（<see cref="CompareSelectionChangedMessage"/>）；
    /// 文本对比行与空选择发送 <c>null</c> 清空面板。</para>
    /// Broadcasts the current selected row's absolute path to the properties panel
    /// (<see cref="CompareSelectionChangedMessage"/>); text-comparison rows and an empty selection send
    /// <c>null</c> to clear the panel.
    /// </summary>
    private void PublishSelectionToProperties(ListBox list)
    {
        CompareRowViewModel? row = null;
        if (list.SelectedItem is CompareRowViewModel selected)
        {
            row = selected;
        }
        else if (list.Selection?.SelectedIndexes.Count == 1)
        {
            // 多选（文本对比）模式没有单一 SelectedItem，按唯一选中索引取行。
            // Multi-select (text comparison) has no single SelectedItem; take the row by the only selected index.
            var index = list.Selection.SelectedIndexes[0];
            if (index >= 0 && index < list.ItemCount)
            {
                row = list.Items[index] as CompareRowViewModel;
            }
        }

        var path = row is not null && row.TreeNode is not null && !row.IsEditable ? ResolveRowPath(row, list) : null;
        WeakReferenceMessenger.Default.Send(new CompareSelectionChangedMessage(
            path,
            row?.TreeNode?.IsDirectory ?? false));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 仅差异导航（ScrollToIndex）触发滚动：普通点选只改 SelectedIndex，不产生任何滚动。
        // Only diff navigation (ScrollToIndex) triggers scrolling: a plain click only changes
        // SelectedIndex and never scrolls.
        if (string.Equals(e.PropertyName, nameof(CompareViewModel.ScrollToIndex), StringComparison.Ordinal))
        {
            ScrollToSelected();
            return;
        }

        // 对比状态决定拖动条是否可用（对比中锁定左右宽度）。
        // Compare state decides whether the splitters are draggable (widths locked while comparing).
        if (string.Equals(e.PropertyName, nameof(CompareViewModel.IsComparing), StringComparison.Ordinal))
        {
            UpdateDividerInteractivity();
            // 进入对比时把两列恢复等分（对比中宽度被锁定，因此这一步就是"开始对比即等分"）。
            // Entering a comparison restores an equal split (the widths are locked while comparing, so this is the "equal split on start" step).
            if (DataContext is CompareViewModel comparing && comparing.IsComparing)
            {
                ResetColumnWidths();
            }
        }

        // 数据加载/模式切换后重新挂接滚动容器：Rows/Mode/IsComparing 变化意味着列表已填充或
        // 列切换了显示内容，此时模板与内部 ScrollViewer 才真正实例化，之前的挂接可能失败。
        // Re-attach the scroll containers after data loads or a mode switch: Rows/Mode/IsComparing
        // changes mean the lists are populated or a column switched its content, so the templates and
        // their inner ScrollViewers are finally realized and an earlier attach may have failed.
        if (e.PropertyName is nameof(CompareViewModel.Rows)
            or nameof(CompareViewModel.Mode)
            or nameof(CompareViewModel.IsComparing))
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    AttachScrollViewers();
                    AttachCompareEditors();
                },
                DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// <para>按角色查找参与对比的列视图；未在对比时返回 <c>null</c>。查找前先按需补解析列表与滚动容器，
    /// 避免新增列后这里拿到未挂接的条目。</para>
    /// Finds the visual of the column playing the given role, or <c>null</c> while not comparing. The column's list and
    /// scroll container are resolved on demand first so an added column is never returned unresolved.
    /// </summary>
    private ColumnVisuals? FindCompareVisuals(CompareColumnSide side)
    {
        var visuals = _columnVisuals.FirstOrDefault(item => item.Column.Side == side);
        if (visuals is not null)
        {
            EnsureColumnResolved(visuals);
        }

        return visuals;
    }

    /// <summary>
    /// <para>差异导航滚动：把两个对比列都顶部对齐滚到 <see cref="CompareViewModel.ScrollToIndex"/>
    /// 指定的行。目标偏移 = 索引 × 行高，**不夹取到 Extent 内**（ScrollViewer 自身对越界偏移有
    /// 兜底；镜像用的 <see cref="ScrollListToIndex"/> 保持夹取行为不变）；<c>_syncing</c> 防止两侧
    /// 回写互相触发镜像。</para>
    /// Diff-navigation scroll: top-aligns both compare columns to the row specified by
    /// <see cref="CompareViewModel.ScrollToIndex"/>. Target offset = index × row height, **without
    /// clamping to the Extent** (the ScrollViewer itself bounds out-of-range offsets; the mirror's
    /// <see cref="ScrollListToIndex"/> keeps its clamping behavior); <c>_syncing</c> prevents the writes
    /// from mirroring each other.
    /// </summary>
    private void ScrollToSelected()
    {
        if (DataContext is not CompareViewModel vm || vm.Rows is null || vm.Rows.Count == 0)
        {
            return;
        }

        var index = vm.ScrollToIndex;
        if (index < 0 || index >= vm.Rows.Count)
        {
            return;
        }

        var left = FindCompareVisuals(CompareColumnSide.Left);
        var right = FindCompareVisuals(CompareColumnSide.Right);
        if (left?.List is null && right?.List is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (left?.List is { } leftList)
            {
                ScrollListTopAligned(leftList, left.Scroll, index);
            }

            if (right?.List is { } rightList)
            {
                ScrollListTopAligned(rightList, right.Scroll, index);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// <para>差异导航专用：把行列表顶部对齐滚到指定行索引，目标偏移 = 索引 × 行高，
    /// 不夹取到 Extent 内（ScrollViewer 自身对越界偏移有兜底）。滚动容器尚未解析时退化为
    /// <see cref="ItemsControl.ScrollIntoView"/> 尽力定位。</para>
    /// Diff-navigation-specific: top-aligns a row list to the given index, target offset = index × row
    /// height, without clamping to the Extent (the ScrollViewer itself bounds out-of-range offsets).
    /// Falls back to <c>ScrollIntoView</c> when the scroll container is not resolved yet.
    /// </summary>
    private static void ScrollListTopAligned(ListBox list, ScrollViewer? scroll, int index)
    {
        if (list.ItemsSource is not IReadOnlyList<CompareRowViewModel> rows || rows.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, rows.Count - 1);

        if (scroll is null)
        {
            list.ScrollIntoView(index);
            return;
        }

        var rowHeight = GetRowHeight(list);
        if (rowHeight <= 0)
        {
            list.ScrollIntoView(index);
            return;
        }

        scroll.Offset = new Vector(scroll.Offset.X, index * rowHeight);
    }

    // ===== 行双击与目录树展开 =====

    // ===== 文本对比编辑器（槽模型 ↔ 两侧编辑器） =====

    /// <summary>同步编辑器文档时置位，避免把程序写入当成用户编辑。</summary>
    private bool _syncingEditors;

    /// <summary>每个对比编辑器的高亮着色器（按行着色整行差异）。</summary>
    private readonly Dictionary<TextEditor, CompareLineHighlighter> _editors = [];

    /// <summary>最后一次右键命中的编辑器（右键菜单项据此定位编辑器）。</summary>
    private TextEditor? _contextEditor;

    /// <summary>编辑器滚动镜像中置位（防回环）。 Set while mirroring editor scrolling (guards against loops).</summary>
    private bool _syncingEditorScroll;

    /// <summary>
    /// <para>两个对比编辑器的滚动镜像：两侧文档等长（行=对齐槽），所以直接按像素镜像偏移就能保持同行对应，
    /// 不需要按行号换算。</para>
    /// Scroll mirroring between the two compare editors: both documents have the same length (one line per aligned slot), so
    /// mirroring the pixel offset keeps the rows aligned without any row-index conversion.
    /// </summary>
    private void OnCompareEditorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingEditorScroll || sender is not ScrollViewer source || !IsLoaded)
        {
            return;
        }

        var editors = EnumerateCompareEditors();
        var sourceEditor = editors.FirstOrDefault(pair => ReferenceEquals(ResolveEditorScrollViewer(pair.Editor), source));
        if (sourceEditor.Editor is null)
        {
            return;
        }

        var target = editors.FirstOrDefault(pair => pair.LeftSide != sourceEditor.LeftSide).Editor is { } targetEditor
            ? ResolveEditorScrollViewer(targetEditor)
            : null;
        if (target is null || Math.Abs(target.Offset.Y - source.Offset.Y) < 0.5)
        {
            return;
        }

        _syncingEditorScroll = true;
        try
        {
            target.Offset = new Vector(target.Offset.X, source.Offset.Y);
        }
        finally
        {
            _syncingEditorScroll = false;
        }
    }

    /// <summary>取编辑器内部的滚动容器（AvaloniaEdit 未公开该部件，按可视树解析）。</summary>
    /// Resolves the editor's inner scroll container (AvaloniaEdit does not expose the part, so the visual tree is used).
    private static ScrollViewer? ResolveEditorScrollViewer(TextEditor editor)
        => editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>
    /// <para>把两个对比编辑器同步成槽模型的投影（行号=对齐序号，填充槽显示斜杠），并刷新差异行着色与选中范围。</para>
    /// Synchronises both compare editors with the slot model's projection (line numbers are alignment indices, fillers show a
    /// slash) and refreshes the diff-line colouring and the selected slot range.
    /// </summary>
    private void SyncCompareEditors(TextEditor? skip = null)
    {
        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        // 同步过程本身也不能抛：任何编辑器异常都转成错误条 + 日志。
        // The synchronisation itself must not throw either: any editor failure becomes an error banner entry plus a log line.
        try
        {
            SyncCompareEditorsCore(vm, skip);
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>同步实现：写入两侧投影并刷新差异着色。 The synchronisation itself: writes both projections and refreshes the diff colouring.</summary>
    private void SyncCompareEditorsCore(CompareViewModel vm, TextEditor? skip)
    {
        _syncingEditors = true;
        try
        {
            foreach (var (editor, leftSide) in EnumerateCompareEditors())
            {
                // 编辑侧保持用户刚输入的内容与光标（模型已按同样的语义更新），只同步另一侧。
                // The edited side keeps the user's text and caret (the model was updated with the same semantics); only the other
                // side is synchronised.
                if (!ReferenceEquals(editor, skip))
                {
                    var text = vm.ProjectText(leftSide);
                    if (!string.Equals(editor.Text, text, StringComparison.Ordinal))
                    {
                        editor.Text = text;
                    }
                }

                // 差异行整行着色（不同=红、单侧=橙/蓝），与行列表期间的规则一致。
                // Whole-line diff colouring (different=red, single-side=orange/blue), matching the row list's rule.
                if (_editors.TryGetValue(editor, out var highlighter))
                {
                    highlighter.SetLines(vm.HighlightedSlots(leftSide).Select(index => index + 1));
                    editor.TextArea.TextView.InvalidateVisual();
                }
            }
        }
        finally
        {
            _syncingEditors = false;
        }
    }

    /// <summary>枚举当前对比中的编辑器及其所属侧。 Enumerates the compare editors with the side they show.</summary>
    private List<(TextEditor Editor, bool LeftSide)> EnumerateCompareEditors()
    {
        var result = new List<(TextEditor, bool)>();
        foreach (var visuals in _columnVisuals)
        {
            if (visuals.CompareEditor is { } editor && visuals.Column.IsCompareSide)
            {
                result.Add((editor, !visuals.Column.UsesRightCells));
            }
        }

        return result;
    }

    /// <summary>在列容器里取文本对比编辑器（按名字查找）。 Finds the compare text editor inside a column container.</summary>
    private static TextEditor? ResolveCompareEditor(ContentControl container)
        => ResolveEditor(container, "CompareTextEditor");

    /// <summary>按名字在列容器里查找编辑器。 Finds an editor by name inside a column container.</summary>
    private static TextEditor? ResolveEditor(ContentControl container, string name)
    {
        foreach (var editor in container.GetVisualDescendants().OfType<TextEditor>())
        {
            if (string.Equals(editor.Name, name, StringComparison.Ordinal))
            {
                return editor;
            }
        }

        return null;
    }

    /// <summary>
    /// <para>接线每一列的编辑器：浏览文本文件用编辑器（内容随列属性双向同步），文本对比用两侧编辑器（改动翻译成槽操作）。</para>
    /// Wires each column's editors: a browsed text file uses the editor (content synchronised both ways with the column's
    /// property) and a text comparison uses both sides' editors (changes translated into slot operations).
    /// </summary>
    private void AttachColumnEditors()
    {
        if (!IsLoaded)
        {
            return;
        }

        foreach (var visuals in _columnVisuals)
        {
            WireBrowseEditor(visuals);
        }

        AttachCompareEditors();
        SyncCompareEditors();
    }

    /// <summary>接线浏览文本编辑器：把列里的文本推给编辑器，并把用户的编辑写回列（标记已修改）。</summary>
    /// Wires the browsing text editor: the column's text is pushed into the editor and the user's edits are written back into
    /// the column (marking it modified).
    private void WireBrowseEditor(ColumnVisuals visuals)
    {
        var editor = ResolveEditor(visuals.Container, "BrowseTextEditor");
        if (editor is null)
        {
            return;
        }

        visuals.BrowseEditor = editor;
        if (visuals.BrowseWired)
        {
            return;
        }

        visuals.BrowseWired = true;
        visuals.ColumnHandler = (_, args) =>
        {
            if (args.PropertyName is not (nameof(CompareColumnViewModel.EditorText)
                or nameof(CompareColumnViewModel.IsTextEditorVisible)))
            {
                return;
            }

            _syncingEditors = true;
            try
            {
                if (!string.Equals(editor.Document.Text, visuals.Column.EditorText, StringComparison.Ordinal))
                {
                    // 写入延后到本次属性通知之后：在编辑器自身的变更通知里回写文档容易触发内部状态冲突。
                    // The write is deferred past the current notification: writing the document back from inside the editor's own
                    // change notification can trip internal state.
                    var text = visuals.Column.EditorText;
                    Dispatcher.UIThread.Post(
                        () =>
                        {
                            _syncingEditors = true;
                            try
                            {
                                // 写回整篇文档会把光标重置到文件开头，先记住光标位置再恢复。
                                // Replacing the whole document resets the caret to the top of the file, so it is captured and restored.
                                var caret = editor.CaretOffset;
                                editor.Document.Text = text;
                                editor.CaretOffset = Math.Clamp(caret, 0, editor.Document.TextLength);
                            }
                            finally
                            {
                                _syncingEditors = false;
                            }
                        },
                        DispatcherPriority.Background);
                }
            }
            finally
            {
                _syncingEditors = false;
            }
        };
        visuals.Column.PropertyChanged += visuals.ColumnHandler;
        editor.AddHandler(ContextRequestedEvent, OnEditorContextRequested, RoutingStrategies.Tunnel);
        editor.Document.Changed += (_, _) =>
        {
            if (_syncingEditors)
            {
                return;
            }

            // 编辑失败也不能闪退：异常写进错误区与日志。
            // An editing failure must not crash the app either: exceptions go to the error banner and the log.
            try
            {
                visuals.Column.EditorText = editor.Document.Text;
            }
            catch (Exception ex)
            {
                if (DataContext is CompareViewModel viewModel)
                {
                    ReportEditorFailure(viewModel, ex);
                }
                else
                {
                    LogService.Instance.Write(LogLevel.Error, "Compare", ex.ToString());
                }
            }
        };
        editor.Document.Text = visuals.Column.EditorText;
    }

    // ===== 文本编辑器的右键菜单 =====

    /// <summary>记录右键命中的编辑器（菜单项在弹出层里，取不到编辑器本身）。</summary>
    /// Records the editor a context request hit (menu items live in a popup and cannot resolve the editor themselves).
    private void OnEditorContextRequested(object? sender, ContextRequestedEventArgs e)
        => _contextEditor = sender as TextEditor;

    /// <summary>菜单项对应的编辑器：优先右键记录，回退到当前聚焦的编辑器。</summary>
    /// The editor a menu item applies to: the recorded context editor, falling back to the focused one.
    private TextEditor? ResolveMenuEditor()
        => _contextEditor ?? EnumerateCompareEditors().Select(pair => pair.Editor).FirstOrDefault(editor => editor.IsFocused)
            ?? _columnVisuals.Select(visuals => visuals.BrowseEditor).FirstOrDefault(editor => editor?.IsFocused == true);

    /// <summary>编辑器右键菜单：剪切。</summary>
    private void OnEditorCut(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuEditor() is { } editor)
        {
            editor.Cut();
        }
    }

    /// <summary>编辑器右键菜单：复制。</summary>
    private void OnEditorCopy(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuEditor() is { } editor)
        {
            editor.Copy();
        }
    }

    /// <summary>编辑器右键菜单：粘贴。</summary>
    private void OnEditorPaste(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuEditor() is { } editor)
        {
            editor.Paste();
        }
    }

    /// <summary>编辑器右键菜单：全选。</summary>
    private void OnEditorSelectAll(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuEditor() is { } editor)
        {
            editor.SelectAll();
        }
    }

    /// <summary>
    /// <para>文本对比编辑器右键菜单「复制到另一侧」：整行选中时复制这些行到对侧相同槽位；只在单行内选中一段文字时，
    /// 用选中文字替换对侧同一行的相同列范围。</para>
    /// The text-comparison editor's "copy to the other side": a whole-line selection copies those lines to the same slots on the other
    /// side, while a partial selection inside one line replaces the counterpart's same column range with the selected text.
    /// </summary>
    private void OnCopyEditorSelectionToOtherSide(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CompareViewModel vm || !vm.IsTextEditorMode || ResolveMenuEditor() is not { } editor)
        {
            return;
        }

        var (owner, leftSide) = FindCompareEditorSide(editor.Document);
        if (owner is null)
        {
            return;
        }

        try
        {
            var selection = editor.TextArea.Selection;
            var startLine = Math.Min(selection.StartPosition.Line, selection.EndPosition.Line) - 1;
            var endLine = Math.Max(selection.StartPosition.Line, selection.EndPosition.Line) - 1;
            var selectedText = selection.IsEmpty ? null : editor.SelectedText;

            // 只有「单行内、且没有整行覆盖」时才按列范围替换，其余情况按整行复制。
            // Only a single-line selection that does not cover whole lines is handled as a column range; everything else copies whole lines.
            var partial = selectedText is not null && startLine == endLine
                && selection.StartPosition.Column > 1;
            vm.CopyEditorSelection(
                fromLeftToRight: leftSide,
                startSlot: Math.Max(0, startLine),
                endSlot: Math.Max(0, endLine),
                selectedText: partial ? selectedText : null,
                startColumn: Math.Max(0, selection.StartPosition.Column - 1),
                endColumn: Math.Max(0, selection.EndPosition.Column - 1));
            SyncCompareEditors();
            ClearEditorUndo(editor);
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>
    /// <para>挂接对比编辑器：载入槽模型投影、拦截会改变行数的编辑（翻译成槽操作后重新投影两侧），
    /// 把行内编辑写回槽模型，并把编辑器选区同步成行选择（供顶部复制按钮使用）。</para>
    /// Attaches a compare editor: loads the slot model's projection, intercepts line-count-changing edits (translated into slot
    /// operations with both sides re-projected afterwards), writes in-line edits back into the slot model and mirrors the
    /// editor's selection into the row selection used by the toolbar's copy buttons.
    /// </summary>
    private void AttachCompareEditors()
    {
        if (!IsLoaded || DataContext is not CompareViewModel vm || !vm.IsTextEditorMode)
        {
            return;
        }

        foreach (var visuals in _columnVisuals)
        {
            var editor = ResolveCompareEditor(visuals.Container);
            visuals.CompareEditor = editor;
            if (editor is null)
            {
                continue;
            }

            if (_editors.ContainsKey(editor))
            {
                continue;
            }

            var highlighter = new CompareLineHighlighter(ResolveDiffBrush());
            editor.TextArea.TextView.BackgroundRenderers.Add(highlighter);
            editor.Document.Changed += OnCompareEditorChanged;
            editor.TextArea.SelectionChanged += OnCompareEditorSelectionChanged;
            // 右键菜单项在弹出层里取不到编辑器，与浏览编辑器一样在隧道阶段记录右键目标。
            // Menu items live in a popup and cannot resolve the editor, so record the context target in the tunnel phase
            // just like the browsing editor does.
            editor.AddHandler(ContextRequestedEvent, OnEditorContextRequested, RoutingStrategies.Tunnel);
            // Ctrl+Z / Ctrl+Y 由两侧共享的撤销历史处理（隧道阶段先拦截）。
            // Ctrl+Z / Ctrl+Y are handled by the history both panes share (intercepted in the tunnel phase).
            editor.AddHandler(KeyDownEvent, OnCompareEditorKeyDown, RoutingStrategies.Tunnel);
            // 两侧文档等长、行高相同，因此编辑器滚动直接按像素镜像即可保持同行对应。
            // Both documents have the same length and row height, so mirroring the pixel offsets keeps the rows aligned.
            if (ResolveEditorScrollViewer(editor) is { } scrollViewer)
            {
                scrollViewer.ScrollChanged += OnCompareEditorScrollChanged;
            }

            _editors[editor] = highlighter;
        }

        SyncCompareEditors();
    }

    /// <summary>取差异行底色（主题画刷优先，取不到时用半透明红）。 Resolves the diff-line brush (theme first, translucent red as fallback).</summary>
    private Avalonia.Media.IBrush ResolveDiffBrush()
        => this.TryFindResource("DiffBackgroundBrush", out var brush) && brush is Avalonia.Media.IBrush found
            ? found
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x40, 0xC4, 0x2B, 0x1C));

    /// <summary>
    /// <para>编辑器改动前置处理：行数会变化的编辑（Enter、删除换行、多行粘贴、剪切整行）在这里取消，
    /// 改由槽模型执行并重新投影两侧，保证两侧文档始终等长；行数不变的行内编辑直接放行。</para>
    /// Pre-change handling: an edit that changes the line count (Enter, deleting a line break, multi-line paste, cutting a
    /// whole line) is cancelled and executed through the slot model with both sides re-projected so the documents stay the
    /// same length; an in-line edit that keeps the line count passes through.
    /// </summary>
    private void OnCompareEditorChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_syncingEditors || sender is not TextDocument document
            || DataContext is not CompareViewModel vm || !vm.IsTextEditorMode)
        {
            return;
        }

        // 任何异常都不能让应用闪退（UI 事件里的未捕获异常会直接终止进程）：出错写进错误区与日志，保持视图可用。
        // No exception may crash the app (an uncaught exception in a UI event kills the process): failures go to the error banner and
        // the log, and the view stays usable.
        try
        {
            var (editor, leftSide) = FindCompareEditorSide(document);
            if (editor is null)
            {
                return;
            }

            // 编辑器的改动整体交给槽模型翻译（行内编辑只改本侧内容；行数变化则在对侧补/收填充槽），
            // 然后只把另一侧同步成新的投影，编辑侧保留用户输入与光标。
            // The editor change is translated by the slot model as a whole (an in-line edit only touches this side's content, a
            // count change adds/removes fillers on the other side) and only the other side is synchronised afterwards, so the
            // edited side keeps the user's text and caret.
            var (startLine, removedLines, lines, _) = DescribeChange(document, e);
            // 先记撤销点（两侧共享），再落到槽模型：一次结构编辑会连同对侧的填充槽一起回退。
            // Record the undo point (shared by both panes) before applying to the slot model, so a structural edit rolls back
            // together with the counterpart's filler.
            vm.RecordTextUndoPoint(
                structural: removedLines != lines.Count,
                leftSlot: CaretSlot(FindEditor(leftSide: true)),
                rightSlot: CaretSlot(FindEditor(leftSide: false)));
            vm.ApplyEditorLines(leftSide, startLine, removedLines, lines);
            // 变更通知里只更新模型：另一侧文档的写入与撤销栈清理全部延后到通知结束之后。
            // 在编辑器自身的变更通知里改文档（哪怕改的是另一侧）或清理撤销栈，都会触发编辑器内部状态冲突并导致闪退。
            // Inside a change notification only the model is updated: writing the other side's document and clearing undo stacks are
            // deferred until the notification has finished. Touching documents (even the other side) or undo stacks from inside a change
            // notification trips internal state in the editor and crashes the app.
            Dispatcher.UIThread.Post(
                () =>
                {
                    try
                    {
                        SyncCompareEditors(editor);
                        ClearEditorUndo(editor);
                    }
                    catch (Exception ex)
                    {
                        ReportEditorFailure(vm, ex);
                    }
                },
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>取某一侧的对比编辑器。 Returns a side's compare editor.</summary>
    private TextEditor? FindEditor(bool leftSide)
        => EnumerateCompareEditors().Where(pair => pair.LeftSide == leftSide).Select(pair => pair.Editor).FirstOrDefault();

    /// <summary>
    /// <para>编辑器处理失败时把原因写进错误区与日志：宁可显示一条错误，也不能让未处理异常结束进程。</para>
    /// Records an editor failure in the error banner and the log: showing an error beats letting an unhandled exception kill the
    /// process.
    /// </summary>
    private void ReportEditorFailure(CompareViewModel vm, Exception ex)
    {
        LogService.Instance.Write(LogLevel.Error, "Compare", ex.ToString());
        vm.ReportError(ex.Message);
    }

    /// <summary>取编辑器光标所在槽号（无光标时为 0）。 Returns the editor's caret slot (0 when there is no caret).</summary>
    private static int CaretSlot(TextEditor? editor) => editor is null ? 0 : Math.Max(0, editor.TextArea.Caret.Line - 1);

    /// <summary>清空编辑器自带的撤销栈（撤销由两侧共享的历史负责）。 Clears the editor's own undo stack (the shared history owns undo).</summary>
    private static void ClearEditorUndo(TextEditor editor) => editor.Document.UndoStack.ClearAll();

    /// <summary>
    /// <para>对比编辑器上的 Ctrl+Z / Ctrl+Y（含 Ctrl+Shift+Z）：回退两侧共享的编辑历史，结构编辑连同对侧填充槽一起回退，
    /// 因此两侧永远保持同行对应。</para>
    /// Ctrl+Z / Ctrl+Y (and Ctrl+Shift+Z) inside a compare editor: rolls the shared history back and forth, so a structural edit is
    /// undone together with the counterpart's filler and the rows stay aligned.
    /// </summary>
    private void OnCompareEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CompareViewModel vm || !vm.IsTextEditorMode
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is not (Key.Z or Key.Y))
        {
            return;
        }

        var redo = e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.Handled = true;
        try
        {
            var caret = redo ? vm.RedoText() : vm.UndoText();
            if (caret is not { } target)
            {
                return;
            }

            SyncCompareEditors();
            foreach (var (editor, leftSide) in EnumerateCompareEditors())
            {
                RestoreEditorCaret(editor, leftSide ? target.LeftSlot : target.RightSlot);
                ClearEditorUndo(editor);
            }
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>把光标放回指定槽所在行的末尾。 Places the caret at the end of the given slot's line.</summary>
    private static void RestoreEditorCaret(TextEditor editor, int slot)
    {
        var document = editor.Document;
        if (document.LineCount == 0)
        {
            return;
        }

        var number = Math.Clamp(slot + 1, 1, document.LineCount);
        var line = document.GetLineByNumber(number);
        editor.CaretOffset = line.Offset + line.Length;
        editor.ScrollToLine(number);
    }

    /// <summary>编辑器选区变化 → 同步成行选择，顶部复制按钮据此复制到另一侧。</summary>
    /// Selection changes mirror into the row selection so the toolbar's copy buttons know which lines to copy.
    private void OnCompareEditorSelectionChanged(object? sender, EventArgs e)
    {
        if (_syncingEditors || DataContext is not CompareViewModel vm || !vm.IsTextEditorMode
            || sender is not TextArea textArea)
        {
            return;
        }

        try
        {
            var (editor, _) = FindCompareEditorSide(textArea.Document);
            if (editor is null)
            {
                return;
            }

            var selection = textArea.Selection;
            var start = Math.Min(selection.StartPosition.Line, selection.EndPosition.Line) - 1;
            var end = Math.Max(selection.StartPosition.Line, selection.EndPosition.Line) - 1;
            var indices = new List<int>();
            for (var line = Math.Max(start, 0); line <= Math.Max(end, 0); line++)
            {
                indices.Add(line);
            }

            vm.UpdateRowSelection(indices);
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>
    /// <para>把一次文档改动换算成「受影响的整行范围 + 该范围替换后的行 + 被替换掉的旧行数」。</para>
    /// <para>注意：<c>Changed</c> 事件触发时文档**已经是改动后**的状态，所以新行直接从文档里读，
    /// 不能拿改动前的区域再套一次插入（那样会把同一个字符应用两遍，表现为输入一个字符出现两个、删除多删）。
    /// 旧行数由「新行数 − 本次插入的换行数 + 本次删除的换行数」还原。</para>
    /// Converts one document change into "the affected whole-line range, its replacement lines and how many old lines it replaced".
    /// Note that when <c>Changed</c> fires the document already holds the post-change state, so the new lines are read straight from the
    /// document — applying the insertion to a pre-change region again would insert the same character twice (typing one character shows
    /// two, deletions remove extra characters). The old line count is recovered as
    /// "new line count − inserted newlines + removed newlines".
    /// </summary>
    private static (int StartLine, int RemovedLines, List<string> Lines, int CaretColumn) DescribeChange(
        TextDocument document,
        DocumentChangeEventArgs e)
    {
        var startLine = document.GetLineByOffset(e.Offset).LineNumber - 1;
        var endOffset = Math.Min(e.Offset + e.InsertionLength, document.TextLength);
        var endLine = document.GetLineByOffset(endOffset).LineNumber - 1;
        var firstLine = document.GetLineByNumber(startLine + 1);
        var lastLine = document.GetLineByNumber(endLine + 1);
        var regionEnd = lastLine.Offset + lastLine.Length;
        var region = document.GetText(firstLine.Offset, Math.Max(0, regionEnd - firstLine.Offset));
        var lines = region.Split('\n').ToList();

        // 旧行数 = 新行数 − 插入的换行数 + 删除的换行数（换行只在受影响的整行范围内）。
        // Old line count = new line count − inserted newlines + removed newlines (newlines only occur inside the affected range).
        var netNewLines = CountNewlines(e.InsertedText.Text) - CountNewlines(e.RemovedText.Text);
        var removedLines = Math.Max(1, lines.Count - netNewLines);
        var caretColumn = lines.Count == 0 ? 0 : lines[^1].Length;
        return (startLine, removedLines, lines, caretColumn);
    }

    /// <summary>数一段文本里的换行符个数。 Counts the newline characters in a piece of text.</summary>
    private static int CountNewlines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>取某个文档所属的对比编辑器与它显示的侧（左/右）。 Finds the compare editor and side owning a document.</summary>
    private (TextEditor? Editor, bool LeftSide) FindCompareEditorSide(TextDocument? document)
    {
        if (document is null)
        {
            return (null, true);
        }

        foreach (var (editor, leftSide) in EnumerateCompareEditors())
        {
            if (ReferenceEquals(editor.Document, document))
            {
                return (editor, leftSide);
            }
        }

        return (null, true);
    }

    /// <summary>结构变化后把光标放回目标行与列。 Restores the caret to the given line and column after a structural change.</summary>
    /// <summary>
    /// <para>差异行整行背景着色器：按行号集合画半透明色块（与行列表期间的整行变色一致）。</para>
    /// Whole-line diff background renderer: paints translucent bands for the given line numbers, matching the row list's
    /// whole-row colouring.
    /// </summary>
    private sealed class CompareLineHighlighter(Avalonia.Media.IBrush brush) : IBackgroundRenderer
    {
        private HashSet<int> _lines = [];

        public KnownLayer Layer => KnownLayer.Background;

        public void SetLines(IEnumerable<int> lines) => _lines = [.. lines];

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_lines.Count == 0 || !textView.VisualLinesValid)
            {
                return;
            }

            // 渲染里的异常会直接崩掉应用：着色失败就放弃本次绘制。
            // An exception during rendering crashes the app, so a failed paint is simply skipped.
            try
            {
                using (drawingContext.PushClip(new Rect(textView.Bounds.Size)))
                {
                    foreach (var line in textView.VisualLines)
                    {
                        if (!_lines.Contains(line.FirstDocumentLine.LineNumber))
                        {
                            continue;
                        }

                        var y = line.VisualTop - textView.ScrollOffset.Y;
                        drawingContext.FillRectangle(brush, new Rect(0, y, textView.Bounds.Width, line.Height));
                    }
                }
            }
            catch (Exception)
            {
                // 忽略本次着色。
                // Skip this paint.
            }
        }
    }

    /// <summary>
    /// <para>「添加文件夹」按钮：弹出多选文件夹对话框，每个选中的文件夹占用一个文件列（优先填充空列，
    /// 不足时在末尾追加）。对比进行中不追加列。</para>
    /// "Add Folder" button: shows a multi-select folder dialog where each chosen folder takes one file
    /// column (filling empty columns first and appending when needed). No column is added while
    /// comparing.
    /// </summary>
    private async void OnAddFoldersClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        if (vm.IsComparing)
        {
            vm.ReportError(LanguageService.Instance.GetString("Compare_AddColumnWhileComparing"));
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Compare_AddFolder"),
                AllowMultiple = true
            });

            var paths = new List<string>();
            foreach (var folder in folders)
            {
                if (folder.TryGetLocalPath() is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }

            if (paths.Count > 0)
            {
                await vm.TryAddPathsAsync(paths);
            }
        }
        catch (Exception ex)
        {
            vm.ReportError(ex.Message);
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    /// <summary>
    /// <para>双击激活一行：目录行展开/折叠（浏览与对比都一样；对比中两侧共享同一节点，一侧展开即两侧同步展开），
    /// 文件行在文件夹对比中于新标签打开该文件的对比，浏览状态下的文件行不做处理。
    /// 「进入某个目录」改为右键菜单项，避免与展开手势冲突。</para>
    /// Activates a row on double-click: a folder row expands/collapses (both while browsing and comparing; a folder
    /// comparison shares one node so one side expands both), a file row opens its comparison in a new tab during a folder
    /// comparison, and a file row while browsing does nothing. Entering a folder moved to the context menu so it no longer
    /// competes with the expand gesture.
    /// </summary>
    private void ActivateRow(CompareRowViewModel row, ListBox list)
    {
        if (row.TreeNode is not { } node)
        {
            return;
        }

        if (node.IsDirectory)
        {
            _ = ToggleRowExpansionAsync(node, list);
            return;
        }

        if (DataContext is CompareViewModel vm
            && ResolveTextRowOwner(list) is { IsCompareSide: true }
            && vm.IsFolderMode)
        {
            vm.OpenFileComparisonInNewTab(node);
        }
    }

    /// <summary>
    /// <para>切换树行节点的展开状态：文件夹对比由文档接管（两侧共享一份对齐行列表），
    /// 浏览状态由所属列重建自己的可见行。</para>
    /// Toggles a tree row's expansion: the document takes over during a folder comparison (both sides share one
    /// aligned row list), while browsing lets the owning column rebuild its own visible rows.
    /// </summary>
    private async Task ToggleRowExpansionAsync(CompareTreeNodeViewModel node, Control source)
    {
        if (DataContext is not CompareViewModel vm || FindColumnFor(source) is not { } column)
        {
            return;
        }

        if (column.IsCompareSide && vm.IsComparing)
        {
            await vm.ToggleTreeExpansionAsync(node);
            return;
        }

        await column.ToggleTreeExpansionAsync(node);
    }

    /// <summary>
    /// <para>按可视元素反查它所在的文件列：直接取最近的行列表的 DataContext（列模板的 DataContext 就是列视图模型）。
    /// 不再依赖缓存的列条目，因此新增第 3、4…列时不会出现「找不到所属列」导致双击/拖拽失效的问题。</para>
    /// Resolves a control's file column from the nearest row list's DataContext (a column template's DataContext is its
    /// column view model). It no longer relies on the cached column visuals, so adding a third, fourth … column cannot
    /// break double-click or drag handling with "no owning column found".
    /// </summary>
    private CompareColumnViewModel? FindColumnFor(Control source)
    {
        if (FindListFor(source)?.DataContext is CompareColumnViewModel direct)
        {
            return direct;
        }

        // 兜底：某些模板/弹出结构里拿不到列表的 DataContext 时，按列容器反查（弹出菜单里的元素走不到这里，
        // 那条路径由右键请求时记录的上下文列负责）。
        // Fallback: when the list's DataContext is unavailable, resolve through the column containers (elements inside the
        // popup menu never get here — that path uses the context column recorded on the context request).
        foreach (var visuals in _columnVisuals)
        {
            if (ReferenceEquals(visuals.Container, source) || visuals.Container.IsVisualAncestorOf(source))
            {
                return visuals.Column;
            }
        }

        return null;
    }

    // ===== 系统文件拖拽 =====

    // ===== 文本行编辑（跨行断行/并行/删行） =====

    /// <summary>
    /// <para>行内编辑快捷键：回车在光标处断行、行首 Backspace 并入上一行、行尾 Delete 并入下一行，
    /// 结构变化后立即重算对比状态，并把光标移到正确位置。</para>
    /// In-line editing keys: Enter splits at the caret, Backspace at the start merges into the previous row and Delete at
    /// the end merges the next row in; the comparison state is recomputed immediately and the caret is restored.
    /// </summary>
    private void OnRowEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not CompareRowViewModel row
            || DataContext is not CompareViewModel vm || FindListFor(box) is not { } list)
        {
            return;
        }

        var leftSide = !IsRightList(list);
        var caret = box.CaretIndex;
        var length = box.Text?.Length ?? 0;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                RestoreCaret(list, SplitRow(list, row, leftSide, caret));
                return;
            case Key.Back when caret == 0 && box.SelectionStart == 0:
                e.Handled = true;
                RestoreCaret(list, JoinRowWithPrevious(list, row, leftSide));
                return;
            case Key.Delete when caret >= length:
                e.Handled = true;
                RestoreCaret(list, JoinRowWithNext(list, row, leftSide));
                return;
        }
    }

    /// <summary>断行：对比状态交给文档（两栏共享行列表），浏览状态交给所属列（各自的文件）。</summary>
    private (int Index, int Caret) SplitRow(ListBox list, CompareRowViewModel row, bool leftSide, int caret)
        => ResolveTextRowOwner(list) is { IsCompareSide: true } && DataContext is CompareViewModel vm && vm.IsComparing
            ? vm.SplitTextRow(row, leftSide, caret)
            : ResolveTextRowOwner(list)?.SplitTextRow(row, leftSide, caret) ?? (0, 0);

    /// <summary>与下一行合并（沿用同一套归属判定）。</summary>
    private (int Index, int Caret) JoinRowWithNext(ListBox list, CompareRowViewModel row, bool leftSide)
        => ResolveTextRowOwner(list) is { IsCompareSide: true } && DataContext is CompareViewModel vm && vm.IsComparing
            ? vm.JoinTextRowWithNext(row, leftSide)
            : ResolveTextRowOwner(list)?.JoinTextRowWithNext(row, leftSide) ?? (0, 0);

    /// <summary>并入上一行（沿用同一套归属判定）。</summary>
    private (int Index, int Caret) JoinRowWithPrevious(ListBox list, CompareRowViewModel row, bool leftSide)
        => ResolveTextRowOwner(list) is { IsCompareSide: true } && DataContext is CompareViewModel vm && vm.IsComparing
            ? vm.JoinTextRowWithPrevious(row, leftSide)
            : ResolveTextRowOwner(list)?.JoinTextRowWithPrevious(row, leftSide) ?? (0, 0);

    /// <summary>取行列表所属的文件列（浏览状态用它承载编辑与保存）。</summary>
    private static CompareColumnViewModel? ResolveTextRowOwner(ListBox list)
        => list.DataContext as CompareColumnViewModel;

    /// <summary>行列表上的 Delete 键：删除所选行在该侧的内容（两侧都空的行整行移除）。</summary>
    /// <param name="sender"><para>行列表。</para>The row list.</param>
    /// <param name="e"><para>按键事件。</para>The key event.</param>
    private void OnRowListKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not CompareViewModel vm)
        {
            return;
        }

        // 列表上的 Ctrl+A 与 Shift+↑/↓：与鼠标框选等价的行选择方式。
        // Ctrl+A and Shift+Up/Down on the list mirror what mouse range selection does.
        switch (e.Key)
        {
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.IsTextMode:
                e.Handled = true;
                list.Selection?.SelectAll();
                return;
            case Key.Up when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && vm.IsTextMode:
                e.Handled = ExtendSelectionByRow(list, -1);
                return;
            case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && vm.IsTextMode:
                e.Handled = ExtendSelectionByRow(list, 1);
                return;
        }

        if (e.Key != Key.Delete || !vm.IsTextMode)
        {
            return;
        }

        // 输入框内的 Delete 由编辑器自己处理，这里只处理列表本身获得焦点的情况。
        // Delete inside an editor belongs to the editor; this handles the list itself having focus.
        if (e.Source is TextBox)
        {
            return;
        }

        e.Handled = true;
        var indices = list.Selection?.SelectedIndexes?.ToList() ?? [];
        var leftSide = !IsRightList(list);
        if (ResolveTextRowOwner(list) is { IsCompareSide: true } && vm.IsComparing)
        {
            vm.UpdateRowSelection(indices);
            vm.DeleteSelectedTextRows(leftSide);
            return;
        }

        ResolveTextRowOwner(list)?.DeleteSelectedTextRows(indices);
    }

    /// <summary>按方向键把行选择范围扩展一行（Shift+↑/↓）。</summary>
    private static bool ExtendSelectionByRow(ListBox list, int delta)
    {
        if (list.ItemsSource is not IReadOnlyList<CompareRowViewModel> rows || rows.Count == 0
            || list.Selection is not { } selection)
        {
            return false;
        }

        var selected = selection.SelectedIndexes;
        if (selected is null || selected.Count == 0)
        {
            selection.Select(0);
            return true;
        }

        var min = selected.Min();
        var max = selected.Max();
        var target = Math.Clamp(delta < 0 ? min - 1 : max + 1, 0, rows.Count - 1);
        selection.Clear();
        selection.SelectRange(Math.Min(min, target), Math.Max(max, target));
        list.ScrollIntoView(target);
        return true;
    }

    /// <summary>文本对比的行右键菜单：删除所选行。</summary>
    private void OnDeleteSelectedTextRows(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        var leftSide = !IsRightTagged(sender);
        if (vm.IsComparing)
        {
            vm.DeleteSelectedTextRows(leftSide);
        }
    }

    /// <summary>
    /// <para>结构变化（断行/并行）后把光标放回目标行的编辑框：先滚到该行，等容器实现后再聚焦并设置光标。</para>
    /// After a structural change (split/join) the caret is restored to the target row's editor: the row is scrolled into
    /// view first, then focused once its container is realized.
    /// </summary>
    private static void RestoreCaret(ListBox list, (int Index, int Caret) target)
    {
        list.ScrollIntoView(target.Index);
        Dispatcher.UIThread.Post(() =>
        {
            var editor = list.ContainerFromIndex(target.Index)?
                .GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (editor is null)
            {
                return;
            }

            editor.Focus();
            editor.CaretIndex = Math.Clamp(target.Caret, 0, editor.Text?.Length ?? 0);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// <para>按可视元素反查它所在的行列表（行编辑框定位所在列用）：沿可视祖先直接找最近的 <see cref="ListBox"/>，
    /// 不依赖缓存，因此列数变化后依然可靠。</para>
    /// Finds the nearest <see cref="ListBox"/> among a control's visual ancestors (used to locate a row editor's column).
    /// Nothing is cached, so it stays reliable no matter how many columns exist.
    /// </summary>
    private ListBox? FindListFor(Control source)
        => source.GetSelfAndVisualAncestors().OfType<ListBox>().FirstOrDefault();

    /// <summary>该行列表是否属于右侧模板（对比右列渲染右侧单元格）。</summary>
    private static bool IsRightList(ListBox list)
        => (list.DataContext as CompareColumnViewModel)?.UsesRightCells == true;

    // ===== 从行拖出到另一个目录（拖拽复制） =====

    /// <summary>按下时记录的拖拽候选（触发事件 + 行 + 起始点），移动超过阈值后才真正开始拖拽。</summary>
    private PointerPressedEventArgs? _dragTrigger;
    private CompareRowViewModel? _dragRow;
    private ListBox? _dragList;
    private Point _dragOrigin;

    /// <summary>跨行框选的起点（在某一行上按下）。</summary>
    private ListBox? _rangeList;
    private int _rangeAnchor = -1;

    /// <summary>正文拖拽的负载路径（拖到文件夹行时用它复制，避免依赖平台拖拽数据的格式差异）。</summary>
    private string? _draggingPath;

    /// <summary>拖拽来源列：跨列复制的兜底目标需要排除它。</summary>
    private CompareColumnViewModel? _dragSourceColumn;

    /// <summary>右键菜单当前作用的行与列（在行上收到 ContextRequested 时记录）。</summary>
    private CompareRowViewModel? _contextRow;
    private CompareColumnViewModel? _contextColumn;

    /// <summary>记录拖拽候选：只有文件/文件夹行（非文本编辑行）可以拖出。</summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ClearDragCandidate();
        _rangeList = null;
        _rangeAnchor = -1;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is not Control { DataContext: CompareRowViewModel row } source
            || FindListFor(source) is not { } list)
        {
            return;
        }

        var index = IndexOfRow(list, row);
        if (index < 0)
        {
            return;
        }

        // 输入框内按下：先按「文字选择」处理，但记录行锚点——指针移动到别的行时就升级为跨行选中，
        // 与 Beyond Compare 里「拖动经过多行即选多行」的手感一致。
        // A press inside an editor starts as text selection, but the row anchor is recorded: once the pointer reaches
        // another row it turns into a cross-row selection, matching dragging over lines in Beyond Compare.
        _rangeList = list;
        _rangeAnchor = index;

        // Multiple 模式下列表的点击是切换语义：点新行不会清掉旧选中，高亮会一路残留。
        // 普通左键按下（无 Ctrl/Shift）在隧道阶段先清空旧选择，列表随后的处理只会选中按下的这一行。
        // A Multiple-mode list toggles on click: pressing a new row keeps the old rows highlighted. A plain left
        // press (no Ctrl/Shift) clears the old selection in the tunnel phase so the list's own handling selects only
        // the pressed row.
        if (list.Selection is { } selection
            && (list.SelectionMode & SelectionMode.Multiple) != 0
            && !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            selection.Clear();
            selection.Select(index);
        }

        // 双击：目录行展开/折叠（对比中两侧同步），文件行在文件夹对比里开新标签的文件对比。
        // 直接按 ClickCount 判定，不依赖手势事件的路由，避免列表项消费事件导致双击失效。
        // Double-click: a folder row expands/collapses (both sides in a folder comparison) and a file row opens its
        // comparison in a new tab while comparing. It is detected from ClickCount so no gesture routing can swallow it.
        if (e.ClickCount == 2)
        {
            ActivateRow(row, list);
            return;
        }

        if (source is TextBox)
        {
            return;
        }

        // 只有文件/文件夹行可以拖出到另一个目录。
        // Only file/folder rows can be dragged out into another directory.
        if (row.TreeNode is null)
        {
            return;
        }

        _dragTrigger = e;
        _dragRow = row;
        _dragList = list;
        _dragOrigin = e.GetPosition(this);
        _dragSourceColumn = FindColumnFor(source);
    }

    /// <summary>
    /// <para>移动超过阈值时开始拖拽：把该行的完整路径放进拖拽数据（文本格式），
    /// 落到另一个文件夹行上即复制进该目录。</para>
    /// Starts a drag once the pointer moved past the threshold: the row's full path becomes the drag payload (text format),
    /// and dropping it on another folder row copies it into that folder.
    /// </summary>
    private async void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        ExtendRangeSelection(e);

        var trigger = _dragTrigger;
        var row = _dragRow;
        var list = _dragList;
        if (trigger is null || row is null || list is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ClearDragCandidate();
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragOrigin.X) < DragThreshold
            && Math.Abs(position.Y - _dragOrigin.Y) < DragThreshold)
        {
            return;
        }

        ClearDragCandidate();
        if (ResolveRowPath(row, list) is not { } path)
        {
            return;
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(path));
        // 应用内拖拽额外记录路径，作为拖放数据格式差异的兜底。
        // An in-view drag also records the path, covering platform differences in the payload's text format.
        _draggingPath = path;
        await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Copy);
    }

    /// <summary>
    /// <para>按住左键在某一行按下后拖过其它行 → 选中整段（跨行框选）；松开或移到行外即结束。</para>
    /// Dragging over other rows while the left button is held selects the whole range (cross-row selection); releasing or
    /// leaving the rows ends it.
    /// </summary>
    private void ExtendRangeSelection(PointerEventArgs e)
    {
        if (_rangeList is not { } list || _rangeAnchor < 0
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 指针下的元素给出当前行；行内的输入框/文本块都带行上下文，列表空白处则忽略。
        // The element under the pointer gives the current row: editors and text blocks inside a row carry the row context,
        // while empty list space is ignored.
        if (e.Source is not Control { DataContext: CompareRowViewModel row })
        {
            return;
        }

        var index = IndexOfRow(list, row);
        if (index < 0 || index == _rangeAnchor)
        {
            return;
        }

        var start = Math.Min(_rangeAnchor, index);
        var end = Math.Max(_rangeAnchor, index);
        var selection = list.Selection;
        if (selection is null)
        {
            return;
        }

        selection.Clear();
        selection.SelectRange(start, end);
    }

    /// <summary>取行在列表中的索引（列表的 ItemsSource 就是文档的行列表）。</summary>
    private static int IndexOfRow(ListBox list, CompareRowViewModel row)
    {
        if (list.ItemsSource is not IReadOnlyList<CompareRowViewModel> rows)
        {
            return -1;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            if (ReferenceEquals(rows[index], row))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>清空当前拖拽候选。</summary>
    private void ClearDragCandidate()
    {
        _dragTrigger = null;
        _dragRow = null;
        _dragList = null;
    }

    /// <summary>解析树行对应的绝对路径（浏览行自带路径，对比行用所在侧的根目录加相对路径）。</summary>
    private string? ResolveRowPath(CompareRowViewModel row, ListBox list)
    {
        if (row.TreeNode is not { } node)
        {
            return null;
        }

        if (node.FullPath.Length > 0)
        {
            return node.FullPath;
        }

        if (DataContext is not CompareViewModel vm || FindColumnFor(list) is not { } column)
        {
            return null;
        }

        var root = column.Side == CompareColumnSide.Right && vm.IsComparing ? vm.RightPath : column.Path;
        return root.Length == 0
            ? null
            : Path.Combine(root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    // ===== 右键菜单（条目操作与文本复制） =====

    /// <summary>
    /// <para>文本对比：把选中行的文本复制到同一行的另一侧（右侧模板传入 Tag=Right）。</para>
    /// Text comparison: copies the selected rows' text to the same rows on the other side (the right template passes
    /// Tag=Right).
    /// </summary>
    private void OnCopyTextToOtherSide(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CompareViewModel vm)
        {
            vm.CopySelectedTextRows(fromLeftToRight: !ContextIsRightSide(sender));
        }
    }

    /// <summary>目录对比：把该行条目复制到另一边。</summary>
    private async void OnCopyToOtherSide(object? sender, RoutedEventArgs e)
        => await TransferComparedEntryAsync(sender, move: false);

    /// <summary>目录对比：把该行条目移动到另一边。</summary>
    private async void OnMoveToOtherSide(object? sender, RoutedEventArgs e)
        => await TransferComparedEntryAsync(sender, move: true);

    /// <summary>
    /// <para>浏览状态：把该行条目复制/移动到相邻列的目录（Tag 给出方向）。</para>
    /// Browsing: copies or moves the row's entry into the neighbouring column's folder (the tag gives the direction).
    /// </summary>
    private async void OnCopyToNeighbourColumn(object? sender, RoutedEventArgs e)
        => await TransferToNeighbourAsync(sender, move: false);

    /// <summary>浏览状态：把该行条目移动到相邻列的目录。</summary>
    private async void OnMoveToNeighbourColumn(object? sender, RoutedEventArgs e)
        => await TransferToNeighbourAsync(sender, move: true);

    /// <summary>删除该行条目（目录递归删除），确认后执行并刷新视图。</summary>
    private async void OnDeleteEntry(object? sender, RoutedEventArgs e)
    {
        var (path, isDirectory, column) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        var owner = DialogService.GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (!await DialogService.ConfirmAsync(owner,
                LanguageService.Instance.GetString("Dialog_ConfirmDelete"),
                string.Format(LanguageService.Instance.GetString("Dialog_ConfirmDeleteMessage"), name),
                LanguageService.Instance.GetString("Menu_Delete")))
        {
            return;
        }

        try
        {
            if (isDirectory)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }

            await ReloadAfterEntryChangeAsync(column);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(ex);
        }
    }

    /// <summary>重命名该行条目（文件或目录），成功后刷新视图。</summary>
    private async void OnRenameEntry(object? sender, RoutedEventArgs e)
    {
        var (path, isDirectory, column) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        var owner = DialogService.GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        var oldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var newName = await DialogService.InputAsync(owner, LanguageService.Instance.GetString("Dialog_Rename"), oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName
            || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return;
        }

        var target = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, newName);
        try
        {
            if (isDirectory)
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            await ReloadAfterEntryChangeAsync(column);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(ex);
        }
    }

    /// <summary>在该行所在目录（目录行则为它本身）新建文件夹，成功后刷新视图。</summary>
    private async void OnCreateFolder(object? sender, RoutedEventArgs e)
    {
        var (path, isDirectory, column) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        var owner = DialogService.GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        var directory = isDirectory ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        if (await ExplorerItemCreationService.CreateAsync(owner, directory, isDirectory: true) is not null)
        {
            await ReloadAfterEntryChangeAsync(column);
        }
    }

    /// <summary>复制该行条目的完整路径到剪贴板。</summary>
    private async void OnCopyEntryPath(object? sender, RoutedEventArgs e)
    {
        var (path, _, _) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(path);
        }
    }

    /// <summary>在系统文件资源管理器中定位该行条目。</summary>
    /// <summary>
    /// <para>浏览状态下「进入此文件夹」：把该列重新根到双击的目录（路径框同步为新路径）。</para>
    /// "Enter this folder" while browsing: re-roots the column at the chosen directory (the path box follows).
    /// </summary>
    private async void OnEnterFolder(object? sender, RoutedEventArgs e)
    {
        if (_contextRow?.TreeNode is not { IsDirectory: true } node || _contextColumn is not { } column)
        {
            return;
        }

        await column.EnterDirectoryAsync(node);
    }

    private void OnOpenEntryInExplorer(object? sender, RoutedEventArgs e)
    {
        var (path, _, _) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(path))
            {
                // Windows 上定位到文件本身；其他平台（或目录）直接打开所在目录。
                // On Windows select the file itself; other platforms (or directories) open the containing folder.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }

            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    /// <summary>目录对比中把某侧条目复制/移动到另一侧（同一相对路径）。</summary>
    private async Task TransferComparedEntryAsync(object? sender, bool move)
    {
        var (path, _, column) = ResolveEntry(sender);
        if (DataContext is not CompareViewModel vm || path is null)
        {
            return;
        }

        var sourceIsLeft = !ContextIsRightSide(sender);
        var targetColumn = sourceIsLeft
            ? vm.Columns.FirstOrDefault(candidate => candidate.Side == CompareColumnSide.Right)
            : vm.Columns.FirstOrDefault(candidate => candidate.Side == CompareColumnSide.Left);
        var target = targetColumn?.Path;
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
        {
            vm.ReportError(LanguageService.Instance.GetString("Compare_NoNeighbourFolder"));
            return;
        }

        await TransferAsync(vm, column ?? _contextColumn, path, target, move);
    }

    /// <summary>浏览状态中把条目复制/移动到相邻列的目录。</summary>
    private async Task TransferToNeighbourAsync(object? sender, bool move)
    {
        var (path, _, column) = ResolveEntry(sender);
        if (DataContext is not CompareViewModel vm || path is null || column is null)
        {
            return;
        }

        var index = vm.Columns.IndexOf(column);
        var neighbourIndex = index + (ContextIsRightSide(sender) ? 1 : -1);
        var target = neighbourIndex >= 0 && neighbourIndex < vm.Columns.Count
            ? vm.Columns[neighbourIndex].Path
            : string.Empty;
        if (target.Length == 0 || !Directory.Exists(target))
        {
            vm.ReportError(LanguageService.Instance.GetString("Compare_NoNeighbourFolder"));
            return;
        }

        await TransferAsync(vm, column, path, target, move);
    }

    /// <summary>
    /// <para>执行一次不覆盖的复制/移动（复用资源管理器的传输实现），成功后刷新相关视图。</para>
    /// Runs one non-overwriting copy/move (reusing the explorer's transfer implementation) and refreshes the affected
    /// views afterwards.
    /// </summary>
    private async Task TransferAsync(
        CompareViewModel vm,
        CompareColumnViewModel? column,
        string source,
        string targetDirectory,
        bool move)
    {
        try
        {
            ExplorerFileOperations.Transfer(source, targetDirectory, move);
            await ReloadAfterEntryChangeAsync(column);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(ex);
        }
    }

    /// <summary>
    /// <para>条目变化（复制/移动/删除/重命名/新建文件夹）后刷新视图：对比中重新比对两侧，
    /// 浏览中重新加载该列。</para>
    /// Refreshes the view after an entry changed (copy/move/delete/rename/new folder): comparing re-runs the
    /// comparison and browsing reloads that column.
    /// </summary>
    private async Task ReloadAfterEntryChangeAsync(CompareColumnViewModel? column)
    {
        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        if (vm.IsComparing)
        {
            await vm.RefreshCompareAsync();
            return;
        }

        if (column is not null)
        {
            await column.ReloadContentAsync();
            return;
        }

        // 拿不到具体列（落点在工具栏/列间隔等）时刷新所有列，保证复制进去的文件一定出现在列表里。
        // When no specific column could be resolved (the drop landed on the toolbar or a divider) every column is reloaded so the copied
        // file always shows up.
        foreach (var visuals in _columnVisuals.ToList())
        {
            await visuals.Column.ReloadContentAsync();
        }
    }

    /// <summary>把操作失败写入对比视图的错误区（并留下日志）。</summary>
    private void ReportOperationFailure(Exception ex)
    {
        LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        if (DataContext is CompareViewModel vm)
        {
            vm.ReportError(ex.Message);
        }
    }

    /// <summary>
    /// <para>解析菜单/拖拽目标对应的条目：返回绝对路径、是否为目录与所在列。
    /// 浏览行的节点自带绝对路径；对比行用点击那一侧的对比根目录加相对路径还原。</para>
    /// Resolves the entry behind a menu click or drop: absolute path, whether it is a directory and the owning column.
    /// A browsing row's node carries the absolute path; a compared row rebuilds it from the clicked side's comparison
    /// root plus the relative path.
    /// </summary>
    private (string? Path, bool IsDirectory, CompareColumnViewModel? Column) ResolveEntry(object? sender)
    {
        // 优先使用右键时记录的上下文行（菜单项本身在弹出层里，取不到列）；直接调用（如拖放）时回退到 sender。
        // The row recorded by the context request wins (menu items live in a popup and cannot resolve a column); a direct
        // call (drops) falls back to the sender.
        var row = _contextRow
            ?? (sender as Control)?.DataContext as CompareRowViewModel;
        if (DataContext is not CompareViewModel vm || row?.TreeNode is not { } node)
        {
            return (null, false, null);
        }

        var column = _contextColumn
            ?? ((sender as Control) is { } control ? FindColumnFor(control) : null);
        if (node.FullPath.Length > 0)
        {
            return (node.FullPath, node.IsDirectory, column);
        }

        if (column is null)
        {
            return (null, node.IsDirectory, null);
        }

        var sourceIsLeft = !IsRightTagged(sender);
        var rootColumn = vm.IsComparing
            ? vm.Columns.FirstOrDefault(candidate => candidate.Side == (sourceIsLeft ? CompareColumnSide.Left : CompareColumnSide.Right)) ?? column
            : column;
        var root = rootColumn.Path;
        if (root.Length == 0)
        {
            return (null, node.IsDirectory, column);
        }

        var relative = node.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        return (Path.Combine(root, relative), node.IsDirectory, column);
    }

    /// <summary>读取菜单项的 Tag 判断它是否来自右侧模板。</summary>
    private static bool IsRightTagged(object? sender)
        => sender is MenuItem { Tag: string tag } && string.Equals(tag, "Right", StringComparison.Ordinal);

    /// <summary>判断右键目标是否位于右侧栏：优先用右键时记录的列，回退到菜单项 Tag。</summary>
    private bool ContextIsRightSide(object? sender)
        => _contextColumn is { UsesRightCells: true } || IsRightTagged(sender);

    /// <summary>
    /// <para>右键请求：行没有提供菜单（十六进制/元数据行、浏览状态下的文本行）时吞掉请求，
    /// 避免弹出空菜单；其余情况交给行上挂的 <c>ContextMenu</c>。</para>
    /// Context request: when the row offers no menu (hex/metadata rows and text rows while browsing) the request is
    /// swallowed so no empty popup appears; otherwise the row's <c>ContextMenu</c> opens as usual.
    /// </summary>
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control { DataContext: CompareRowViewModel row } control)
        {
            return;
        }

        // 菜单项在弹出层里，取不到行列表祖先；这里记录右键目标（行 + 列），菜单命令据此定位文件。
        // Menu items live in a popup and cannot find the row list through their ancestors, so the context request records
        // the target (row + column) and the menu commands resolve the file from it.
        _contextRow = row;
        _contextColumn = FindColumnFor(control);
        if (row.Menu == CompareRowMenuKind.None)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// <para>拖拽悬停事件：仅接受文件/文件夹路径的复制拖放（Avalonia 12 的
    /// <c>DataTransfer</c> 数据载体与 <c>DataFormat.File</c> 格式）。
    /// OS 拖拽（如 Windows 资源管理器）的文件格式可能以平台实例暴露，
    /// <c>Contains(DataFormat.File)</c> 不命中，故再以 <c>TryGetFiles</c> 直接探测，
    /// 保证光标与 Drop 的实际消费结果一致。</para>
    /// Drag-over event: accepts copy drops of file/folder paths only (Avalonia 12's
    /// <c>DataTransfer</c> payload with the <c>DataFormat.File</c> format).
    /// OS-originated drags (e.g. from Windows Explorer) may expose the file format as a
    /// platform instance that <c>Contains(DataFormat.File)</c> misses, so the handler also
    /// probes <c>TryGetFiles</c> directly, keeping the cursor in sync with what Drop consumes.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFilePayload(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// <para>判定拖放数据是否携带文件/文件夹路径：先检查通用文件格式（应用内/进程内拖拽），
    /// 再以 <c>TryGetFiles</c> 直接探测（与 Drop 消费一致），最后扫描格式列表中的系统文件格式名。</para>
    /// Determines whether the drag payload carries file/folder paths: checks the universal file format
    /// first (in-process drags), then probes <c>TryGetFiles</c> directly (matching what Drop consumes),
    /// and finally scans the format list for the OS file-format name.
    /// </summary>
    private bool HasFilePayload(IDataTransfer transfer)
        => UotanStudio.Views.Controls.FileDropPaths.HasFilePayload(transfer, "CompareView")
            || LooksLikeExistingPath(transfer)
            || _draggingPath is not null;

    /// <summary>
    /// <para>拖放事件：把拖入的文件/文件夹路径交给视图模型，每个路径占用一个文件列
    /// （含 OS 拖拽的平台格式提取）。立即标记 <c>Handled</c> 并消费数据，再异步加载——避免异步
    /// 异常导致拖放状态丢失。</para>
    /// Drop event: hands the dropped file/folder paths to the view model, where each path takes one file
    /// column (including platform-format extraction for OS drags). Marks <c>Handled</c> and consumes the
    /// payload immediately, then loads asynchronously — so an async failure cannot leave the drop
    /// unhandled.
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        var paths = ExtractDropPaths(e.DataTransfer);
        var inViewDrag = _draggingPath is not null;
        _draggingPath = null;
        if (paths.Count == 0)
        {
            return;
        }

        // 拖到某个文件夹行上 → 复制进该目录（从资源管理器或应用内拖一个文件到另一个目录即可完成复制）。
        // Dropping onto a folder row copies into that folder (drag a file from the explorer or from the app onto another
        // directory to copy it).
        // 视图内拖拽（跨列复制）再按指针位置兜底：落在列表空白、列头、列间隔或工具栏上时同样能定位目标列，
        // 否则「拖到另一列」会因为落点不在行上而毫无反应。
        // An in-view drag (cross-column copy) also falls back to the pointer position so dropping on empty list space, a column header,
        // a divider or the toolbar still resolves the target column instead of doing nothing.
        var dropTarget = ResolveDropTarget(e.Source, inViewDrag)
            ?? (inViewDrag ? ResolveInViewDropFolder(e.GetPosition(this)) : null);
        if (dropTarget is not null)
        {
            var copied = false;
            foreach (var path in paths)
            {
                try
                {
                    ExplorerFileOperations.Transfer(path, dropTarget, move: false);
                    copied = true;
                }
                catch (Exception ex)
                {
                    ReportOperationFailure(ex);
                }
            }

            // 复制成功后自动刷新目标列（对比中重新比对、浏览中重新枚举目录内容），不需要再手动点刷新。
            // After a successful copy the target column refreshes itself (re-compares while comparing, re-enumerates the folder while
            // browsing), so no manual refresh is needed.
            if (copied)
            {
                var column = ResolveEntry(e.Source) is var (_, _, resolved) ? resolved : null;
                column ??= _columnVisuals
                    .Select(visuals => visuals.Column)
                    .FirstOrDefault(candidate => string.Equals(candidate.Path, dropTarget, StringComparison.OrdinalIgnoreCase));
                await ReloadAfterEntryChangeAsync(column);
            }

            return;
        }

        if (paths.Count > 0)
        {
            await vm.TryAddPathsAsync(paths);
        }
    }

    /// <summary>
    /// <para>解析拖拽落点：落在目录树行（目录）上时返回该目录的绝对路径，否则返回 <c>null</c>（按「加载为新列」处理）。</para>
    /// Resolves a drop target: a directory tree row yields that directory's absolute path, anything else returns
    /// <c>null</c> (handled as "load into columns").
    /// </summary>
    /// <param name="source"><para>拖放命中的元素。</para>The element hit by the drop.</param>
    /// <param name="inViewDrag"><para>是否来自视图内的行拖拽（可放宽落点：同一列里的任何位置都算落到该列目录）。</para>Whether the drag started inside the view (its target may fall back to the column's own folder).</param>
    /// <summary>
    /// <para>视图内拖拽按指针位置兜底定位目标目录：先用指针所在列的目录，再退一步取「不是拖拽来源列」的第一个目录列。</para>
    /// Position-based fallback for an in-view drag: first the folder of the column under the pointer, then the first folder column that
    /// is not the drag source.
    /// </summary>
    /// <param name="position"><para>指针在视图内的坐标。</para>The pointer position inside the view.</param>
    private string? ResolveInViewDropFolder(Point position)
    {
        var column = ResolveColumnAt(position);
        if (column is not null && Directory.Exists(column.Path))
        {
            return column.Path;
        }

        foreach (var visuals in _columnVisuals)
        {
            if (!ReferenceEquals(visuals.Column, _dragSourceColumn) && Directory.Exists(visuals.Column.Path))
            {
                return visuals.Column.Path;
            }
        }

        return null;
    }

    /// <summary>取指针所在（或最接近）的列。 Returns the column under the pointer, or the closest one.</summary>
    private CompareColumnViewModel? ResolveColumnAt(Point position)
    {
        CompareColumnViewModel? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var visuals in _columnVisuals)
        {
            var bounds = visuals.Container.Bounds;
            if (bounds.Contains(position))
            {
                return visuals.Column;
            }

            var distance = Math.Min(Math.Abs(position.X - bounds.Left), Math.Abs(position.X - bounds.Right));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = visuals.Column;
            }
        }

        return nearest;
    }

    private string? ResolveDropTarget(object? source, bool inViewDrag)
    {
        if (source is not Control { DataContext: CompareRowViewModel row } control
            || row.TreeNode is not { IsDirectory: true } node)
        {
            // 视图内拖拽落在非目录行上时，退化为「落到该行所在列的目录」。
            // An in-view drag dropped on a non-folder row falls back to the folder of that row's column.
            if (inViewDrag && source is Control controlOnRow
                && FindColumnFor(controlOnRow) is { } fallbackColumn
                && Directory.Exists(fallbackColumn.Path))
            {
                return fallbackColumn.Path;
            }

            return null;
        }

        if (node.FullPath.Length > 0)
        {
            return node.FullPath;
        }

        var column = FindColumnFor(control);
        if (column is null)
        {
            return null;
        }

        var root = column.Side == CompareColumnSide.Right && DataContext is CompareViewModel comparing && comparing.IsComparing
            ? comparing.RightPath
            : column.Path;
        return root.Length == 0
            ? null
            : Path.Combine(root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// <para>从拖放数据提取文件/文件夹路径：先按通用文件格式取条目，失败时再按平台文件
    /// 格式（<c>FileDrop</c>）取条目数据（宽容枚举 <see cref="IStorageItem"/>）。</para>
    /// Extracts file/folder paths from the drag payload: first via the universal file format, then
    /// via the platform file format (<c>FileDrop</c>) reading the item data as an enumerable of
    /// <see cref="IStorageItem"/>.
    /// </summary>
    private static IReadOnlyList<string> TryExtractPaths(IDataTransfer transfer)
        => UotanStudio.Views.Controls.FileDropPaths.TryExtractPaths(transfer, "CompareView");

    /// <summary>
    /// <para>从拖放数据取路径：优先平台文件格式（资源管理器等外部拖拽），其次文本格式
    /// （视图内从行拖出的负载，必须是真实存在的路径）。</para>
    /// Extracts paths from a drag payload: the platform file formats first (external drags such as Explorer), then the
    /// text format (an in-view row drag, which must name an existing path).
    /// </summary>
    private IReadOnlyList<string> ExtractDropPaths(IDataTransfer transfer)
    {
        var paths = TryExtractPaths(transfer);
        if (paths.Count > 0)
        {
            return paths;
        }

        if (TryGetDragText(transfer) is { } text && (File.Exists(text) || Directory.Exists(text)))
        {
            return [text];
        }

        // 应用内拖拽：拖放数据的文本格式取不到时用拖出时记录的路径。
        // In-view drags: when the payload's text format cannot be read, use the path recorded when the drag started.
        if (_draggingPath is { } recorded && (File.Exists(recorded) || Directory.Exists(recorded)))
        {
            return [recorded];
        }

        return paths;
    }

    /// <summary>判断拖放数据是否是「视图内拖出的行路径」（用于拖拽光标反馈）。</summary>
    private static bool LooksLikeExistingPath(IDataTransfer transfer)
        => TryGetDragText(transfer) is { } text && (File.Exists(text) || Directory.Exists(text));

    /// <summary>读取拖放数据中的文本负载（内建拖拽用文本格式携带路径）；异常与空文本返回 <c>null</c>。</summary>
    private static string? TryGetDragText(IDataTransfer transfer)
    {
        try
        {
            var text = transfer.TryGetText();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// <para>缩进换算：把条目深度转换为左边距（基础 8 像素 + 深度 × 14 像素），文件夹比较按层级缩进路径。
/// 基础边距用于把图标与行首的状态色条分开，避免第一层条目紧贴色条。在 XAML 中以
/// <c>{x:Static compareviews:DepthToIndentConverter.Instance}</c> 引用。</para>
/// Converts a row depth to a left margin (an 8 px base plus depth × 14 px), indenting folder rows by their level. The base margin
/// separates the icon from the status stripe at the start of the row so the first level does not touch it. Referenced from XAML as
/// <c>{x:Static compareviews:DepthToIndentConverter.Instance}</c>.
/// </summary>
public sealed class DepthToIndentConverter : IValueConverter
{
    /// <summary>共享实例，供 XAML 以 <c>{x:Static}</c> 引用。<para>Shared instance for XAML <c>{x:Static}</c> use.</para></summary>
    public static DepthToIndentConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => 0.0
        };

        // 基础边距 + 层级缩进：状态色条（3px 列）与图标之间留出可见间隔。
        // Base margin plus per-level indentation: leaves a visible gap between the 3 px status stripe and the icon.
        return new Thickness((depth * 14.0) + 8.0, 0, 0, 0);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
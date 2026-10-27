using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Compare;

namespace UotanStudio.Views.Compare;

/// <summary>
/// Compare view: generates side-by-side columns from the column collection (each gap holding a divider
/// and a draggable splitter), where a column shows a directory tree (double-click to expand, collapsed
/// right after import), a single file's content or comparison cells. The view also wires OS file
/// drag-and-drop (each dropped path takes one column) plus synchronised scrolling and selection
/// between the two compare columns.
/// <para>对比视图：按文件列集合动态生成并排的列（列间带分割线与可拖动分隔条），每列显示目录树
/// （双击展开，导入后保持折叠）、单文件内容或对比结果单元格。视图层同时接线系统文件拖拽
/// （拖入的每个路径占用一个文件列）以及对比两列的同步滚动与选中联动。</para>
/// </summary>
public partial class CompareView : UserControl
{
    /// <summary>
    /// Row-template <c>MinHeight</c>: every row is a fixed 20 px, used to estimate the row index
    /// from a scroll offset (fallback when measuring fails).
    /// <para>行模板 <c>MinHeight</c>：各行恒为 20px，用于按偏移估算行索引（实测失败时兜底）。</para>
    /// </summary>
    private const double RowMinHeight = 20.0;

    /// <summary>
    /// Column gap width: the 1 px divider line sits in the middle with draggable space around it.
    /// <para>列间隔宽度：1px 分割线落在中间，两侧留出可抓取的拖动区域。</para>
    /// </summary>
    private const double DividerWidth = 5.0;

    /// <summary>
    /// Minimum width of one column; it lives on the column definition so the <see cref="GridSplitter"/>
    /// honours it while dragging.
    /// <para>单列最小宽度：写在列定义上，<see cref="GridSplitter"/> 拖动时才会遵守。</para>
    /// </summary>
    private const double MinColumnWidth = 140.0;

    /// <summary>
    /// Minimum pointer movement (in pixels) before a drag starts; anything smaller counts as a click.
    /// <para>开始拖拽所需的最小移动距离（像素）：小于它视为点击，避免误触开始拖拽。</para>
    /// </summary>
    private const double DragThreshold = 4.0;

    private readonly List<ColumnVisuals> _columnVisuals = [];
    private readonly List<GridSplitter> _columnDividers = [];
    private readonly List<ColumnDefinition> _columnDefinitions = [];
    private CompareViewModel? _vm;
    private bool _syncing;
    private bool _syncingSelection;

    private double _mirroredLeftY = double.NaN;
    private double _mirroredRightY = double.NaN;

    /// <summary>
    /// Creates the compare view and initializes its components.
    /// <para>创建对比视图并初始化组件。</para>
    /// </summary>
    public CompareView()
    {
        InitializeComponent();

        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        DataContextChanged += OnDataContextChanged;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPreviewPointerMoved, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// The runtime parts of one file column: its container, the row list actually shown, and that
    /// list's scroll container.
    /// <para>一个文件列在视图层的运行时部件：列容器、本列实际显示的行列表与其滚动容器。</para>
    /// </summary>
    private sealed class ColumnVisuals(CompareColumnViewModel column, ContentControl container)
    {
        /// <summary>
        /// The column view model.
        /// <para>本列视图模型。</para>
        /// </summary>
        public CompareColumnViewModel Column { get; } = column;

        /// <summary>
        /// The container hosting the column template.
        /// <para>承载列模板的容器。</para>
        /// </summary>
        public ContentControl Container { get; } = container;

        /// <summary>
        /// The row list shown by this column; tree columns have none.
        /// <para>本列当前显示的行列表；目录树列没有行列表。</para>
        /// </summary>
        public ListBox? List { get; set; }

        /// <summary>
        /// The row list's inner scroll container.
        /// <para>行列表内部滚动容器。</para>
        /// </summary>
        public ScrollViewer? Scroll { get; set; }

        /// <summary>
        /// The browsing text editor (text files).
        /// <para>浏览文本编辑器（文本文件）。</para>
        /// </summary>
        public TextEditor? BrowseEditor { get; set; }

        /// <summary>
        /// The text-comparison editor.
        /// <para>文本对比编辑器。</para>
        /// </summary>
        public TextEditor? CompareEditor { get; set; }

        /// <summary>
        /// Whether the browsing editor is wired up (including the column listener).
        /// <para>浏览编辑器是否已接线（含列属性监听）。</para>
        /// </summary>
        public bool BrowseWired { get; set; }

        /// <summary>
        /// The column property-change handler (kept for unsubscribing).
        /// <para>列属性变化处理器（用于退订）。</para>
        /// </summary>
        public PropertyChangedEventHandler? ColumnHandler { get; set; }
    }

    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        OnDataContextChanged(sender, EventArgs.Empty);
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
    /// Unhandled-exception safety net: when the stack comes from the compare view (or its editors) the failure is logged, shown in the
    /// error banner and marked handled so the app does not die; anything else keeps its original behaviour.
    /// <para>未处理异常兜底：调用栈来自对比视图（含其编辑器）时记录日志、写错误条并标记已处理，避免直接崩退；
    /// 其它来源保持原行为。</para>
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
            vm.ReportError(UserError.Describe(e.Exception));
        }
    }

    /// <summary>
    /// Data-context change: switches the property-changed and column-collection subscriptions (avoids
    /// leaks) and rebuilds the columns.
    /// <para>DataContext 变更：切换属性通知与列集合订阅（防泄漏），并重建列。</para>
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
    /// Builds the grid from the column collection: one star-width column per file column (with a minimum width)
    /// and, in every gap, a 1px divider plus a directly draggable <see cref="GridSplitter"/> (it must share the
    /// grid cell, because inside a <c>ContentControl</c>'s template it cannot resolve its own column). While
    /// comparing the splitter is disabled (widths are locked) and only the divider stays.
    /// <para>按列集合生成网格：每列一个 <c>*</c> 宽列（带最小宽度），列间插入 1px 分割线与可直接拖动的
    /// <see cref="GridSplitter"/>（必须与列格子同层，放进 <c>ContentControl</c> 模板里会定位不到自己的列）。
    /// 对比状态下拖动条被禁用（宽度不允许调整），只保留分割线。</para>
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
            _columnDefinitions.Add(columnDefinition);

            var container = new ContentControl
            {
                Content = vm.Columns[index],
                ContentTemplate = columnTemplate,
                Focusable = false
            };
            container.Loaded += OnColumnContainerLoaded;
            Grid.SetColumn(container, ColumnGrid.ColumnDefinitions.Count - 1);
            ColumnGrid.Children.Add(container);
            _columnVisuals.Add(new ColumnVisuals(vm.Columns[index], container));
        }

        UpdateDividerInteractivity();
        Dispatcher.UIThread.Post(AttachColumnEditors, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Disables the splitters while comparing (the panes' widths are locked) and keeps them draggable while
    /// browsing multiple file columns.
    /// <para>对比状态下禁用拖动条（左右宽度锁定），多列文件浏览时保持可拖动。</para>
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
    /// Resets every column to an equal share: entering a comparison locks the pane widths, and this puts them back to an equal split,
    /// overwriting any widths the user dragged.
    /// <para>把各列宽度恢复成等分：开始对比时两栏宽度被锁定，此时回到等分状态（覆盖用户手动拖动的列宽）。</para>
    /// </summary>
    private void ResetColumnWidths()
    {
        foreach (var definition in _columnDefinitions)
        {
            definition.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    /// <summary>
    /// Resolves each column's effective row list and subscribes to its <c>ScrollChanged</c>; scrolling
    /// is only mirrored between the two compare columns. Calling again unsubscribes the old instances
    /// first (avoids duplicate subscriptions).
    /// <para>解析每列实际显示的行列表并订阅其 <c>ScrollChanged</c>；只有参与对比的两列之间才做
    /// 滚动镜像。再次调用会先退订旧实例（防重复订阅）。</para>
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
    /// Attaches the scroll container when a column's container is first realized: a newly added third, fourth … column
    /// has no template yet when the earlier attach runs, so this retry is what makes the new column's scroll mirroring and
    /// selection synchronisation work.
    /// <para>列容器首次进入可视树时补挂滚动容器：新增第 3、4…列时其模板尚未应用，早先那次挂接会落空，
    /// 只有在这里再补一次，新列的滚动镜像/选中同步才生效。</para>
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
    /// Finds the row list a column should show inside its container: the compare right column takes the
    /// right-cell list and everything else the left-cell list (browsing a single file also lands in the
    /// left list).
    /// <para>在列容器内查找本列应显示的行列表：对比右列取右侧单元格列表，其余取左侧单元格列表
    /// （浏览单文件内容同样落在左侧列表）。</para>
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
    /// Precisely resolves the real inner scroll container of a row list: prefers the template part
    /// <c>PART_ScrollViewer</c> (exposed as <see cref="ListBox.Scroll"/>) so outer/nested scroll
    /// containers are not picked up; when the template is not applied yet it falls back to the
    /// innermost ScrollViewer containing the items panel root.
    /// <para>精确解析行列表真正的内部滚动容器：优先取模板部件 <c>PART_ScrollViewer</c>
    /// （即 <see cref="ListBox.Scroll"/>），避免 <c>FindDescendantOfType</c> 命中外部/嵌套滚动容器；
    /// 模板尚未应用时退而求其次，在子树内找包含 ItemsPanelRoot 的最内层 ScrollViewer。</para>
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
    /// Synchronised scrolling: converts the scrolled column's **first visible row index** into the
    /// target column's vertical offset (index × row height, clamped to the target's Extent), keeping
    /// the same Rows row at the same height in both columns. Row-index alignment is more robust than
    /// mirroring raw pixel offsets against sub-pixel drift and virtualized Extent estimation; only the
    /// vertical axis is synced, horizontal stays independent. <c>_syncing</c> guards against the
    /// mirrored write re-entering this handler in a loop.
    /// <para>左右两列同步滚动：把发起滚动一列的**第一个可见行索引**换算成目标列的垂直偏移
    /// （索引 × 行高，并夹取到目标侧 Extent 内），使同一 Rows 行落在两侧同一水平高度。相比直接
    /// 镜像像素偏移，行索引对齐对子像素漂移与虚拟化下 Extent 估算偏差更稳健；只同步垂直方向，
    /// 水平各自独立。<c>_syncing</c> 防止回写再次触发本回调形成回环。</para>
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
    /// Scrolls a row list to the given row index: target offset = index × row height (clamped to the
    /// Extent). Uses an exact offset instead of <see cref="ItemsControl.ScrollIntoView"/>, whose
    /// semantics are "scroll until the item is visible" and which does nothing when the item is
    /// already in the viewport — leaving the columns misaligned.
    /// <para>把行列表滚到指定行索引：目标偏移 = 索引 × 行高（夹取到 Extent 内）。用偏移量
    /// 精确设置而非 <see cref="ItemsControl.ScrollIntoView"/>——后者的语义是「滚到该项可见」，
    /// 若该项已在视口内则不做任何滚动，两侧位置仍会不同步。</para>
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
    /// Returns the effective single-row height: measured from the first realized container with a
    /// non-zero height (the row template fixes <c>MinHeight=20</c>, so all columns share one row
    /// height); falls back to a fixed 20 when no container is realized yet.
    /// <para>取单行实际高度：从行列表已生成的容器中实测首个非零高度（行模板
    /// <c>MinHeight=20</c> 恒定，各列行高一致）；尚未布局出容器时回退到固定 20。</para>
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

    /// <summary>
    /// Row-list selection changed: updates the document's
    /// <see cref="CompareViewModel.SelectedIndex"/> (used to target the copy/delete/diff-navigation
    /// commands) and selects the same row in the other compare column; selection inside a browsing
    /// column (a single file's content) does not take part in compare synchronisation.
    /// <para>行列表选中变化：更新文档的 <see cref="CompareViewModel.SelectedIndex"/>（复制/删除/
    /// 差异导航据此定位），并把同一行同步选中到另一侧对比列；浏览列（单文件内容）的选中不参与
    /// 对比联动。</para>
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

        _vm.UpdateRowSelection(list.Selection?.SelectedIndexes ?? []);

        PublishSelectionToProperties(list);

        if (!visuals.Column.IsCompareSide)
        {
            return;
        }

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

        var sourceIndexes = list.Selection?.SelectedIndexes ?? [];
        var targetIndexes = otherList.Selection?.SelectedIndexes ?? [];
        if (sourceIndexes.Count == targetIndexes.Count
            && !sourceIndexes.Except(targetIndexes).Any())
        {
            return;
        }

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
    /// Broadcasts the current selected row's absolute path to the properties panel
    /// (<see cref="CompareSelectionChangedMessage"/>); text-comparison rows and an empty selection send
    /// <c>null</c> to clear the panel.
    /// <para>把当前选中行以绝对路径广播给属性面板（<see cref="CompareSelectionChangedMessage"/>）；
    /// 文本对比行与空选择发送 <c>null</c> 清空面板。</para>
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
        if (string.Equals(e.PropertyName, nameof(CompareViewModel.ScrollToIndex), StringComparison.Ordinal))
        {
            ScrollToSelected();
            return;
        }

        if (string.Equals(e.PropertyName, nameof(CompareViewModel.IsComparing), StringComparison.Ordinal))
        {
            UpdateDividerInteractivity();
            if (DataContext is CompareViewModel comparing && comparing.IsComparing)
            {
                ResetColumnWidths();
            }
        }

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
    /// Finds the visual of the column playing the given role, or <c>null</c> while not comparing. The column's list and
    /// scroll container are resolved on demand first so an added column is never returned unresolved.
    /// <para>按角色查找参与对比的列视图；未在对比时返回 <c>null</c>。查找前先按需补解析列表与滚动容器，
    /// 避免新增列后这里拿到未挂接的条目。</para>
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
    /// Diff-navigation scroll: top-aligns both compare columns to the row specified by
    /// <see cref="CompareViewModel.ScrollToIndex"/>. Target offset = index × row height, **without
    /// clamping to the Extent** (the ScrollViewer itself bounds out-of-range offsets; the mirror's
    /// <see cref="ScrollListToIndex"/> keeps its clamping behavior); <c>_syncing</c> prevents the writes
    /// from mirroring each other.
    /// <para>差异导航滚动：把两个对比列都顶部对齐滚到 <see cref="CompareViewModel.ScrollToIndex"/>
    /// 指定的行。目标偏移 = 索引 × 行高，**不夹取到 Extent 内**（ScrollViewer 自身对越界偏移有
    /// 兜底；镜像用的 <see cref="ScrollListToIndex"/> 保持夹取行为不变）；<c>_syncing</c> 防止两侧
    /// 回写互相触发镜像。</para>
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
    /// Diff-navigation-specific: top-aligns a row list to the given index, target offset = index × row
    /// height, without clamping to the Extent (the ScrollViewer itself bounds out-of-range offsets).
    /// Falls back to <c>ScrollIntoView</c> when the scroll container is not resolved yet.
    /// <para>差异导航专用：把行列表顶部对齐滚到指定行索引，目标偏移 = 索引 × 行高，
    /// 不夹取到 Extent 内（ScrollViewer 自身对越界偏移有兜底）。滚动容器尚未解析时退化为
    /// <see cref="ItemsControl.ScrollIntoView"/> 尽力定位。</para>
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

    /// <summary>
    /// "Add Folder" button: shows a multi-select folder dialog where each chosen folder takes one file
    /// column (filling empty columns first and appending when needed). No column is added while
    /// comparing.
    /// <para>「添加文件夹」按钮：弹出多选文件夹对话框，每个选中的文件夹占用一个文件列（优先填充空列，
    /// 不足时在末尾追加）。对比进行中不追加列。</para>
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

            List<string> paths = [];
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
            vm.ReportError(UserError.Describe(ex));
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    /// <summary>
    /// Activates a row on double-click: a folder row expands/collapses (both while browsing and comparing; a folder
    /// comparison shares one node so one side expands both), a file row opens its comparison in a new tab during a folder
    /// comparison, and a file row while browsing does nothing. Entering a folder is a context-menu item so it does not
    /// compete with the expand gesture.
    /// <para>双击激活一行：目录行展开/折叠（浏览与对比都一样；对比中两侧共享同一节点，一侧展开即两侧同步展开），
    /// 文件行在文件夹对比中于新标签打开该文件的对比，浏览状态下的文件行不做处理。
    /// 「进入某个目录」是右键菜单项，避免与双击展开手势冲突。</para>
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
    /// Toggles a tree row's expansion: the document takes over during a folder comparison (both sides share one
    /// aligned row list), while browsing lets the owning column rebuild its own visible rows.
    /// <para>切换树行节点的展开状态：文件夹对比由文档接管（两侧共享一份对齐行列表），
    /// 浏览状态由所属列重建自己的可见行。</para>
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
    /// Resolves a control's file column from the nearest row list's DataContext (a column template's DataContext is its
    /// column view model). Resolving through the row list works for any number of columns (third, fourth …), so double-click
    /// and drag handling always find the owning column.
    /// <para>按可视元素反查它所在的文件列：直接取最近的行列表的 DataContext（列模板的 DataContext 就是列视图模型）。
    /// 直接按行列表反查所属列，任意列数（第 3、4…列）都能正确定位，保证双击/拖拽不失效。</para>
    /// </summary>
    private CompareColumnViewModel? FindColumnFor(Control source)
    {
        if (FindListFor(source)?.DataContext is CompareColumnViewModel direct)
        {
            return direct;
        }

        foreach (var visuals in _columnVisuals)
        {
            if (ReferenceEquals(visuals.Container, source) || visuals.Container.IsVisualAncestorOf(source))
            {
                return visuals.Column;
            }
        }

        return null;
    }

    /// <summary>
    /// In-line editing keys: Enter splits at the caret, Backspace at the start merges into the previous row and Delete at
    /// the end merges the next row in; the comparison state is recomputed immediately and the caret is restored.
    /// <para>行内编辑快捷键：回车在光标处断行、行首 Backspace 并入上一行、行尾 Delete 并入下一行，
    /// 结构变化后立即重算对比状态，并把光标移到正确位置。</para>
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
    /// <param name="sender">The row list. <para>行列表。</para></param>
    /// <param name="e">The key event. <para>按键事件。</para></param>
    private void OnRowListKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not CompareViewModel vm)
        {
            return;
        }

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
    /// After a structural change (split/join) the caret is restored to the target row's editor: the row is scrolled into
    /// view first, then focused once its container is realized.
    /// <para>结构变化（断行/并行）后把光标放回目标行的编辑框：先滚到该行，等容器实现后再聚焦并设置光标。</para>
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
    /// Finds the nearest <see cref="ListBox"/> among a control's visual ancestors (used to locate a row editor's column).
    /// Nothing is cached, so it stays reliable no matter how many columns exist.
    /// <para>按可视元素反查它所在的行列表（行编辑框定位所在列用）：沿可视祖先直接找最近的 <see cref="ListBox"/>，
    /// 不依赖缓存，因此列数变化后依然可靠。</para>
    /// </summary>
    private ListBox? FindListFor(Control source)
        => source.GetSelfAndVisualAncestors().OfType<ListBox>().FirstOrDefault();

    /// <summary>该行列表是否属于右侧模板（对比右列渲染右侧单元格）。</summary>
    private static bool IsRightList(ListBox list)
        => (list.DataContext as CompareColumnViewModel)?.UsesRightCells == true;

}

/// <summary>
/// Converts a row depth to a left margin (an 8 px base plus depth × 14 px), indenting folder rows by their level. The base margin
/// separates the icon from the status stripe at the start of the row so the first level does not touch it. Referenced from XAML as
/// <c>{x:Static compareviews:DepthToIndentConverter.Instance}</c>.
/// <para>缩进换算：把条目深度转换为左边距（基础 8 像素 + 深度 × 14 像素），文件夹比较按层级缩进路径。
/// 基础边距用于把图标与行首的状态色条分开，避免第一层条目紧贴色条。在 XAML 中以
/// <c>{x:Static compareviews:DepthToIndentConverter.Instance}</c> 引用。</para>
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

        return new Thickness((depth * 14.0) + 8.0, 0, 0, 0);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
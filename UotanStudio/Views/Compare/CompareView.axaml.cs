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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        DetachScrollViewers();
        UnsubscribeViewModel();
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

            ColumnGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star)
            {
                MinWidth = MinColumnWidth
            });

            var container = new ContentControl
            {
                Content = vm.Columns[index],
                ContentTemplate = columnTemplate,
                Focusable = false
            };
            Grid.SetColumn(container, ColumnGrid.ColumnDefinitions.Count - 1);
            ColumnGrid.Children.Add(container);
            _columnVisuals.Add(new ColumnVisuals(vm.Columns[index], container));
        }

        UpdateDividerInteractivity();
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
        if (other?.List is not { } otherList || otherList.SelectedIndex == list.SelectedIndex)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            otherList.SelectedIndex = list.SelectedIndex;
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
            Dispatcher.UIThread.Post(AttachScrollViewers, DispatcherPriority.Loaded);
        }
    }

    /// <summary>按角色查找参与对比的列视图；未在对比时返回 <c>null</c>。</summary>
    private ColumnVisuals? FindCompareVisuals(CompareColumnSide side)
        => _columnVisuals.FirstOrDefault(visuals => visuals.Column.Side == side);

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
    /// <para>行双击：浏览状态下目录行进入该目录（路径框同步为新路径），对比状态下目录行展开/折叠
    /// （两侧共享同一节点，一侧展开即两侧同步展开）；文件行在文件夹对比中于新标签打开该文件的对比。</para>
    /// Row double-click: while browsing a directory row enters that directory (the path box follows), while
    /// comparing it expands or collapses the row (both sides share one node, so expanding one side expands the
    /// other); a file row opens that file's comparison in a new tab during a folder comparison.
    /// </summary>
    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control source || source.DataContext is not CompareRowViewModel row
            || row.TreeNode is not { } node)
        {
            return;
        }

        e.Handled = true;
        var column = FindColumnFor(source);
        if (node.IsDirectory)
        {
            // 未对比时双击目录进入该目录（路径框同步）；对比中展开/折叠。
            // While not comparing, double-clicking a directory enters it (the path box follows); while
            // comparing it expands or collapses the row.
            if (DataContext is not CompareViewModel viewModel || column is null)
            {
                return;
            }

            if (!viewModel.IsComparing)
            {
                await column.EnterDirectoryAsync(node);
                return;
            }

            await ToggleRowExpansionAsync(node, source);
            return;
        }

        if (DataContext is CompareViewModel vm
            && column is { IsCompareSide: true }
            && vm.IsFolderMode)
        {
            vm.OpenFileComparisonInNewTab(node);
        }
    }

    /// <summary>
    /// <para>展开箭头点击：与双击目录行同样的展开/折叠行为，单点也能操作。</para>
    /// Expander click: the same expand/collapse behaviour as double-clicking a directory row, so a single click
    /// works too.
    /// </summary>
    private async void OnTreeExpanderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CompareRowViewModel { TreeNode: { } node } } source)
        {
            return;
        }

        e.Handled = true;
        await ToggleRowExpansionAsync(node, source);
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

    /// <summary>按可视元素反查它所在的文件列（行模板内的展开按钮与行列表都能定位到所属列）。</summary>
    private CompareColumnViewModel? FindColumnFor(Control source)
    {
        foreach (var visuals in _columnVisuals)
        {
            if (visuals.List is { } list && list.IsVisualAncestorOf(source))
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
                RestoreCaret(list, vm.SplitTextRow(row, leftSide, caret));
                return;
            case Key.Back when caret == 0 && box.SelectionStart == 0:
                e.Handled = true;
                RestoreCaret(list, vm.JoinTextRowWithPrevious(row, leftSide));
                return;
            case Key.Delete when caret >= length:
                e.Handled = true;
                RestoreCaret(list, vm.JoinTextRowWithNext(row, leftSide));
                return;
        }
    }

    /// <summary>行列表上的 Delete 键：删除所选行在该侧的内容（两侧都空的行整行移除）。</summary>
    /// <param name="sender"><para>行列表。</para>The row list.</param>
    /// <param name="e"><para>按键事件。</para>The key event.</param>
    private void OnRowListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not ListBox list || DataContext is not CompareViewModel vm
            || !vm.IsTextMode)
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
        vm.UpdateRowSelection(list.Selection?.SelectedIndexes ?? []);
        vm.DeleteSelectedTextRows(leftSide: !IsRightList(list));
    }

    /// <summary>文本对比的行右键菜单：删除所选行。</summary>
    private void OnDeleteSelectedTextRows(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CompareViewModel vm)
        {
            vm.DeleteSelectedTextRows(leftSide: !IsRightTagged(sender));
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

    /// <summary>按可视元素反查它所在的行列表（行编辑框定位所在列用）。</summary>
    private ListBox? FindListFor(Control source)
    {
        foreach (var visuals in _columnVisuals)
        {
            if (visuals.List is { } list && list.IsVisualAncestorOf(source))
            {
                return list;
            }
        }

        return null;
    }

    /// <summary>该行列表是否属于右侧模板（对比右列渲染右侧单元格）。</summary>
    private bool IsRightList(ListBox list)
    {
        foreach (var visuals in _columnVisuals)
        {
            if (ReferenceEquals(visuals.List, list))
            {
                return visuals.Column.UsesRightCells;
            }
        }

        return false;
    }

    // ===== 从行拖出到另一个目录（拖拽复制） =====

    /// <summary>按下时记录的拖拽候选（触发事件 + 行 + 起始点），移动超过阈值后才真正开始拖拽。</summary>
    private PointerPressedEventArgs? _dragTrigger;
    private CompareRowViewModel? _dragRow;
    private ListBox? _dragList;
    private Point _dragOrigin;

    /// <summary>记录拖拽候选：只有文件/文件夹行（非文本编辑行）可以拖出。</summary>
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ClearDragCandidate();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is TextBox
            || e.Source is not Control { DataContext: CompareRowViewModel row }
            || row.TreeNode is null
            || sender is not ListBox list)
        {
            return;
        }

        _dragTrigger = e;
        _dragRow = row;
        _dragList = list;
        _dragOrigin = e.GetPosition(this);
    }

    /// <summary>
    /// <para>移动超过阈值时开始拖拽：把该行的完整路径放进拖拽数据（文本格式），
    /// 落到另一个文件夹行上即复制进该目录。</para>
    /// Starts a drag once the pointer moved past the threshold: the row's full path becomes the drag payload (text format),
    /// and dropping it on another folder row copies it into that folder.
    /// </summary>
    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
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
        await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Copy);
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
            vm.CopySelectedTextRows(fromLeftToRight: !IsRightTagged(sender));
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
        if (path is null || column is null)
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
        if (path is null || column is null)
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
        if (path is null || column is null)
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
        if (DataContext is not CompareViewModel vm || path is null || column is null)
        {
            return;
        }

        var sourceIsLeft = !IsRightTagged(sender);
        var targetColumn = sourceIsLeft
            ? vm.Columns.FirstOrDefault(candidate => candidate.Side == CompareColumnSide.Right)
            : vm.Columns.FirstOrDefault(candidate => candidate.Side == CompareColumnSide.Left);
        var target = targetColumn?.Path;
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
        {
            vm.ReportError(LanguageService.Instance.GetString("Compare_NoNeighbourFolder"));
            return;
        }

        await TransferAsync(vm, column, path, target, move);
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
        var neighbourIndex = index + (IsRightTagged(sender) ? 1 : -1);
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
        CompareColumnViewModel column,
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
    private async Task ReloadAfterEntryChangeAsync(CompareColumnViewModel column)
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

        await column.ReloadContentAsync();
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
        if (DataContext is not CompareViewModel vm || sender is not Control control
            || control.DataContext is not CompareRowViewModel row || row.TreeNode is not { } node)
        {
            return (null, false, null);
        }

        var column = FindColumnFor(control);
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

    /// <summary>
    /// <para>右键请求：行没有提供菜单（十六进制/元数据行、浏览状态下的文本行）时吞掉请求，
    /// 避免弹出空菜单；其余情况交给行上挂的 <c>ContextMenu</c>。</para>
    /// Context request: when the row offers no menu (hex/metadata rows and text rows while browsing) the request is
    /// swallowed so no empty popup appears; otherwise the row's <c>ContextMenu</c> opens as usual.
    /// </summary>
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Control { DataContext: CompareRowViewModel row } && row.Menu == CompareRowMenuKind.None)
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
    private static bool HasFilePayload(IDataTransfer transfer)
        => UotanStudio.Views.Controls.FileDropPaths.HasFilePayload(transfer, "CompareView")
            || LooksLikeExistingPath(transfer);

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
        if (paths.Count == 0)
        {
            return;
        }

        // 拖到某个文件夹行上 → 复制进该目录（从资源管理器或应用内拖一个文件到另一个目录即可完成复制）。
        // Dropping onto a folder row copies into that folder (drag a file from the explorer or from the app onto another
        // directory to copy it).
        if (ResolveDropTarget(e.Source) is { } dropTarget)
        {
            foreach (var path in paths)
            {
                try
                {
                    ExplorerFileOperations.Transfer(path, dropTarget, move: false);
                }
                catch (Exception ex)
                {
                    ReportOperationFailure(ex);
                }
            }

            if (ResolveEntry(e.Source) is var (_, _, column) && column is not null)
            {
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
    private string? ResolveDropTarget(object? source)
    {
        if (source is not Control { DataContext: CompareRowViewModel row } control
            || row.TreeNode is not { IsDirectory: true } node)
        {
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
    private static IReadOnlyList<string> ExtractDropPaths(IDataTransfer transfer)
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
/// <para>缩进换算：把条目深度转换为左边距（深度 × 14 像素），文件夹比较按层级缩进路径。
/// 在 XAML 中以 <c>{x:Static compareviews:DepthToIndentConverter.Instance}</c> 引用。</para>
/// Converts a row depth to a left margin (depth × 14 px), indenting folder rows by their level.
/// Referenced from XAML as <c>{x:Static compareviews:DepthToIndentConverter.Instance}</c>.
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

        return new Thickness(depth * 14.0, 0, 0, 0);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

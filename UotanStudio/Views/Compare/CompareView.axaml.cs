using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.ViewModels.Compare;

namespace UotanStudio.Views.Compare;

/// <summary>
/// <para>对比页面视图：统一双栏对比面板（文件夹 / 文本 / 二进制共用同一套左右双栏布局），
/// 空态时提示拖入文件或从菜单选择。视图层同时接线系统文件拖拽：两个文件/文件夹 → 新建比较，
/// 单个路径 → 替换当前比较的对应侧。左右两栏同步滚动，选中行变化时两栏联动定位。</para>
/// Compare-page view: a unified two-pane compare panel shared by the folder / text / binary
/// modes, hinting at dragging files in or picking them from the menu while empty. The view wires
/// OS file drag-and-drop: two files/folders start a new comparison, a single path replaces the
/// matching side of the active comparison. The two panes scroll in sync and both follow the
/// selected row.
/// </summary>
public partial class CompareView : UserControl
{
    private ScrollViewer? _leftScroll;
    private ScrollViewer? _rightScroll;
    private CompareViewModel? _vm;
    private bool _syncing;
    private bool _syncingSelection;

    // 最近一次镜像写入的目标偏移（NaN=无标记）：抑制镜像回声——写入目标栏后，目标栏因布局/
    // 异步触发的 ScrollChanged 会把偏移按行索引回写给源栏，拖动中改写源偏移会放大鼠标位移
    // （上拉过量）；记录写入值并在回声到达时忽略，可切断该回环。
    // Last mirrored write's target offset (NaN = none): suppresses mirror echoes — after writing
    // the target pane, the target's layout/asynchronous ScrollChanged could mirror the offset back
    // to the source pane by row index, and rewriting the source offset mid-drag amplifies the mouse
    // movement (up-drag overshoot); recording the written value and ignoring the echo breaks it.
    private double _mirroredLeftY = double.NaN;
    private double _mirroredRightY = double.NaN;

    /// <summary>
    /// <para>行模板 <c>MinHeight</c>：各行恒为 20px，用于按偏移估算行索引（实测失败时兜底）。</para>
    /// Row-template <c>MinHeight</c>: every row is a fixed 20 px, used to estimate the row index
    /// from a scroll offset (fallback when measuring fails).
    /// </summary>
    private const double RowMinHeight = 20.0;

    /// <summary>
    /// <para>创建对比页面视图并初始化组件。</para>
    /// Creates the compare-page view and initializes its components.
    /// </summary>
    public CompareView()
    {
        InitializeComponent();

        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    // ===== 双栏同步滚动 =====

    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        OnDataContextChanged(sender, EventArgs.Empty);
        // ListBox 的内部 ScrollViewer 随模板实例化后才存在：Loaded 时模板可能尚未应用
        //（数据未加载/页面未激活），故延迟到布局完成后挂接；数据加载完成后还会再挂接一次。
        // The inner ScrollViewer of a ListBox exists only after its template is realized: at Loaded
        // the template may not be applied yet (no data / inactive page), so attach after layout;
        // attaching again after data loads covers the template-realized case.
        Dispatcher.UIThread.Post(AttachScrollViewers, DispatcherPriority.Loaded);
        ScrollToSelected();
    }

    private void OnViewUnloaded(object? sender, RoutedEventArgs e)
    {
        DetachScrollViewers();
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm = null;
        }
    }

    /// <summary>
    /// <para>从左右 ListBox 的模板中取内部 <see cref="ScrollViewer"/> 并订阅 <c>ScrollChanged</c>。
    /// ListBox 的内部滚动容器在其模板实例化后才会存在，故在 Loaded 时解析；再次加载时换绑新的
    /// 实例（先退订旧的，防止重复订阅）。</para>
    /// Resolves the internal <see cref="ScrollViewer"/> of each ListBox and subscribes to
    /// <c>ScrollChanged</c>. The inner scroll container only exists after the ListBox template is
    /// realized, so it is resolved on Loaded; re-attaching on a later load re-binds to the new
    /// instance (unsubscribing the old one first to avoid duplicate subscriptions).
    /// </summary>
    private void AttachScrollViewers()
    {
        if (!IsLoaded)
        {
            return;
        }

        AttachOneScrollViewer(LeftList, ref _leftScroll);
        AttachOneScrollViewer(RightList, ref _rightScroll);
        _mirroredLeftY = double.NaN;
        _mirroredRightY = double.NaN;
    }

    /// <summary>
    /// <para>换绑单个 ListBox 的滚动容器：先退订旧实例，再订阅解析出的新实例（防重复订阅）。</para>
    /// Re-binds one ListBox's scroll container: unsubscribes the old instance first, then
    /// subscribes to the newly resolved one (avoids duplicate subscriptions).
    /// </summary>
    private void AttachOneScrollViewer(ListBox list, ref ScrollViewer? slot)
    {
        var scroller = ResolveListScrollViewer(list);
        if (scroller is null || ReferenceEquals(scroller, slot))
        {
            return;
        }

        if (slot is not null)
        {
            slot.ScrollChanged -= OnScrollChanged;
        }

        scroller.ScrollChanged += OnScrollChanged;
        slot = scroller;
    }

    /// <summary>
    /// <para>精确解析 ListBox 真正的内部滚动容器：优先取模板部件 <c>PART_ScrollViewer</c>
    /// （即 <see cref="ListBox.Scroll"/>，随 ListBox 模板一起生成），避免 <c>FindDescendantOfType</c>
    /// 命中外部/嵌套滚动容器；模板尚未应用时退而求其次，在子树内找包含 ItemsPanelRoot 的
    /// 最内层 ScrollViewer，最后才回退到 <c>FindDescendantOfType</c>。</para>
    /// Precisely resolves the real inner scroll container of a ListBox: prefers the template part
    /// <c>PART_ScrollViewer</c> (exposed as <see cref="ListBox.Scroll"/>, created together with the
    /// ListBox template) so outer/nested scroll containers are not picked up; when the template is
    /// not yet applied, falls back to the innermost ScrollViewer in the subtree containing the
    /// items panel root, and finally to <c>FindDescendantOfType</c>.
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

    /// <summary>
    /// <para>卸载时退订两侧滚动容器，防止悬挂引用。</para>
    /// Unsubscribes from both scroll containers on unload to avoid dangling references.
    /// </summary>
    private void DetachScrollViewers()
    {
        if (_leftScroll is not null)
        {
            _leftScroll.ScrollChanged -= OnScrollChanged;
            _leftScroll = null;
        }

        if (_rightScroll is not null)
        {
            _rightScroll.ScrollChanged -= OnScrollChanged;
            _rightScroll = null;
        }
    }

    /// <summary>
    /// <para>左右栏同步滚动：把发起滚动的一栏当前**第一个可见行索引**换算成目标栏的垂直偏移
    /// （索引 × 行高，并夹取到目标侧 Extent 内），使同一 Rows 行落在两侧同一水平高度。相比直接
    /// 镜像像素偏移，行索引对齐对子像素漂移与虚拟化下 Extent 估算偏差更稳健；只同步垂直方向，
    /// 水平各自独立。<c>_syncing</c> 防止回写再次触发本回调形成回环。</para>
    /// Synchronized scrolling: converts the scrolled pane's current **first visible row index**
    /// into the target pane's vertical offset (index × row height, clamped to the target's Extent),
    /// keeping the same Rows row on the same horizontal position in both panes. Row-index alignment
    /// is more robust than mirroring raw pixel offsets against sub-pixel drift and virtualized
    /// Extent estimation; only the vertical axis is synced, horizontal stays independent.
    /// <c>_syncing</c> guards against the mirrored write re-entering this handler in a loop.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer source)
        {
            return;
        }

        if (!ReferenceEquals(source, _leftScroll) && !ReferenceEquals(source, _rightScroll))
        {
            return;
        }

        var sourceIsLeft = ReferenceEquals(source, _leftScroll);
        var target = sourceIsLeft ? _rightScroll : _leftScroll;
        if (target is null)
        {
            return;
        }

        // 镜像回声抑制：若本次滚动正是我们最近一次写入该栏的偏移（布局/异步延迟触发），
        // 视为自己的回写，忽略并清除标记——否则偏移会被按行索引回写给正在拖动的源栏，
        // 拖动中改写源偏移会放大鼠标位移（上拉过量）。
        // Mirror-echo suppression: if this scroll is exactly the offset we last wrote to this pane
        // (fired asynchronously by layout), treat it as our own echo, ignore it and clear the
        // marker — otherwise the offset would be mirrored back to the pane being dragged by row
        // index, and rewriting the source offset mid-drag amplifies the mouse movement.
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

        if (_syncing)
        {
            return;
        }

        var sourceList = sourceIsLeft ? LeftList : RightList;
        var targetList = sourceIsLeft ? RightList : LeftList;

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
                // Record the written target offset for echo suppression: synchronous echoes are
                // caught by _syncing, asynchronous (layout) echoes by this marker.
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
    /// <para>把 ListBox 滚到指定行索引：目标偏移 = 索引 × 行高（夹取到 Extent 内）。用偏移量
    /// 精确设置而非 <see cref="ItemsControl.ScrollIntoView"/>——后者的语义是“滚到该项可见”，
    /// 若该项已在视口内则不做任何滚动，两侧位置仍会不同步。滚动容器尚未解析时退化为
    /// <c>ScrollIntoView</c> 尽力定位。目标偏移与当前偏差小于 0.5px 时跳过，避免无谓回写。</para>
    /// Scrolls a ListBox to the given row index: target offset = index × row height (clamped to the
    /// Extent). Uses an exact offset instead of <see cref="ItemsControl.ScrollIntoView"/>, whose
    /// semantics are “scroll until the item is visible” and which does nothing when the item is
    /// already in the viewport — leaving the panes misaligned. Falls back to <c>ScrollIntoView</c>
    /// when the scroll container is not resolved yet. Skips when the target offset differs by less
    /// than 0.5 px to avoid redundant writes.
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
    /// <para>取单行实际高度：从 ListBox 已生成的容器中实测首个非零高度（行模板
    /// <c>MinHeight=20</c> 恒定，各侧行高一致）；尚未布局出容器时回退到固定 20。</para>
    /// Returns the effective single-row height: measured from the first realized container with a
    /// non-zero height (the row template fixes <c>MinHeight=20</c>, so all panes share the same row
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
    /// <para>左栏选中变化：在右侧栏中查找同一行对象（按引用），选中之；
    /// 同时更新 VM 的 <see cref="CompareViewModel.SelectedIndex"/> 供差异导航命令使用。</para>
    /// Left-pane selection changed: finds the same row object (by reference) in the right pane
    /// and selects it; updates the VM's <see cref="CompareViewModel.SelectedIndex"/> for the
    /// diff-navigation commands.
    /// </summary>
    private void OnLeftSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _vm is null)
        {
            return;
        }

        var item = LeftList.SelectedItem as CompareRowViewModel;
        SyncVmSelectedIndex(item);
        SyncOtherPaneSelection(item, RightList);
    }

    /// <summary>
    /// <para>右栏选中变化：在左侧栏中查找同一行对象（按引用），选中之；
    /// 同时更新 VM 的 <see cref="CompareViewModel.SelectedIndex"/> 供差异导航命令使用。</para>
    /// Right-pane selection changed: finds the same row object (by reference) in the left pane
    /// and selects it; updates the VM's <see cref="CompareViewModel.SelectedIndex"/> for the
    /// diff-navigation commands.
    /// </summary>
    private void OnRightSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _vm is null)
        {
            return;
        }

        var item = RightList.SelectedItem as CompareRowViewModel;
        SyncVmSelectedIndex(item);
        SyncOtherPaneSelection(item, LeftList);
    }

    /// <summary>
    /// <para>把 VM 的 <see cref="CompareViewModel.SelectedIndex"/> 同步到当前选中行（在 <see cref="CompareViewModel.Rows"/> 中的索引）。</para>
    /// Syncs the VM's <see cref="CompareViewModel.SelectedIndex"/> to the currently selected row's
    /// index in <see cref="CompareViewModel.Rows"/>.
    /// </summary>
    private void SyncVmSelectedIndex(CompareRowViewModel? item)
    {
        if (item is null || _vm?.Rows is null)
        {
            return;
        }

        var index = IndexOf(_vm.Rows, item);
        if (index >= 0)
        {
            _vm.SelectedIndex = index;
        }
    }

    /// <summary>
    /// <para>在另一侧栏中查找与当前选中行同一引用（同一行对象）的条目并选中；
    /// 找不到则不改变对方选中；使用 <c>_syncingSelection</c> 防回环。</para>
    /// Finds the same row object (by reference) in the other pane's list and selects it;
    /// does nothing when the item is not found; uses <c>_syncingSelection</c> to guard against
    /// feedback loops.
    /// </summary>
    private void SyncOtherPaneSelection(CompareRowViewModel? item, ListBox otherList)
    {
        if (item is null || otherList.ItemsSource is not IReadOnlyList<CompareRowViewModel> otherRows)
        {
            return;
        }

        var otherIndex = IndexOf(otherRows, item);
        if (otherIndex >= 0)
        {
            _syncingSelection = true;
            try
            {
                otherList.SelectedIndex = otherIndex;
            }
            finally
            {
                _syncingSelection = false;
            }
        }
    }

    /// <summary>按引用查找行对象在列表中的索引；找不到返回 -1。</summary>
    private static int IndexOf(IReadOnlyList<CompareRowViewModel> list, CompareRowViewModel target)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], target))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// <para>DataContext 变更：先退订旧视图模型的属性通知，再订阅新视图模型（防泄漏）。</para>
    /// Data-context change: unsubscribe the old view model's property-changed first, then
    /// subscribe the new one (prevents leaks).
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as CompareViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }
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

        // 数据加载/模式切换后重新挂接滚动容器：Rows/LeftRows/RightRows 变化意味着列表已填充，
        // 此时 ListBox 模板与内部 ScrollViewer 才真正实例化，之前的挂接可能失败。
        // Re-attach the scroll containers after data loads or a mode switch: Rows/LeftRows/RightRows
        // changes mean the lists are populated and the ListBox templates (and their inner
        // ScrollViewers) are finally realized, so an earlier attach may have failed.
        if (e.PropertyName is nameof(CompareViewModel.Rows)
            or nameof(CompareViewModel.LeftRows)
            or nameof(CompareViewModel.RightRows)
            or nameof(CompareViewModel.Mode)
            or nameof(CompareViewModel.HasComparison))
        {
            Dispatcher.UIThread.Post(AttachScrollViewers, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// <para>差异导航滚动：把左右两栏都顶部对齐滚到 <see cref="CompareViewModel.ScrollToIndex"/>
    /// 指定的行（先按引用在两侧行列表中查找，找不到则忽略；文本/十六进制模式两侧共享同一列表
    /// 可确保找到）。目标偏移 = 索引 × 行高，**不夹取到 Extent 内**（ScrollViewer 自身对越界
    /// 偏移有兜底；镜像用的 <see cref="ScrollListToIndex"/> 保持夹取行为不变）；
    /// <c>_syncing</c> 防止两侧回写互相触发镜像。</para>
    /// Diff-navigation scroll: top-aligns both panes to the row specified by
    /// <see cref="CompareViewModel.ScrollToIndex"/> (looks the item up by reference in each pane's
    /// list; ignored when the item is not found — both modes share the same list so the item is
    /// always found). Target offset = index × row height, **without clamping to the Extent** (the
    /// ScrollViewer itself bounds out-of-range offsets; the mirror's <see cref="ScrollListToIndex"/>
    /// keeps its clamping behavior unchanged); <c>_syncing</c> prevents the writes from mirroring
    /// each other.
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

        var item = vm.Rows[index];
        var leftIndex = vm.LeftRows is null ? -1 : IndexOf(vm.LeftRows, item);
        var rightIndex = vm.RightRows is null ? -1 : IndexOf(vm.RightRows, item);

        _syncing = true;
        try
        {
            if (leftIndex >= 0)
            {
                ScrollListTopAligned(LeftList, _leftScroll, leftIndex);
            }

            if (rightIndex >= 0)
            {
                ScrollListTopAligned(RightList, _rightScroll, rightIndex);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// <para>差异导航专用：把 ListBox 顶部对齐滚到指定行索引，目标偏移 = 索引 × 行高，
    /// 不夹取到 Extent 内（ScrollViewer 自身对越界偏移有兜底）。滚动容器尚未解析时退化为
    /// <see cref="ItemsControl.ScrollIntoView"/> 尽力定位。与滚动镜像共用的
    /// <see cref="ScrollListToIndex"/> 保持夹取行为不变。</para>
    /// Diff-navigation-specific: top-aligns a ListBox to the given row index, target offset =
    /// index × row height, without clamping to the Extent (the ScrollViewer itself bounds
    /// out-of-range offsets). Falls back to <c>ScrollIntoView</c> when the scroll container is not
    /// resolved yet. The mirror-shared <see cref="ScrollListToIndex"/> keeps its clamping behavior.
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

    // ===== 系统文件拖拽（原实现保留） =====

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
    /// 再以 <c>TryGetFiles</c> 直接探测（与 Drop 消费一致；Avalonia 12.1.1 的 Win32 后端把
    /// CF_HDROP 映射为 <c>Universal</c> 的 <c>DataFormat.File</c>，此处可命中），最后扫描格式
    /// 列表中的系统文件格式名（Windows 为 <c>FileDrop</c>）。</para>
    /// Determines whether the drag payload carries file/folder paths: checks the universal file
    /// format first (in-process drags), then probes <c>TryGetFiles</c> directly (matching what Drop
    /// consumes; Avalonia 12.1.1's Win32 backend maps CF_HDROP to a <c>Universal</c>
    /// <c>DataFormat.File</c>, which this catches), and finally scans the format list for the OS
    /// file-format name (<c>FileDrop</c> on Windows).
    /// </summary>
    private static bool HasFilePayload(IDataTransfer transfer)
    {
        if (transfer.Contains(DataFormat.File))
        {
            return true;
        }

        try
        {
            if (transfer.TryGetFiles() is { Length: > 0 })
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "CompareView", ex.Message);
        }

        foreach (var format in transfer.Formats)
        {
            if (string.Equals(format.Identifier, "FileDrop", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <para>拖放事件：把拖入的文件/文件夹路径交给视图模型打开比较（含 OS 拖拽的平台格式提取）。
    /// 立即标记 <c>Handled</c> 并消费数据，再异步打开比较——避免异步异常导致拖放状态丢失。</para>
    /// Drop event: hands the dropped file/folder paths to the view model to open a comparison
    /// (including platform-format extraction for OS drags). Marks <c>Handled</c> and consumes the
    /// payload immediately, then opens the comparison asynchronously — so an async failure cannot
    /// leave the drop unhandled.
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        var paths = TryExtractPaths(e.DataTransfer);
        if (paths.Count > 0)
        {
            await vm.TryOpenDroppedPathsAsync(paths);
        }
    }

    /// <summary>
    /// <para>从拖放数据提取文件/文件夹路径：先按通用文件格式取条目，失败时再按平台文件
    /// 格式（<c>FileDrop</c>）取条目数据（宽容枚举 <see cref="IStorageItem"/>）。</para>
    /// Extracts file/folder paths from the drag payload: first via the universal file format, then
    /// via the platform file format (<c>FileDrop</c>) reading the item data as an enumerable of
    /// <see cref="IStorageItem"/>.
    /// </summary>
    private static IReadOnlyList<string> TryExtractPaths(IDataTransfer transfer)
    {
        var items = transfer.TryGetFiles();
        if (items is { Length: > 0 })
        {
            return items.Select(static item => item.Path.LocalPath).ToList();
        }

        foreach (var format in transfer.Formats)
        {
            if (!string.Equals(format.Identifier, "FileDrop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var item in transfer.Items)
            {
                if (!item.Contains(format))
                {
                    continue;
                }

                try
                {
                    if (item.TryGetRaw(format) is System.Collections.IEnumerable enumerable)
                    {
                        var storage = enumerable.OfType<IStorageItem>().ToArray();
                        if (storage.Length > 0)
                        {
                            return storage.Select(static s => s.Path.LocalPath).ToList();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Write(LogLevel.Error, "CompareView", ex.Message);
                }
            }
        }

        return [];
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
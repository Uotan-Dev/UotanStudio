using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System.ComponentModel;
using System.Globalization;
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
        AttachScrollViewers();
        ScrollToSelected();
    }

    private void OnViewUnloaded(object? sender, RoutedEventArgs e)
    {
        DetachScrollViewers();
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
        var left = LeftList.FindDescendantOfType<ScrollViewer>();
        if (left is not null && !ReferenceEquals(left, _leftScroll))
        {
            if (_leftScroll is not null)
            {
                _leftScroll.ScrollChanged -= OnScrollChanged;
            }

            left.ScrollChanged += OnScrollChanged;
            _leftScroll = left;
        }

        var right = RightList.FindDescendantOfType<ScrollViewer>();
        if (right is not null && !ReferenceEquals(right, _rightScroll))
        {
            if (_rightScroll is not null)
            {
                _rightScroll.ScrollChanged -= OnScrollChanged;
            }

            right.ScrollChanged += OnScrollChanged;
            _rightScroll = right;
        }
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
    /// <para>左右栏同步滚动：把发起滚动的一栏的垂直偏移镜像到另一栏（只同步垂直方向，
    /// 水平各自独立）。<c>_syncing</c> 防止镜像写入再次触发本回调形成回环；目标侧行数可能
    /// 更少，偏移需夹取到其滚动范围（Extent − Viewport）内。</para>
    /// Synchronized scrolling: mirrors the vertical offset of the scrolled pane to the other
    /// (vertical only; each pane scrolls horizontally on its own). <c>_syncing</c> guards against
    /// the mirrored write re-entering this handler in a loop; the target may have fewer rows, so
    /// the offset is clamped to its scroll range (Extent − Viewport).
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncing || sender is not ScrollViewer source)
        {
            return;
        }

        var target = ReferenceEquals(source, _leftScroll) ? _rightScroll : _leftScroll;
        if (target is null)
        {
            return;
        }

        var maxVertical = Math.Max(0, target.Extent.Height - target.Viewport.Height);
        var y = Math.Clamp(source.Offset.Y, 0, maxVertical);

        _syncing = true;
        try
        {
            target.Offset = new Vector(target.Offset.X, y);
        }
        finally
        {
            _syncing = false;
        }
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
        if (!string.Equals(e.PropertyName, nameof(CompareViewModel.SelectedIndex), StringComparison.Ordinal))
        {
            return;
        }

        ScrollToSelected();
    }

    /// <summary>
    /// <para>选中项变化时让左右两栏都滚动到对应行（先按引用在两侧行列表中查找，
    /// 找不到则忽略；文本/十六进制模式两侧共享同一列表可确保找到）。</para>
    /// Scrolls both panes to the selected row when the selection changes (looks up the item by
    /// reference in each pane's list; ignored when the item is not found — in text/hex mode both
    /// panes share the same list so the item is always found).
    /// </summary>
    private void ScrollToSelected()
    {
        if (DataContext is not CompareViewModel vm || vm.Rows is null || vm.Rows.Count == 0)
        {
            return;
        }

        var index = vm.SelectedIndex;
        if (index < 0 || index >= vm.Rows.Count)
        {
            return;
        }

        var item = vm.Rows[index];
        var leftIndex = IndexOf(vm.LeftRows, item);
        var rightIndex = IndexOf(vm.RightRows, item);
        if (leftIndex >= 0)
        {
            LeftList.ScrollIntoView(leftIndex);
        }
        if (rightIndex >= 0)
        {
            RightList.ScrollIntoView(rightIndex);
        }
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
    /// <para>拖放事件：把拖入的文件/文件夹路径交给视图模型打开比较（含 OS 拖拽的平台格式提取）。</para>
    /// Drop event: hands the dropped file/folder paths to the view model to open a comparison
    /// (including platform-format extraction for OS drags).
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        var paths = TryExtractPaths(e.DataTransfer);
        if (paths.Count > 0)
        {
            await vm.TryOpenDroppedPathsAsync(paths);
        }

        e.Handled = true;
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

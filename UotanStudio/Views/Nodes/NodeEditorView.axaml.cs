using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Views.Nodes;

public partial class NodeEditorView : UserControl
{
    /// <summary><para>三栏在正常宽度下的最小宽度（像素）。</para>Pane minimums in pixels at normal widths.</summary>
    private const double LeftPaneMinimum = 150;
    private const double CenterPaneMinimum = 200;
    private const double RightPaneMinimum = 150;

    private bool _draggingLeft;
    private bool _draggingRight;
    private double _dragOriginX;
    private double _startLeft;
    private double _startCenter;
    private double _startRight;

    public NodeEditorView()
    {
        InitializeComponent();
        // 订阅画布选中变更：NodifyEditor 内部维护 SelectedItems，需在此同步到 ViewModel
        // Subscribe to canvas selection changes: NodifyEditor keeps its own SelectedItems, so sync to the ViewModel here.
        Editor.SelectionChanged += OnEditorSelectionChanged;
        // 面板变宽/变窄时按可用宽度调整三栏最小宽度：窄面板下三栏等比例缩小而不是溢出被裁。
        // Re-scale the pane minimums with the available width so a narrow pane shrinks all three instead of
        // overflowing (which used to clip the right pane).
        PaneLayout.SizeChanged += (_, _) => ApplyPaneMinimums();
    }

    /// <summary>
    /// <para>左分割线按下：记录起始指针位置与三栏当前宽度，并捕获指针以便拖出拖动条也能继续跟随。</para>
    /// Left splitter pressed: records the pointer origin and the current pane widths, and captures the pointer
    /// so the drag keeps following even outside the handle.
    /// </summary>
    private void OnLeftSplitterPressed(object? sender, PointerPressedEventArgs e) => BeginDrag(sender, e, left: true);

    /// <summary><para>右分割线按下。</para>Right splitter pressed.</summary>
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

    /// <summary><para>拖动中：把指针位移换算成相邻两栏的宽度搬运。</para>While dragging: turns the pointer offset into a width trade between the two adjacent panes.</summary>
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

    /// <summary><para>松开指针：结束拖动。</para>Pointer released: ends the drag.</summary>
    private void OnSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndDrag(e.Pointer);
        e.Handled = true;
    }

    /// <summary><para>指针捕获丢失（例如窗口失活）：结束拖动，避免残留拖动状态。</para>Pointer capture lost (window deactivated and so on): ends the drag so no state is left behind.</summary>
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
    /// <para>搬运宽度：被拖动的两栏共享的总宽保持不变，因此第三栏的宽度与比例完全不受影响；
    /// 两栏各自不小于当前生效的最小宽度，所以拖到头就停。</para>
    /// Trades width: the pair being dragged keeps its combined width, so the third pane's width and share are
    /// untouched, and each pane is clamped to the effective minimum so the drag simply stops at the limit.
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
    /// <para>把三栏宽度写回列定义：写入的是与像素等值的星号权重，因此窗口后续变宽/变窄时三栏仍按同一比例缩放。</para>
    /// Writes the three pane widths back as star weights equal to those pixel values, so later window resizes
    /// keep scaling all three panes in the same proportion.
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
    /// <para>最小宽度随可用宽度等比缩放：可用宽度不足以放下三个最小宽度时一起缩小，
    /// 这样任何尺寸下三栏都放得下，不会出现最后一栏被裁掉的情况。</para>
    /// Scales the minimums down when the available width cannot hold all three, so every size fits and the last
    /// pane is never clipped.
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

    /// <summary><para>把当前生效的最小宽度应用到列定义（面板尺寸变化时调用）。</para>Applies the effective minimums to the column definitions (called when the pane size changes).</summary>
    private void ApplyPaneMinimums()
    {
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
    /// <para>画布节点选中变更时，将新增选中中的第一个节点同步到 ViewModel，驱动右侧属性面板。</para>
    /// When the canvas node selection changes, sync the first added node to the ViewModel to drive the properties panel.
    /// </summary>
    /// <param name="sender"><para>事件源。</para>Event source.</param>
    /// <param name="e"><para>选中变更事件参数。</para>The selection changed args.</param>
    private void OnEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is NodeEditorViewModel vm)
        {
            foreach (var item in e.AddedItems)
            {
                if (item is UotanStudio.ViewModels.NodeViewModels.FlowNodeViewModel node)
                {
                    vm.SelectNode(node);
                    break;
                }
            }
        }
    }

    private void OnToolboxItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is NodeToolboxItem item)
        {
            if (DataContext is NodeEditorViewModel vm)
            {
                var node = item.CreateNode();
                vm.AddNode(node);
            }
        }
    }

    /// <summary>
    /// <para>路径端口「浏览」按钮：通过存储提供程序弹出文件选择器，把所选路径写入该端口的初始值。</para>
    /// Path-port browse button: opens a storage-provider file picker and writes the chosen path into the port's initial value.
    /// </summary>
    /// <param name="sender">The browse button whose Tag carries the target connector.</param>
    /// <param name="e">Routed event args.</param>
    private async void OnBrowsePathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NodeConnectorViewModel connector } || VisualRoot is not Window window)
        {
            return;
        }

        var storage = window.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = UotanStudio.Services.LanguageService.Instance.GetString("Dialog_SelectFile"),
            AllowMultiple = false
        });
        if (files is { Count: > 0 } && files[0].TryGetLocalPath() is { Length: > 0 } localPath)
        {
            connector.InitialValue = localPath;
        }
    }
}
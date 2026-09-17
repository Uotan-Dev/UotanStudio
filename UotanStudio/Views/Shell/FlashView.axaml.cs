using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>烧录页面：刷机包选择、分区列表、操作面板与日志输出。</para>
/// Flash page: package selection, partition list, action panel and log output.
/// </summary>
/// <remarks>
/// <para>本页只提供界面与布局，尚未接刷写后端；按钮不执行任何刷写动作。</para>
/// This page is UI and layout only. No flash backend is wired yet, so the action buttons stay inert.
/// </remarks>
public partial class FlashView : UserControl
{
    /// <summary>
    /// <para>页面左右留白，与 <c>FlashLayout</c> 的 Margin 保持一致。</para>
    /// Page inset on both sides; kept in sync with the <c>FlashLayout</c> margin.
    /// </summary>
    private const double PageMargin = 12;
    /// <summary><para>栏位之间的间距。</para>The gap between panes.</summary>
    private const double PaneGap = 12;
    /// <summary>
    /// <para>操作栏固定宽度：按「最宽的两个复选框同行」取值（复选框网格因此保持两列对齐），
    /// 窗口变窄时不再压缩它，只压缩日志和列表。</para>
    /// Fixed action-pane width, sized so the widest check-box pair still fits on one line with the two-column grid;
    /// resizing narrows the log and the list instead of this pane.
    /// </summary>
    private const double ActionPaneWidth = 292;
    private const double MinimumLogWidth = 340;
    private const double MinimumListWidth = 360;
    /// <summary>
    /// <para>宽屏时日志与分区列表的宽度比；中间列表分得更多，日志与列表按此比例先被压缩。</para>
    /// Width ratio between the log and the partition list on wide screens; the middle list gets the larger share and
    /// both shrink proportionally before the fixed action pane does.
    /// </summary>
    private const double WideLogShare = 1;
    private const double WideListShare = 2.5;
    /// <summary>
    /// <para>使用「日志在左」三栏布局所需的最小页面宽度；低于该宽度时日志移到分区列表下方。
    /// 阈值由日志的最小宽度反推，日志最小宽度一变，切换点随之变化，不会出现三栏比窄屏更挤的情况。</para>
    /// Minimum page width for the three-column layout with the log on the left; below it the log moves under the
    /// list. The threshold is derived from the log's minimum width so both stay in step.
    /// </summary>
    private const double WideMinimumWidth =
        ((MinimumLogWidth * (WideLogShare + WideListShare)) / WideLogShare) +
        (PaneGap * 2) + ActionPaneWidth + (PageMargin * 2);
    /// <summary>
    /// <para>窄屏时列表区域与日志区域的高度比例：优先保证列表能显示的行数，日志只占下方较小一段。</para>
    /// Height ratio between the list area and the log area in the stacked layout: the list's row capacity comes first.
    /// </summary>
    private const double StackedListRatio = 2.5;
    private const double StackedLogRatio = 1;
    private const double StackedListMinimumHeight = 600;
    private const double StackedLogMinimumHeight = 400;
    /// <summary>
    /// <para>刷机包一行放不下时的换行阈值：低于该宽度时按钮移到路径框下方，避免路径框被压成窄条。</para>
    /// Wrap threshold for the package bar: below this width the buttons move under the path box so the box keeps
    /// a usable width.
    /// </summary>
    private const double MinimumPackageRowWidth = 430;

    private readonly FlashViewModel _model = new();
    private bool? _wide;
    private bool? _packageButtonsStacked;

    /// <summary><para>构造页面并应用当前宽度的布局。</para>Creates the page and applies the layout for the current width.</summary>
    public FlashView()
    {
        InitializeComponent();
        DataContext = _model;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateResponsiveLayout();
    }

    /// <summary>
    /// <para>按可用宽度在三栏与上下两种排布之间切换；比例由 <see cref="GridLength"/> 星号分配，不写死像素宽度。</para>
    /// Switches between the three-column and the stacked arrangement based on the available width; the proportions
    /// come from star-sized grid lengths instead of fixed pixel widths.
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        var wide = Bounds.Width >= WideMinimumWidth;
        if (_wide == wide)
        {
            return;
        }

        _wide = wide;
        if (wide)
        {
            // 宽屏三栏：日志在左、分区列表居中、操作栏在右；刷机包只占据列表所在的那一栏，
            // 日志与操作栏跨满内容高度，窗口变窄时先按比例压缩日志和列表。
            // 日志在左时关闭整页滚动：三栏内容始终压缩进视口，不出现滚动条。
            // Wide three-column layout: log left, partitions centred, actions right. The package bar only covers
            // the list column; the log and the action pane span the full content height. Page scrolling is off here
            // so the three columns always fit the viewport without a scroll bar.
            PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            FlashLayout.ColumnDefinitions =
            [
                new ColumnDefinition(new GridLength(WideLogShare, GridUnitType.Star)) { MinWidth = MinimumLogWidth },
                new ColumnDefinition(new GridLength(PaneGap)),
                new ColumnDefinition(new GridLength(WideListShare, GridUnitType.Star)) { MinWidth = MinimumListWidth },
                new ColumnDefinition(new GridLength(PaneGap)),
                new ColumnDefinition(new GridLength(ActionPaneWidth))
            ];
            FlashLayout.RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                GapRow(),
                new RowDefinition(GridLength.Star),
                new RowDefinition(new GridLength(0)),
                new RowDefinition(new GridLength(0))
            ];
            Place(PackageBar, row: 0, column: 2);
            Place(LogPane, row: 0, column: 0, rowSpan: 3);
            Place(PartitionPane, row: 2, column: 2);
            Place(ActionPane, row: 0, column: 4, rowSpan: 3);
        }
        else
        {
            // 窄屏：日志移到分区列表下方，列表优先占据上方高度（列表:日志 = 2.5:1），
            // 操作栏跨满刷机包与列表两行，顶部对齐刷机包，窗口高度不足时整页滚动。
            // Stacked layout: the log moves under the list and the list keeps 2.5:1 of the height. The action pane
            // spans the package row and the list row so it reaches the top; short windows scroll the page.
            PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            FlashLayout.ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(PaneGap)),
                new ColumnDefinition(new GridLength(ActionPaneWidth)),
                new ColumnDefinition(new GridLength(0)),
                new ColumnDefinition(new GridLength(0))
            ];
            FlashLayout.RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                GapRow(),
                new RowDefinition(new GridLength(StackedListRatio, GridUnitType.Star))
                {
                    MinHeight = StackedListMinimumHeight
                },
                GapRow(),
                new RowDefinition(new GridLength(StackedLogRatio, GridUnitType.Star))
                {
                    MinHeight = StackedLogMinimumHeight
                }
            ];
            Place(PackageBar, row: 0, column: 0);
            Place(PartitionPane, row: 2, column: 0);
            Place(ActionPane, row: 0, column: 2, rowSpan: 3);
            Place(LogPane, row: 4, column: 0, columnSpan: 3);
        }
    }

    /// <summary>
    /// <para>创建固定厚度的间隔行；限制最大高度，避免跨行面板把间隔行撑高。</para>
    /// Creates a fixed-height gap row; the maximum height stops a row-spanned pane from inflating it.
    /// </summary>
    /// <returns><para>间隔行定义。</para>The gap row definition.</returns>
    private static RowDefinition GapRow() => new(new GridLength(PaneGap)) { MaxHeight = PaneGap };

    /// <summary>
    /// <para>把栏位放到网格的指定行列；每次显式重设跨度，避免上一种布局的合并单元格残留。</para>
    /// Places a pane at the given grid position; spans are always rewritten so the previous layout's merges never stick.
    /// </summary>
    /// <param name="control"><para>要摆放的控件。</para>The control to place.</param>
    /// <param name="row"><para>目标行。</para>The target row.</param>
    /// <param name="column"><para>目标列。</para>The target column.</param>
    /// <param name="rowSpan"><para>行跨度。</para>The row span.</param>
    /// <param name="columnSpan"><para>列跨度。</para>The column span.</param>
    private static void Place(Control control, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetRowSpan(control, rowSpan);
        Grid.SetColumnSpan(control, columnSpan);
    }

    /// <summary>
    /// <para>刷机包一行的自适应：宽度不足时把两个按钮换到路径框下方。</para>
    /// Package-bar adaptation: when the row is too narrow the two buttons wrap under the path box.
    /// </summary>
    /// <param name="sender"><para>事件源。</para>The event source.</param>
    /// <param name="e"><para>尺寸变更参数。</para>The size-change arguments.</param>
    private void OnPackageBarSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (PackageBarGrid.Bounds.Width <= 0)
        {
            return;
        }

        var stacked = PackageBarGrid.Bounds.Width < MinimumPackageRowWidth;
        if (_packageButtonsStacked == stacked)
        {
            return;
        }

        _packageButtonsStacked = stacked;
        // 在布局回调里直接改行列，会让本行的应得高度停留在旧值；放到下一轮布局再改。
        // Mutating rows inside a layout callback leaves the row's desired height stale, so defer it to the next pass.
        Dispatcher.UIThread.Post(() => ApplyPackageBarLayout(stacked), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// <para>按需要的排布重建刷机包一行；每次显式重设行列与跨度，避免残留上一次的合并单元格。</para>
    /// Rebuilds the package row for the requested arrangement; rows, columns and spans are always rewritten.
    /// </summary>
    /// <param name="stacked"><para>是否使用按钮换行的两行排布。</para>Whether the buttons wrap onto a second row.</param>
    private void ApplyPackageBarLayout(bool stacked)
    {
        if (stacked)
        {
            PackageBarGrid.RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(8)),
                new RowDefinition(GridLength.Auto)
            ];
            PackageBarGrid.ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            ];
            Place(PackageButtonRow, row: 2, column: 0, columnSpan: 2);
            PackageButtonRow.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        }
        else
        {
            PackageBarGrid.RowDefinitions = [new RowDefinition(GridLength.Auto)];
            PackageBarGrid.ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ];
            Place(PackageButtonRow, row: 0, column: 2);
            PackageButtonRow.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        }
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e) => _model.LogText = string.Empty;

    private async void OnChooseFileClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_ChooseFile"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
        {
            _model.PackagePath = path;
        }
    }

    private async void OnChooseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Flash_ChooseFolder"),
            AllowMultiple = false
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
        {
            _model.PackagePath = path;
        }
    }
}
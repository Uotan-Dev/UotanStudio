using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UotanStudio.ViewModels.Flash;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Flash page: package selection, partition list, action panel and log output.
/// <para>烧录页面：刷机包选择、分区列表、操作面板与日志输出。</para>
/// </summary>
/// <remarks>
/// The page owns the controls and forwards every action to <see cref="FlashViewModel"/>, which holds the
/// flash orchestration, its state and the application services.
/// <para>本页持有控件并把每个动作转发给 <see cref="FlashViewModel"/>；刷写编排、状态与应用服务由视图模型持有。</para>
/// </remarks>
public partial class FlashView : UserControl
{
    /// <summary>
    /// Page inset on both sides; kept in sync with the <c>FlashLayout</c> margin.
    /// <para>页面左右留白，与 <c>FlashLayout</c> 的 Margin 保持一致。</para>
    /// </summary>
    private const double PageMargin = 12;
    /// <summary>The gap between panes. <para>栏位之间的间距。</para></summary>
    private const double PaneGap = 12;
    /// <summary>
    /// Fixed action-pane width, sized so the widest check-box pair still fits on one line with the two-column grid;
    /// resizing narrows the log and the list instead of this pane.
    /// <para>操作栏固定宽度：按「最宽的两个复选框同行」取值（复选框网格因此保持两列对齐），
    /// 窗口变窄时不再压缩它，只压缩日志和列表。</para>
    /// </summary>
    private const double ActionPaneWidth = 320;
    private const double MinimumLogWidth = 340;
    private const double MinimumListWidth = 360;
    /// <summary>
    /// Width ratio between the log and the partition list on wide screens; the middle list gets the larger share and
    /// both shrink proportionally before the fixed action pane does.
    /// <para>宽屏时日志与分区列表的宽度比；中间列表分得更多，日志与列表按此比例先被压缩。</para>
    /// </summary>
    private const double WideLogShare = 1;
    private const double WideListShare = 2.5;

    /// <summary>
    /// Minimum page width for the three-column layout with the log on the left; below it the log moves under the
    /// list. The threshold is derived from the log's minimum width so both stay in step.
    /// <para>使用「日志在左」三栏布局所需的最小页面宽度；低于该宽度时日志移到分区列表下方。
    /// 阈值由日志的最小宽度反推，日志最小宽度一变，切换点随之变化，不会出现三栏比窄屏更挤的情况。</para>
    /// </summary>
    private const double WideMinimumWidth =
        ((MinimumLogWidth * (WideLogShare + WideListShare)) / WideLogShare) +
        (PaneGap * 2) + ActionPaneWidth + (PageMargin * 2);
    /// <summary>
    /// Height ratio between the list area and the log area in the stacked layout: the list's row capacity comes first.
    /// <para>窄屏时列表区域与日志区域的高度比例：优先保证列表能显示的行数，日志只占下方较小一段。</para>
    /// </summary>
    private const double StackedListRatio = 2.5;
    private const double StackedLogRatio = 1;
    private const double StackedListMinimumHeight = 600;
    private const double StackedLogMinimumHeight = 400;
    /// <summary>
    /// Wrap threshold for the package bar: below this width the buttons move under the path box so the box keeps
    /// a usable width.
    /// <para>刷机包一行放不下时的换行阈值：低于该宽度时按钮移到路径框下方，避免路径框被压成窄条。</para>
    /// </summary>
    private const double MinimumPackageRowWidth = 630;

    private readonly FlashViewModel _model = new();
    private bool? _wide;
    private bool? _packageButtonsStacked;

    /// <summary>Creates the page and applies the layout for the current width. <para>构造页面并应用当前宽度的布局。</para></summary>
    public FlashView()
    {
        InitializeComponent();
        DataContext = _model;
        _model.Attach(this);
        AttachedToVisualTree += (_, _) => _model.OnAttached();
        DetachedFromVisualTree += (_, _) => _model.OnDetached();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateResponsiveLayout();
    }

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
            LogOverallProgress.IsVisible = true;
            PartitionOverallProgress.IsVisible = false;
            LogBox.Margin = new Thickness(12, 0, 12, 8);
        }
        else
        {

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
            LogOverallProgress.IsVisible = false;
            PartitionOverallProgress.IsVisible = true;
            LogBox.Margin = new Thickness(12, 0, 12, 12);
        }
    }

    /// <summary>
    /// Creates a fixed-height gap row; the maximum height stops a row-spanned pane from inflating it.
    /// <para>创建固定厚度的间隔行；限制最大高度，避免跨行面板把间隔行撑高。</para>
    /// </summary>
    /// <returns>The gap row definition. <para>间隔行定义。</para></returns>
    private static RowDefinition GapRow() => new(new GridLength(PaneGap)) { MaxHeight = PaneGap };

    /// <summary>
    /// Places a pane at the given grid position; spans are always rewritten so the previous layout's merges never stick.
    /// <para>把栏位放到网格的指定行列；每次显式重设跨度，避免上一种布局的合并单元格残留。</para>
    /// </summary>
    /// <param name="control">The control to place. <para>要摆放的控件。</para></param>
    /// <param name="row">The target row. <para>目标行。</para></param>
    /// <param name="column">The target column. <para>目标列。</para></param>
    /// <param name="rowSpan">The row span. <para>行跨度。</para></param>
    /// <param name="columnSpan">The column span. <para>列跨度。</para></param>
    private static void Place(Control control, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetRowSpan(control, rowSpan);
        Grid.SetColumnSpan(control, columnSpan);
    }

    /// <summary>
    /// Package-bar adaptation: when the row is too narrow the two buttons wrap under the path box.
    /// <para>刷机包一行的自适应：宽度不足时把两个按钮换到路径框下方。</para>
    /// </summary>
    /// <param name="sender">The event source. <para>事件源。</para></param>
    /// <param name="e">The size-change arguments. <para>尺寸变更参数。</para></param>
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

        Dispatcher.UIThread.Post(() => ApplyPackageBarLayout(stacked), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Rebuilds the package row for the requested arrangement; rows, columns and spans are always rewritten.
    /// <para>按需要的排布重建刷机包一行；每次显式重设行列与跨度，避免残留上一次的合并单元格。</para>
    /// </summary>
    /// <param name="stacked">Whether the buttons wrap onto a second row. <para>是否使用按钮换行的两行排布。</para></param>
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

    private void OnPackagePathLostFocus(object? sender, RoutedEventArgs e) => _model.OnPackagePathLostFocus(sender, e);
    private void OnChooseFileClick(object? sender, RoutedEventArgs e) => _model.OnChooseFileClick(sender, e);
    private void OnChooseFolderClick(object? sender, RoutedEventArgs e) => _model.OnChooseFolderClick(sender, e);
    private void OnGetPackageClick(object? sender, RoutedEventArgs e) => _model.OnGetPackageClick(sender, e);
    private void OnClearLogClick(object? sender, RoutedEventArgs e) => _model.OnClearLogClick(sender, e);
    private void OnChoosePartitionImageClick(object? sender, RoutedEventArgs e) => _model.OnChoosePartitionImageClick(sender, e);
    private void OnExpertModeClick(object? sender, RoutedEventArgs e) => _model.OnExpertModeClick(sender, e);
    private void OnReadInfoClick(object? sender, RoutedEventArgs e) => _model.OnReadInfoClick(sender, e);
    private void OnFlashSelectedClick(object? sender, RoutedEventArgs e) => _model.OnFlashSelectedClick(sender, e);
    private void OnFlashEdlBundleClick(object? sender, RoutedEventArgs e) => _model.OnFlashEdlBundleClick(sender, e);
    private void OnEdlSerialFlashClick(object? sender, RoutedEventArgs e) => _model.OnEdlSerialFlashClick(sender, e);
    private void OnEraseSelectedClick(object? sender, RoutedEventArgs e) => _model.OnEraseSelectedClick(sender, e);
    private void OnSwitchSlotClick(object? sender, RoutedEventArgs e) => _model.OnSwitchSlotClick(sender, e);
    private void OnSlotResetCurrentClick(object? sender, RoutedEventArgs e) => _model.OnSlotResetCurrentClick(sender, e);
    private void OnSlotSetAClick(object? sender, RoutedEventArgs e) => _model.OnSlotSetAClick(sender, e);
    private void OnSlotSetBClick(object? sender, RoutedEventArgs e) => _model.OnSlotSetBClick(sender, e);
    private void OnCancelFlashClick(object? sender, RoutedEventArgs e) => _model.OnCancelFlashClick(sender, e);
    private void OnRootFileSourceChanged(object? sender, SelectionChangedEventArgs e) => _model.OnRootFileSourceChanged(sender, e);
    private void OnReadbackSelectedClick(object? sender, RoutedEventArgs e) => _model.OnReadbackSelectedClick(sender, e);
    private void OnRebootSystemClick(object? sender, RoutedEventArgs e) => _model.OnRebootSystemClick(sender, e);
    private void OnRebootBootloaderClick(object? sender, RoutedEventArgs e) => _model.OnRebootBootloaderClick(sender, e);
    private void OnRebootRecoveryClick(object? sender, RoutedEventArgs e) => _model.OnRebootRecoveryClick(sender, e);
    private void OnRebootFastbootdClick(object? sender, RoutedEventArgs e) => _model.OnRebootFastbootdClick(sender, e);
    private void OnRebootEdlClick(object? sender, RoutedEventArgs e) => _model.OnRebootEdlClick(sender, e);
    private void OnPowerOffClick(object? sender, RoutedEventArgs e) => _model.OnPowerOffClick(sender, e);
    private void OnRebootMetaClick(object? sender, RoutedEventArgs e) => _model.OnMtkRebootMetaClick(sender, e);
    private void OnRebootAndroidTestClick(object? sender, RoutedEventArgs e) => _model.OnMtkRebootAndroidTestClick(sender, e);
    private void OnChooseRootPackageClick(object? sender, RoutedEventArgs e) => _model.OnChooseRootPackageClick(sender, e);
    private void OnRefreshMtkPortsClick(object? sender, RoutedEventArgs e) => _model.OnRefreshMtkPortsClick(sender, e);
    private void OnChooseMtkDaClick(object? sender, RoutedEventArgs e) => _model.OnChooseMtkDaClick(sender, e);
    private void OnChooseMtkEmiClick(object? sender, RoutedEventArgs e) => _model.OnChooseMtkEmiClick(sender, e);
    private void OnChooseMtkKeyDirClick(object? sender, RoutedEventArgs e) => _model.OnChooseMtkKeyDirClick(sender, e);
    private void OnChooseMtkAuthFileClick(object? sender, RoutedEventArgs e) => _model.OnChooseMtkAuthFileClick(sender, e);
    private void OnMtkFlashClick(object? sender, RoutedEventArgs e) => _model.OnMtkFlashClick(sender, e);
    private void OnMtkReadbackClick(object? sender, RoutedEventArgs e) => _model.OnMtkReadbackClick(sender, e);
    private void OnMtkShowGptClick(object? sender, RoutedEventArgs e) => _model.OnMtkShowGptClick(sender, e);
    private void OnMtkRepairGptClick(object? sender, RoutedEventArgs e) => _model.OnMtkRepairGptClick(sender, e);
    private void OnMtkFullDumpClick(object? sender, RoutedEventArgs e) => _model.OnMtkFullDumpClick(sender, e);
    private void OnMtkDumpPartitionListClick(object? sender, RoutedEventArgs e) => _model.OnMtkDumpPartitionListClick(sender, e);
    private void OnMtkDumpWholeFlashClick(object? sender, RoutedEventArgs e) => _model.OnMtkDumpWholeFlashClick(sender, e);
    private void OnMtkReadSectorsClick(object? sender, RoutedEventArgs e) => _model.OnMtkReadSectorsClick(sender, e);
    private void OnMtkReadAreaClick(object? sender, RoutedEventArgs e) => _model.OnMtkReadAreaClick(sender, e);
    private void OnMtkEraseClick(object? sender, RoutedEventArgs e) => _model.OnMtkEraseClick(sender, e);
    private void OnMtkSetSpeedClick(object? sender, RoutedEventArgs e) => _model.OnMtkSetSpeedClick(sender, e);
    private void OnMtkRpmbReadClick(object? sender, RoutedEventArgs e) => _model.OnMtkRpmbReadClick(sender, e);
    private void OnMtkRpmbCapacityClick(object? sender, RoutedEventArgs e) => _model.OnMtkRpmbCapacityClick(sender, e);
    private void OnMtkRebootClick(object? sender, RoutedEventArgs e) => _model.OnMtkRebootClick(sender, e);
    private void OnMtkRebootFastbootClick(object? sender, RoutedEventArgs e) => _model.OnMtkRebootFastbootClick(sender, e);
    private void OnMtkPowerOffClick(object? sender, RoutedEventArgs e) => _model.OnMtkPowerOffClick(sender, e);
    private void OnMtkRebootMetaClick(object? sender, RoutedEventArgs e) => _model.OnMtkRebootMetaClick(sender, e);
    private void OnMtkRebootAndroidTestClick(object? sender, RoutedEventArgs e) => _model.OnMtkRebootAndroidTestClick(sender, e);
    private void OnMtkXmlPropertyQueryClick(object? sender, RoutedEventArgs e) => _model.OnMtkXmlPropertyQueryClick(sender, e);
    private void OnMtkPreloaderPeekClick(object? sender, RoutedEventArgs e) => _model.OnMtkPreloaderPeekClick(sender, e);
    private void OnMtkPreloaderReturnToBromClick(object? sender, RoutedEventArgs e) => _model.OnMtkPreloaderReturnToBromClick(sender, e);
    private void OnMtkPreloaderDisableWatchdogClick(object? sender, RoutedEventArgs e) => _model.OnMtkPreloaderDisableWatchdogClick(sender, e);
    private void OnMtkPreloaderJumpClick(object? sender, RoutedEventArgs e) => _model.OnMtkPreloaderJumpClick(sender, e);
    private void OnMtkPreloaderBootloaderJumpClick(object? sender, RoutedEventArgs e) => _model.OnMtkPreloaderBootloaderJumpClick(sender, e);
    private void OnEdlReadStorageInfoClick(object? sender, RoutedEventArgs e) => _model.OnEdlReadStorageInfoClick(sender, e);
    private void OnEdlChooseProgrammerClick(object? sender, RoutedEventArgs e) => _model.OnEdlChooseProgrammerClick(sender, e);
    private void OnEdlChooseProgrammerDirClick(object? sender, RoutedEventArgs e) => _model.OnEdlChooseProgrammerDirClick(sender, e);
    private void OnEdlProgrammerPickChanged(object? sender, SelectionChangedEventArgs e) => _model.OnEdlProgrammerPickChanged(sender, e);
    private void OnEdlReadbackClick(object? sender, RoutedEventArgs e) => _model.OnEdlReadbackClick(sender, e);
    private void OnEdlEraseClick(object? sender, RoutedEventArgs e) => _model.OnEdlEraseClick(sender, e);
    private void OnEdlResetClick(object? sender, RoutedEventArgs e) => _model.OnEdlResetClick(sender, e);
    private void OnEdlPowerOffClick(object? sender, RoutedEventArgs e) => _model.OnEdlPowerOffClick(sender, e);
    private void OnEdlResetToEdlClick(object? sender, RoutedEventArgs e) => _model.OnEdlResetToEdlClick(sender, e);
    private void OnEdlReadLunClick(object? sender, RoutedEventArgs e) => _model.OnEdlReadLunClick(sender, e);
    private void OnEdlReadManifestClick(object? sender, RoutedEventArgs e) => _model.OnEdlReadManifestClick(sender, e);
    private void OnEdlErasePartitionSectorsClick(object? sender, RoutedEventArgs e) => _model.OnEdlErasePartitionSectorsClick(sender, e);
    private void OnEdlPreviewEraseAllClick(object? sender, RoutedEventArgs e) => _model.OnEdlPreviewEraseAllClick(sender, e);
    private void OnEdlEraseAllClick(object? sender, RoutedEventArgs e) => _model.OnEdlEraseAllClick(sender, e);
    private void OnEdlSetBootableClick(object? sender, RoutedEventArgs e) => _model.OnEdlSetBootableClick(sender, e);
    private void OnEdlPartitionDigestClick(object? sender, RoutedEventArgs e) => _model.OnEdlPartitionDigestClick(sender, e);
    private void OnEdlChooseUfsProfileClick(object? sender, RoutedEventArgs e) => _model.OnEdlChooseUfsProfileClick(sender, e);
    private void OnEdlUfsPreviewClick(object? sender, RoutedEventArgs e) => _model.OnEdlUfsPreviewClick(sender, e);
    private void OnEdlUfsProvisionClick(object? sender, RoutedEventArgs e) => _model.OnEdlUfsProvisionClick(sender, e);
    private void OnEdlUfsResetClick(object? sender, RoutedEventArgs e) => _model.OnEdlUfsResetClick(sender, e);
    private void OnChooseEdlNandTableClick(object? sender, RoutedEventArgs e) => _model.OnChooseEdlNandTableClick(sender, e);
    private void OnEdlNandReadMapClick(object? sender, RoutedEventArgs e) => _model.OnEdlNandReadMapClick(sender, e);
    private void OnEdlNandMarkBadClick(object? sender, RoutedEventArgs e) => _model.OnEdlNandMarkBadClick(sender, e);
    private void OnEdlNandUnmarkBadClick(object? sender, RoutedEventArgs e) => _model.OnEdlNandUnmarkBadClick(sender, e);
    private void OnEdlNandRegistersClick(object? sender, RoutedEventArgs e) => _model.OnEdlNandRegistersClick(sender, e);
    private void OnEdlNandCodewordClick(object? sender, RoutedEventArgs e) => _model.OnEdlNandCodewordClick(sender, e);
    private void OnEdlNandFlashModeClick(object? sender, RoutedEventArgs e) => _model.OnEdlNandFlashModeClick(sender, e);
    private void OnEdlNandSecureModeClick(object? sender, RoutedEventArgs e) => _model.OnEdlNandSecureModeClick(sender, e);
    private void OnEdlNandSendTableClick(object? sender, RoutedEventArgs e) => _model.OnEdlNandSendTableClick(sender, e);
    private void OnEdlNandCloseClick(object? sender, RoutedEventArgs e) => _model.OnEdlNandCloseClick(sender, e);
    private void OnExtractSelectedClick(object? sender, RoutedEventArgs e) => _model.OnExtractSelectedClick(sender, e);
    private void OnBuildWiredPackageClick(object? sender, RoutedEventArgs e) => _model.OnBuildWiredPackageClick(sender, e);
}
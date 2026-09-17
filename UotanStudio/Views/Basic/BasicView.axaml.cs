using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using UotanStudio.ViewModels.Basic;

namespace UotanStudio.Views.Basic;

/// <summary>
/// <para>基础页面：左侧菜单与设备状态、中间菜单视图、右侧固定的投屏/日志面板。</para>
/// Basic page: a left-hand menu with device status, the middle section view and the fixed mirror/log panel on the right.
/// </summary>
/// <remarks>
/// <para>窗口变窄时先隐藏右侧固定面板，继续变窄才把左侧菜单收起为图标栏（隐藏设备状态与菜单文字，
/// 菜单本身仍可点击），保证「先让出右侧、最后才动左侧菜单」。</para>
/// Narrowing the window hides the fixed right panel first; only after that does the left-hand menu collapse into an
/// icon rail (device status and menu labels hidden, entries still clickable), so the right side always yields first.
/// </remarks>
public partial class BasicView : UserControl
{
    /// <summary>
    /// <para>展开左侧菜单所需的最小页面宽度；低于该宽度时菜单收起为图标栏（右侧面板此时已隐藏）。</para>
    /// Minimum page width for the expanded left menu; below it the menu collapses into the icon rail (the right
    /// panel is already hidden by then).
    /// </summary>
    private const double ExpandedMenuMinimumWidth = 960;

    /// <summary>
    /// <para>显示右侧固定面板所需的最小页面宽度；低于该宽度时先隐藏右侧面板，只保留菜单与中间视图。</para>
    /// Minimum page width for the fixed right panel; below it the right panel is hidden first and only the menu and
    /// the middle view remain.
    /// </summary>
    private const double SidePanelMinimumWidth = 1280;

    private const double ExpandedMenuWidth = 232;
    private const double RailMenuWidth = 64;

    /// <summary>
    /// <para>右侧固定面板的宽度：按页面宽度取比例，并限制在上下限之间。投屏画面是竖屏 9:20 的手机比例，
    /// 面板过窄会让画面被高度以外的因素压小，过宽则只是留白，这里用上下限卡住。</para>
    /// Width of the fixed right panel: a share of the page width clamped between the two bounds. The mirror surface
    /// is a portrait 9:20 phone shape, so an over-narrow panel shrinks the picture while an over-wide one only adds
    /// blank space; the clamp keeps it in between.
    /// </summary>
    private const double SidePanelWidthShare = 0.3;
    private const double SidePanelMinimumPanelWidth = 340;
    private const double SidePanelMaximumPanelWidth = 560;

    /// <summary>
    /// <para>右侧面板里 Tab 标题条与内边距占用的高度：面板高度减去它约等于投屏区域的可用高度。</para>
    /// Height taken by the right panel's tab strip and padding; the panel height minus this is roughly the mirror
    /// area's available height.
    /// </summary>
    private const double SidePanelChromeHeight = 40;

    /// <summary>
    /// <para>投屏画面与面板左右边框之间保留的间距；再宽就只是留白。</para>
    /// Gap kept between the mirror surface and the panel's left/right border; anything wider is just blank space.
    /// </summary>
    private const double SidePanelInset = 12;

    private const double PaneGap = 12;
    private const double MiddleMinimumWidth = 240;

    /// <summary>
    /// <para>判定「当前分段」时允许视口顶部多出的一点容差：刚滚进视野的分段也算当前分段。</para>
    /// Tolerance at the top of the viewport when picking the current section, so a section that just scrolled into
    /// view already counts as current.
    /// </summary>
    private const double SectionSyncTolerance = 24;

    /// <summary>
    /// <para>菜单点击后抑制回传滚动的时长，取值略大于 <c>SectionScroller</c> 的偏移过渡（0.35s）。</para>
    /// Window during which scroll changes are ignored after a menu jump; slightly longer than the scroller's
    /// 0.35s offset transition.
    /// </summary>
    private static readonly TimeSpan MenuScrollSettleDelay = TimeSpan.FromMilliseconds(500);

    private readonly BasicViewModel _model = new();
    private bool? _rail;
    private bool? _sidePanel;
    private double _sidePanelWidth = -1;
    private bool _syncingMenuFromScroll;
    private bool _settlingMenuScroll;

    /// <summary><para>构造页面并应用当前宽度的布局。</para>Creates the page and applies the layout for the current width.</summary>
    public BasicView()
    {
        InitializeComponent();
        DataContext = _model;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        MenuList.SelectionChanged += OnMenuSelectionChanged;
        SectionScroller.ScrollChanged += OnSectionScrollChanged;
        UpdateResponsiveLayout();
    }

    /// <summary>
    /// <para>菜单选中项变化时把中栏滚到对应分段；由滚动同步回来的选中变化直接返回，避免两边互相追赶。</para>
    /// Scrolls the middle column to the section picked in the menu; selections pushed back by the scroll position are
    /// ignored so the two never chase each other.
    /// </summary>
    private void OnMenuSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = MenuList.SelectedIndex;
        if (_syncingMenuFromScroll || index < 0 || index >= SectionStack.Children.Count)
        {
            return;
        }

        ScrollToSection(index);
    }

    /// <summary>
    /// <para>把中栏滚到指定分段：偏移取分段在内容里的高度，并卡在可滚动范围内。</para>
    /// Scrolls the middle column to a section: the offset is the section's position in the content, clamped to the
    /// scrollable range.
    /// </summary>
    private void ScrollToSection(int index)
    {
        if (SectionStack.Children[index] is not Control section)
        {
            return;
        }

        var top = section.TranslatePoint(default, SectionStack)?.Y ?? 0;
        var maximum = Math.Max(0, SectionScroller.Extent.Height - SectionScroller.Viewport.Height);
        _settlingMenuScroll = true;
        SectionScroller.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, Math.Clamp(top, 0, maximum)));
        DispatcherTimer.RunOnce(() => _settlingMenuScroll = false, MenuScrollSettleDelay);
    }

    /// <summary>
    /// <para>手动滚动时把左侧菜单同步到当前分段，菜单高亮不会停在过期的一项上。</para>
    /// Follows a manual scroll by moving the menu selection to the current section, so the highlight never sticks on
    /// a stale entry.
    /// </summary>
    private void OnSectionScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_settlingMenuScroll || SectionStack.Children.Count == 0)
        {
            return;
        }

        var current = 0;
        for (var index = 0; index < SectionStack.Children.Count; index++)
        {
            if (SectionStack.Children[index] is not Control section)
            {
                continue;
            }

            var top = (section.TranslatePoint(default, SectionStack)?.Y ?? 0) - SectionScroller.Offset.Y;
            if (top <= SectionSyncTolerance)
            {
                current = index;
            }
        }

        if (current == _model.SelectedSection)
        {
            return;
        }

        _syncingMenuFromScroll = true;
        _model.SelectedSection = current;
        _syncingMenuFromScroll = false;
    }

    /// <summary>
    /// <para>按可用宽度在展开菜单与图标栏之间切换；列宽由 <see cref="GridLength"/> 统一重建，
    /// 不留下上一次布局的列定义。</para>
    /// Switches between the expanded menu and the icon rail based on the available width; the columns are rebuilt
    /// from <see cref="GridLength"/> so no definition from the previous layout sticks around.
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        var rail = Bounds.Width < ExpandedMenuMinimumWidth;
        var showSidePanel = Bounds.Width >= SidePanelMinimumWidth;
        // 面板宽度是页面宽度的比例，宽度变化但显隐状态未变时也必须重算列宽。
        // The panel width is a share of the page width, so the columns must be rebuilt even when only the width
        // changed and neither visibility state did.
        var panelWidth = showSidePanel ? SidePanelWidthFor(Bounds.Width, Bounds.Height) : 0;
        var stateChanged = _rail != rail || _sidePanel != showSidePanel;
        if (!stateChanged && Math.Abs(panelWidth - _sidePanelWidth) < 0.5)
        {
            return;
        }

        _rail = rail;
        _sidePanel = showSidePanel;
        _sidePanelWidth = panelWidth;
        BasicLayout.ColumnDefinitions =
        [
            new ColumnDefinition(new GridLength(rail ? RailMenuWidth : ExpandedMenuWidth)),
            new ColumnDefinition(new GridLength(PaneGap)),
            new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = MiddleMinimumWidth },
            new ColumnDefinition(new GridLength(PaneGap)),
            new ColumnDefinition(new GridLength(panelWidth))
        ];

        if (!stateChanged)
        {
            return;
        }

        SidePanel.IsVisible = showSidePanel;
        StatusBlock.IsVisible = !rail;
        MenuLogo.Width = rail ? 32 : 56;
        MenuLogo.Height = rail ? 32 : 56;
        MenuList.Classes.Set("menu-rail", rail);
    }

    /// <summary>
    /// <para>按页面宽度与高度计算右侧固定面板宽度：先取页面宽度的比例并卡上下限，再按投屏画面的
    /// 9:20 比例收到「画面宽度 + 两侧间距」，避免画面居中后左右留下大片空白。</para>
    /// Computes the fixed right panel's width from the page size: a share of the page width clamped between the
    /// bounds, then reduced to "mirror width + both insets" following the 9:20 surface aspect so the centred picture
    /// never leaves a wide band of blank space on either side.
    /// </summary>
    /// <param name="pageWidth"><para>当前页面宽度。</para>The current page width.</param>
    /// <param name="pageHeight"><para>当前页面高度。</para>The current page height.</param>
    /// <returns><para>面板宽度。</para>The panel width.</returns>
    private static double SidePanelWidthFor(double pageWidth, double pageHeight)
    {
        var proportional = Math.Clamp(
            pageWidth * SidePanelWidthShare, SidePanelMinimumPanelWidth, SidePanelMaximumPanelWidth);
        if (pageHeight <= SidePanelChromeHeight)
        {
            return proportional;
        }

        var mirrorWidth = (pageHeight - SidePanelChromeHeight)
                          * (BasicMirrorPanelView.ScreenWidthUnits / BasicMirrorPanelView.ScreenHeightUnits);
        return Math.Clamp(
            Math.Min(proportional, mirrorWidth + SidePanelInset),
            SidePanelMinimumPanelWidth,
            SidePanelMaximumPanelWidth);
    }
}
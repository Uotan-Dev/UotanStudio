using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace UotanStudio.Themes;

/// <summary>
/// Provides mouse wheel, toggle-button expansion, and VS-style tab pinning behavior for <see cref="DocumentTabStrip"/>:
/// expands to multiple rows on wheel down or toggle click, collapses to a single row while keeping the active tab in the
/// first row, and pins selected tabs to the leading edge of the strip.
/// <para>为 <see cref="DocumentTabStrip"/> 提供鼠标滚轮、按钮展开以及类似 Visual Studio 的标签固定行为：
/// 滚轮向下滚动或点击右侧按钮时展开为多行，收起时将当前活动标签重排至第一行，并支持将单个标签固定到最前端。</para>
/// </summary>
public static partial class DocumentTabStripWheelHandler
{
    private const string ExpandToggleButtonName = "PART_ExpandToggleButton";
    private const string PinTabButtonName = "PART_PinTabButton";
    private const double DefaultSingleRowHeight = 26.0;
    private const double MaxExpandedHeightCap = 500.0;
    private const double OverflowTolerance = 2.0;

    private static readonly ConditionalWeakTable<IDockable, StrongBox<bool>> PinnedTabs = [];

    static DocumentTabStripWheelHandler()
    {
        EnableWheelExpandProperty.Changed.AddClassHandler<DocumentTabStrip>(OnEnableWheelExpandChanged);
    }

    /// <summary>
    /// Gets whether wheel expansion is enabled on the target element.
    /// <para>获取目标元素是否启用滚轮展开。</para>
    /// </summary>
    /// <param name="element">The target element.<para>目标元素。</para></param>
    /// <returns>True when wheel expansion is enabled; otherwise, false.<para>若启用滚轮展开则为 true；否则为 false。</para></returns>
    [GeneratedAttachedProperty(DefaultValue = false)]
    public static partial bool GetEnableWheelExpand(DocumentTabStrip element);

    /// <summary>
    /// Gets whether the tab strip is expanded to multiple rows.
    /// <para>获取标签栏是否已展开为多行。</para>
    /// </summary>
    /// <param name="element">The target element.<para>目标元素。</para></param>
    /// <returns>True when the tab strip is expanded to multiple rows; otherwise, false.<para>若标签栏已展开为多行则为 true；否则为 false。</para></returns>
    [GeneratedAttachedProperty(DefaultValue = false)]
    public static partial bool GetIsExpanded(DocumentTabStrip element);

    /// <summary>
    /// Returns whether the specified document dockable is pinned to the leading edge of its tab strip.
    /// <para>返回指定文档停靠项是否已固定到标签栏最前端。</para>
    /// </summary>
    /// <param name="dockable">The target dockable.<para>目标停靠项。</para></param>
    /// <returns>True when the dockable is pinned; otherwise, false.<para>已固定返回 true；否则返回 false。</para></returns>
    public static bool IsTabPinned(IDockable? dockable)
        => dockable is not null && PinnedTabs.TryGetValue(dockable, out var state) && state.Value;

    /// <summary>
    /// Pins or unpins a document dockable in the specified tab strip and moves it to the pinned or unpinned boundary.
    /// <para>在指定标签栏中固定或取消固定文档停靠项，并将其移动到固定区末尾或非固定区首位。</para>
    /// </summary>
    /// <param name="strip">The owning document tab strip.<para>所属文档标签栏。</para></param>
    /// <param name="dockable">The dockable to pin or unpin.<para>要固定或取消固定的停靠项。</para></param>
    /// <param name="isPinned">True to pin to the leading edge; false to unpin.<para>为 true 固定到最前；为 false 取消固定。</para></param>
    public static void SetTabPinned(DocumentTabStrip strip, IDockable dockable, bool isPinned)
    {
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(dockable);

        var currentlyPinned = IsTabPinned(dockable);
        if (currentlyPinned == isPinned)
        {
            SyncTabPinnedClass(strip, dockable, isPinned);
            return;
        }

        PinnedTabs.GetOrCreateValue(dockable).Value = isPinned;

        var dock = (strip.DataContext as IDock) ?? (dockable.Owner as IDock);
        if (dock?.VisibleDockables is { } visibleDockables && visibleDockables.Count > 1)
        {
            var currentIndex = visibleDockables.IndexOf(dockable);
            if (currentIndex >= 0)
            {
                var pinnedCountBefore = 0;
                for (var i = 0; i < visibleDockables.Count; i++)
                {
                    var item = visibleDockables[i];
                    if (ReferenceEquals(item, dockable))
                    {
                        continue;
                    }

                    if (IsTabPinned(item))
                    {
                        pinnedCountBefore++;
                    }
                }

                var targetIndex = Math.Clamp(pinnedCountBefore, 0, visibleDockables.Count - 1);
                if (targetIndex != currentIndex)
                {
                    MoveDockable(strip, dock, visibleDockables, dockable, currentIndex, targetIndex);
                }
            }
        }

        SyncTabPinnedClass(strip, dockable, isPinned);
        SchedulePostLayoutSync(strip);
    }

    /// <summary>
    /// Toggles the pinned state of a document dockable in the specified tab strip.
    /// <para>切换指定标签栏中文档停靠项的固定状态。</para>
    /// </summary>
    /// <param name="strip">The owning document tab strip.<para>所属文档标签栏。</para></param>
    /// <param name="dockable">The dockable whose pinned state should be toggled.<para>要切换固定状态的停靠项。</para></param>
    public static void ToggleTabPinned(DocumentTabStrip strip, IDockable dockable)
        => SetTabPinned(strip, dockable, !IsTabPinned(dockable));

    private static void OnEnableWheelExpandChanged(DocumentTabStrip strip, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is true)
        {
            strip.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
            strip.AddHandler(Button.ClickEvent, OnStripButtonClick, RoutingStrategies.Bubble);
            strip.AttachedToVisualTree += OnStripAttachedToVisualTree;
            strip.LayoutUpdated += OnStripLayoutUpdated;
            strip.SelectionChanged += OnStripSelectionChanged;
            strip.SizeChanged += OnStripSizeChanged;
            strip.ContainerPrepared += OnStripContainerPrepared;
            ResetPanelMaxWidth(strip);
        }
        else
        {
            strip.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
            strip.RemoveHandler(Button.ClickEvent, OnStripButtonClick);
            strip.AttachedToVisualTree -= OnStripAttachedToVisualTree;
            strip.LayoutUpdated -= OnStripLayoutUpdated;
            strip.SelectionChanged -= OnStripSelectionChanged;
            strip.SizeChanged -= OnStripSizeChanged;
            strip.ContainerPrepared -= OnStripContainerPrepared;
        }
    }

    private static void OnStripContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is DocumentTabStripItem item)
        {
            item.Classes.Set("pinned", e.Container.DataContext is IDockable dockable && IsTabPinned(dockable));
        }
    }

    private static void OnStripButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not DocumentTabStrip strip)
        {
            return;
        }

        if (e.Source is Button { Name: ExpandToggleButtonName })
        {
            if (strip.Orientation == Avalonia.Layout.Orientation.Horizontal)
            {
                ToggleExpanded(strip);
                e.Handled = true;
            }

            return;
        }

        if (e.Source is Button { Name: PinTabButtonName } pinButton)
        {
            var tabItem = pinButton.FindAncestorOfType<DocumentTabStripItem>();
            if (tabItem?.DataContext is IDockable dockable)
            {
                ToggleTabPinned(strip, dockable);
                e.Handled = true;
            }
        }
    }

    private static void OnStripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DocumentTabStrip strip
            || strip.Orientation != Avalonia.Layout.Orientation.Horizontal
            || GetIsExpanded(strip))
        {
            return;
        }

        ScheduleCollapsedSelectionEnsureVisible(strip);
    }

    private static void OnStripSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not DocumentTabStrip strip || strip.Orientation != Avalonia.Layout.Orientation.Horizontal)
        {
            return;
        }

        if (GetIsExpanded(strip))
        {
            UpdateExpandedMaxHeight(strip);
        }
        else
        {
            ScheduleCollapsedSelectionEnsureVisible(strip);
        }
    }

    private static void OnStripLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not DocumentTabStrip strip || strip.Orientation != Avalonia.Layout.Orientation.Horizontal)
        {
            return;
        }

        UpdateOverflowState(strip);
        if (GetIsExpanded(strip))
        {
            UpdateExpandedMaxHeight(strip);
        }
    }

    private static void ToggleExpanded(DocumentTabStrip strip)
    {
        if (GetIsExpanded(strip))
        {
            Collapse(strip);
        }
        else if (HasOverflow(strip))
        {
            Expand(strip);
        }
    }

    private static void Expand(DocumentTabStrip strip)
    {
        if (!GetIsExpanded(strip))
        {
            SetIsExpanded(strip, true);
            strip.Classes.Set("expanded", true);
            UpdateExpandedMaxHeight(strip);
        }
    }

    private static void Collapse(DocumentTabStrip strip)
    {
        if (!GetIsExpanded(strip))
        {
            return;
        }

        MoveSelectedDocumentToFirstRowEnd(strip);

        SetIsExpanded(strip, false);
        strip.Classes.Set("expanded", false);

        var scrollViewer = strip.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer != null)
        {
            scrollViewer.ClearValue(Layoutable.MaxHeightProperty);
            scrollViewer.Offset = new Vector(0, 0);
        }

        SchedulePostLayoutSync(strip);
    }

    private static void OnStripAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is DocumentTabStrip strip)
        {
            ResetPanelMaxWidth(strip);
            UpdateOverflowState(strip);
        }
    }

    private static void ResetPanelMaxWidth(DocumentTabStrip strip)
    {
        foreach (var visual in strip.GetVisualDescendants())
        {
            if (visual is Panel { Name: "PART_Panel" } panel)
            {
                if (!double.IsPositiveInfinity(panel.MaxWidth))
                {
                    panel.ClearValue(Layoutable.MaxWidthProperty);
                }
                break;
            }
        }
    }

    private static void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not DocumentTabStrip strip || strip.Orientation != Avalonia.Layout.Orientation.Horizontal)
        {
            return;
        }

        if (e.Delta.Y < -0.1)
        {
            if (!GetIsExpanded(strip) && HasOverflow(strip))
            {
                Expand(strip);
                e.Handled = true;
            }
        }
        else if (e.Delta.Y > 0.1)
        {
            if (GetIsExpanded(strip))
            {
                var scrollViewer = strip.FindDescendantOfType<ScrollViewer>();
                if (scrollViewer != null && scrollViewer.Offset.Y > 1.0)
                {
                    return;
                }

                Collapse(strip);
                e.Handled = true;
            }
        }
    }

    private static bool HasOverflow(DocumentTabStrip strip)
    {
        var scrollViewer = strip.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer == null)
        {
            return false;
        }

        var rowHeight = GetSingleRowHeight(strip);
        return scrollViewer.Extent.Height > rowHeight + OverflowTolerance;
    }

    private static void UpdateOverflowState(DocumentTabStrip strip)
    {
        var hasOverflow = HasOverflow(strip);
        strip.Classes.Set("has-overflow", hasOverflow);

        if (!hasOverflow && GetIsExpanded(strip))
        {
            Collapse(strip);
        }
    }

    private static double GetSingleRowHeight(DocumentTabStrip strip)
    {
        if (strip.TryFindResource("DocumentTabStripMinHeight", strip.ActualThemeVariant, out var resource)
            && resource is double height
            && height > 0)
        {
            return height;
        }

        return DefaultSingleRowHeight;
    }

    private static void UpdateExpandedMaxHeight(DocumentTabStrip strip)
    {
        var scrollViewer = strip.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer == null)
        {
            return;
        }

        var rowHeight = GetSingleRowHeight(strip);
        var hostHeight = strip.FindAncestorOfType<DocumentControl>()?.Bounds.Height ?? 0;
        var capAligned = Math.Max(rowHeight * 2, Math.Floor(MaxExpandedHeightCap / rowHeight) * rowHeight);
        double targetMaxHeight;

        if (hostHeight > rowHeight * 2)
        {
            var hostBudget = Math.Floor(hostHeight * 0.4 / rowHeight) * rowHeight;
            targetMaxHeight = Math.Clamp(hostBudget, rowHeight * 2, capAligned);
        }
        else
        {
            targetMaxHeight = Math.Min(rowHeight * 8, capAligned);
        }

        if (Math.Abs(scrollViewer.MaxHeight - targetMaxHeight) > 0.5)
        {
            scrollViewer.MaxHeight = targetMaxHeight;
        }
    }

    private static void ScheduleCollapsedSelectionEnsureVisible(DocumentTabStrip strip)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (GetIsExpanded(strip) || strip.Orientation != Avalonia.Layout.Orientation.Horizontal)
            {
                return;
            }

            MoveSelectedDocumentToFirstRowEnd(strip);

            var scrollViewer = strip.FindDescendantOfType<ScrollViewer>();
            if (scrollViewer != null && scrollViewer.Offset.Y > 0.5)
            {
                scrollViewer.Offset = new Vector(0, 0);
            }
        }, DispatcherPriority.Loaded);
    }

    private static void SchedulePostLayoutSync(DocumentTabStrip strip)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = strip.FindDescendantOfType<ScrollViewer>();
            if (!GetIsExpanded(strip) && scrollViewer != null)
            {
                scrollViewer.Offset = new Vector(0, 0);
            }

            UpdateOverflowState(strip);
        }, DispatcherPriority.Loaded);
    }

    private static void SyncTabPinnedClass(DocumentTabStrip strip, IDockable dockable, bool isPinned)
    {
        if (strip.ContainerFromItem(dockable) is DocumentTabStripItem item)
        {
            item.Classes.Set("pinned", isPinned);
        }
    }

    private static void MoveSelectedDocumentToFirstRowEnd(DocumentTabStrip strip)
    {
        if (strip.SelectedItem is not IDockable selectedDockable)
        {
            return;
        }

        var selectedContainer = strip.ContainerFromItem(selectedDockable) as Control;
        if (selectedContainer == null || selectedContainer.Bounds.Y <= 10.0)
        {
            return;
        }

        var dock = (strip.DataContext as IDock) ?? (selectedDockable.Owner as IDock);
        if (dock?.VisibleDockables is not { } visibleDockables || visibleDockables.Count <= 1)
        {
            return;
        }

        var currentIndex = visibleDockables.IndexOf(selectedDockable);
        if (currentIndex < 0)
        {
            return;
        }

        var selectedIsPinned = IsTabPinned(selectedDockable);
        var pinnedCount = 0;
        for (var i = 0; i < visibleDockables.Count; i++)
        {
            if (IsTabPinned(visibleDockables[i]))
            {
                pinnedCount++;
            }
        }

        var wrapPanel = strip.FindDescendantOfType<WrapPanel>();
        var scrollViewer = strip.FindDescendantOfType<ScrollViewer>();
        var availableWidth = wrapPanel?.Bounds.Width ?? 0;
        if (availableWidth <= 0)
        {
            availableWidth = scrollViewer?.Bounds.Width ?? strip.Bounds.Width;
        }

        if (availableWidth <= 0)
        {
            return;
        }

        var selectedWidth = (selectedContainer.Bounds.Width > 0
            ? selectedContainer.Bounds.Width
            : selectedContainer.DesiredSize.Width)
            + selectedContainer.Margin.Left + selectedContainer.Margin.Right;

        if (selectedWidth <= 0)
        {
            selectedWidth = 100.0;
        }

        var minTargetIndex = selectedIsPinned ? 0 : pinnedCount;
        var maxTargetIndex = selectedIsPinned ? Math.Max(0, pinnedCount - 1) : visibleDockables.Count - 1;
        var accumulatedWidth = 0.0;
        var targetIndex = minTargetIndex;

        for (var i = 0; i < visibleDockables.Count; i++)
        {
            var item = visibleDockables[i];
            if (ReferenceEquals(item, selectedDockable))
            {
                continue;
            }

            var itemContainer = strip.ContainerFromItem(item) as Control;
            if (itemContainer == null || itemContainer.Bounds.Y > 10.0)
            {
                break;
            }

            var itemWidth = (itemContainer.Bounds.Width > 0
                ? itemContainer.Bounds.Width
                : itemContainer.DesiredSize.Width)
                + itemContainer.Margin.Left + itemContainer.Margin.Right;

            if (accumulatedWidth + itemWidth + selectedWidth <= availableWidth - 2.0)
            {
                accumulatedWidth += itemWidth;
                if (i + 1 >= minTargetIndex)
                {
                    targetIndex = i + 1;
                }
            }
            else
            {
                break;
            }
        }

        targetIndex = Math.Clamp(targetIndex, minTargetIndex, maxTargetIndex);

        if (targetIndex != currentIndex)
        {
            MoveDockable(strip, dock, visibleDockables, selectedDockable, currentIndex, targetIndex);
        }
    }

    private static void MoveDockable(
        DocumentTabStrip strip,
        IDock dock,
        IList<IDockable> visibleDockables,
        IDockable dockable,
        int currentIndex,
        int targetIndex)
    {
        try
        {
            var wasActive = ReferenceEquals(dock.ActiveDockable, dockable);
            if (visibleDockables is ObservableCollection<IDockable> observableCollection)
            {
                observableCollection.Move(currentIndex, targetIndex);
            }
            else
            {
                visibleDockables.RemoveAt(currentIndex);
                visibleDockables.Insert(targetIndex, dockable);
            }

            if (wasActive)
            {
                dock.ActiveDockable = dockable;
                strip.SelectedItem = dockable;
            }
        }
        catch
        {
        }
    }
}
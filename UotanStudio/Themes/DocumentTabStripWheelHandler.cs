using System.Collections.ObjectModel;
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
/// Provides mouse wheel expansion behavior for <see cref="DocumentTabStrip"/>,
/// expanding to multiple rows on wheel down and collapsing to a single row on wheel up.
/// <para>为 <see cref="DocumentTabStrip"/> 提供鼠标滚轮展开行为：
/// 滚轮向下滚动时展开为多行，向上滚动时收起为单行。</para>
/// </summary>
public static class DocumentTabStripWheelHandler
{
    /// <summary>
    /// Defines the <see cref="EnableWheelExpand"/> attached property.
    /// <para>定义 <see cref="EnableWheelExpand"/> 附加属性。</para>
    /// </summary>
    public static readonly AttachedProperty<bool> EnableWheelExpandProperty =
        AvaloniaProperty.RegisterAttached<DocumentTabStrip, bool>(
            "EnableWheelExpand",
            typeof(DocumentTabStripWheelHandler),
            defaultValue: false);

    /// <summary>
    /// Defines the <see cref="IsExpanded"/> attached property.
    /// <para>定义 <see cref="IsExpanded"/> 附加属性。</para>
    /// </summary>
    public static readonly AttachedProperty<bool> IsExpandedProperty =
        AvaloniaProperty.RegisterAttached<DocumentTabStrip, bool>(
            "IsExpanded",
            typeof(DocumentTabStripWheelHandler),
            defaultValue: false);

    static DocumentTabStripWheelHandler()
    {
        EnableWheelExpandProperty.Changed.AddClassHandler<DocumentTabStrip>(OnEnableWheelExpandChanged);
    }

    /// <summary>
    /// Gets the value of the <see cref="EnableWheelExpandProperty"/> on the target element.
    /// <para>获取目标元素上的 <see cref="EnableWheelExpandProperty"/> 附加属性值。</para>
    /// </summary>
    /// <param name="element">The target element.<para>目标元素。</para></param>
    /// <returns>True when wheel expansion is enabled; otherwise, false.<para>若启用滚轮展开则为 true；否则为 false。</para></returns>
    public static bool GetEnableWheelExpand(DocumentTabStrip element) =>
        element.GetValue(EnableWheelExpandProperty);

    /// <summary>
    /// Sets the value of the <see cref="EnableWheelExpandProperty"/> on the target element.
    /// <para>设置目标元素上的 <see cref="EnableWheelExpandProperty"/> 附加属性值。</para>
    /// </summary>
    /// <param name="element">The target element.<para>目标元素。</para></param>
    /// <param name="value">True to enable wheel expansion; otherwise, false.<para>若启用滚轮展开则为 true；否则为 false。</para></param>
    public static void SetEnableWheelExpand(DocumentTabStrip element, bool value) =>
        element.SetValue(EnableWheelExpandProperty, value);

    /// <summary>
    /// Gets the value of the <see cref="IsExpandedProperty"/> on the target element.
    /// <para>获取目标元素上的 <see cref="IsExpandedProperty"/> 附加属性值。</para>
    /// </summary>
    /// <param name="element">The target element.<para>目标元素。</para></param>
    /// <returns>True when the tab strip is expanded to multiple rows; otherwise, false.<para>若标签栏已展开为多行则为 true；否则为 false。</para></returns>
    public static bool GetIsExpanded(DocumentTabStrip element) =>
        element.GetValue(IsExpandedProperty);

    /// <summary>
    /// Sets the value of the <see cref="IsExpandedProperty"/> on the target element.
    /// <para>设置目标元素上的 <see cref="IsExpandedProperty"/> 附加属性值。</para>
    /// </summary>
    /// <param name="element">The target element.<para>目标元素。</para></param>
    /// <param name="value">True to expand to multiple rows; false to collapse to a single row.<para>若展开为多行则为 true；若收起为单行则为 false。</para></param>
    public static void SetIsExpanded(DocumentTabStrip element, bool value) =>
        element.SetValue(IsExpandedProperty, value);

    private static void OnEnableWheelExpandChanged(DocumentTabStrip strip, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is true)
        {
            strip.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
            strip.AttachedToVisualTree += OnStripAttachedToVisualTree;
            ResetPanelMaxWidth(strip);
        }
        else
        {
            strip.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
            strip.AttachedToVisualTree -= OnStripAttachedToVisualTree;
        }
    }

    private static void OnStripAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is DocumentTabStrip strip)
        {
            ResetPanelMaxWidth(strip);
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
            if (!GetIsExpanded(strip))
            {
                SetIsExpanded(strip, true);
                strip.Classes.Set("expanded", true);
            }

            e.Handled = true;
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

                MoveSelectedDocumentToFirstRowEnd(strip);

                SetIsExpanded(strip, false);
                strip.Classes.Set("expanded", false);

                if (scrollViewer != null)
                {
                    scrollViewer.Offset = new Vector(0, 0);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (scrollViewer != null)
                    {
                        scrollViewer.Offset = new Vector(0, 0);
                    }

                    var selected = strip.SelectedItem;
                    if (selected != null && strip.ContainerFromItem(selected) is Control ctrl)
                    {
                        ctrl.BringIntoView();
                    }
                }, DispatcherPriority.Loaded);
            }

            e.Handled = true;
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

        var accumulatedWidth = 0.0;
        var targetIndex = 0;

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

            if (accumulatedWidth + itemWidth + selectedWidth <= availableWidth - 1.0)
            {
                accumulatedWidth += itemWidth;
                targetIndex = i + 1;
            }
            else
            {
                break;
            }
        }

        if (targetIndex != currentIndex)
        {
            try
            {
                if (visibleDockables is ObservableCollection<IDockable> observableCollection)
                {
                    observableCollection.Move(currentIndex, targetIndex);
                }
                else
                {
                    visibleDockables.RemoveAt(currentIndex);
                    visibleDockables.Insert(targetIndex, selectedDockable);
                }

                dock.ActiveDockable = selectedDockable;
                strip.SelectedItem = selectedDockable;
            }
            catch
            {
            }
        }
    }
}
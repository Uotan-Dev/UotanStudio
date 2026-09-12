using DeliveryImageItem = UotanStudio.ViewModels.Shell.DeliveryImage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UotanStudio.Views.Controls;

public partial class DeliveryCompositionView
{
    private DeliveryImageItem? _draggedImage;
    private IPointer? _dragPointer;
    private Point _dragStart;
    private Point _dragPosition;
    private bool _isImageDragging;
    private int _dropIndex;
    private readonly DispatcherTimer _dragScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private void InitializeImageDragging()
    {
        OrderedImages.AddHandler(PointerPressedEvent, OnImagePointerPressed, RoutingStrategies.Tunnel);
        OrderedImages.AddHandler(PointerMovedEvent, OnImagePointerMoved, RoutingStrategies.Tunnel);
        OrderedImages.AddHandler(PointerReleasedEvent, OnImagePointerReleased, RoutingStrategies.Tunnel);
        OrderedImages.PointerCaptureLost += (_, _) => CancelImageDrag();
        OrderedImages.KeyDown += OnImageKeyDown;
        _dragScrollTimer.Tick += (_, _) => ScrollWhileDragging();
    }

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(OrderedImages).Properties.IsLeftButtonPressed || e.Source is not Visual source) return;
        var ancestors = source.GetVisualAncestors().Prepend(source).TakeWhile(visual => visual != OrderedImages).ToArray();
        if (ancestors.Any(visual => visual is Button or CheckBox or TextBox) ||
            ancestors.OfType<ListBoxItem>().FirstOrDefault()?.DataContext is not DeliveryImageItem item || item.IsAutoZero) return;
        CancelImageDrag();
        _draggedImage = item;
        _dragStart = _dragPosition = e.GetPosition(OrderedImages);
        _dragPointer = e.Pointer;
        OrderedImages.SelectedItem = item;
        OrderedImages.Focus();
        e.Pointer.Capture(OrderedImages);
        e.Handled = true;
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedImage is null || e.Pointer != _dragPointer) return;
        _dragPosition = e.GetPosition(OrderedImages);
        if (!_isImageDragging)
        {
            if (Math.Abs(_dragPosition.X - _dragStart.X) < 5 && Math.Abs(_dragPosition.Y - _dragStart.Y) < 5) return;
            _isImageDragging = true;
            _dragScrollTimer.Start();
        }
        UpdateDropTarget();
        e.Handled = true;
    }

    private void UpdateDropTarget()
    {
        var containers = OrderedImages.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        foreach (var container in containers)
            container.Classes.Set("dragging", ReferenceEquals(container.DataContext, _draggedImage));
        var rows = containers.Where(container => container.DataContext is DeliveryImageItem)
            .Select(container => (Container: container, Index: ActiveImages.IndexOf((DeliveryImageItem)container.DataContext!),
                Top: container.TranslatePoint(default, OrderedImages)?.Y ?? 0))
            .Where(row => row.Index >= 0).OrderBy(row => row.Index).ToArray();
        _dropIndex = ActiveImages.Count;
        var markerY = OrderedImages.Bounds.Height - 2;
        foreach (var row in rows)
        {
            if (_dragPosition.Y < row.Top + row.Container.Bounds.Height / 2)
            {
                _dropIndex = row.Index;
                markerY = row.Top;
                break;
            }
            _dropIndex = row.Index + 1;
            markerY = row.Top + row.Container.Bounds.Height;
        }
        DropIndicator.Margin = new Thickness(0, Math.Clamp(markerY, 0, Math.Max(0, OrderedImages.Bounds.Height - 2)), 0, 0);
        DropIndicator.IsVisible = true;
    }

    private void ScrollWhileDragging()
    {
        if (!_isImageDragging || OrderedImages.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
        var delta = _dragPosition.Y < 24 ? -16 : _dragPosition.Y > OrderedImages.Bounds.Height - 24 ? 16 : 0;
        if (delta == 0) return;
        scroll.Offset = new Vector(scroll.Offset.X,
            Math.Clamp(scroll.Offset.Y + delta, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        UpdateDropTarget();
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != _dragPointer) return;
        var item = _draggedImage;
        var from = item is null ? -1 : ActiveImages.IndexOf(item);
        var to = _dropIndex > from ? _dropIndex - 1 : _dropIndex;
        var shouldMove = _isImageDragging && from >= 0 && to != from;
        CancelImageDrag();
        if (shouldMove)
        {
            ActiveImages.Move(from, Math.Clamp(to, 0, ActiveImages.Count - 1));
            OrderedImages.SelectedItem = item;
            OrderedImages.ScrollIntoView(item!);
        }
        e.Handled = true;
    }

    private void OnImageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source)
            .TakeWhile(visual => visual != OrderedImages).Any(visual => visual is TextBox)) return;
        if (e.Key == Key.Escape && _draggedImage is not null)
        {
            CancelImageDrag();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down &&
                 OrderedImages.SelectedItem is DeliveryImageItem { IsAutoZero: false } selected)
        {
            var from = ActiveImages.IndexOf(selected);
            var to = from + (e.Key == Key.Up ? -1 : 1);
            if (from >= 0 && to >= 0 && to < ActiveImages.Count) ActiveImages.Move(from, to);
            OrderedImages.ScrollIntoView(selected);
            e.Handled = true;
        }
    }

    private void CancelImageDrag()
    {
        _dragScrollTimer.Stop();
        _draggedImage = null;
        _isImageDragging = false;
        var pointer = _dragPointer;
        _dragPointer = null;
        if (DropIndicator is not null) DropIndicator.IsVisible = false;
        if (OrderedImages is not null)
            foreach (var row in OrderedImages.GetVisualDescendants().OfType<ListBoxItem>()) row.Classes.Remove("dragging");
        if (pointer is not null && pointer.Captured == OrderedImages) pointer.Capture(null);
    }
}

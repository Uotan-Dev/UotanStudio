using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UotanStudio.ViewModels.Compare;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Compare;

public partial class CompareView
{
    private bool _syncingHexScroll;
    private bool _navigatingHex;

    private IEnumerable<HexViewer> HexEditors() => this.GetVisualDescendants().OfType<HexViewer>()
        .Where(viewer => viewer.IsVisible && viewer.DataContext is CompareColumnViewModel { IsCompareHexEditorVisible: true });

    private void OnHexEditorLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is HexViewer viewer)
        {
            viewer.CaretMoved += OnHexCaretMoved;
            viewer.RangeEdited += OnHexRangeEdited;
            viewer.ViewportChanged += OnHexViewportChanged;
        }
    }

    private void OnHexEditorUnloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is HexViewer viewer)
        {
            viewer.CaretMoved -= OnHexCaretMoved;
            viewer.RangeEdited -= OnHexRangeEdited;
            viewer.ViewportChanged -= OnHexViewportChanged;
        }
    }

    private void OnHexCaretMoved(HexViewer viewer, int offset)
    {
        if (!_navigatingHex && viewer.DataContext is CompareColumnViewModel { IsCompareHexEditorVisible: true }
            && DataContext is CompareViewModel vm)
        {
            vm.SetHexNavigationPosition(offset);
        }
    }

    private void NavigateHexEditors(CompareViewModel vm, int index)
    {
        _navigatingHex = true;
        _syncingHexScroll = true;
        try
        {
            var offset = vm.HexNavigationOffset >= 0 ? vm.HexNavigationOffset : (long)index * 16;
            foreach (var editor in HexEditors())
            {
                editor.GoToOffset(offset);
            }
        }
        finally
        {
            _syncingHexScroll = false;
            _navigatingHex = false;
        }
    }

    private void OnHexRangeEdited(HexViewer viewer, int offset, int count)
    {
        if (viewer.DataContext is CompareColumnViewModel column)
        {
            column.HexBytesEdited(offset, count);
            if (column.IsCompareHexEditorVisible)
            {
                foreach (var editor in HexEditors())
                {
                    editor.RefreshComparison();
                }
            }
        }
    }

    private void OnHexViewportChanged(HexViewer source)
    {
        if (_syncingHexScroll || !source.IsVisible
            || source.DataContext is not CompareColumnViewModel { IsCompareHexEditorVisible: true })
        {
            return;
        }
        _syncingHexScroll = true;
        try
        {
            foreach (var target in HexEditors())
            {
                if (!ReferenceEquals(source, target))
                {
                    target.ScrollOffset = source.ScrollOffset;
                }
            }
        }
        finally
        {
            _syncingHexScroll = false;
        }
    }

    private void OnHexEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && sender is HexViewer { DataContext: CompareColumnViewModel column })
        {
            column.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }
}
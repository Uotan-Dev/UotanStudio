using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Material.Icons;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>可调整大小的目录树与文件列表，窄面板下使用上下布局。</para>
/// A resizable directory tree and file list, stacked vertically in narrow panels.
/// </summary>
public partial class FileManagerEditorView : UserControl
{
    private bool _stacked;
    private GridLength _treeWidth = GridLength.Auto;
    private GridLength _listWidth = new(1, GridUnitType.Star);
    private GridLength _treeHeight = new(1, GridUnitType.Star);
    private GridLength _listHeight = new(3, GridUnitType.Star);

    /// <summary>
    /// <para>创建文件管理器编辑器视图。</para>
    /// Creates the file-manager editor view.
    /// </summary>
    public FileManagerEditorView()
    {
        InitializeComponent();
    }

    private void OnBrowserSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 600;
        if (stacked == _stacked)
        {
            UpdatePaneLimits(e.NewSize.Width, e.NewSize.Height);
            return;
        }

        if (stacked)
        {
            _treeWidth = BrowserGrid.ColumnDefinitions[0].Width;
            _listWidth = BrowserGrid.ColumnDefinitions[2].Width;
            BrowserGrid.ColumnDefinitions = new ColumnDefinitions("*");
            BrowserGrid.RowDefinitions = new RowDefinitions
            {
                new() { Height = _treeHeight },
                new() { Height = new GridLength(7) },
                new() { Height = _listHeight }
            };
        }
        else
        {
            _treeHeight = BrowserGrid.RowDefinitions[0].Height;
            _listHeight = BrowserGrid.RowDefinitions[2].Height;
            BrowserGrid.RowDefinitions = new RowDefinitions("*");
            BrowserGrid.ColumnDefinitions = new ColumnDefinitions
            {
                new() { Width = _treeWidth, MinWidth = 120 },
                new() { Width = new GridLength(7) },
                new() { Width = _listWidth, MinWidth = 180 }
            };
        }

        foreach (var control in new Control[] { SplitterLine, PaneSplitter, SplitterGrip })
        {
            Grid.SetColumn(control, stacked ? 0 : 1);
            Grid.SetRow(control, stacked ? 1 : 0);
        }

        Grid.SetColumn(FilePane, stacked ? 0 : 2);
        Grid.SetRow(FilePane, stacked ? 2 : 0);
        PaneSplitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        PaneSplitter.Cursor = new Cursor(stacked ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
        SplitterLine.Width = stacked ? double.NaN : 3;
        SplitterLine.Height = stacked ? 3 : double.NaN;
        SplitterLine.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        SplitterLine.VerticalAlignment = stacked ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        SplitterGrip.Kind = stacked ? MaterialIconKind.DragHorizontal : MaterialIconKind.DragVertical;
        SplitterGrip.Width = stacked ? 20 : 7;
        SplitterGrip.Height = stacked ? 7 : 20;
        _stacked = stacked;
        UpdatePaneLimits(e.NewSize.Width, e.NewSize.Height);
    }

    private void UpdatePaneLimits(double width, double height)
    {
        if (_stacked)
        {
            var available = Math.Max(0, height - 7);
            BrowserGrid.RowDefinitions[0].MinHeight = Math.Min(80, available / 3);
            BrowserGrid.RowDefinitions[2].MinHeight = Math.Min(120, available / 2);
        }
        else
        {
            BrowserGrid.ColumnDefinitions[0].MaxWidth = Math.Max(120, width - 187);
        }
    }

    private void OnFilePaneSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        FilePane.Classes.Set("compact", e.NewSize.Width < 480);
    }

    private void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FileManagerEditorViewModel vm
            && e.Source is Control control
            && control.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()
                is { DataContext: FileManagerEntryViewModel entry })
        {
            vm.OpenEntry(entry);
            e.Handled = true;
        }
    }

    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FileManagerEditorViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && vm.SelectedEntry is { } entry)
        {
            vm.OpenEntry(entry);
            e.Handled = true;
        }
        else if ((e.Key == Key.Back || (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.Alt))
                 && vm.NavigateUpCommand.CanExecute(null))
        {
            vm.NavigateUpCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnFileListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not FileManagerEditorViewModel vm)
        {
            return;
        }

        foreach (var item in e.AddedItems.OfType<FileManagerEntryViewModel>())
        {
            if (!vm.SelectedEntries.Contains(item))
            {
                vm.SelectedEntries.Add(item);
            }
        }

        foreach (var item in e.RemovedItems.OfType<FileManagerEntryViewModel>())
        {
            vm.SelectedEntries.Remove(item);
        }
    }
}
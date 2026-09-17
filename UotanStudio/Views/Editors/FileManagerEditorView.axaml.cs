using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Material.Icons;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>可调整大小的目录树与文件列表，窄面板下使用上下布局；也可以只保留文件列表
/// （<see cref="ShowDirectoryTree"/> 为 <c>false</c> 时去掉目录树与分隔条）。</para>
/// A resizable directory tree and file list, stacked vertically in narrow panels; the file list can also be shown
/// on its own (<see cref="ShowDirectoryTree"/> set to <c>false</c> drops the tree and the splitter).
/// </summary>
public partial class FileManagerEditorView : UserControl
{
    /// <summary><para><see cref="ShowDirectoryTree"/> 的样式属性。</para>Styled property for <see cref="ShowDirectoryTree"/>.</summary>
    public static readonly StyledProperty<bool> ShowDirectoryTreeProperty =
        AvaloniaProperty.Register<FileManagerEditorView, bool>(nameof(ShowDirectoryTree), defaultValue: true);

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
        ApplyDirectoryTreeVisibility();
    }

    /// <summary>
    /// <para>是否显示左侧目录树；为 <c>false</c> 时只保留文件列表，供「基础」页等只需要文件视图的调用方复用。</para>
    /// Whether the left-hand directory tree is shown; when <c>false</c> only the file list remains, so callers such
    /// as the basic page can reuse the file view on its own.
    /// </summary>
    public bool ShowDirectoryTree
    {
        get => GetValue(ShowDirectoryTreeProperty);
        set => SetValue(ShowDirectoryTreeProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowDirectoryTreeProperty)
        {
            ApplyDirectoryTreeVisibility();
        }
    }

    /// <summary>
    /// <para>按 <see cref="ShowDirectoryTree"/> 切换目录树与分隔条的可见性，并把网格重置成对应形状。</para>
    /// Toggles the tree and the splitter according to <see cref="ShowDirectoryTree"/> and resets the grid to the
    /// matching shape.
    /// </summary>
    private void ApplyDirectoryTreeVisibility()
    {
        var showTree = ShowDirectoryTree;
        FileTree.IsVisible = showTree;
        SplitterLine.IsVisible = showTree;
        PaneSplitter.IsVisible = showTree;
        SplitterGrip.IsVisible = showTree;
        if (showTree)
        {
            _stacked = Bounds.Width > 0 && Bounds.Width < 600;
            ApplyLayout(_stacked);
            return;
        }

        _stacked = false;
        BrowserGrid.RowDefinitions = new RowDefinitions("*");
        BrowserGrid.ColumnDefinitions = new ColumnDefinitions("*");
        Grid.SetColumn(FilePane, 0);
        Grid.SetRow(FilePane, 0);
    }

    private void OnBrowserSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!ShowDirectoryTree)
        {
            return;
        }

        var stacked = e.NewSize.Width < 600;
        if (stacked == _stacked)
        {
            UpdatePaneLimits(e.NewSize.Width, e.NewSize.Height);
            return;
        }

        ApplyLayout(stacked);
        UpdatePaneLimits(e.NewSize.Width, e.NewSize.Height);
    }

    /// <summary>
    /// <para>把目录树与文件列表按左右或上下排布；切换前保留用户调整过的比例。</para>
    /// Arranges the tree and the file list side by side or stacked, keeping the proportions the user adjusted.
    /// </summary>
    /// <param name="stacked"><para>是否使用上下排布。</para>Whether the stacked arrangement is used.</param>
    private void ApplyLayout(bool stacked)
    {
        // Preserve independently adjusted proportions when docking changes the available width.
        if (stacked)
        {
            if (BrowserGrid.ColumnDefinitions.Count >= 3)
            {
                _treeWidth = BrowserGrid.ColumnDefinitions[0].Width;
                _listWidth = BrowserGrid.ColumnDefinitions[2].Width;
            }

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
            if (BrowserGrid.RowDefinitions.Count >= 3)
            {
                _treeHeight = BrowserGrid.RowDefinitions[0].Height;
                _listHeight = BrowserGrid.RowDefinitions[2].Height;
            }

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
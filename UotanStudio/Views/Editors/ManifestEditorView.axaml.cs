using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using Material.Icons;
using UotanStudio.Messages;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>Manifest（AXML）编辑器视图：左栏元素结构树 + 右栏属性编辑与 XML 文本预览。</para>
/// Manifest (AXML) editor view: a left element tree plus right attribute-editing and XML preview panes.
/// </summary>
public partial class ManifestEditorView : UserControl
{
    private bool _isRightViewSideBySide;
    private double _stackedAttributesWeight = 1;
    private double _stackedSourceWeight = 1;
    private double _sideAttributesWeight = 1;
    private double _sideSourceWeight = 1;

    /// <summary>
    /// <para>创建 Manifest 编辑器视图并初始化组件。</para>
    /// Creates the Manifest editor view and initializes its components.
    /// </summary>
    public ManifestEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// <para>在右侧上下布局和左右布局之间切换。</para>
    /// Switches the right pane between stacked and side-by-side layouts.
    /// </summary>
    private void OnToggleRightViewClick(object? sender, RoutedEventArgs e)
    {
        if (_isRightViewSideBySide)
        {
            RememberSideBySideRatio();
            _isRightViewSideBySide = false;
            ApplyStackedLayout();
        }
        else
        {
            RememberStackedRatio();
            _isRightViewSideBySide = true;
            ApplySideBySideLayout();
        }
    }

    private void RememberStackedRatio()
    {
        _stackedAttributesWeight = NormalizeLayoutWeight(AttributesPane.Bounds.Height);
        _stackedSourceWeight = NormalizeLayoutWeight(SourceEditor.Bounds.Height);
    }

    private void RememberSideBySideRatio()
    {
        _sideAttributesWeight = NormalizeLayoutWeight(AttributesPane.Bounds.Width);
        _sideSourceWeight = NormalizeLayoutWeight(SourceEditor.Bounds.Width);
    }

    private void ApplyStackedLayout()
    {
        RightPaneGrid.ColumnDefinitions = CreateSingleColumn();
        RightPaneGrid.RowDefinitions = CreateRows(_stackedAttributesWeight, _stackedSourceWeight);

        SetPanePosition(AttributesPane, 0, 0);
        SetPanePosition(RightPaneSplitterTrack, 1, 0);
        SetPanePosition(RightPaneSplitter, 1, 0);
        SetPanePosition(RightPaneSplitterGrip, 1, 0);
        SetPanePosition(SourceEditor, 2, 0);

        RightPaneSplitter.ResizeDirection = GridResizeDirection.Rows;
        RightPaneSplitter.Width = double.NaN;
        RightPaneSplitter.Height = 7;
        RightPaneSplitter.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        RightPaneSplitter.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        RightPaneSplitter.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

        RightPaneSplitterGrip.Kind = MaterialIconKind.DragHorizontal;
        RightPaneSplitterGrip.Width = 20;
        RightPaneSplitterGrip.Height = 7;
        RightViewLayoutIcon.Kind = MaterialIconKind.ViewSplitVertical;
    }

    private void ApplySideBySideLayout()
    {
        RightPaneGrid.ColumnDefinitions = CreateColumns(_sideAttributesWeight, _sideSourceWeight);
        RightPaneGrid.RowDefinitions = CreateSingleRow();

        SetPanePosition(AttributesPane, 0, 0);
        SetPanePosition(RightPaneSplitterTrack, 0, 1);
        SetPanePosition(RightPaneSplitter, 0, 1);
        SetPanePosition(RightPaneSplitterGrip, 0, 1);
        SetPanePosition(SourceEditor, 0, 2);

        RightPaneSplitter.ResizeDirection = GridResizeDirection.Columns;
        RightPaneSplitter.Width = 7;
        RightPaneSplitter.Height = double.NaN;
        RightPaneSplitter.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        RightPaneSplitter.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        RightPaneSplitter.Cursor = new Cursor(StandardCursorType.SizeWestEast);

        RightPaneSplitterGrip.Kind = MaterialIconKind.DragVertical;
        RightPaneSplitterGrip.Width = 7;
        RightPaneSplitterGrip.Height = 20;
        RightViewLayoutIcon.Kind = MaterialIconKind.ViewSplitHorizontal;
    }

    private static RowDefinitions CreateRows(double firstWeight, double secondWeight)
    {
        var definitions = new RowDefinitions
        {
            new(firstWeight, GridUnitType.Star),
            new(3, GridUnitType.Pixel),
            new(secondWeight, GridUnitType.Star),
        };
        return definitions;
    }

    private static ColumnDefinitions CreateColumns(double firstWeight, double secondWeight)
    {
        var definitions = new ColumnDefinitions
        {
            new(firstWeight, GridUnitType.Star),
            new(3, GridUnitType.Pixel),
            new(secondWeight, GridUnitType.Star),
        };
        return definitions;
    }

    private static RowDefinitions CreateSingleRow()
    {
        return new RowDefinitions
        {
            new(1, GridUnitType.Star),
        };
    }

    private static ColumnDefinitions CreateSingleColumn()
    {
        return new ColumnDefinitions
        {
            new(1, GridUnitType.Star),
        };
    }

    private static void SetPanePosition(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
    }

    private static double NormalizeLayoutWeight(double value)
    {
        return double.IsFinite(value) && value > 0 ? value : 1;
    }

    /// <summary>
    /// <para>左栏结构树选中变更：同步 ViewModel 选中项，并广播节点选中消息
    /// （供属性面板展示选中元素的详情）。</para>
    /// Left tree selection changed: syncs the ViewModel selection and broadcasts a node-selection
    /// message so the properties panel can show the selected element's details.
    /// </summary>
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ManifestEditorViewModel vm)
        {
            vm.SelectedNode = ElementTree.SelectedItem as ManifestTreeNodeViewModel;
            WeakReferenceMessenger.Default.Send(new ManifestNodeSelectionChangedMessage(vm, vm.SelectedNode));
        }
    }
}

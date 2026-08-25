using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

public partial class NodeEditorView : UserControl
{
    public NodeEditorView()
    {
        InitializeComponent();
        // 订阅画布选中变更：NodifyEditor 内部维护 SelectedItems，需在此同步到 ViewModel
        // Subscribe to canvas selection changes: NodifyEditor keeps its own SelectedItems, so sync to the ViewModel here.
        Editor.SelectionChanged += OnEditorSelectionChanged;
    }

    /// <summary>
    /// <para>画布节点选中变更时，将新增选中中的第一个节点同步到 ViewModel，驱动右侧属性面板。</para>
    /// When the canvas node selection changes, sync the first added node to the ViewModel to drive the properties panel.
    /// </summary>
    /// <param name="sender"><para>事件源。</para>Event source.</param>
    /// <param name="e"><para>选中变更事件参数。</para>The selection changed args.</param>
    private void OnEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is NodeEditorViewModel vm)
        {
            foreach (var item in e.AddedItems)
            {
                if (item is UotanStudio.ViewModels.NodeViewModels.FlowNodeViewModel node)
                {
                    vm.SelectNode(node);
                    break;
                }
            }
        }
    }

    private void OnToolboxItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is NodeToolboxItem item)
        {
            if (DataContext is NodeEditorViewModel vm)
            {
                var node = item.CreateNode();
                vm.AddNode(node);
            }
        }
    }
}
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NodifyM.Avalonia.Controls;
using System.Collections.Generic;
using System.Linq;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Pages;

public partial class NodeEditorView : UserControl
{
    public NodeEditorView()
    {
        InitializeComponent();
        KeyDown += OnViewKeyDown;
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

    /// <summary>
    /// <para>处理键盘 Delete 键：删除选中节点与选中连接线。</para>
    /// Handles the Delete key: removes selected nodes and selected connections.
    /// </summary>
    /// <remarks>
    /// <para>NodifyEditor 的 SelectedItems 仅跟踪节点；连接线选中需从 Selection 中筛选 NodeConnectionViewModel。</para>
    /// NodifyEditor's SelectedItems only tracks nodes; connection selection must be filtered from Selection for NodeConnectionViewModel.
    /// </remarks>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (DataContext is not NodeEditorViewModel vm) return;
        if (this.FindControl<NodifyEditor>("Editor") is not { } editor) return;

        var removed = false;

        // 删除选中节点（SelectedItems 为 IList，含 FlowNodeViewModel）
        var selectedItems = editor.SelectedItems;
        var selectedNodes = selectedItems is null
            ? new List<FlowNodeViewModel>()
            : selectedItems.OfType<FlowNodeViewModel>().ToList();
        foreach (var node in selectedNodes)
        {
            vm.RemoveNode(node);
            removed = true;
        }

        // 删除选中连接线：从 Selection.SelectedItems 中筛 NodeConnectionViewModel
        if (editor.Selection?.SelectedItems is IEnumerable<object> selConns)
        {
            var selectedConnections = selConns.OfType<NodeConnectionViewModel>().ToList();
            foreach (var conn in selectedConnections)
            {
                vm.RemoveConnection(conn);
                removed = true;
            }
        }

        // 退路：若 Selection 未暴露，扫画布所有连接查 IsSelected（部分 NodifyM 版本以依赖属性标记）
        if (!removed)
        {
            foreach (var conn in vm.Connections.ToList())
            {
                // NodeConnectionViewModel 当前无 IsSelected 属性；此分支实际为占位
                // 真实选中来源上面 Selection.SelectedItems；无则不删
            }
        }

        if (removed) e.Handled = true;
    }
}

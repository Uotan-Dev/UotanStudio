using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NodifyM.Avalonia.Controls;
using UotanStudio.Areas.NodeEditor.ViewModels;
using UotanStudio.Services;

namespace UotanStudio.Areas.NodeEditor;

/// <summary>
/// Node editor view for creating and managing flow nodes.
/// <para>节点编辑器视图，用于创建和管理流节点。</para>
/// </summary>
public partial class NodeEditorView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NodeEditorView"/> class.
    /// <para>初始化 <see cref="NodeEditorView"/> 类的新实例。</para>
    /// </summary>
    public NodeEditorView()
    {
        InitializeComponent();
        KeyDown += OnViewKeyDown;
        ContextRequested += OnContextRequested;
    }

    /// <summary>
    /// Handles the toolbox item clicked event.
    /// <para>处理工具箱项点击事件。</para>
    /// </summary>
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
    /// Handles the Delete key: removes selected nodes and selected connections.
    /// <para>处理键盘 Delete 键：删除选中节点与选中连接线。</para>
    /// </summary>
    /// <remarks>
    /// NodifyEditor's SelectedItems only tracks nodes; connection selection must be filtered from Selection for NodeConnectionViewModel.
    /// <para>NodifyEditor 的 SelectedItems 仅跟踪节点；连接线选中需从 Selection 中筛选 NodeConnectionViewModel。</para>
    /// </remarks>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        if (DataContext is not NodeEditorViewModel vm)
        {
            return;
        }

        if (this.FindControl<NodifyEditor>("Editor") is not { } editor)
        {
            return;
        }

        var removed = false;

        var selectedItems = editor.SelectedItems;
        var selectedNodes = selectedItems is null
            ? []
            : selectedItems.OfType<FlowNodeViewModel>().ToList();
        foreach (var node in selectedNodes)
        {
            vm.RemoveNode(node);
            removed = true;
        }

        // 删除选中连接线
        if (editor.Selection?.SelectedItems is IEnumerable<object> selConns)
        {
            var selectedConnections = selConns.OfType<NodeConnectionViewModel>().ToList();
            foreach (var conn in selectedConnections)
            {
                vm.RemoveConnection(conn);
                removed = true;
            }
        }

        if (removed)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Handles the context-requested event: dispatches node / connection / port / canvas-empty menus by the hit target's DataContext type.
    /// <para>处理右键请求：根据命中目标的 DataContext 类型分派节点/连接线/端口/画布空白菜单。</para>
    /// </summary>
    /// <remarks>
    /// NodifyM's Connection/Node/NodeInput/NodeOutput controls do not natively support ContextMenu; handled centrally at the host layer.
    /// <para>NodifyM 的 Connection/Node/NodeInput/NodeOutput 控件本身不支持 ContextMenu，故在宿主层统一处理。</para>
    /// </remarks>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not NodeEditorViewModel vm)
        {
            return;
        }

        if (this.FindControl<NodifyEditor>("Editor") is not { } editor)
        {
            return;
        }

        // 命中测试：沿可视树从事件源向上找首个带识别性 DataContext 的控件
        var hit = ResolveHitTarget(e.Source);
        var menu = hit switch
        {
            FlowNodeViewModel node => BuildNodeMenu(vm, node),
            NodeConnectionViewModel conn => BuildConnectionMenu(vm, conn),
            NodeConnectorViewModel port => BuildPortMenu(vm, port),
            _ => BuildCanvasMenu(vm)
        };

        // 显示菜单（相对于控件坐标）
        menu.Open(editor);
        e.Handled = true;
    }

    /// <summary>
    /// Walks up the visual tree from the event source to find the first view model with a recognizable DataContext.
    /// <para>沿可视树从事件源向上找首个具识别性 DataContext 的视图模型。</para>
    /// </summary>
    private static object? ResolveHitTarget(object? source)
    {
        if (source is not Avalonia.Visual visual)
        {
            return null;
        }

        Avalonia.StyledElement? node = visual;
        while (node is not null)
        {
            if (node is Control c && c.DataContext is FlowNodeViewModel fn)
            {
                return fn;
            }

            if (node is Control cc && cc.DataContext is NodeConnectionViewModel nc)
            {
                return nc;
            }

            if (node is Control pc && pc.DataContext is NodeConnectorViewModel pk)
            {
                return pk;
            }

            node = node.Parent;
        }
        return null;
    }

    /// <summary>
    /// Builds the node context menu: delete node, duplicate node, run this node, isolate (disconnect all).
    /// <para>构建节点右键菜单：删除节点、复制节点、运行此节点、隔离（断开所有连接）。</para>
    /// </summary>
    private static ContextMenu BuildNodeMenu(NodeEditorViewModel vm, FlowNodeViewModel node)
    {
        var menu = new ContextMenu();
        var lng = LanguageService.Instance;

        var itemDelete = new MenuItem { Header = lng.GetString("Node_Ctx_DeleteNode") };
        itemDelete.Click += (_, _) => vm.RemoveNode(node);

        var itemDuplicate = new MenuItem { Header = lng.GetString("Node_Ctx_DuplicateNode") };
        itemDuplicate.Click += (_, _) =>
        {
            var copy = node switch
            {
                FirmwareNodeViewModel => new FirmwareNodeViewModel(node.Key),
                FlashNodeViewModel => new FlashNodeViewModel(node.Key),
                DataNodeViewModel => new DataNodeViewModel(node.Key),
                FlowControlNodeViewModel => new FlowControlNodeViewModel(node.Key),
                OutputNodeViewModel => new OutputNodeViewModel(node.Key),
                _ => new FlowNodeViewModel(node.Title, node.Category, node.Key)
            };
            copy.Title = node.Title;
            copy.Location = new Avalonia.Point(node.Location.X + 40, node.Location.Y + 40);
            for (var i = 0; i < node.Input.Count; i++)
            {
                copy.Input.Add(new NodeConnectorViewModel { Title = node.Input[i].Title, Value = node.Input[i].Value });
            }

            for (var i = 0; i < node.Output.Count; i++)
            {
                copy.Output.Add(new NodeConnectorViewModel { Title = node.Output[i].Title, Value = node.Output[i].Value });
            }

            vm.AddNode(copy);
        };

        var itemRun = new MenuItem { Header = lng.GetString("Node_Ctx_RunNode") };
        itemRun.Click += (_, _) => vm.RunCommand.Execute(null);

        var itemIsolate = new MenuItem { Header = lng.GetString("Node_Ctx_IsolateNode") };
        itemIsolate.Click += (_, _) =>
        {
            var related = vm.Connections
                .Where(c => c.Source?.Node == node || c.Target?.Node == node)
                .ToList();
            foreach (var conn in related)
            {
                vm.RemoveConnection(conn);
            }
        };

        menu.Items.Add(itemDelete);
        menu.Items.Add(itemDuplicate);
        menu.Items.Add(new Separator());
        menu.Items.Add(itemRun);
        menu.Items.Add(itemIsolate);
        return menu;
    }

    /// <summary>
    /// Builds the connection context menu: delete connection, disconnect (preserve both endpoints' data).
    /// <para>构建连接线右键菜单：删除连接、断开（保留两端数据）。</para>
    /// </summary>
    private static ContextMenu BuildConnectionMenu(NodeEditorViewModel vm, NodeConnectionViewModel conn)
    {
        var menu = new ContextMenu();
        var lng = LanguageService.Instance;

        var itemDelete = new MenuItem { Header = lng.GetString("Node_Ctx_DeleteConnection") };
        itemDelete.Click += (_, _) => vm.RemoveConnection(conn);

        var itemDisconnect = new MenuItem { Header = lng.GetString("Node_Ctx_DisconnectConnection") };
        itemDisconnect.Click += (_, _) => conn.Disconnect();

        menu.Items.Add(itemDelete);
        menu.Items.Add(itemDisconnect);
        return menu;
    }

    /// <summary>
    /// Builds the port context menu: disconnect all connections on this port.
    /// <para>构建端口右键菜单：断开端口所有连接。</para>
    /// </summary>
    private static ContextMenu BuildPortMenu(NodeEditorViewModel vm, NodeConnectorViewModel port)
    {
        var menu = new ContextMenu();
        var lng = LanguageService.Instance;

        var itemDisconnect = new MenuItem { Header = lng.GetString("Node_Ctx_DisconnectPort") };
        itemDisconnect.Click += (_, _) =>
        {
            var related = vm.Connections
                .Where(c => c.Source == port || c.Target == port)
                .ToList();
            foreach (var conn in related)
            {
                vm.RemoveConnection(conn);
            }
        };

        menu.Items.Add(itemDisconnect);
        return menu;
    }

    /// <summary>
    /// Builds the canvas-empty context menu: select all, clear canvas, run all, reset zoom.
    /// <para>构建画布空白右键菜单：全选、清空画布、运行全部、重置缩放。</para>
    /// </summary>
    private static ContextMenu BuildCanvasMenu(NodeEditorViewModel vm)
    {
        var menu = new ContextMenu();
        var lng = LanguageService.Instance;

        var itemSelectAll = new MenuItem { Header = lng.GetString("Node_Ctx_SelectAll") };
        itemSelectAll.Click += (_, _) =>
        {
            foreach (var n in vm.Nodes)
            {
                n.IsSelected = true;
            }
        };

        var itemClearAll = new MenuItem { Header = lng.GetString("Node_Ctx_ClearAll") };
        itemClearAll.Click += (_, _) => vm.ClearAllCommand.Execute(null);

        var itemRunAll = new MenuItem { Header = lng.GetString("Node_Ctx_RunAll") };
        itemRunAll.Click += (_, _) => vm.RunCommand.Execute(null);

        var itemResetZoom = new MenuItem { Header = lng.GetString("Node_Ctx_ResetZoom") };
        itemResetZoom.Click += (_, _) => vm.ResetZoomCommand.Execute(null);

        menu.Items.Add(itemSelectAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(itemRunAll);
        menu.Items.Add(itemClearAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(itemResetZoom);
        return menu;
    }
}
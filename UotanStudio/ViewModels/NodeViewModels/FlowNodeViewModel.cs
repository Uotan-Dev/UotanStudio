using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using NodifyM.Avalonia.ViewModelBase;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>节点类别枚举，定义不同类型的工具箱节点。</para>
/// Enum defining different categories of toolbox nodes.
/// </summary>
public enum NodeCategory
{
    /// <summary>
    /// <para>固件操作节点。</para>
    /// Firmware operation node.
    /// </summary>
    Firmware,
    /// <summary>
    /// <para>烧录操作节点。</para>
    /// Flash operation node.
    /// </summary>
    Flash,
    /// <summary>
    /// <para>数据处理节点。</para>
    /// Data processing node.
    /// </summary>
    Data,
    /// <summary>
    /// <para>流程控制节点。</para>
    /// Flow control node.
    /// </summary>
    Flow,
    /// <summary>
    /// <para>输出节点。</para>
    /// Output node.
    /// </summary>
    Output
}

/// <summary>
/// <para>节点视图模型，表示节点编辑器画布上的一个节点。实现 INodePosition 接口以支持节点定位。</para>
/// View model for a node, representing a single node on the node editor canvas. Implements INodePosition for node positioning.
/// </summary>
public partial class FlowNodeViewModel : ViewModelBase, INodePosition
{
    private string _title = string.Empty;
    private NodeCategory _category;

    /// <summary>
    /// <para>获取或设置节点的显示标题。</para>
    /// Gets or sets the display title of the node.
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>
    /// <para>获取或设置节点在画布上的位置。</para>
    /// Gets or sets the position of the node on the canvas.
    /// </summary>
    public Point Location
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>获取或设置节点的类别。</para>
    /// Gets or sets the category of the node.
    /// </summary>
    public NodeCategory Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    /// <summary>
    /// <para>获取或设置节点是否被选中。</para>
    /// Gets or sets whether the node is selected.
    /// </summary>
    public bool IsSelected
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// <para>获取节点的输入连接器集合。添加连接器时自动绑定 Node 反向引用。</para>
    /// Gets the collection of input connectors for the node. Node back-reference is auto-bound when a connector is added.
    /// </summary>
    public ObservableCollection<NodeConnectorViewModel> Input { get; }

    /// <summary>
    /// <para>获取节点的输出连接器集合。添加连接器时自动绑定 Node 反向引用。</para>
    /// Gets the collection of output connectors for the node. Node back-reference is auto-bound when a connector is added.
    /// </summary>
    public ObservableCollection<NodeConnectorViewModel> Output { get; }

    /// <summary>
    /// <para>获取该节点上的全部连接（运行时由 NodeEditorViewModel 维护，用于连接器断开回溯）。</para>
    /// Gets all connections on this node (maintained at runtime by NodeEditorViewModel, used for connector disconnect backtracking).
    /// </summary>
    public ObservableCollection<NodeConnectionViewModel> NodeConnections { get; } = [];

    /// <summary>
    /// <para>创建具有指定标题和类别的节点。</para>
    /// Creates a node with the specified title and category.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    /// <param name="category">
    /// <para>节点的类别。</para>
    /// The category of the node.
    /// </param>
    public FlowNodeViewModel(string title, NodeCategory category)
    {
        _title = title;
        _category = category;
        Input = [];
        Output = [];
        Input.CollectionChanged += OnConnectorsChanged;
        Output.CollectionChanged += OnConnectorsChanged;
    }

    private void OnConnectorsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (NodeConnectorViewModel connector in e.NewItems)
            {
                connector.Node = this;
            }
        }
    }
}
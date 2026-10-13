namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// View model for a node connection, representing a link between two connectors.
/// <para>节点连接线视图模型，表示两个连接器之间的连接。</para>
/// </summary>
public partial class NodeConnectionViewModel : ViewModelBase
{
    /// <summary>Whether this connection is selected on the canvas.</summary>
    public bool IsSelected
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Gets or sets the source connector (output port) of the connection.
    /// <para>获取或设置连接的源连接器（输出端口）。</para>
    /// </summary>
    public NodeConnectorViewModel? Source
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Gets or sets the target connector (input port) of the connection.
    /// <para>获取或设置连接的目标连接器（输入端口）。</para>
    /// </summary>
    public NodeConnectorViewModel? Target
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Creates a connection view model.
    /// <para>创建连接线视图模型。</para>
    /// </summary>
    /// <param name="source">源连接器（输出端口）。</param>
    /// <param name="target">目标连接器（输入端口）。</param>
    public NodeConnectionViewModel(NodeConnectorViewModel source, NodeConnectorViewModel target)
    {
        Source = source;
        Target = target;
    }
}
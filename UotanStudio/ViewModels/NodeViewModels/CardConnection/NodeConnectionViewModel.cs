namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>节点连接线视图模型，表示两个连接器之间的连接。</para>
    /// View model for a node connection, representing a link between two connectors.
    /// </summary>
    public partial class NodeConnectionViewModel : ViewModelBase
    {
        private NodeConnectorViewModel? _source;
        private NodeConnectorViewModel? _target;

        /// <summary>
        /// <para>获取或设置连接的源连接器（输出端口）。</para>
        /// Gets or sets the source connector (output port) of the connection.
        /// </summary>
        public NodeConnectorViewModel? Source
        {
            get => _source;
            set => SetProperty(ref _source, value);
        }

        /// <summary>
        /// <para>获取或设置连接的目标连接器（输入端口）。</para>
        /// Gets or sets the target connector (input port) of the connection.
        /// </summary>
        public NodeConnectorViewModel? Target
        {
            get => _target;
            set => SetProperty(ref _target, value);
        }

        /// <summary>
        /// <para>创建连接线视图模型。</para>
        /// Creates a connection view model.
        /// </summary>
        /// <param name="source">源连接器（输出端口）。</param>
        /// <param name="target">目标连接器（输入端口）。</param>
        public NodeConnectionViewModel(NodeConnectorViewModel source, NodeConnectorViewModel target)
        {
            _source = source;
            _target = target;
        }
    }
}
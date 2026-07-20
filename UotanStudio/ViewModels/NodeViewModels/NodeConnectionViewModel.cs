using CommunityToolkit.Mvvm.Input;
using System.Linq;

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
        private string? _text;

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
        /// <para>获取或设置连接线上显示的文本（可选）。</para>
        /// Gets or sets the text displayed on the connection (optional).
        /// </summary>
        public string? Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        /// <summary>
        /// <para>断开该连接。从两端节点的连接集合中移除，并更新连接状态。</para>
        /// Disconnects this connection. Removes it from the connection collections of both endpoint nodes, and updates the connected state.
        /// </summary>
        [RelayCommand]
        public void Disconnect()
        {
            // 先缓存两端引用，避免在枚举 Connections 时访问被修改的 NodeConnections
            var source = Source;
            var target = Target;
            var sourceNode = source?.Node;
            var targetNode = target?.Node;

            if (sourceNode is not null)
                sourceNode.NodeConnections.Remove(this);
            if (targetNode is not null && targetNode != sourceNode)
                targetNode.NodeConnections.Remove(this);

            // 在 NodeConnections 已更新后再重算 IsConnected，避免 InvalidOperationException
            if (source is not null)
                source.SetConnected(source.Connections.Any());
            if (target is not null)
                target.SetConnected(target.Connections.Any());
        }

        /// <summary>
        /// <para>在连接线指定位置分裂为两段（当前实现简化为断开原连接）。</para>
        /// Splits the connection at the specified position into two segments (current implementation simplifies this to disconnecting the original connection).
        /// </summary>
        [RelayCommand]
        public void Split()
        {
            // 分裂操作需要插入一个中间节点，当前实现简化为断开原连接，
            // 由 NodeEditorViewModel 的 RemoveConnection 流程接管。
            Disconnect();
        }
    }
}

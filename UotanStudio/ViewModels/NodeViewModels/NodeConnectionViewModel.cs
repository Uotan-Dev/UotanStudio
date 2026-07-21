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
        /// <para>请求宿主（NodeEditorViewModel）删除本连接的回调。宿主在创建连接时绑定。</para>
        /// Callback requesting the host (NodeEditorViewModel) to remove this connection. Bound by the host on creation.
        /// </summary>
        public System.Action<NodeConnectionViewModel>? RemoveRequested { get; set; }

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
            var source = Source;
            var target = Target;
            var sourceNode = source?.Node;
            var targetNode = target?.Node;

            if (sourceNode is not null)
                sourceNode.NodeConnections.Remove(this);
            if (targetNode is not null && targetNode != sourceNode)
                targetNode.NodeConnections.Remove(this);

            // 重算 IsConnected 前需先更新 NodeConnections，避免枚举时集合变更
            if (source is not null)
                source.SetConnected(source.Connections.Any());
            if (target is not null)
                target.SetConnected(target.Connections.Any());
        }

        /// <summary>
        /// <para>在连接线指定位置分裂为两段。NodifyM 的 Connection 控件未暴露 RemoveCommand，故借 SplitCommand 这条已绑定路径触发删除。</para>
        /// Splits the connection at the specified position. NodifyM's Connection control does not expose RemoveCommand, so we reuse the bound SplitCommand path to trigger removal.
        /// </summary>
        [RelayCommand]
        public void Split()
        {
            // NodifyM 的 Connection 无 RemoveCommand；把 Split 按钮当作删除入口，
            // 调用 Remove() 完成断开 + 请求宿主从 Connections 集合移除。
            Remove();
        }

        /// <summary>
        /// <para>删除本连接线。先断开两端连接关系，再通过 RemoveRequested 回调请求宿主从集合移除。</para>
        /// Removes this connection. Disconnects both endpoints first, then asks the host to remove it from the collection via RemoveRequested.
        /// </summary>
        [RelayCommand]
        public void Remove()
        {
            // 先断开两端口的连接关系（更新 IsConnected、清理 NodeConnections）
            Disconnect();
            // 再请求宿主从 Connections 集合移除本对象
            RemoveRequested?.Invoke(this);
        }
    }
}

using Avalonia;
using System;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>节点连接器视图模型，表示节点上的输入或输出端口。</para>
    /// View model for a node connector, representing an input or output port on a node.
    /// </summary>
    public partial class NodeConnectorViewModel : ViewModelBase
    {
        private string _title = string.Empty;
        private Guid _id = Guid.NewGuid();
        private Point _anchor;
        private int _maxConnections = 0;
        private bool _isConnected;

        /// <summary>
        /// <para>获取或设置连接器的显示标题。</para>
        /// Gets or sets the display title of the connector.
        /// </summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>
        /// <para>获取或设置连接器的唯一标识符。</para>
        /// Gets or sets the unique identifier of the connector.
        /// </summary>
        public Guid Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        /// <summary>
        /// <para>获取或设置连接器的锚点位置（用于连接线定位）。</para>
        /// Gets or sets the anchor position of the connector (used for connection line positioning).
        /// </summary>
        public Point Anchor
        {
            get => _anchor;
            set => SetProperty(ref _anchor, value);
        }

        /// <summary>
        /// <para>获取或设置连接器允许的最大连接数，0 表示不限制。</para>
        /// Gets or sets the maximum number of connections allowed for this connector. 0 means unlimited.
        /// </summary>
        public int MaxConnections
        {
            get => _maxConnections;
            set => SetProperty(ref _maxConnections, value);
        }

        /// <summary>
        /// <para>获取或设置连接器是否已连接。</para>
        /// Gets or sets whether the connector is connected.
        /// </summary>
        public bool IsConnected
        {
            get => _isConnected;
            set => SetProperty(ref _isConnected, value);
        }
    }
}

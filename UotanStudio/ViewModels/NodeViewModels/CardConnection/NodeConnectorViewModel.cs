using System;
using NodifyM.Avalonia.ViewModelBase;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>节点连接器视图模型，表示节点上的输入或输出端口。</para>
    /// View model for a node connector, representing an input or output port on a node.
    /// </summary>
    public partial class NodeConnectorViewModel : ConnectorViewModelBase
    {
        private Guid _id = Guid.NewGuid();
        private int _maxConnections;
        private object? _data;

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
        /// <para>获取或设置连接器允许的最大连接数，0 表示不限制。</para>
        /// Gets or sets the maximum number of connections allowed for this connector. 0 means unlimited.
        /// </summary>
        public int MaxConnections
        {
            get => _maxConnections;
            set => SetProperty(ref _maxConnections, value);
        }

        /// <summary>
        /// <para>获取或设置该端口承载的数据值。执行引擎将上游输出写入连接的下游输入，节点可在执行时读取输入值并把结果写入输出值。</para>
        /// Gets or sets the data value carried by this port. The execution engine copies upstream outputs into
        /// connected downstream inputs; nodes read input values and write results to output values during execution.
        /// </summary>
        public object? Data
        {
            get => _data;
            set => SetProperty(ref _data, value);
        }
    }
}
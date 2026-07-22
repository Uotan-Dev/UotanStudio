using Avalonia;
using NodifyM.Avalonia.ViewModelBase;
using System.Collections.ObjectModel;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>节点视图模型，表示节点编辑器画布上的一个节点。实现 INodePosition 接口以支持节点定位。</para>
    /// View model for a node, representing a single node on the node editor canvas. Implements INodePosition for node positioning.
    /// </summary>
    public partial class FlowNodeViewModel : ViewModelBase, INodePosition
    {
        private string _title = string.Empty;
        private Point _location;
        private NodeCategory _category;
        private bool _isSelected;

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
            get => _location;
            set => SetProperty(ref _location, value);
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
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// <para>获取节点的输入连接器集合。</para>
        /// Gets the collection of input connectors for the node.
        /// </summary>
        public ObservableCollection<NodeConnectorViewModel> Input { get; } = [];

        /// <summary>
        /// <para>获取节点的输出连接器集合。</para>
        /// Gets the collection of output connectors for the node.
        /// </summary>
        public ObservableCollection<NodeConnectorViewModel> Output { get; } = [];

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
        }
    }
}
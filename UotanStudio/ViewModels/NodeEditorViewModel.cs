using Avalonia;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using UotanStudio.Services;

namespace UotanStudio.ViewModels
{
    /// <summary>
    /// <para>节点编辑器主视图模型，管理节点画布、连接线、工具箱和执行状态。</para>
    /// Main view model for the node editor, managing the node canvas, connections, toolbox, and execution state.
    /// </summary>
    public partial class NodeEditorViewModel : ViewModelBase
    {
        private ObservableCollection<NodeViewModels.FlowNodeViewModel> _nodes = [];
        private ObservableCollection<NodeViewModels.NodeConnectionViewModel> _connections = [];
        private double _zoom = 1.0;
        private bool _showMinimap = true;
        private string _searchText = string.Empty;
        private string _executionStatus = LanguageService.Instance.GetString("Node_StatusReady");
        private Point _nextNodeLocation = new(100, 100);
        private static readonly Point DefaultNodeLocation = new(100, 100);

        /// <summary>
        /// <para>获取或设置画布上的节点集合。</para>
        /// Gets or sets the collection of nodes on the canvas.
        /// </summary>
        public ObservableCollection<NodeViewModels.FlowNodeViewModel> Nodes
        {
            get => _nodes;
            set => SetProperty(ref _nodes, value);
        }

        /// <summary>
        /// <para>获取或设置节点间的连接线集合。</para>
        /// Gets or sets the collection of connections between nodes.
        /// </summary>
        public ObservableCollection<NodeViewModels.NodeConnectionViewModel> Connections
        {
            get => _connections;
            set => SetProperty(ref _connections, value);
        }

        /// <summary>
        /// <para>获取或设置画布的缩放级别。</para>
        /// Gets or sets the zoom level of the canvas.
        /// </summary>
        public double Zoom
        {
            get => _zoom;
            set => SetProperty(ref _zoom, value);
        }

        /// <summary>
        /// <para>获取或设置是否显示小地图。</para>
        /// Gets or sets whether to show the minimap.
        /// </summary>
        public bool ShowMinimap
        {
            get => _showMinimap;
            set => SetProperty(ref _showMinimap, value);
        }

        /// <summary>
        /// <para>获取或设置节点搜索文本。</para>
        /// Gets or sets the node search text.
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value);
        }

        /// <summary>
        /// <para>获取或设置执行状态文本。</para>
        /// Gets or sets the execution status text.
        /// </summary>
        public string ExecutionStatus
        {
            get => _executionStatus;
            set => SetProperty(ref _executionStatus, value);
        }

        /// <summary>
        /// <para>获取节点工具箱分类列表。</para>
        /// Gets the list of node toolbox categories.
        /// </summary>
        public ObservableCollection<NodeToolboxCategory> ToolboxCategories { get; } = [];

        /// <summary>
        /// <para>初始化节点编辑器视图模型，创建默认工具箱分类和示例节点。</para>
        /// Initializes the node editor view model, creating default toolbox categories and example nodes.
        /// </summary>
        public NodeEditorViewModel()
        {
            InitializeToolbox();
        }

        private void InitializeToolbox()
        {
            ToolboxCategories.Add(new NodeToolboxCategory(
                LanguageService.Instance.GetString("Node_CatFirmware"),
                NodeViewModels.NodeCategory.Firmware,
                [
                    new NodeToolboxItem("Unpack Firmware", () => NodeViewModels.FirmwareNodeViewModel.CreateUnpackNode()),
                    new NodeToolboxItem("Pack Firmware", () => NodeViewModels.FirmwareNodeViewModel.CreatePackNode()),
                    new NodeToolboxItem("Verify Signature", () => NodeViewModels.FirmwareNodeViewModel.CreateVerifyNode()),
                    new NodeToolboxItem("Extract Partition", () => NodeViewModels.FirmwareNodeViewModel.CreateExtractNode()),
                ]
            ));

            ToolboxCategories.Add(new NodeToolboxCategory(
                LanguageService.Instance.GetString("Node_CatFlash"),
                NodeViewModels.NodeCategory.Flash,
                [
                    new NodeToolboxItem("Connect Device", () => NodeViewModels.FlashNodeViewModel.CreateConnectNode()),
                    new NodeToolboxItem("Flash Partition", () => NodeViewModels.FlashNodeViewModel.CreateFlashNode()),
                    new NodeToolboxItem("Erase Partition", () => NodeViewModels.FlashNodeViewModel.CreateEraseNode()),
                    new NodeToolboxItem("Reboot Device", () => NodeViewModels.FlashNodeViewModel.CreateRebootNode()),
                ]
            ));

            ToolboxCategories.Add(new NodeToolboxCategory(
                LanguageService.Instance.GetString("Node_CatData"),
                NodeViewModels.NodeCategory.Data,
                [
                    new NodeToolboxItem("Read File", () => NodeViewModels.DataNodeViewModel.CreateFileReadNode()),
                    new NodeToolboxItem("Format Convert", () => NodeViewModels.DataNodeViewModel.CreateConvertNode()),
                    new NodeToolboxItem("Checksum", () => NodeViewModels.DataNodeViewModel.CreateChecksumNode()),
                    new NodeToolboxItem("Compare", () => NodeViewModels.DataNodeViewModel.CreateCompareNode()),
                ]
            ));

            ToolboxCategories.Add(new NodeToolboxCategory(
                LanguageService.Instance.GetString("Node_CatFlow"),
                NodeViewModels.NodeCategory.Flow,
                []
            ));

            ToolboxCategories.Add(new NodeToolboxCategory(
                LanguageService.Instance.GetString("Node_CatOutput"),
                NodeViewModels.NodeCategory.Output,
                [
                    new NodeToolboxItem("Log Output", () => NodeViewModels.OutputNodeViewModel.CreateLogNode()),
                    new NodeToolboxItem("Progress Report", () => NodeViewModels.OutputNodeViewModel.CreateProgressNode()),
                    new NodeToolboxItem("Save Result", () => NodeViewModels.OutputNodeViewModel.CreateSaveNode()),
                ]
            ));
        }

        /// <summary>
        /// <para>将指定节点添加到画布。</para>
        /// Adds the specified node to the canvas.
        /// </summary>
        /// <param name="node">
        /// <para>要添加的节点。</para>
        /// The node to add.
        /// </param>
        public void AddNode(NodeViewModels.FlowNodeViewModel node)
        {
            if (node.Location == DefaultNodeLocation)
            {
                node.Location = _nextNodeLocation;
                _nextNodeLocation = new Point(_nextNodeLocation.X + 40, _nextNodeLocation.Y + 40);
            }

            Nodes.Add(node);
        }

        /// <summary>
        /// <para>从画布移除指定节点。</para>
        /// Removes the specified node from the canvas.
        /// </summary>
        /// <param name="node">
        /// <para>要移除的节点。</para>
        /// The node to remove.
        /// </param>
        public void RemoveNode(NodeViewModels.FlowNodeViewModel node)
        {
            var relatedConnections = Connections
                .Where(c => c.Source != null && c.Target != null &&
                    (IsConnectorInNode(c.Source, node) || IsConnectorInNode(c.Target, node)))
                .ToList();

            foreach (var conn in relatedConnections)
            {
                Connections.Remove(conn);
            }

            Nodes.Remove(node);
        }

        private static bool IsConnectorInNode(NodeViewModels.NodeConnectorViewModel connector, NodeViewModels.FlowNodeViewModel node)
        {
            return node.Input.Contains(connector) || node.Output.Contains(connector);
        }

        /// <summary>
        /// <para>在两个连接器之间创建连接。</para>
        /// Creates a connection between two connectors.
        /// </summary>
        /// <param name="source">
        /// <para>源连接器（输出端口）。</para>
        /// The source connector (output port).
        /// </param>
        /// <param name="target">
        /// <para>目标连接器（输入端口）。</para>
        /// The target connector (input port).
        /// </param>
        public void CreateConnection(NodeViewModels.NodeConnectorViewModel source, NodeViewModels.NodeConnectorViewModel target)
        {
            Connections.Add(new NodeViewModels.NodeConnectionViewModel
            {
                Source = source,
                Target = target
            });
        }

        [RelayCommand]
        private void ZoomIn()
        {
            Zoom = Math.Min(Zoom * 1.2, 3.0);
        }

        [RelayCommand]
        private void ZoomOut()
        {
            Zoom = Math.Max(Zoom / 1.2, 0.2);
        }

        [RelayCommand]
        private void ResetZoom()
        {
            Zoom = 1.0;
        }

        [RelayCommand]
        private void ToggleMinimap()
        {
            ShowMinimap = !ShowMinimap;
        }

        [RelayCommand]
        private void DeleteSelected()
        {
            // 删除选中的节点和连接线由 NodifyEditor 内部管理
            // 此命令可通过后续扩展实现更复杂的删除逻辑
        }
    }

    /// <summary>
    /// <para>节点工具箱分类，包含同类别的节点项列表。</para>
    /// Node toolbox category, containing a list of node items in the same category.
    /// </summary>
    public class NodeToolboxCategory
    {
        /// <summary>
        /// <para>获取分类名称。</para>
        /// Gets the category name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// <para>获取节点类别。</para>
        /// Gets the node category.
        /// </summary>
        public NodeViewModels.NodeCategory Category { get; }

        /// <summary>
        /// <para>获取分类下的节点项列表。</para>
        /// Gets the list of node items in this category.
        /// </summary>
        public ObservableCollection<NodeToolboxItem> Items { get; }

        /// <summary>
        /// <para>创建工具箱分类。</para>
        /// Creates a toolbox category.
        /// </summary>
        /// <param name="name">
        /// <para>分类名称。</para>
        /// The category name.
        /// </param>
        /// <param name="category">
        /// <para>节点类别。</para>
        /// The node category.
        /// </param>
        /// <param name="items">
        /// <para>分类下的节点项列表。</para>
        /// The list of node items in this category.
        /// </param>
        public NodeToolboxCategory(string name, NodeViewModels.NodeCategory category, ObservableCollection<NodeToolboxItem> items)
        {
            Name = name;
            Category = category;
            Items = items;
        }
    }

    /// <summary>
    /// <para>节点工具箱项，表示工具箱中可添加到画布的节点模板。</para>
    /// Node toolbox item, representing a node template in the toolbox that can be added to the canvas.
    /// </summary>
    public class NodeToolboxItem
    {
        /// <summary>
        /// <para>获取节点项名称。</para>
        /// Gets the node item name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// <para>获取创建节点实例的工厂方法。</para>
        /// Gets the factory method to create a node instance.
        /// </summary>
        public Func<NodeViewModels.FlowNodeViewModel> CreateNode { get; }

        /// <summary>
        /// <para>创建工具箱项。</para>
        /// Creates a toolbox item.
        /// </summary>
        /// <param name="name">
        /// <para>节点项名称。</para>
        /// The node item name.
        /// </param>
        /// <param name="createNode">
        /// <para>创建节点实例的工厂方法。</para>
        /// The factory method to create a node instance.
        /// </param>
        public NodeToolboxItem(string name, Func<NodeViewModels.FlowNodeViewModel> createNode)
        {
            Name = name;
            CreateNode = createNode;
        }
    }
}

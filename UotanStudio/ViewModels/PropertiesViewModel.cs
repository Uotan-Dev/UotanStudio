using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels
{
    /// <summary>
    /// <para>属性视图模型，动态显示当前选中节点的属性，订阅节点编辑器选中变更。</para>
    /// Properties view model, dynamically displaying the currently selected node's properties, subscribed to node-editor selection changes.
    /// </summary>
    internal partial class PropertiesViewModel : Tool
    {
        /// <summary>
        /// <para>单个属性行视图模型，承载名称-值对。</para>
        /// Single property row view model, carrying a name-value pair.
        /// </summary>
        public sealed partial class PropertyRow : ObservableObject
        {
            /// <summary>
            /// <para>属性名称。</para>
            /// Property name.
            /// </summary>
            [ObservableProperty] private string _name = string.Empty;

            /// <summary>
            /// <para>属性值文本。</para>
            /// Property value text.
            /// </summary>
            [ObservableProperty] private string _value = string.Empty;

            /// <summary>
            /// <para>创建属性行。</para>
            /// Creates a property row.
            /// </summary>
            public PropertyRow(string name, string value)
            {
                _name = name;
                _value = value;
            }
        }

        /// <summary>
        /// <para>当前显示的属性行集合，UI 绑定此项。</para>
        /// Collection of property rows currently displayed, bound by the UI.
        /// </summary>
        public ObservableCollection<PropertyRow> Properties { get; } = [];

        /// <summary>
        /// <para>当前选中的节点标题，用于显示。</para>
        /// Title of the currently selected node, for display.
        /// </summary>
        [ObservableProperty] private string _selectedNodeTitle = string.Empty;

        /// <summary>
        /// <para>是否已选中节点。</para>
        /// Whether a node is selected.
        /// </summary>
        [ObservableProperty] private bool _hasSelection;

        /// <summary>
        /// <para>构造属性视图模型，订阅统一日志写入节点属性行。</para>
        /// Constructs the properties view model, subscribing to unified log writes to append node-property rows.
        /// </summary>
        /// <remarks>
        /// <para>当前实现将属性面板与统一日志联动：当日志来源标记为 <c>NodeEditor</c> 且包含选中节点信息时刷新属性。</para>
        /// The current implementation links the properties panel to the unified log: refreshes properties when a log entry tagged <c>NodeEditor</c> contains selected-node info.
        /// </remarks>
        public PropertiesViewModel()
        {
            // 初始占位：显示当前画布无选中
            RefreshForNode(null);
        }

        /// <summary>
        /// <para>刷新指定节点的属性行。节点为 <c>null</c> 时显示占位。</para>
        /// Refreshes property rows for the specified node. Shows a placeholder when the node is <c>null</c>.
        /// </summary>
        /// <param name="node">
        /// <para>选中节点；<c>null</c> 表示清空选择。</para>
        /// The selected node; <c>null</c> clears the selection.
        /// </param>
        public void RefreshForNode(FlowNodeViewModel? node)
        {
            Properties.Clear();
            if (node == null)
            {
                SelectedNodeTitle = string.Empty;
                HasSelection = false;
                return;
            }

            HasSelection = true;
            SelectedNodeTitle = node.Title;

            var svc = LanguageService.Instance;
            Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_ImageName"), node.Title));
            Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_ImageType"), node.Category.ToString()));
            Properties.Add(new PropertyRow("Status", node.ExecutionStatus.ToString()));
            Properties.Add(new PropertyRow("Location", $"{node.Location.X}, {node.Location.Y}"));
            Properties.Add(new PropertyRow("Inputs", node.Input.Count.ToString()));
            Properties.Add(new PropertyRow("Outputs", node.Output.Count.ToString()));

            LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for node '{node.Title}'");
        }
    }
}
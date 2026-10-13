using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Firmware.DeviceTree.Dt;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Device-tree node tree item, wrapping a <see cref="DeviceTreeNode"/> for the left-hand tree view.
/// <para>设备树节点树项，包装 <see cref="DeviceTreeNode"/> 供左侧 TreeView 展示。</para>
/// </summary>
public partial class DtbNodeViewModel : ObservableObject
{
    /// <summary>
    /// The underlying device-tree node.
    /// <para>对应的设备树节点。</para>
    /// </summary>
    public DeviceTreeNode Node { get; }

    /// <summary>
    /// The full path of the node (e.g. /soc/i2c@0).
    /// <para>节点的完整路径（如 /soc/i2c@0）。</para>
    /// </summary>
    public string FullPath => Node.GetPath();

    /// <summary>
    /// The display name of the node; the root is shown as /.
    /// <para>节点显示名称；根节点显示为 /。</para>
    /// </summary>
    public string Name => string.IsNullOrEmpty(Node.Name) ? "/" : Node.Name;

    /// <summary>
    /// The child node tree items.
    /// <para>子节点树项。</para>
    /// </summary>
    public ObservableCollection<DtbNodeViewModel> Children { get; } = [];

    /// <summary>
    /// Whether the node is selected (linkage selection state).
    /// <para>节点是否被选中（联动选中状态）。</para>
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Whether the node is expanded.
    /// <para>节点是否已展开。</para>
    /// </summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>
    /// Creates a node tree item, recursively building its children.
    /// <para>创建节点树项并递归构建其子项。</para>
    /// </summary>
    /// <param name="node">The device-tree node to wrap. <para>要包装的设备树节点。</para></param>
    public DtbNodeViewModel(DeviceTreeNode node)
    {
        Node = node;
        foreach (var child in node.Children)
        {
            Children.Add(new DtbNodeViewModel(child));
        }
    }
}
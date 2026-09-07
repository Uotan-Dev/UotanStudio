using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>设备树节点树项，包装 <see cref="DeviceTreeNode"/> 供左侧 TreeView 展示。</para>
/// Device-tree node tree item, wrapping a <see cref="DeviceTreeNode"/> for the left-hand tree view.
/// </summary>
public partial class DtbNodeViewModel : ObservableObject
{
    /// <summary>
    /// <para>对应的设备树节点。</para>
    /// The underlying device-tree node.
    /// </summary>
    public DeviceTreeNode Node { get; }

    /// <summary>
    /// <para>节点的完整路径（如 /soc/i2c@0）。</para>
    /// The full path of the node (e.g. /soc/i2c@0).
    /// </summary>
    public string FullPath => Node.GetPath();

    /// <summary>
    /// <para>节点显示名称；根节点显示为 /。</para>
    /// The display name of the node; the root is shown as /.
    /// </summary>
    public string Name => string.IsNullOrEmpty(Node.Name) ? "/" : Node.Name;

    /// <summary>
    /// <para>子节点树项。</para>
    /// The child node tree items.
    /// </summary>
    public ObservableCollection<DtbNodeViewModel> Children { get; } = [];

    /// <summary>
    /// <para>节点是否被选中（联动选中状态）。</para>
    /// Whether the node is selected (linkage selection state).
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// <para>节点是否已展开。</para>
    /// Whether the node is expanded.
    /// </summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>
    /// <para>创建节点树项并递归构建其子项。</para>
    /// Creates a node tree item, recursively building its children.
    /// </summary>
    /// <param name="node"><para>要包装的设备树节点。</para>The device-tree node to wrap.</param>
    public DtbNodeViewModel(DeviceTreeNode node)
    {
        Node = node;
        foreach (var child in node.Children)
        {
            Children.Add(new DtbNodeViewModel(child));
        }
    }
}
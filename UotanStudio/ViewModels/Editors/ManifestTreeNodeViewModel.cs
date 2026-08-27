using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Firmware.AXml;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>Manifest 结构树项（左栏绑定）：元素节点，按 XML 层次组织。</para>
/// Manifest structure tree item (left-pane binding): element nodes organized by XML hierarchy.
/// </summary>
public partial class ManifestTreeNodeViewModel : ObservableObject
{
    /// <summary>
    /// <para>创建 Manifest 结构树项。</para>
    /// Creates a Manifest structure tree item.
    /// </summary>
    /// <param name="element">对应的 AXML 元素。<para>The backing AXML element.</para></param>
    public ManifestTreeNodeViewModel(AXmlElement element)
    {
        Element = element;
        Name = element.Name;
    }

    /// <summary>
    /// <para>对应的 AXML 元素（编辑时直接修改其属性/文本）。</para>
    /// The backing AXML element (attributes/text edited in place).
    /// </summary>
    public AXmlElement Element { get; }

    /// <summary>
    /// <para>节点显示名称（元素名，如 manifest / application / activity）。</para>
    /// Display name (element name, e.g. manifest / application / activity).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>子元素节点集合。</para>
    /// Child element nodes.
    /// </summary>
    public ObservableCollection<ManifestTreeNodeViewModel> Children { get; } = [];

    /// <summary>
    /// <para>获取或设置节点是否已展开。</para>
    /// Gets or sets whether the node is expanded.
    /// </summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>
    /// <para>获取或设置节点是否被选中。</para>
    /// Gets or sets whether the node is selected.
    /// </summary>
    [ObservableProperty] private bool _isSelected;
}

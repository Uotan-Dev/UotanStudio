using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Firmware.AndroidMeta.AXml;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Manifest structure tree item (left-pane binding): element nodes organized by XML hierarchy.
/// <para>Manifest 结构树项（左栏绑定）：元素节点，按 XML 层次组织。</para>
/// </summary>
public partial class ManifestTreeNodeViewModel : ObservableObject
{
    /// <summary>
    /// Creates a Manifest structure tree item.
    /// <para>创建 Manifest 结构树项。</para>
    /// </summary>
    /// <param name="element">对应的 AXML 元素。<para>The backing AXML element.</para></param>
    public ManifestTreeNodeViewModel(AXmlElement element)
    {
        Element = element;
        Name = element.Name;
    }

    /// <summary>
    /// The backing AXML element (attributes/text edited in place).
    /// <para>对应的 AXML 元素（编辑时直接修改其属性/文本）。</para>
    /// </summary>
    public AXmlElement Element { get; }

    /// <summary>
    /// Display name (element name, e.g. manifest / application / activity).
    /// <para>节点显示名称（元素名，如 manifest / application / activity）。</para>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Child element nodes.
    /// <para>子元素节点集合。</para>
    /// </summary>
    public ObservableCollection<ManifestTreeNodeViewModel> Children { get; } = [];

    /// <summary>
    /// Gets or sets whether the node is expanded.
    /// <para>获取或设置节点是否已展开。</para>
    /// </summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>
    /// Gets or sets whether the node is selected.
    /// <para>获取或设置节点是否被选中。</para>
    /// </summary>
    [ObservableProperty] private bool _isSelected;
}
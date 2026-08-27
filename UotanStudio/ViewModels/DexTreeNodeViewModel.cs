using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>DEX 结构树项（左栏绑定）：类 / 方法 / 字段节点，按层级组织。</para>
/// DEX structure tree item (left-pane binding): class / method / field nodes, organized hierarchically.
/// </summary>
public partial class DexTreeNodeViewModel : ObservableObject
{
    /// <summary>
    /// <para>创建 DEX 结构树项。</para>
    /// Creates a DEX structure tree item.
    /// </summary>
    /// <param name="name">显示名称。<para>Display name.</para></param>
    /// <param name="kind">节点种类（class / method / field）。<para>Node kind (class / method / field).</para></param>
    /// <param name="detail">补充详情（如方法签名）。<para>Additional detail (e.g. method signature).</para></param>
    public DexTreeNodeViewModel(string name, string kind, string detail = "")
    {
        Name = name;
        Kind = kind;
        Detail = detail;
    }

    /// <summary>
    /// <para>获取节点显示名称（如 com.example.Foo、onCreate）。</para>
    /// Gets the display name (e.g. com.example.Foo, onCreate).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>获取节点种类（class / method / field）。</para>
    /// Gets the node kind (class / method / field).
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// <para>获取补充详情（如方法签名 "(Landroid/os/Bundle;)V"）。</para>
    /// Gets the additional detail (e.g. method signature "(Landroid/os/Bundle;)V").
    /// </summary>
    public string Detail { get; }

    /// <summary>
    /// <para>获取子节点集合（类的成员、方法无子节点）。</para>
    /// Gets the child nodes (class members; methods have no children).
    /// </summary>
    public ObservableCollection<DexTreeNodeViewModel> Children { get; } = [];

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
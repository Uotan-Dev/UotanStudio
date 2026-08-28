using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>ARSC 结构树项（左栏绑定）：包 → 类型 → 配置 → 条目四级层次。</para>
/// ARSC structure tree item (left-pane binding): the package → type → configuration → entry hierarchy.
/// </summary>
public partial class ArscTreeNodeViewModel : ObservableObject
{
    /// <summary>
    /// <para>创建 ARSC 结构树项。</para>
    /// Creates an ARSC structure tree item.
    /// </summary>
    public ArscTreeNodeViewModel(string name, object? payload = null)
    {
        Name = name;
        Payload = payload;
    }

    /// <summary>
    /// <para>节点显示名称（包名 / 类型名 / 配置描述 / 条目键）。</para>
    /// Display name (package name / type name / config description / entry key).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// <para>节点负载对象：包/类型/条目等模型引用（详情面板取值用）。</para>
    /// Node payload: the model reference (package/type/entry) used by the details pane.
    /// </summary>
    public object? Payload { get; }

    /// <summary>
    /// <para>子节点集合。</para>
    /// Child nodes.
    /// </summary>
    public ObservableCollection<ArscTreeNodeViewModel> Children { get; } = [];

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

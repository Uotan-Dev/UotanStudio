using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Firmware.AndroidMeta.Arsc;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// The model reference one ARSC tree node carries; the case set is exactly <see cref="ResPackage"/>,
/// <see cref="ResType"/> and <see cref="ResEntryEntry"/>, so a <c>switch</c> over the held value
/// covers every payload a tree node can hold and a case can only be introduced by changing this file.
/// <para>ARSC 树节点携带的模型引用；分支集合恰为 <see cref="ResPackage"/>、<see cref="ResType"/> 与
/// <see cref="ResEntryEntry"/>，因此对所持值的 <c>switch</c> 覆盖树节点可能持有的全部负载，
/// 新增分支只能通过修改本文件完成。</para>
/// </summary>
/// <remarks>
/// The cases are positional: the first is a package, the second a type configuration, the third an
/// entry. The union default carries no payload, matching a node built without one.
/// <para>分支按位置排列：第一个是包，第二个是类型配置，第三个是条目。联合的默认值不携带负载，
/// 与未携带负载构建的节点一致。</para>
/// </remarks>
public union ArscNodePayload(ResPackage, ResType, ResEntryEntry);

/// <summary>
/// ARSC structure tree item (left-pane binding): the package → type → configuration → entry hierarchy.
/// <para>ARSC 结构树项（左栏绑定）：包 → 类型 → 配置 → 条目四级层次。</para>
/// </summary>
public partial class ArscTreeNodeViewModel : ObservableObject
{
    /// <summary>
    /// Creates an ARSC structure tree item.
    /// <para>创建 ARSC 结构树项。</para>
    /// </summary>
    /// <param name="name">Display name of the node. <para>节点的显示名称。</para></param>
    /// <param name="payload">The model reference the details pane reads, or null for a bare node.
    /// <para>详情面板读取的模型引用；裸节点为 null。</para></param>
    public ArscTreeNodeViewModel(string name, ArscNodePayload? payload = null)
    {
        Name = name;
        Payload = payload;
    }

    /// <summary>
    /// Display name (package name / type name / config description / entry key).
    /// <para>节点显示名称（包名 / 类型名 / 配置描述 / 条目键）。</para>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Node payload: the model reference (package/type/entry) used by the details pane, held as one
    /// closed union case; null when the node carries no payload.
    /// <para>节点负载对象：包/类型/条目等模型引用，以闭合联合分支持有；节点不带负载时为 null。</para>
    /// </summary>
    public ArscNodePayload? Payload { get; }

    /// <summary>
    /// Child nodes.
    /// <para>子节点集合。</para>
    /// </summary>
    public ObservableCollection<ArscTreeNodeViewModel> Children { get; } = [];

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
using System.Collections.Generic;
using UotanStudio.Areas.NodeEditor.ViewModels;

namespace UotanStudio.Core.Flow;

/// <summary>
/// <para>.uotflow 流程文档的数据载体（可序列化 DTO）。</para>
/// Data carrier of a .uotflow flow document (serializable DTO).
/// </summary>
public sealed class FlowDocumentDto
{
    /// <summary>
    /// <para>获取或设置文档中的节点列表。</para>
    /// Gets or sets the list of nodes in the document.
    /// </summary>
    public List<FlowNodeDto> Nodes { get; set; } = [];

    /// <summary>
    /// <para>获取或设置文档中的连接线列表。</para>
    /// Gets or sets the list of connections in the document.
    /// </summary>
    public List<FlowConnectionDto> Connections { get; set; } = [];
}

/// <summary>
/// <para>流程节点的可序列化数据。</para>
/// Serializable data of a flow node.
/// </summary>
public sealed class FlowNodeDto
{
    /// <summary>
    /// <para>获取或设置节点唯一标识。</para>
    /// Gets or sets the unique identifier of the node.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置节点的显示标题（保存时的本地化文本）。</para>
    /// Gets or sets the display title of the node (localized text at save time).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置节点类别，加载时用于映射回节点工厂方法。</para>
    /// Gets or sets the node category, used to map back to a node factory method on load.
    /// </summary>
    public NodeCategory NodeCategory { get; set; }

    /// <summary>
    /// <para>获取或设置节点在画布上的 X 坐标。</para>
    /// Gets or sets the X coordinate of the node on the canvas.
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// <para>获取或设置节点在画布上的 Y 坐标。</para>
    /// Gets or sets the Y coordinate of the node on the canvas.
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// <para>获取或设置各输入端口的值（按端口顺序）。</para>
    /// Gets or sets the values of each input connector (in connector order).
    /// </summary>
    public List<string> InputValues { get; set; } = [];
}

/// <summary>
/// <para>两个节点之间连接线的可序列化数据。</para>
/// Serializable data of a connection between two nodes.
/// </summary>
public sealed class FlowConnectionDto
{
    /// <summary>
    /// <para>获取或设置源节点标识。</para>
    /// Gets or sets the identifier of the source node.
    /// </summary>
    public string SourceNodeId { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置源节点输出端口索引。</para>
    /// Gets or sets the output port index of the source node.
    /// </summary>
    public int SourcePort { get; set; }

    /// <summary>
    /// <para>获取或设置目标节点标识。</para>
    /// Gets or sets the identifier of the target node.
    /// </summary>
    public string TargetNodeId { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置目标节点输入端口索引。</para>
    /// Gets or sets the input port index of the target node.
    /// </summary>
    public int TargetPort { get; set; }
}
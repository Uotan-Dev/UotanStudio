using System.Collections.Generic;

namespace UotanStudio.Core.Inspector;

/// <summary>
/// <para>固件部件结构树节点：描述一个部件文件解析出的子结构（分区、条目或头部摘要）。</para>
/// Firmware component structure tree node: describes the sub-structure parsed from a
/// component file (partitions, entries or a header summary).
/// </summary>
/// <param name="name"><para>节点名称。</para>The node name.</param>
public sealed class InspectorNode(string name)
{
    /// <summary>
    /// <para>获取节点名称（分区名、条目名或文件名）。</para>
    /// Gets the node name (partition name, entry name or file name).
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// <para>获取子节点集合。</para>
    /// Gets the child nodes.
    /// </summary>
    public List<InspectorNode> Children { get; } = [];

    /// <summary>
    /// <para>获取或设置附加详情（尺寸、格式、摘要等）。</para>
    /// Gets or sets additional detail (size, format, summary, etc.).
    /// </summary>
    public string? Detail { get; set; }
}
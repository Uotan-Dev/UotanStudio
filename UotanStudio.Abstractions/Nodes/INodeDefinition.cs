using System.Collections.Generic;

namespace UotanStudio.Abstractions.Nodes;

/// <summary>
/// <para>节点定义，描述一个可放入工具箱的节点模板。</para>
/// Node definition, describing a node template that can be placed in the toolbox.
/// </summary>
public interface INodeDefinition
{
    /// <summary>
    /// <para>节点类型唯一标识（如 <c>firmware.unpack</c>）。</para>
    /// Unique node type identifier (e.g. <c>firmware.unpack</c>).
    /// </summary>
    string TypeId { get; }

    /// <summary>
    /// <para>显示名称。</para>
    /// Display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// <para>所属类别。</para>
    /// Category.
    /// </summary>
    NodeCategory Category { get; }

    /// <summary>
    /// <para>简短描述（用于工具箱提示）。</para>
    /// Short description (used for toolbox tooltips).
    /// </summary>
    string Description { get; }

    /// <summary>
    /// <para>提供此节点定义的插件标识（内置为 <c>uotanstudio.builtin</c>）。</para>
    /// Plugin identifier providing this node definition (built-in is <c>uotanstudio.builtin</c>).
    /// </summary>
    string PluginId { get; }

    /// <summary>
    /// <para>节点的连接器（端口）定义列表。</para>
    /// List of connector (port) definitions for the node.
    /// </summary>
    IReadOnlyList<ConnectorDefinition> Connectors { get; }

    /// <summary>
    /// <para>创建节点实例的工厂方法。</para>
    /// Factory method that creates a node instance.
    /// </summary>
    /// <returns>
    /// <para>新的 <see cref="INodeInstance"/>。</para>
    /// A new <see cref="INodeInstance"/>.
    /// </returns>
    INodeInstance CreateInstance();
}

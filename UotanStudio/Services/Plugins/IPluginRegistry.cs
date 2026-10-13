using System.Diagnostics.CodeAnalysis;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.Services.Plugins;

/// <summary>
/// Registration and lookup surface every node flows through, so built-in nodes and plugin-provided nodes are registered, ordered, enumerated and removed by exactly one implementation.
/// <para>所有节点共用的注册与查询面：内置节点与插件提供的节点都由同一实现完成注册、排序、枚举与移除。</para>
/// </summary>
public interface IPluginRegistry
{
    /// <summary>
    /// Gets a snapshot of built-in descriptors followed by plugin descriptors ordered by display name; the snapshot is stable for the duration of the call.
    /// <para>获取描述符快照：内置项在前，插件项按显示名排序；快照在本次读取内保持稳定。</para>
    /// </summary>
    /// <value>
    /// The ordered descriptor snapshot.
    /// <para>按序排列的描述符快照。</para>
    /// </value>
    IReadOnlyList<NodeDescriptor> Descriptors { get; }

    /// <summary>
    /// Gets the stable type keys of the built-in nodes, excluding plugin-provided nodes.
    /// <para>获取内置节点的稳定类型键，不含插件提供的节点。</para>
    /// </summary>
    /// <value>
    /// The set of built-in type keys.
    /// <para>内置类型键的集合。</para>
    /// </value>
    IReadOnlySet<string> BuiltInTypeKeys { get; }

    /// <summary>
    /// Raised whenever the set of registered descriptors changes.
    /// <para>注册描述符集合发生变化时触发。</para>
    /// </summary>
    event EventHandler? DescriptorsChanged;

    /// <summary>
    /// Registers or replaces one descriptor, optionally binding it to the package that provided it.
    /// <para>注册或替换一个描述符，并可选地绑定提供它的包。</para>
    /// </summary>
    /// <param name="descriptor">
    /// The descriptor to register.
    /// <para>要注册的描述符。</para>
    /// </param>
    /// <param name="package">
    /// The owning package, or <see langword="null"/> for a built-in node.
    /// <para>所属包；内置节点为 <see langword="null"/>。</para>
    /// </param>
    void RegisterNode(NodeDescriptor descriptor, VisualPluginPackage? package = null);

    /// <summary>
    /// Removes one descriptor and its owning package binding.
    /// <para>移除一个描述符及其所属包绑定。</para>
    /// </summary>
    /// <param name="typeKey">
    /// The type key to remove.
    /// <para>要移除的类型键。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a descriptor was removed.
    /// <para>确实移除了描述符时为 <see langword="true"/>。</para>
    /// </returns>
    bool UnregisterNode(string typeKey);

    /// <summary>
    /// Gets the owning package for one type key.
    /// <para>获取某个类型键所属的包。</para>
    /// </summary>
    /// <param name="typeKey">
    /// The type key to look up.
    /// <para>要查询的类型键。</para>
    /// </param>
    /// <param name="package">
    /// The owning package when found.
    /// <para>找到时返回所属包。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the type key belongs to a registered package.
    /// <para>该类型键属于已注册包时为 <see langword="true"/>。</para>
    /// </returns>
    bool TryGetPackage(string typeKey, [NotNullWhen(true)] out VisualPluginPackage? package);

    /// <summary>
    /// Publishes every current descriptor into the node type registry.
    /// <para>把当前全部描述符发布到节点类型注册表。</para>
    /// </summary>
    void RegisterAll();
}
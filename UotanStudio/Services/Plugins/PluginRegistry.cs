using System.Diagnostics.CodeAnalysis;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Plugins;

/// <summary>
/// Default registry implementation: seeded built-in descriptors and layered plugin descriptors
/// are enumerated and resolved through one surface under a single lock with atomic snapshot
/// semantics.
/// <para>默认注册表实现：以种子形式给出的内置描述符与叠加其上的插件描述符都经同一注册面枚举与解析，
/// 由单一锁保护并具备原子快照语义。</para>
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly Lock _lock = new();
    private readonly IReadOnlyList<NodeDescriptor> _builtInDescriptors;
    private readonly Dictionary<string, NodeDescriptor> _pluginDescriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VisualPluginPackage> _pluginPackages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the application-wide registry seeded with the built-in node catalog.
    /// <para>获取以内置节点目录为种子创建的应用级注册表。</para>
    /// </summary>
    /// <value>The shared registry instance.<para>共享的注册表实例。</para></value>
    public static PluginRegistry Shared { get; } = new(NodeCatalogSeed.CreateDescriptors());

    /// <summary>
    /// Creates a registry around an ordered set of built-in descriptors.
    /// <para>围绕一组有序内置描述符创建注册表。</para>
    /// </summary>
    /// <param name="builtInDescriptors">The ordered built-in descriptors.<para>有序的内置描述符。</para></param>
    public PluginRegistry(IEnumerable<NodeDescriptor> builtInDescriptors)
    {
        ArgumentNullException.ThrowIfNull(builtInDescriptors);
        _builtInDescriptors = [.. builtInDescriptors];
        BuiltInTypeKeys = _builtInDescriptors.Select(descriptor => descriptor.TypeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a snapshot of built-in descriptors followed by plugin descriptors ordered by display
    /// name; the snapshot is stable for the duration of the call.
    /// <para>获取描述符快照：内置项在前，插件项按显示名排序；快照在本次调用内保持稳定。</para>
    /// </summary>
    /// <value>The ordered descriptor snapshot.<para>有序的描述符快照。</para></value>
    public IReadOnlyList<NodeDescriptor> Descriptors
    {
        get
        {
            lock (_lock)
            {
                return [.. _builtInDescriptors, .. _pluginDescriptors.Values.OrderBy(descriptor => descriptor.DisplayName)];
            }
        }
    }

    /// <summary>
    /// Gets the stable type keys of the built-in nodes, excluding plugin nodes.
    /// <para>获取内置节点的稳定类型键，不含插件节点。</para>
    /// </summary>
    /// <value>The built-in type keys.<para>内置类型键集合。</para></value>
    public IReadOnlySet<string> BuiltInTypeKeys { get; }

    /// <summary>
    /// Raised whenever the set of registered descriptors changes.
    /// <para>注册描述符集合发生变化时触发。</para>
    /// </summary>
    public event EventHandler? DescriptorsChanged;

    /// <summary>
    /// Registers or replaces one descriptor, optionally binding it to the package that provided
    /// it.
    /// <para>注册或替换一个描述符，并可选地绑定提供它的包。</para>
    /// </summary>
    /// <param name="descriptor">The descriptor to register.<para>要注册的描述符。</para></param>
    /// <param name="package">The owning package, or <see langword="null"/> for a built-in node.
    /// <para>所属包；内置节点为 <see langword="null"/>。</para></param>
    public void RegisterNode(NodeDescriptor descriptor, VisualPluginPackage? package = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_lock)
        {
            _pluginDescriptors[descriptor.TypeKey] = descriptor;
            if (package is null)
            {
                _pluginPackages.Remove(descriptor.TypeKey);
            }
            else
            {
                _pluginPackages[descriptor.TypeKey] = package;
            }
            NodeTypeRegistry.Register(descriptor.TypeKey, descriptor.Factory);
        }
        DescriptorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes one descriptor and its owning package binding.
    /// <para>移除一个描述符及其所属包绑定。</para>
    /// </summary>
    /// <param name="typeKey">The type key to remove.<para>要移除的类型键。</para></param>
    /// <returns><see langword="true"/> when a descriptor was removed.
    /// <para>确实移除了描述符时为 <see langword="true"/>。</para></returns>
    public bool UnregisterNode(string typeKey)
    {
        ArgumentNullException.ThrowIfNull(typeKey);
        lock (_lock)
        {
            if (!_pluginDescriptors.Remove(typeKey))
            {
                return false;
            }
            _pluginPackages.Remove(typeKey);
            NodeTypeRegistry.Unregister(typeKey);
        }
        DescriptorsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Gets the owning package for one type key.
    /// <para>获取某个类型键所属的包。</para>
    /// </summary>
    /// <param name="typeKey">The type key to look up.<para>要查询的类型键。</para></param>
    /// <param name="package">The owning package when found.<para>找到时返回所属包。</para></param>
    /// <returns><see langword="true"/> when the type key belongs to a registered package.
    /// <para>该类型键属于已注册包时为 <see langword="true"/>。</para></returns>
    public bool TryGetPackage(string typeKey, [NotNullWhen(true)] out VisualPluginPackage? package)
    {
        lock (_lock)
        {
            return _pluginPackages.TryGetValue(typeKey, out package);
        }
    }

    /// <summary>
    /// Publishes every current descriptor into the node type registry.
    /// <para>把当前全部描述符发布到节点类型注册表。</para>
    /// </summary>
    public void RegisterAll()
    {
        lock (_lock)
        {
            foreach (var descriptor in Descriptors)
            {
                NodeTypeRegistry.Register(descriptor.TypeKey, descriptor.Factory);
            }
        }
    }
}
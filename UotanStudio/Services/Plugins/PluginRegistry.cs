using System.Diagnostics.CodeAnalysis;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.Services.Plugins;

/// <summary>
/// <para>Default registry implementation. Built-in descriptors are supplied once as an ordered seed and
/// plugin descriptors are layered on top, so both kinds are enumerated and resolved through the same
/// surface. A single lock guards every mutation and every snapshot, matching the atomic snapshot
/// semantics the toolbox and execution preflight rely on.</para>
/// <para>默认注册表实现。内置描述符以有序种子一次性给出，插件描述符叠加在其上，两类节点都经同一注册面枚举与解析。
/// 单一锁保护所有变更与快照，保持工具箱与执行预检依赖的原子快照语义。</para>
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly Lock _lock = new();
    private readonly IReadOnlyList<NodeDescriptor> _builtInDescriptors;
    private readonly Dictionary<string, NodeDescriptor> _pluginDescriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VisualPluginPackage> _pluginPackages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <para>Gets the application-wide registry seeded with the host's built-in node catalog.</para>
    /// <para>获取以宿主内置节点目录为种子创建的应用级注册表。</para>
    /// </summary>
    public static PluginRegistry Shared { get; } = new(NodeCatalogSeed.CreateDescriptors());

    /// <summary>
    /// <para>Creates a registry around an ordered set of built-in descriptors; isolated tests supply
    /// their own seed.</para>
    /// <para>围绕一组有序内置描述符创建注册表；隔离测试提供自己的种子。</para>
    /// </summary>
    /// <param name="builtInDescriptors">
    /// <para>The ordered built-in descriptors.</para>
    /// <para>有序的内置描述符。</para>
    /// </param>
    public PluginRegistry(IEnumerable<NodeDescriptor> builtInDescriptors)
    {
        ArgumentNullException.ThrowIfNull(builtInDescriptors);
        _builtInDescriptors = [.. builtInDescriptors];
        BuiltInTypeKeys = _builtInDescriptors.Select(descriptor => descriptor.TypeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IReadOnlySet<string> BuiltInTypeKeys { get; }

    /// <inheritdoc />
    public event EventHandler? DescriptorsChanged;

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public bool TryGetPackage(string typeKey, [NotNullWhen(true)] out VisualPluginPackage? package)
    {
        lock (_lock)
        {
            return _pluginPackages.TryGetValue(typeKey, out package);
        }
    }

    /// <inheritdoc />
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
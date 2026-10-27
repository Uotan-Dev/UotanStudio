using System.Collections.Concurrent;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Node type registry: registers node factories by a stable TypeKey, used by the toolbox
/// and by project loading to rebuild concrete node instances.
/// <para>节点类型注册表：以稳定 TypeKey 注册节点工厂，供工具箱与工程加载重建节点。</para>
/// </summary>
public static class NodeTypeRegistry
{
    private static readonly ConcurrentDictionary<string, Func<FlowNodeViewModel>> _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a node factory. Re-registering the same TypeKey overwrites the old factory.
    /// <para>注册节点工厂。重复注册同一 TypeKey 时覆盖旧工厂。</para>
    /// </summary>
    /// <param name="typeKey">稳定的节点类型键。<para>Stable node type key.</para></param>
    /// <param name="factory">创建节点实例的工厂方法。<para>Factory that creates a node instance.</para></param>
    public static void Register(string typeKey, Func<FlowNodeViewModel> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[typeKey] = factory;
    }

    /// <summary>Removes a dynamically registered node type.
    /// <para>移除动态注册的节点类型。</para>
    /// </summary>
    public static bool Unregister(string typeKey)
        => !string.IsNullOrWhiteSpace(typeKey) && _factories.TryRemove(typeKey, out _);

    /// <summary>
    /// Creates a node instance by TypeKey; returns <c>null</c> when the key is not registered.
    /// <para>按 TypeKey 创建节点实例；未注册时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="typeKey">节点类型键。<para>Node type key.</para></param>
    /// <returns>节点实例；未注册返回 <c>null</c>。<para>The node instance; <c>null</c> when unregistered.</para></returns>
    public static FlowNodeViewModel? Create(string typeKey)
    {
        if (string.IsNullOrWhiteSpace(typeKey)) return null;
        return _factories.TryGetValue(typeKey, out var factory) ? factory() : null;
    }

    /// <summary>
    /// Determines whether the specified TypeKey is registered.
    /// <para>判断指定 TypeKey 是否已注册。</para>
    /// </summary>
    /// <param name="typeKey">节点类型键。<para>Node type key.</para></param>
    /// <returns>已注册返回 <c>true</c>。<para><c>true</c> when registered.</para></returns>
    public static bool Contains(string typeKey)
        => !string.IsNullOrWhiteSpace(typeKey) && _factories.ContainsKey(typeKey);

    /// <summary>
    /// Gets all registered TypeKeys.
    /// <para>获取全部已注册 TypeKey 列表。</para>
    /// </summary>
    public static IReadOnlyCollection<string> Keys => [.. _factories.Keys];
}
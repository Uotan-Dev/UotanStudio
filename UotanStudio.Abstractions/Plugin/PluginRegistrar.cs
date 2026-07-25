using System;
using System.Collections.Generic;
using UotanStudio.Abstractions.Nodes;

namespace UotanStudio.Abstractions.Plugin;

/// <summary>
/// <para>插件注册器，由插件入口在初始化时调用，将节点定义、命令等注册到运行时。</para>
/// Plugin registrar, invoked by plugin entry points during initialization to register node definitions, commands, etc.
/// </summary>
public sealed class PluginRegistrar
{
    private readonly List<INodeDefinition> _nodeDefinitions = new();
    private readonly Dictionary<string, Func<object?>> _services = new();

    /// <summary>
    /// <para>已注册的节点定义（只读）。</para>
    /// Registered node definitions (read-only).
    /// </summary>
    public IReadOnlyList<INodeDefinition> NodeDefinitions => _nodeDefinitions;

    /// <summary>
    /// <para>注册节点定义。</para>
    /// Registers a node definition.
    /// </summary>
    public void RegisterNodeDefinition(INodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _nodeDefinitions.Add(definition);
    }

    /// <summary>
    /// <para>注册服务实例工厂（AOT 友好：仅通过接口契约访问）。</para>
    /// Registers a service instance factory (AOT-friendly: accessed only via interface contracts).
    /// </summary>
    public void RegisterService<T>(Func<T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        _services[typeof(T).FullName!] = () => factory();
    }

    /// <summary>
    /// <para>获取服务实例。</para>
    /// Resolves a service instance.
    /// </summary>
    public T? GetService<T>() where T : class
    {
        if (_services.TryGetValue(typeof(T).FullName!, out var factory))
        {
            return factory() as T;
        }
        return null;
    }
}

/// <summary>
/// <para>插件工厂委托，由 <see cref="PluginRegistry"/> 在加载时调用，构造插件实例。</para>
/// Plugin factory delegate, invoked by <see cref="PluginRegistry"/> during loading to construct the plugin instance.
/// </summary>
public delegate IPlugin PluginFactoryDelegate();

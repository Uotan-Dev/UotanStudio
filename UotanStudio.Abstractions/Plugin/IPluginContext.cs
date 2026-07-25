using System.Collections.Generic;
using UotanStudio.Abstractions.Nodes;

namespace UotanStudio.Abstractions.Plugin;

/// <summary>
/// <para>插件运行上下文，提供注册 API、日志服务与服务定位。</para>
/// Plugin runtime context, providing registration APIs, logging service and service locator.
/// </summary>
public interface IPluginContext
{
    /// <summary>
    /// <para>注册节点定义到节点编辑器工具箱。</para>
    /// Registers a node definition into the node editor toolbox.
    /// </summary>
    void RegisterNodeDefinition(INodeDefinition definition);

    /// <summary>
    /// <para>获取所有已注册的节点定义。</para>
    /// Gets all registered node definitions.
    /// </summary>
    IReadOnlyList<INodeDefinition> NodeDefinitions { get; }

    /// <summary>
    /// <para>插件日志写入器，统一推送到主日志窗口。</para>
    /// Plugin log writer, unified push into the main log window.
    /// </summary>
    IPluginLog Log { get; }

    /// <summary>
    /// <para>获取插件运行所需的服务实例（AOT 友好，仅通过接口契约访问）。</para>
    /// Resolves a service instance required by the plugin (AOT-friendly, accessed only via interface contracts).
    /// </summary>
    /// <typeparam name="T">
    /// <para>服务类型（必须是引用类型）。</para>
    /// Service type (must be a reference type).
    /// </typeparam>
    /// <returns>
    /// <para>服务实例；未注册时返回 <c>null</c>。</para>
    /// Service instance; returns <c>null</c> when not registered.
    /// </returns>
    T? GetService<T>() where T : class;
}

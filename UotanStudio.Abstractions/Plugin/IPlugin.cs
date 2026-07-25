using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Abstractions.Plugin;

/// <summary>
/// <para>插件接口，所有节点编辑器插件均需实现此接口。</para>
/// Interface that every node editor plugin must implement.
/// </summary>
/// <remarks>
/// <para>插件通过 <see cref="PluginAttribute"/> 标注并由 <c>PluginRegistry</c> 在编译期注册，全程 AOT 兼容、零反射。</para>
/// Plugins are annotated with <see cref="PluginAttribute"/> and registered at compile time by <c>PluginRegistry</c>, fully AOT-compatible with no reflection.
/// </remarks>
public interface IPlugin
{
    /// <summary>
    /// <para>插件唯一标识符（建议使用反向域名，如 <c>com.example.myplugin</c>）。</para>
    /// Unique identifier of the plugin (reverse-DNS recommended, e.g. <c>com.example.myplugin</c>).
    /// </summary>
    string Id { get; }

    /// <summary>
    /// <para>插件显示名称。</para>
    /// Display name of the plugin.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// <para>插件版本号（语义化版本，如 <c>1.0.0</c>）。</para>
    /// Plugin version (semver, e.g. <c>1.0.0</c>).
    /// </summary>
    string Version { get; }

    /// <summary>
    /// <para>插件作者。</para>
    /// Author of the plugin.
    /// </summary>
    string Author { get; }

    /// <summary>
    /// <para>插件描述。</para>
    /// Description of the plugin.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// <para>异步初始化插件，注册节点定义、命令等。</para>
    /// Asynchronously initializes the plugin, registering node definitions, commands, etc.
    /// </summary>
    /// <param name="context">
    /// <para>插件运行上下文，提供注册 API 与日志服务。</para>
    /// Plugin runtime context, providing registration APIs and logging service.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// Cancellation token.
    /// </param>
    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>异步关闭插件，释放资源。</para>
    /// Asynchronously shuts down the plugin, releasing resources.
    /// </summary>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// Cancellation token.
    /// </param>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}

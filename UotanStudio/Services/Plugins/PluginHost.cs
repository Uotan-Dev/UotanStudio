using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;

namespace UotanStudio.Services.Plugins;

/// <summary>
/// Composition-root owner of the plugin subsystem: holds the node <see cref="IPluginRegistry"/>,
/// the visual-plugin manager and the runtime capability verdict for code-dependent plugin kinds.
/// <para>插件子系统的组合根持有者：持有节点注册表 <see cref="IPluginRegistry"/>、可视化插件管理器，
/// 以及针对依赖代码的插件种类的运行时能力判定。</para>
/// </summary>
public sealed class PluginHost
{
    /// <summary>
    /// Gets the process-wide plugin instance wrapping the shared registry and singleton manager.
    /// <para>获取包装共享注册表与单例管理器的进程级插件持有实例。</para>
    /// </summary>
    /// <value>The shared <see cref="PluginHost"/> instance.<para>共享的 <see cref="PluginHost"/> 实例。</para></value>
    public static PluginHost Current { get; } = new();

    /// <summary>
    /// Gets the node registration surface shared by built-in and plugin nodes.
    /// <para>获取内置节点与插件节点共用的节点注册面。</para>
    /// </summary>
    /// <value>The registry in use.<para>正在使用的注册表。</para></value>
    public IPluginRegistry Registry { get; }

    /// <summary>
    /// Gets the owned visual-plugin manager; its identity equals
    /// <see cref="VisualPluginManager.Instance"/>.
    /// <para>获取持有的可视化插件管理器；其身份等同于 <see cref="VisualPluginManager.Instance"/>。</para>
    /// </summary>
    /// <value>The held manager instance.<para>所持有的管理器实例。</para></value>
    public VisualPluginManager Plugins { get; }

    /// <summary>
    /// Gets the effective plugin runtime capability for this process; data and script
    /// plugins are usable at every level.
    /// <para>获取本进程生效的插件运行时能力；数据与脚本插件在任何等级都可用。</para>
    /// </summary>
    /// <value>The effective runtime capability.<para>生效的运行时能力。</para></value>
    public PluginRuntimeCapability Capability => RuntimeCapabilityProbe.EffectiveCapability;

    /// <summary>
    /// Gets the plugin kinds blocked under the current capability; empty on a full JIT
    /// runtime and non-empty only for code-dependent kinds on an AOT or trimmed publish.
    /// <para>获取当前能力下被禁用的插件种类；完整 JIT 运行时为空，仅在 AOT 或裁剪发布下对依赖代码的种类非空。</para>
    /// </summary>
    /// <value>The blocked plugin kinds.<para>被禁用的插件种类。</para></value>
    public IReadOnlyList<VisualPluginKind> BlockedKinds => PluginKindPolicy.BlockedKinds(Capability);

    /// <summary>
    /// Creates an instance around the shared registry and default plugin manager.
    /// <para>围绕共享注册表与默认插件管理器创建实例。</para>
    /// </summary>
    public PluginHost() : this(PluginRegistry.Shared, VisualPluginManager.Instance)
    {
    }

    /// <summary>
    /// Creates an instance around explicit components.
    /// <para>围绕显式组件创建实例。</para>
    /// </summary>
    /// <param name="registry">The node registration surface to expose.<para>要暴露的节点注册面。</para></param>
    /// <param name="plugins">The manager to own.<para>要持有的管理器。</para></param>
    public PluginHost(IPluginRegistry registry, VisualPluginManager plugins)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
    }
}
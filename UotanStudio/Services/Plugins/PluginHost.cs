using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;

namespace UotanStudio.Services.Plugins;

/// <summary>
/// <para>Composition-root owner of the plugin subsystem: holds the application-wide node
/// <see cref="IPluginRegistry"/> and <see cref="VisualPluginManager"/> together with the runtime
/// capability verdict that decides whether code-dependent plugin kinds may activate. Instances are
/// created once during desktop startup; the manager identity stays the existing singleton so existing
/// UI bindings keep working.</para>
/// <para>插件子系统的组合根持有者：持有应用级节点注册表 <see cref="IPluginRegistry"/> 与
/// <see cref="VisualPluginManager"/>，以及决定代码类插件能否激活的运行时能力判定。实例在桌面启动时创建一次；
/// 管理器身份沿用既有单例，既有界面绑定不受影响。</para>
/// </summary>
public sealed class PluginHost
{
    /// <summary>
    /// <para>Gets the application-wide plugin host wrapping the shared registry and singleton manager.</para>
    /// <para>获取包装共享注册表与单例管理器的应用级插件宿主。</para>
    /// </summary>
    public static PluginHost Current { get; } = new();

    /// <summary>
    /// <para>Gets the node registration surface shared by built-in and plugin nodes.</para>
    /// <para>获取内置节点与插件节点共用的节点注册面。</para>
    /// </summary>
    public IPluginRegistry Registry { get; }

    /// <summary>
    /// <para>Gets the owned visual-plugin manager; its identity equals
    /// <see cref="VisualPluginManager.Instance"/>.</para>
    /// <para>获取持有的可视化插件管理器；其身份等同于 <see cref="VisualPluginManager.Instance"/>。</para>
    /// </summary>
    public VisualPluginManager Plugins { get; }

    /// <summary>
    /// <para>Gets the effective plugin runtime capability for this process; data and script
    /// plugins are usable at every level.</para>
    /// <para>获取本进程生效的插件运行时能力；数据与脚本插件在任何等级都可用。</para>
    /// </summary>
    public PluginRuntimeCapability Capability => RuntimeCapabilityProbe.EffectiveCapability;

    /// <summary>
    /// <para>Gets the plugin kinds this host blocks under the current capability; empty on a full JIT
    /// runtime and non-empty only for code-dependent kinds on an AOT or trimmed publish.</para>
    /// <para>获取本宿主在当前能力下禁用的插件种类；完整 JIT 运行时为空，仅在 AOT 或裁剪发布下对依赖代码的
    /// 种类非空。</para>
    /// </summary>
    public IReadOnlyList<VisualPluginKind> BlockedKinds => PluginKindPolicy.BlockedKinds(Capability);

    /// <summary>
    /// <para>Creates a host around the shared registry and default plugin manager.</para>
    /// <para>围绕共享注册表与默认插件管理器创建宿主。</para>
    /// </summary>
    public PluginHost() : this(PluginRegistry.Shared, VisualPluginManager.Instance)
    {
    }

    /// <summary>
    /// <para>Creates a host around explicit components; used by isolated tests.</para>
    /// <para>围绕显式组件创建宿主；供隔离测试使用。</para>
    /// </summary>
    /// <param name="registry">
    /// <para>The node registration surface to expose.</para>
    /// <para>要暴露的节点注册面。</para>
    /// </param>
    /// <param name="plugins">
    /// <para>The manager to own.</para>
    /// <para>要持有的管理器。</para>
    /// </param>
    public PluginHost(IPluginRegistry registry, VisualPluginManager plugins)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
    }
}
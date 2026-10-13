using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Plugins;

/// <summary>
/// Policy deciding which plugin kinds require dynamic code and are blocked on an AOT or trimmed
/// publish; the mapping lives here, not in the package manifest, so packages cannot understate
/// requirements.
/// <para>判定哪些插件种类需要动态代码、因而在 AOT 或裁剪发布下被禁用；该映射由策略自身持有而不写入包清单，
/// 包无法自称比实际更轻的需求。</para>
/// </summary>
public static class PluginKindPolicy
{
    /// <summary>
    /// Gets whether the given plugin kind can only run when the process supports dynamic code.
    /// Unknown kinds are treated as code-dependent and fail closed.
    /// <para>获取给定插件种类是否只能在进程支持动态代码时运行；未分类的种类一律视为依赖代码，新增种类若未在此登记会保守禁用。</para>
    /// </summary>
    /// <param name="kind">The plugin kind to classify.<para>要分类的插件种类。</para></param>
    /// <returns><see langword="true"/> when the kind requires dynamic code.
    /// <para>该种类需要动态代码时为 <see langword="true"/>。</para></returns>
    public static bool RequiresDynamicCode(VisualPluginKind kind) => kind switch
    {
        VisualPluginKind.WorkflowTemplate => false,
        VisualPluginKind.CompositeNode => false,
        VisualPluginKind.ScriptNode => false,
        _ => true
    };

    /// <summary>
    /// Gets whether the given kind is unusable under the supplied runtime capability; only
    /// code-dependent kinds are blocked under <see cref="PluginRuntimeCapability.None"/>.
    /// <para>获取给定种类在指定运行时能力下是否不可用；只有依赖代码的种类在 <see cref="PluginRuntimeCapability.None"/> 下被禁用。</para>
    /// </summary>
    /// <param name="kind">The plugin kind to evaluate.<para>要评估的插件种类。</para></param>
    /// <param name="capability">The effective runtime capability of the current process.<para>当前进程生效的运行时能力。</para></param>
    /// <returns><see langword="true"/> when the kind must not activate.
    /// <para>该种类不得激活时为 <see langword="true"/>。</para></returns>
    public static bool IsActivationBlocked(VisualPluginKind kind, PluginRuntimeCapability capability) =>
        RequiresDynamicCode(kind) && capability == PluginRuntimeCapability.None;

    /// <summary>
    /// Lists every plugin kind blocked under the supplied capability.
    /// <para>列出在指定能力下被禁用的全部插件种类。</para>
    /// </summary>
    /// <param name="capability">The effective runtime capability of the current process.<para>当前进程生效的运行时能力。</para></param>
    /// <returns>The blocked kinds, empty when none are blocked.
    /// <para>被禁用的种类；无禁用项时为空。</para></returns>
    public static IReadOnlyList<VisualPluginKind> BlockedKinds(PluginRuntimeCapability capability) =>
        [.. Enum.GetValues<VisualPluginKind>().Where(kind => IsActivationBlocked(kind, capability))];
}
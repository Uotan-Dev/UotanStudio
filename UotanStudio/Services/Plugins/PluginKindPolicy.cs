using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Plugins;

/// <summary>
/// <para>Host-side policy deciding which plugin kinds need dynamic code and are therefore blocked on a
/// NativeAOT or trimmed publish. The table lives in the host, never in the package manifest, so a
/// package cannot claim a lighter requirement than it really has.</para>
/// <para>宿主侧策略：判定哪些插件种类需要动态代码、因而在 NativeAOT 或裁剪发布下被禁用。该表由宿主持有，
/// 不写进包清单，因此包无法自称比实际更轻的需求。</para>
/// </summary>
public static class PluginKindPolicy
{
    /// <summary>
    /// <para>Gets whether the given plugin kind can only run when the process supports dynamic code.
    /// Kinds that are unknown to this host are treated as code-dependent, so introducing a new kind
    /// without classifying it here fails closed instead of silently activating.</para>
    /// <para>获取给定插件种类是否只能在进程支持动态代码时运行。宿主未知的种类一律视为依赖代码，因而新增种类
    /// 若未在此分类会保守禁用，而不是静默放行。</para>
    /// </summary>
    /// <param name="kind">
    /// <para>The plugin kind to classify.</para>
    /// <para>要分类的插件种类。</para>
    /// </param>
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
    /// <para>Gets whether the given kind is unusable under the supplied runtime capability. Data and
    /// script kinds stay usable at every level; only code-dependent kinds are blocked under
    /// <see cref="PluginRuntimeCapability.None"/>.</para>
    /// <para>获取给定种类在指定运行时能力下是否不可用。数据与脚本种类在任何等级都可用；只有依赖代码的种类在
    /// <see cref="PluginRuntimeCapability.None"/> 下被禁用。</para>
    /// </summary>
    /// <param name="kind">
    /// <para>The plugin kind to evaluate.</para>
    /// <para>要评估的插件种类。</para>
    /// </param>
    /// <param name="capability">
    /// <para>The effective runtime capability of the current process.</para>
    /// <para>当前进程生效的运行时能力。</para>
    /// </param>
    /// <returns><see langword="true"/> when the kind must not activate.
    /// <para>该种类不得激活时为 <see langword="true"/>。</para></returns>
    public static bool IsActivationBlocked(VisualPluginKind kind, PluginRuntimeCapability capability) =>
        RequiresDynamicCode(kind) && capability == PluginRuntimeCapability.None;

    /// <summary>
    /// <para>Lists every plugin kind blocked under the supplied capability, for diagnostics and the
    /// plugin pane explanation.</para>
    /// <para>列出在指定能力下被禁用的全部插件种类，供诊断与插件面板说明使用。</para>
    /// </summary>
    /// <param name="capability">
    /// <para>The effective runtime capability of the current process.</para>
    /// <para>当前进程生效的运行时能力。</para>
    /// </param>
    /// <returns>The blocked kinds, empty when none are blocked.
    /// <para>被禁用的种类；无禁用项时为空。</para></returns>
    public static IReadOnlyList<VisualPluginKind> BlockedKinds(PluginRuntimeCapability capability) =>
        [.. Enum.GetValues<VisualPluginKind>().Where(kind => IsActivationBlocked(kind, capability))];
}
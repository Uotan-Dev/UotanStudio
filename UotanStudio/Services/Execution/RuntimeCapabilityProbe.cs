using System.Runtime.CompilerServices;

namespace UotanStudio.Services.Execution;

/// <summary>
/// <para>Plugin runtime capability levels. JSON and Lua script plugins remain usable at every
/// level; <see cref="CodeLoading"/> additionally allows reflection-based code plugins that would
/// break a NativeAOT or trimmed publish.</para>
/// <para>插件运行时能力等级。JSON 与 Lua 脚本插件在任何等级都可用；<see cref="CodeLoading"/>
/// 额外允许基于反射的代码类插件，这类插件在 NativeAOT 或裁剪发布下会失效。</para>
/// </summary>
public enum PluginRuntimeCapability
{
    /// <summary>
    /// <para>NativeAOT or trimmed publish: data and script plugins only.</para>
    /// <para>NativeAOT 或裁剪发布：仅数据与脚本插件可用。</para>
    /// </summary>
    None,

    /// <summary>
    /// <para>Full JIT runtime: all plugin kinds are available.</para>
    /// <para>完整 JIT 运行时：全部插件类型可用。</para>
    /// </summary>
    CodeLoading
}

/// <summary>
/// <para>Detects the host runtime's dynamic-code support once at startup and exposes the plugin
/// capability level derived from it. NativeAOT publishes have no JIT and no dynamic IL, so
/// reflection-based code plugins must stay disabled there; trimmed JIT builds are treated the
/// same way to stay conservative.</para>
/// <para>在启动时一次性探测宿主运行时的动态代码支持并给出由其派生的插件能力等级。NativeAOT
/// 发布没有 JIT 与动态 IL，基于反射的代码类插件必须保持禁用；裁剪的 JIT 构建按同样保守策略处理。</para>
/// </summary>
public static class RuntimeCapabilityProbe
{
    private static readonly bool DynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    /// <para>Gets a value indicating whether the running process supports dynamic code (JIT and
    /// dynamic IL). NativeAOT publishes report <see langword="false"/>.</para>
    /// <para>获取当前进程是否支持动态代码（JIT 与动态 IL）；NativeAOT 发布为
    /// <see langword="false"/>。</para>
    /// </summary>
    public static bool IsDynamicCodeSupported => DynamicCodeSupported;

    /// <summary>
    /// <para>Gets the plugin runtime capability derived from the publish form; evaluated once so
    /// the value is stable for the process lifetime.</para>
    /// <para>获取由发布形式派生的插件运行时能力；进程生命周期内求值一次并保持稳定。</para>
    /// </summary>
    public static PluginRuntimeCapability Capability => DynamicCodeSupported
        ? PluginRuntimeCapability.CodeLoading
        : PluginRuntimeCapability.None;

    /// <summary>
    /// <para>Overrides the probed capability for isolated tests; production code must not call
    /// this. Passing <see langword="null"/> restores the real probe result.</para>
    /// <para>为隔离测试覆盖探测到的能力等级；生产代码不得调用。传 <see langword="null"/>
    /// 可恢复真实探测结果。</para>
    /// </summary>
    /// <param name="capability">
    /// <para>Forced capability, or <see langword="null"/> to restore the probed value.</para>
    /// <para>强制的能力等级；传 <see langword="null"/> 恢复探测值。</para>
    /// </param>
    internal static void SetCapabilityOverride(PluginRuntimeCapability? capability)
    {
        _capabilityOverride = capability;
    }

    private static PluginRuntimeCapability? _capabilityOverride;

    /// <summary>
    /// <para>Gets the effective capability, honoring a test override when present.</para>
    /// <para>获取生效的能力等级；存在测试覆盖时使用覆盖值。</para>
    /// </summary>
    internal static PluginRuntimeCapability EffectiveCapability => _capabilityOverride ?? Capability;
}
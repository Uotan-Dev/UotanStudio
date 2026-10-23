using System.Runtime.CompilerServices;

namespace UotanStudio.Workflow.Execution;

/// <summary>Plugin runtime capability levels derived from dynamic-code support.<para>插件运行时能力等级，由动态代码支持派生。</para></summary>
public enum PluginRuntimeCapability
{
    /// <summary>Data and script plugins only, as in a NativeAOT or trimmed publish.<para>仅数据与脚本插件可用，如 NativeAOT 或裁剪发布。</para></summary>
    None,

    /// <summary>All plugin kinds are available on the full JIT runtime.<para>完整 JIT 运行时下全部插件类型可用。</para></summary>
    CodeLoading
}

/// <summary>Detects the process runtime's dynamic-code support once and exposes the derived plugin capability level.<para>一次性探测进程运行时的动态代码支持，并给出派生的插件能力等级。</para></summary>
public static class RuntimeCapabilityProbe
{
    private static readonly bool DynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>Indicates whether the running process supports dynamic code (JIT and dynamic IL).<para>指示当前进程是否支持动态代码（JIT 与动态 IL）。</para></summary>
    /// <value>True when dynamic code is supported.<para>支持动态代码时为真。</para></value>
    public static bool IsDynamicCodeSupported => DynamicCodeSupported;

    /// <summary>Gets the plugin runtime capability derived from the publish form, evaluated once.<para>获取由发布形式派生的插件运行时能力，仅求值一次。</para></summary>
    /// <value>The derived plugin runtime capability.<para>派生的插件运行时能力。</para></value>
    public static PluginRuntimeCapability Capability => DynamicCodeSupported
        ? PluginRuntimeCapability.CodeLoading
        : PluginRuntimeCapability.None;

    /// <summary>Overrides the probed capability level for isolated verification.<para>为隔离校验覆盖探测到的能力等级。</para></summary>
    /// <param name="capability">Forced capability, or null to restore the probed value.<para>强制的能力等级，传空以恢复探测值。</para></param>
    internal static void SetCapabilityOverride(PluginRuntimeCapability? capability)
    {
        _capabilityOverride = capability;
    }

    private static PluginRuntimeCapability? _capabilityOverride;

    /// <summary>Gets the effective capability, honoring an override when present.<para>获取生效的能力等级，存在覆盖时使用覆盖值。</para></summary>
    /// <value>The capability in effect for this process.<para>本进程当前生效的能力等级。</para></value>
    internal static PluginRuntimeCapability EffectiveCapability => _capabilityOverride ?? Capability;
}
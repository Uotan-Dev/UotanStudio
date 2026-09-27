namespace UotanStudio.Comm.Protocols.Common.Evidence;

/// <summary>
/// Tri-state support marker for a capability that a peer may report, omit, or contradict.
/// <para>对端可能上报、缺失或矛盾的能力的三态支持标记。</para>
/// <see cref="Unknown"/> is never equivalent to <see cref="Supported"/>; operations that require
/// the capability must fail closed while it remains unknown.
/// <para><see cref="Unknown"/> 绝不等同于 <see cref="Supported"/>；能力仍为未知时，
/// 依赖该能力的操作必须失败关闭。</para>
/// </summary>
public enum FeatureSupport
{
    /// <summary>
    /// The capability was not reported, or reports conflict. This is the zero value so an
    /// omitted field fails closed instead of silently granting a capability.
    /// <para>该能力未被上报，或上报结果互相矛盾。其为默认零值，字段缺失时按失败关闭处理，
    /// 而非静默授予能力。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The peer positively reported support for this capability.
    /// <para>对端明确上报支持该能力。</para>
    /// </summary>
    Supported = 1,

    /// <summary>
    /// The peer positively reported the absence of this capability.
    /// <para>对端明确上报不支持该能力。</para>
    /// </summary>
    Unsupported = 2
}
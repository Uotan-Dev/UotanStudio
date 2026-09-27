using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk.Profile;

/// <summary>
/// The terminal decision of a loader-entry selection evaluation.
/// <para>加载器条目选择评估的终态决策。</para>
/// </summary>
public enum MtkProfileDecision
{
    /// <summary>
    /// Exactly one structurally valid entry matched the queried device identity.
    /// <para>恰好一个结构有效的条目与已查询的设备身份匹配。</para>
    /// </summary>
    SingleMatch,

    /// <summary>
    /// No structurally valid entry matched; nothing may be uploaded.
    /// <para>没有任何结构有效的条目匹配；不得上传任何内容。</para>
    /// </summary>
    NoMatch,

    /// <summary>
    /// More than one structurally valid entry matched, so the container alone cannot identify the
    /// loader without an additional discriminating resource.
    /// <para>多个结构有效的条目均匹配，仅凭容器无法在缺少额外判别资源的情况下确定加载器。</para>
    /// </summary>
    Ambiguous,

    /// <summary>
    /// The evidence is insufficient for an automatic decision (for example every candidate lacks a
    /// complete usable DA1/DA2 region pair or lacks a software revision); the caller must select
    /// the entry explicitly.
    /// <para>证据不足以自动决策（例如所有候选项都缺少完整可用的 DA1/DA2 区域配对，或缺少软件修订版本）；
    /// 调用方必须显式选择条目。</para>
    /// </summary>
    NeedsExplicitSelection
}

/// <summary>
/// The explained result of evaluating a loader profile against a queried MTK device identity.
/// <para>针对已查询的 MTK 设备身份评估加载器配置后的、带依据的结果。</para>
/// The decision semantics mirror the EDL programmer selector: unknown evidence never defaults to
/// a match, and every decision carries the SHA-256 of the evaluated container.
/// <para>决策语义与 EDL programmer 选择器对齐：未知证据绝不默认视为匹配，且每种决策都携带被评估容器的 SHA-256。</para>
/// </summary>
public sealed record MtkProfileSelection
{
    /// <summary>
    /// Gets the matched descriptor, present only for <see cref="MtkProfileDecision.SingleMatch"/>.
    /// <para>获取匹配的配置描述符；仅在 <see cref="MtkProfileDecision.SingleMatch"/> 时存在。</para>
    /// </summary>
    public MtkDaDescriptor? Descriptor { get; init; }

    /// <summary>
    /// Gets the zero-based matched entry index, or <see langword="null"/> unless a single entry was
    /// selected. <para>获取匹配的从零开始条目索引；除非选出了唯一条目，否则为 <see langword="null"/>。</para>
    /// </summary>
    public int? EntryIndex { get; init; }

    /// <summary>
    /// Gets the validated DA1 region index of the selected entry.
    /// <para>获取所选条目经校验的 DA1 区域索引。</para>
    /// </summary>
    public int? StageOneRegionIndex { get; init; }

    /// <summary>
    /// Gets the validated, distinct DA2 region index of the selected entry.
    /// <para>获取所选条目经校验且互不相同的 DA2 区域索引。</para>
    /// </summary>
    public int? StageTwoRegionIndex { get; init; }

    /// <summary>
    /// Gets the terminal decision.
    /// <para>获取终态决策。</para>
    /// </summary>
    public required MtkProfileDecision Decision { get; init; }

    /// <summary>
    /// Gets the human-readable, localization-neutral reasons supporting the decision; never empty.
    /// <para>获取支持该决策的人类可读、与本地化无关的原因；永不为空。</para>
    /// </summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>
    /// Gets the SHA-256 hex digest of the evaluated container bytes.
    /// <para>获取被评估容器字节的 SHA-256 十六进制摘要。</para>
    /// </summary>
    public required string Sha256Hex { get; init; }

    /// <summary>
    /// Builds the immutable evidence record for this selection; the phase is always
    /// <c>da-profile</c> and the reasons travel in <see cref="ProtocolEvidence.Detail"/>.
    /// <para>为本选择构建不可变证据记录；阶段恒为 <c>da-profile</c>，原因随
    /// <see cref="ProtocolEvidence.Detail"/> 一并记录。</para>
    /// </summary>
    /// <returns>
    /// A <see cref="ProtocolEvidence"/> bound to the container hash.
    /// <para>绑定到容器哈希的 <see cref="ProtocolEvidence"/>。</para>
    /// </returns>
    public ProtocolEvidence ToEvidence() => new()
    {
        Protocol = "mtk",
        Phase = "da-profile",
        Outcome = Decision switch
        {
            MtkProfileDecision.SingleMatch => CommandOutcomeKind.Completed,
            MtkProfileDecision.NeedsExplicitSelection => CommandOutcomeKind.Unknown,
            _ => CommandOutcomeKind.Rejected
        },
        Detail = $"{Decision}: {string.Join(" | ", Reasons)}",
        ResourceSha256Hex = Sha256Hex
    };
}
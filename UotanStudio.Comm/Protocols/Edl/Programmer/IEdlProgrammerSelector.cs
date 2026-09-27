namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// Chooses one programmer from a catalog for a Sahara device identity, with explainable reasons.
/// <para>依据 Sahara 设备身份在目录中选择单个 programmer，并给出可解释的理由。</para>
/// Implementations must return a decision even when evidence is missing; the session then refuses to
/// upload. A selector never performs device I/O and must react to cancellation before returning.
/// <para>即使证据缺失，实现也必须返回结论，由会话据此拒绝上传。选择器从不执行设备 I/O，
/// 并且必须在返回前响应取消。</para>
/// </summary>
public interface IEdlProgrammerSelector
{
    /// <summary>
    /// Evaluates the catalog against the identity and returns the decision with per-candidate reasons.
    /// <para>对照身份评估目录，返回结论及逐候选理由。</para>
    /// </summary>
    /// <param name="identity">
    /// The Sahara-reported identity, or <see langword="null"/> when the device did not report one.
    /// <para>Sahara 上报的身份；设备未上报时为 <see langword="null"/>。</para>
    /// </param>
    /// <param name="catalog">
    /// The validated catalog to evaluate. <para>待评估的已校验目录。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation for provider-side work. <para>取消提供方侧的工作。</para>
    /// </param>
    /// <returns>
    /// The selection with <see cref="EdlProgrammerSelection.Decision"/>, the winning descriptor when
    /// uniquely matched, and the reason of every candidate.
    /// <para>选择结果：包含 <see cref="EdlProgrammerSelection.Decision"/>、唯一匹配时的胜出描述符，
    /// 以及每个候选项的理由。</para>
    /// </returns>
    EdlProgrammerSelection Select(SaharaDeviceIdentity? identity, EdlProgrammerCatalog catalog,
        CancellationToken cancellationToken);
}
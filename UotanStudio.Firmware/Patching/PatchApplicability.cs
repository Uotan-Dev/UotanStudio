namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Preflight state, independent from whether any output has been published.
/// <para>预检状态，与是否已发布任何输出无关。</para>
/// </summary>
public enum PatchApplicability
{
    /// <summary>
    /// The target is compatible and still in the original state, so the patch can be applied.
    /// <para>目标兼容且仍处于原始状态，补丁可以应用。</para>
    /// </summary>
    Applicable,
    /// <summary>The target is compatible and already equals the desired state. <para>目标兼容且已处于期望状态。</para></summary>
    AlreadyApplied,
    /// <summary>The target is incompatible with this patch and is skipped. <para>目标与此补丁不兼容，将被跳过。</para></summary>
    NotApplicable,
    /// <summary>
    /// The target is compatible but its state is ambiguous or unknown, so the check conflicts.
    /// <para>目标兼容但状态存在歧义或未知，检查视为冲突。</para>
    /// </summary>
    Conflict
}

/// <summary>
/// Classifies complete original/desired-state checks without treating partial matches as success.
/// <para>对完整的原始/期望状态检查进行分类，不把部分匹配当作成功。</para>
/// </summary>
public static class PatchApplicabilityCheck
{
    /// <summary>
    /// Incompatible targets are skipped; ambiguous or unknown compatible states are conflicts.
    /// <para>不兼容的目标会被跳过；兼容但存在歧义或未知的状态视为冲突。</para>
    /// </summary>
    /// <param name="compatible">
    /// Whether the target is compatible with the patch at all; incompatible targets are never conflicts.
    /// <para>目标是否与补丁兼容；不兼容的目标不会构成冲突。</para>
    /// </param>
    /// <param name="originalStateMatches">
    /// <see langword="true"/> when the complete target equals the patch's original state.
    /// <para>目标的完整内容等于补丁原始状态时为 <see langword="true"/>。</para>
    /// </param>
    /// <param name="desiredStateMatches">
    /// <see langword="true"/> when the complete target equals the patch's desired state.
    /// <para>目标的完整内容等于补丁期望状态时为 <see langword="true"/>。</para>
    /// </param>
    /// <returns><see cref="PatchApplicability.NotApplicable"/> for incompatible targets; otherwise
    /// <see cref="PatchApplicability.Conflict"/> when both flags agree (neither state or both states match),
    /// <see cref="PatchApplicability.AlreadyApplied"/> when only the desired state matches, and
    /// <see cref="PatchApplicability.Applicable"/> when only the original state matches.
    /// <para>不兼容目标返回 <see cref="PatchApplicability.NotApplicable"/>；两个标志一致（两个状态都不匹配或都匹配）
    /// 时返回 <see cref="PatchApplicability.Conflict"/>；仅期望状态匹配时返回 <see cref="PatchApplicability.AlreadyApplied"/>；
    /// 仅原始状态匹配时返回 <see cref="PatchApplicability.Applicable"/>。</para></returns>
    public static PatchApplicability Evaluate(bool compatible, bool originalStateMatches, bool desiredStateMatches)
    {
        if (!compatible)
        {
            return PatchApplicability.NotApplicable;
        }
        if (originalStateMatches == desiredStateMatches)
        {
            return PatchApplicability.Conflict;
        }
        return desiredStateMatches ? PatchApplicability.AlreadyApplied : PatchApplicability.Applicable;
    }
}
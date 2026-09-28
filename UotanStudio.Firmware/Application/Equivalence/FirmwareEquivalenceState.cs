namespace UotanStudio.Firmware.Application.Equivalence;

/// <summary>
/// Tri-state outcome of one equivalence dimension, following the repository precedent where a
/// <see cref="System.Nullable{T}"/> verification flag stays null when the check never ran: a dimension
/// that was not computed is reported as <see cref="NotEvaluated"/> and never defaults to
/// <see cref="Equivalent"/>.
/// <para>单一等价维度的三态结果，沿用仓库中“未执行的校验其可空标记保持为 null”的先例：未计算的维度报告为
/// <see cref="NotEvaluated"/>，绝不默认当作 <see cref="Equivalent"/>。</para>
/// </summary>
public enum FirmwareEquivalenceState
{
    /// <summary>No check ran for this dimension, so no equivalence claim may be made.
    /// <para>该维度没有任何检查被执行，因此不得做出等价性声明。</para></summary>
    NotEvaluated = 0,

    /// <summary>The dimension was evaluated and every comparison passed.
    /// <para>该维度已被评估，且所有比较均通过。</para></summary>
    Equivalent,

    /// <summary>The dimension was evaluated and at least one comparison failed.
    /// <para>该维度已被评估，且至少一项比较失败。</para></summary>
    NotEquivalent,
}
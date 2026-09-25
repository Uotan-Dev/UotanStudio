namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// An explicit operation within an exactly named GPT partition; no slot inference or implicit erase.
/// <para>在名称完全匹配的 GPT 分区内执行的显式操作；不进行槽位推断，也不隐式擦除。</para>
/// </summary>
/// <param name="Kind">Raw, sparse or erase operation. <para>原始、稀疏或擦除操作。</para></param>
/// <param name="PartitionName">
/// Exact case-sensitive GPT name; ambiguous names are rejected.
/// <para>精确且区分大小写的 GPT 名称；存在歧义的名称会被拒绝。</para>
/// </param>
/// <param name="Offset">
/// Block-aligned byte offset relative to the named partition.
/// <para>相对于命名分区的按块对齐字节偏移。</para>
/// </param>
/// <param name="Length">
/// Exact raw/erase length or sparse target capacity, bounded by the named partition.
/// <para>精确的原始/擦除长度或稀疏目标容量，受命名分区边界约束。</para>
/// </param>
/// <param name="SourcePath">Raw/sparse source path; null for erase. <para>原始/稀疏源路径；擦除时为 null。</para></param>
/// <param name="SourceOffset">Raw source byte offset; zero for sparse/erase. <para>原始源字节偏移；稀疏/擦除时为零。</para></param>
/// <param name="Verify">
/// Whether written extents require readback; invalid for erase.
/// <para>写出的范围是否需要回读；对擦除无效。</para>
/// </param>
public sealed record MtkPartitionFlashStep(MtkFlashStepKind Kind, string PartitionName, long Offset, long Length,
    string? SourcePath = null, long SourceOffset = 0, bool Verify = false);

public sealed partial class MtkFlashPlan
{
    /// <summary>
    /// Gets the GPT observation that must be revalidated under the execution lease, or null for
    /// explicit physical plans.
    /// <para>获取必须在执行租约下重新校验的 GPT 观测结果；显式物理计划为 null。</para>
    /// </summary>
    /// <value>
    /// The snapshot bound to this plan by partition-relative preparation; plans built directly from
    /// physical identifiers leave it null.
    /// <para>由基于分区的准备流程绑定到本计划的快照；直接使用物理标识符构建的计划保持为 null。</para>
    /// </value>
    public MtkGptSnapshot? GptSnapshot { get; private set; }

    /// <summary>
    /// Resolves named ranges and reuses whole-plan source/sparse preflight without device I/O.
    /// <para>解析命名范围，并在不执行设备 I/O 的情况下复用整计划的源/稀疏预检。</para>
    /// </summary>
    /// <param name="snapshot">
    /// A validated observation from the intended session and physical region.
    /// <para>来自目标会话与物理区域、已校验的观测结果。</para>
    /// </param>
    /// <param name="steps">
    /// One to 256 explicitly ordered partition-relative steps.
    /// <para>1 到 256 个显式排序、分区相对的步骤。</para>
    /// </param>
    /// <param name="cancellationToken">The preflight cancellation token. <para>预检取消令牌。</para></param>
    /// <returns>
    /// A one-shot plan that requires fresh matching GPT metadata before any destructive operation.
    /// <para>单次使用的计划；在任何破坏性操作之前都要求重新取得与其匹配的最新 GPT 元数据。</para>
    /// </returns>
    /// <exception cref="ArgumentException">A selected step is null. <para>某个选定步骤为 null。</para></exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="snapshot"/> or <paramref name="steps"/> is null.
    /// <para><paramref name="snapshot"/> 或 <paramref name="steps"/> 为 null。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The step count is outside one to 256, or a requested range exceeds its named GPT partition.
    /// <para>步骤数量不在 1 到 256 之间，或请求的范围超出其命名 GPT 分区。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested while resolving the named ranges.
    /// <para>在解析命名范围期间请求了取消。</para>
    /// </exception>
    public static MtkFlashPlan Prepare(MtkGptSnapshot snapshot, IReadOnlyList<MtkPartitionFlashStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "A flash plan requires between one and 256 steps.");
        }
        var selected = steps.ToArray();
        var physical = new MtkFlashStep[selected.Length];
        for (var index = 0; index < selected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = selected[index] ?? throw new ArgumentException("A flash step is null.", nameof(steps));
            var partition = snapshot.Table.FindPartition(step.PartitionName);
            if (step.Offset < 0 || step.Length <= 0 || step.Offset > partition.ByteLength
                || step.Length > partition.ByteLength - step.Offset)
            {
                throw new ArgumentOutOfRangeException(nameof(steps), "A requested range exceeds its named GPT partition.");
            }
            physical[index] = new MtkFlashStep(step.Kind, snapshot.PartitionId,
                checked(partition.ByteOffset + step.Offset), step.Length, step.SourcePath, step.SourceOffset, step.Verify);
        }
        var plan = Prepare(snapshot.Storage, physical, cancellationToken);
        plan.GptSnapshot = snapshot;
        return plan;
    }
}
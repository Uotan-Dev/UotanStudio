namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>The current phase of one executable range.
/// <para>单个可执行范围的当前阶段。</para></summary>
public enum MtkFlashPhase
{
    /// <summary>Source bytes are being written.
    /// <para>正在写入源字节。</para></summary>
    Writing,
    /// <summary>The target is performing an erase; byte progress is unknown until completion.
    /// <para>目标正在执行擦除；在完成之前字节进度未知。</para></summary>
    Erasing,
    /// <summary>Written bytes are being compared with readback.
    /// <para>正在将已写入字节与回读结果进行比较。</para></summary>
    Verifying,
    /// <summary>The operation and any requested verification completed successfully.
    /// <para>该操作及所请求的校验均已成功完成。</para></summary>
    OperationCompleted
}

/// <summary>Phase-local byte progress; full transferred bytes alone do not imply final command success.
/// <para>阶段内的字节进度；仅字节传输完毕并不意味着最终命令成功。</para></summary>
/// <param name="OperationIndex">The zero-based executable range index.
/// <para>从零开始的可执行范围索引。</para></param>
/// <param name="OperationCount">The number of executable ranges.
/// <para>可执行范围的数量。</para></param>
/// <param name="StepIndex">The originating input step index.
/// <para>来源的输入步骤索引。</para></param>
/// <param name="Phase">The current phase.
/// <para>当前阶段。</para></param>
/// <param name="ProcessedBytes">The phase's processed bytes.
/// <para>本阶段已处理的字节数。</para></param>
/// <param name="TotalBytes">The phase's total bytes.
/// <para>本阶段的总字节数。</para></param>
public sealed record MtkFlashPlanProgress(int OperationIndex, int OperationCount, int StepIndex,
    MtkFlashPhase Phase, long ProcessedBytes, long TotalBytes);

/// <summary>Returned only after the complete plan and its requested readbacks succeed.
/// <para>仅在完整计划及其所请求的回读校验全部成功后返回。</para></summary>
/// <param name="CompletedOperations">The number of completed physical operations.
/// <para>已完成的物理操作数量。</para></param>
/// <param name="WrittenBytes">The total written bytes, excluding sparse holes.
/// <para>写入的总字节数，不含稀疏空洞。</para></param>
/// <param name="ErasedBytes">The total ranges acknowledged as erased.
/// <para>被确认为已擦除的范围总量。</para></param>
/// <param name="VerifiedBytes">The total bytes matched by readback.
/// <para>回读比对一致的总字节数。</para></param>
public sealed record MtkFlashPlanResult(int CompletedOperations, long WrittenBytes, long ErasedBytes, long VerifiedBytes);
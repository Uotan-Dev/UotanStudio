namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// One progress report of a SpacemiT flash run.
/// <para>一次 SpacemiT 烧录运行的单条进度报告。</para>
/// </summary>
/// <param name="Phase">Run phase: <c>action</c>, <c>stage</c>, <c>flash-table</c>, <c>flash-partition</c>, <c>skip</c> or <c>done</c>. <para>运行阶段：<c>action</c>、<c>stage</c>、<c>flash-table</c>、<c>flash-partition</c>、<c>skip</c> 或 <c>done</c>。</para></param>
/// <param name="Partition">Target partition name when the report belongs to one. <para>报告所属时的目标分区名。</para></param>
/// <param name="BytesCompleted">Cumulative payload bytes completed for the current transfer; <see langword="null"/> when not applicable. <para>当前传输已完成的累计负载字节；不适用时为 <see langword="null"/>。</para></param>
/// <param name="BytesTotal">Total payload bytes of the current transfer; <see langword="null"/> when unknown. <para>当前传输的负载总字节；未知时为 <see langword="null"/>。</para></param>
/// <param name="Message">Human-readable status line. <para>人类可读的状态行。</para></param>
public sealed record SpacemiTFlashProgress(
    string Phase,
    string? Partition,
    long? BytesCompleted,
    long? BytesTotal,
    string Message);
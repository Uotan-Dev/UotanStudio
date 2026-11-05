namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// One progress report of a Sunxi flash run.
/// <para>一次全志烧录运行的单条进度报告。</para>
/// </summary>
/// <param name="Phase">Run phase: <c>erase</c>, <c>flash</c>, <c>download</c>, <c>verify</c>, <c>skip</c>,
/// <c>tool</c> or <c>done</c>. <para>运行阶段：<c>erase</c>、<c>flash</c>、<c>download</c>、<c>verify</c>、
/// <c>skip</c>、<c>tool</c> 或 <c>done</c>。</para></param>
/// <param name="Target">Step name the report belongs to; partition steps carry the partition name.
/// <para>报告所属的步骤名；分区步骤携带分区名。</para></param>
/// <param name="BytesCompleted">Cumulative payload bytes completed for the current transfer;
/// <see langword="null"/> when not applicable. <para>当前传输已完成的累计负载字节；不适用时为
/// <see langword="null"/>。</para></param>
/// <param name="BytesTotal">Total payload bytes of the current transfer; <see langword="null"/> when unknown.
/// <para>当前传输的负载总字节；未知时为 <see langword="null"/>。</para></param>
/// <param name="Message">Human-readable status line. <para>人类可读的状态行。</para></param>
public sealed record SunxiFlashProgress(
    string Phase,
    string? Target,
    long? BytesCompleted,
    long? BytesTotal,
    string Message);
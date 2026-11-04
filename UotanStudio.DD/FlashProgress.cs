namespace UotanStudio.DD;

/// <summary>
/// Immutable snapshot of a flash operation's progress.
/// <para>烧录操作进度的不可变快照。</para>
/// </summary>
public sealed record FlashProgress
{
    /// <summary>
    /// Gets the phase that produced this snapshot.
    /// <para>获取产生此快照的阶段。</para>
    /// </summary>
    public required FlashStage Stage { get; init; }

    /// <summary>
    /// Gets the total bytes processed across every target, including verification reads.
    /// <para>获取跨全部目标已处理的字节总数，包含校验回读。</para>
    /// </summary>
    public required long BytesProcessed { get; init; }

    /// <summary>
    /// Gets the expected total bytes across every target, or <see langword="null"/> when the image size is unknown.
    /// <para>获取跨全部目标的预期字节总数；镜像大小未知时为 <see langword="null"/>。</para>
    /// </summary>
    public long? TotalBytes { get; init; }

    /// <summary>
    /// Gets the completion percentage from 0 to 100, or <see langword="null"/> when <see cref="TotalBytes"/> is unknown.
    /// <para>获取 0 到 100 的完成百分比；<see cref="TotalBytes"/> 未知时为 <see langword="null"/>。</para>
    /// </summary>
    public double? Percentage { get; init; }

    /// <summary>
    /// Gets the current throughput in bytes per second across every target.
    /// <para>获取跨全部目标的当前吞吐量，单位为字节每秒。</para>
    /// </summary>
    public double BytesPerSecond { get; init; }

    /// <summary>
    /// Gets the estimated remaining time, or <see langword="null"/> when it cannot be estimated.
    /// <para>获取预计剩余时间；无法估算时为 <see langword="null"/>。</para>
    /// </summary>
    public TimeSpan? RemainingTime { get; init; }

    /// <summary>
    /// Gets the number of targets sharing the operation.
    /// <para>获取参与本次操作的目标数量。</para>
    /// </summary>
    public required int TargetCount { get; init; }
}
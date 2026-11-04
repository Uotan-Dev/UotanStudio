using System.Diagnostics;

namespace UotanStudio.DD;

/// <summary>
/// Projects byte counts from a flash operation into <see cref="FlashProgress"/> snapshots for an
/// <see cref="IProgress{T}"/> sink.
/// <para>把烧录操作的字节计数转换为 <see cref="FlashProgress"/> 快照并发送到 <see cref="IProgress{T}"/> 接收器。</para>
/// </summary>
/// <remarks>
/// Each stage restarts its own stopwatch so throughput and remaining time never mix write time with
/// verification time. A missing sink disables all reporting without changing the operation.
/// <para>
/// 每个阶段都会重启自己的秒表，因此吞吐量与剩余时间不会混合写入与校验的耗时。接收器为空时关闭全部上报，
/// 不改变操作本身。
/// </para>
/// </remarks>
internal sealed class FlashProgressReporter
{
    private readonly IProgress<FlashProgress>? _progress;
    private readonly int _targetCount;
    private readonly Stopwatch _stageWatch = new();
    private FlashStage _stage = FlashStage.Preparing;

    /// <summary>
    /// Creates a reporter for one operation.
    /// <para>为一次操作创建上报器。</para>
    /// </summary>
    /// <param name="progress">Optional sink; <see langword="null"/> disables reporting.
    /// <para>可选的接收器；<see langword="null"/> 时关闭上报。</para></param>
    /// <param name="targetCount">Number of targets sharing the operation.
    /// <para>参与本次操作的目标数量。</para></param>
    internal FlashProgressReporter(IProgress<FlashProgress>? progress, int targetCount)
    {
        _progress = progress;
        _targetCount = targetCount;
    }

    /// <summary>
    /// Switches to a new stage and emits its zero-progress snapshot.
    /// <para>切换到新阶段并发送该阶段的零进度快照。</para>
    /// </summary>
    /// <param name="stage">Stage that starts now.<para>当前开始的阶段。</para></param>
    /// <param name="totalBytes">Expected bytes for the stage, or <see langword="null"/> when unknown.
    /// <para>该阶段的预期字节数；未知时为 <see langword="null"/>。</para></param>
    internal void EnterStage(FlashStage stage, long? totalBytes)
    {
        _stage = stage;
        _stageWatch.Restart();
        Emit(stage, 0, totalBytes);
    }

    /// <summary>
    /// Emits progress for the current stage.
    /// <para>发送当前阶段的进度。</para>
    /// </summary>
    /// <param name="processed">Bytes processed so far across every target.
    /// <para>跨全部目标已处理的字节数。</para></param>
    /// <param name="totalBytes">Expected bytes, or <see langword="null"/> when unknown.
    /// <para>预期字节数；未知时为 <see langword="null"/>。</para></param>
    internal void Report(long processed, long? totalBytes) => Emit(_stage, processed, totalBytes);

    /// <summary>
    /// Emits the terminal snapshot after every target finished successfully.
    /// <para>全部目标成功完成后发送最终快照。</para>
    /// </summary>
    /// <param name="processed">Total bytes written across every target.
    /// <para>跨全部目标写入的总字节数。</para></param>
    /// <param name="totalBytes">Expected bytes, or <see langword="null"/> when unknown.
    /// <para>预期字节数；未知时为 <see langword="null"/>。</para></param>
    /// <param name="elapsed">Wall-clock duration of the whole operation.
    /// <para>整个操作的实际耗时。</para></param>
    internal void Complete(long processed, long? totalBytes, TimeSpan elapsed)
    {
        _progress?.Report(new FlashProgress
        {
            Stage = FlashStage.Completed,
            BytesProcessed = totalBytes ?? processed,
            TotalBytes = totalBytes,
            Percentage = 100,
            BytesPerSecond = elapsed.TotalSeconds > 0 ? processed / elapsed.TotalSeconds : 0,
            RemainingTime = TimeSpan.Zero,
            TargetCount = _targetCount,
        });
    }

    private void Emit(FlashStage stage, long processed, long? totalBytes)
    {
        if (_progress is null)
        {
            return;
        }

        var elapsed = _stageWatch.Elapsed;
        var speed = elapsed.TotalSeconds > 0.05 ? processed / elapsed.TotalSeconds : 0;
        double? percentage = totalBytes switch
        {
            > 0 => Math.Min(100, processed * 100.0 / totalBytes.Value),
            0 => 100,
            _ => null,
        };
        TimeSpan? remaining = totalBytes is { } total && speed > 0 && processed < total
            ? TimeSpan.FromSeconds((total - processed) / speed)
            : null;
        _progress.Report(new FlashProgress
        {
            Stage = stage,
            BytesProcessed = processed,
            TotalBytes = totalBytes,
            Percentage = percentage,
            BytesPerSecond = speed,
            RemainingTime = remaining,
            TargetCount = _targetCount,
        });
    }
}
using System.Diagnostics;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Coalesces cumulative progress positions so a sink receives at most one notification per reporting window and always
/// receives a terminal notification whose processed value equals the total.
/// <para>合并累计的进度位置，使接收端在每个上报窗口内至多收到一条通知，并且必定收到一条已处理量等于总量的终止通知。</para>
/// </summary>
/// <remarks>
/// Reported values are cumulative positions rather than increments, so concurrent producers may publish the same
/// position; the largest position seen so far is the one published, which keeps the notification stream
/// non-decreasing. The window matches <see cref="ExtractionByteCounter"/> so both notify at the same rate, and the
/// first report bypasses the window so short operations still notify per item while long operations gain an immediate
/// baseline. Reports arriving after <see cref="Complete"/> are dropped, so a late producer cannot move the published
/// position back below the total.
/// <para>上报的是累计位置而非增量，因此并发生产者可能发布相同的位置；实际发出的是见过的最大位置，由此保证通知流不会递减。
/// 窗口长度与 <see cref="ExtractionByteCounter"/> 相同，两者以同一频率通知；首条报告不受窗口限制，因此短操作仍逐项通知，
/// 长操作也能立即取得基线。<see cref="Complete"/> 之后到达的报告会被丢弃，迟到的生产者无法把已发布的位置退回总量以下。</para>
/// </remarks>
internal sealed class ProgressThrottle : IProgress<(long Processed, long Total)>
{
    private const long ReportWindowMilliseconds = 100;
    private const long NotEmitted = -1;

    private readonly Lock gate = new();
    private readonly IProgress<(long Processed, long Total)>? progress;
    private readonly CancellationToken token;
    private readonly long total;
    private long lastReport = Stopwatch.GetTimestamp();
    private long processed;
    private long emitted = NotEmitted;
    private bool completed;

    /// <summary>
    /// Creates a throttle whose terminal notification always carries <paramref name="total"/>.
    /// <para>创建一个节流器，其终止通知始终携带 <paramref name="total"/>。</para>
    /// </summary>
    /// <param name="total">Authoritative total attached to every notification, regardless of the value reported.
    /// <para>附加到每条通知上的权威总量，与上报携带的数值无关。</para></param>
    /// <param name="progress">The sink receiving the coalesced notifications; <see langword="null"/> discards them.
    /// <para>接收合并后通知的目标；为 <see langword="null"/> 时丢弃通知。</para></param>
    /// <param name="token">Cancels notifications alongside the measured work. <para>与计量工作一同响应取消。</para></param>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was already signalled.
    /// <para><paramref name="token"/> 已被触发。</para></exception>
    internal ProgressThrottle(long total, IProgress<(long Processed, long Total)>? progress, CancellationToken token = default)
    {
        this.total = total;
        this.progress = progress;
        this.token = token;
        token.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The <see cref="CancellationToken"/> given to the constructor was signalled.
    /// <para>构造时传入的 <see cref="CancellationToken"/> 已被触发。</para></exception>
    public void Report((long Processed, long Total) value)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (completed)
            {
                return;
            }

            processed = Math.Max(processed, value.Processed);
            if (emitted != NotEmitted && Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds < ReportWindowMilliseconds)
            {
                return;
            }

            Emit(processed);
        }
    }

    /// <summary>
    /// Publishes the terminal position once, unless the total has already been published.
    /// <para>发布一次终止位置；若总量已经发布则不再重复。</para>
    /// </summary>
    internal void Complete()
    {
        lock (gate)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            if (emitted != total)
            {
                Emit(total);
            }
        }
    }

    private void Emit(long value)
    {
        lastReport = Stopwatch.GetTimestamp();
        emitted = value;
        progress?.Report((value, total));
    }
}

/// <summary>
/// Narrows cumulative long positions onto the entry-count sink exposed by extraction overloads.
/// <para>把 long 型累计位置收敛到解包重载对外暴露的条目数接收端。</para>
/// </summary>
/// <param name="progress">The entry-count sink; <see langword="null"/> discards reports.
/// <para>条目数接收端；为 <see langword="null"/> 时丢弃报告。</para></param>
internal sealed class EntryCountProgress(IProgress<(int processed, int total)>? progress) : IProgress<(long Processed, long Total)>
{
    /// <inheritdoc />
    public void Report((long Processed, long Total) value) => progress?.Report((checked((int)value.Processed), checked((int)value.Total)));
}
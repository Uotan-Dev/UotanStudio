namespace UotanStudio.Services.Flash;

/// <summary>
/// Reports the cumulative byte totals a transfer produces on the calling thread instead of posting them to the
/// thread pool, which <see cref="Progress{T}"/> does.
/// <para>在调用线程上上报传输产生的累计字节数，而不是像 <see cref="Progress{T}"/> 那样投递到线程池。</para>
/// </summary>
/// A fastboot download reports one total per wire chunk, and the chunk loop is the only producer: the progress of
/// each chunk has to reach the log in transfer order. <see cref="Progress{T}"/> schedules one callback per report
/// on the thread pool, where a slow consumer lets the queued callbacks collapse into a late burst that repeats a
/// single status line and hides the ones between them.
/// <para>fastboot 下载按每个线上分块上报一次总数，且分块循环是唯一的生产者：每个分块的进度必须按传输顺序到达
/// 日志。<see cref="Progress{T}"/> 会把每次上报各自排到线程池，消费端稍慢时排队的回调就退化成一次迟到的突发，
/// 重复同一行状态而盖掉其间的每一行。</para>
/// <param name="publish">Receives every reported byte total on the calling thread.
/// <para>在调用线程上接收每一次上报的字节总数。</para></param>
internal sealed class SynchronousFlashProgress(Action<long> publish) : IProgress<long>
{
    /// <summary>
    /// Forwards the byte total immediately, so the caller observes one report per chunk in transfer order.
    /// <para>立即转发字节总数，使调用方按传输顺序观察到每个分块的一次上报。</para>
    /// </summary>
    /// <param name="value">The cumulative byte total. <para>累计字节总数。</para></param>
    public void Report(long value) => publish(value);
}
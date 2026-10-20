using System.Diagnostics;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Publishes cumulative byte totals to a flashing sink at most once per 100 ms window and remembers the newest
/// value so the owner can flush it when the operation ends, including on the throw and cancel paths. One
/// producer loop is expected: the streaming or area-read loop calls <see cref="Report"/> and the enclosing
/// operation calls <see cref="Flush"/> from its <c>finally</c>, so no lock or other synchronization is needed.
/// <para>按累计字节值向刷写接收方发布进度，每个 100 毫秒窗口最多一次，并记住最新的值，好让拥有者在操作结束时
/// 刷新它，其中包含抛出与取消路径。这里假定只有一个生产循环：流式或区域读取循环调用
/// <see cref="Report"/>，外层操作在自己的 <c>finally</c> 中调用 <see cref="Flush"/>，因此无需加锁或做任何
/// 其他同步。</para>
/// </summary>
internal sealed class ThrottledFlashByteProgress(Action<long> publish) : IProgress<long>
{
    /// <summary>Window in milliseconds during which at most one report is published.
    /// <para>最多发布一次报告的窗口长度（毫秒）。</para></summary>
    private const int WindowMilliseconds = 100;

    /// <summary>Sentinel for "no value published yet", a byte total no loop can produce.
    /// <para>表示“尚未发布任何值”的哨兵值，即任何循环都不可能产生的字节总数。</para></summary>
    private const long NoValue = -1L;

    private long lastPublishedBytes = NoValue;
    private long latestBytes = NoValue;
    private long lastPublishedTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Forwards the byte total unless it repeats the value just published or the window has not
    /// elapsed yet.
    /// <para>除非该字节总数与刚刚发布的值重复或窗口尚未到期，否则将其转发。</para></summary>
    /// <param name="value">The cumulative byte total. <para>累计字节总数。</para></param>
    public void Report(long value)
    {
        latestBytes = value;
        if (value == lastPublishedBytes)
        {
            return;
        }

        if (Stopwatch.GetElapsedTime(lastPublishedTimestamp).TotalMilliseconds < WindowMilliseconds)
        {
            return;
        }

        Publish(value);
    }

    /// <summary>
    /// Publishes the newest byte total when the window suppressed it, so a finished, faulted or cancelled
    /// operation never leaves the sink on a stale count. Repeating the value already published is a no-op,
    /// which keeps the terminal flush from emitting a second copy of the last window's report.
    /// <para>当窗口抑制了最新的字节总数时就把它发布出去，因此已结束、已失败或已取消的操作不会让接收方停留在
    /// 过期的计数上。与已发布取值重复时不做事，这样终局刷新就不会再发一份上一个窗口报告的副本。</para>
    /// </summary>
    internal void Flush()
    {
        if (latestBytes != lastPublishedBytes)
        {
            Publish(latestBytes);
        }
    }

    private void Publish(long value)
    {
        lastPublishedBytes = value;
        lastPublishedTimestamp = Stopwatch.GetTimestamp();
        publish(value);
    }
}
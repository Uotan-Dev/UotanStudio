using System.Collections.Concurrent;

namespace UotanStudio.Services.Flash;

/// <summary>Shares the existing per-partition 100 ms progress policy; instances keep separate histories.
/// <para>共享按分区的 100 毫秒进度策略；各实例保留独立的历史记录。</para></summary>
internal sealed class PartitionProgressThrottle
{
    private readonly ConcurrentDictionary<string, (string Status, long Ticks)> _lastReports = new();

    /// <summary>Accepts changed statuses, elapsed intervals and every 100% report.
    /// <para>接受状态变化、经过的时间间隔以及每一次 100% 报告。</para></summary>
    /// <param name="partition">The partition key, or null for the shared key.
    /// <para>分区键，为 null 时使用共享键。</para></param>
    /// <param name="percent">The completion percentage of the report.<para>报告的完成百分比。</para></param>
    /// <param name="status">The status text carried by the report.<para>报告携带的状态文本。</para></param>
    /// <param name="now">The current timestamp in ticks.<para>以计时单位表示的当前时间戳。</para></param>
    /// <returns>Whether the report should be emitted now.<para>当前报告是否应当发出。</para></returns>
    internal bool ShouldReport(string? partition, int percent, string status, long now)
    {
        var key = partition ?? string.Empty;
        if (_lastReports.TryGetValue(key, out var last)
            && percent != 100
            && string.Equals(status, last.Status, StringComparison.Ordinal)
            && now - last.Ticks < 100)
            return false;
        _lastReports[key] = (status, now);
        return true;
    }
}
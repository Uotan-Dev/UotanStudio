using System.Collections.Concurrent;

namespace UotanStudio.Services.Flash;

/// <summary>Shares the existing per-partition 100 ms progress policy; instances keep separate histories.</summary>
internal sealed class PartitionProgressThrottle
{
    private readonly ConcurrentDictionary<string, (string Status, long Ticks)> _lastReports = new();

    /// <summary>Accepts changed statuses, elapsed intervals and every 100% report, as before.</summary>
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
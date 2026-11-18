using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class MirrorExportStallWatcherTests
{
    [Fact]
    public void OnPacketArrived_FrozenCounterBeyondStallWindow_TriggersExactlyOnce()
    {
        var watcher = new MirrorExportStallWatcher();
        Assert.False(watcher.OnPacketArrived(1000, compositedFrames: 42, compositionCovering: true));
        Assert.False(watcher.OnPacketArrived(1000 + MirrorExportStallWatcher.StallMilliseconds - 1, 42, true));
        Assert.True(watcher.OnPacketArrived(1000 + MirrorExportStallWatcher.StallMilliseconds, 42, true));
        Assert.False(watcher.OnPacketArrived(1000 + MirrorExportStallWatcher.StallMilliseconds + 5000, 42, true));
    }

    [Fact]
    public void OnPacketArrived_CounterAdvances_RearmsInsteadOfTriggering()
    {
        var watcher = new MirrorExportStallWatcher();
        Assert.False(watcher.OnPacketArrived(1000, 10, true));
        Assert.False(watcher.OnPacketArrived(1000 + MirrorExportStallWatcher.StallMilliseconds, 11, true));
        Assert.False(watcher.OnPacketArrived(1000 + 2 * MirrorExportStallWatcher.StallMilliseconds, 12, true));
    }

    [Fact]
    public void OnPacketArrived_CoverageRevoked_ClearsBaselineAndDoesNotTrigger()
    {
        var watcher = new MirrorExportStallWatcher();
        Assert.False(watcher.OnPacketArrived(1000, 10, true));
        Assert.False(watcher.OnPacketArrived(1000 + 100, 10, compositionCovering: false));
        Assert.False(watcher.OnPacketArrived(1000 + MirrorExportStallWatcher.StallMilliseconds + 100, 10, true));
    }

    [Fact]
    public void OnPacketArrived_CoverageRevokedThenRestated_WaitsForAFullNewWindow()
    {
        var watcher = new MirrorExportStallWatcher();
        Assert.False(watcher.OnPacketArrived(1000, 10, true));
        Assert.False(watcher.OnPacketArrived(1000 + 2000, 10, compositionCovering: false));
        Assert.False(watcher.OnPacketArrived(1000 + 2001, 10, true));
        Assert.False(watcher.OnPacketArrived(1000 + 2001 + MirrorExportStallWatcher.StallMilliseconds - 1, 10, true));
        Assert.True(watcher.OnPacketArrived(1000 + 2001 + MirrorExportStallWatcher.StallMilliseconds, 10, true));
    }
}
using System.Globalization;
using System.Threading;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Covers the shared progress meter's formatting and sliding-window speed/ETA estimation.</summary>
public sealed class ProgressMeterTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1_000_000_000;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(double seconds) => _timestamp += (long)(seconds * TimestampFrequency);
    }

    [Fact]
    public void SlidingWindowUsesClockFrequencyAndDropsOldThroughput()
    {
        var clock = new ManualClock(); var meter = new ProgressMeter(clock);
        meter.Report(0, 20000);
        clock.Advance(1); meter.Report(10000, 20000);
        clock.Advance(4); meter.Report(10400, 20000);
        clock.Advance(1); meter.Report(10500, 20000);
        Assert.Equal(100d, meter.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(95), meter.EstimatedRemaining);
    }

    [Fact]
    public void ChangingUnitsDoesNotReuseByteSpeedAsItemSpeed()
    {
        var clock = new ManualClock(); var meter = new ProgressMeter(clock);
        meter.Report(0, 1000); clock.Advance(1); meter.Report(100, 1000);
        meter.ReportItems(500, 1000);
        Assert.Null(meter.BytesPerSecond);
        clock.Advance(1); meter.ReportItems(502, 1000);
        Assert.Equal("502|1000|2/s", meter.FormatStats("{0}|{1}|{2}"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1610612736, "1.5 GB")]
    public void FormatSize_RendersBase1024Units(double bytes, string expected)
    {
        Assert.Equal(expected, ProgressMeter.FormatSize(bytes));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1024)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FormatSize_NegativeOrNonFinite_RendersDash(double bytes)
    {
        Assert.Equal("—", ProgressMeter.FormatSize(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void FormatSpeed_InvalidValue_RendersDash(double bytesPerSecond)
    {
        Assert.Equal("—", ProgressMeter.FormatSpeed(bytesPerSecond));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1536)]
    [InlineData(1048576)]
    public void FormatSpeed_PositiveValue_AppendsPerSecondSuffix(double bytesPerSecond)
    {
        Assert.EndsWith("/s", ProgressMeter.FormatSpeed(bytesPerSecond));
        Assert.NotEqual("—", ProgressMeter.FormatSpeed(bytesPerSecond));
    }

    [Fact]
    public void FormatEta_NullOrZero_RendersDash()
    {
        Assert.Equal("—", ProgressMeter.FormatEta(null));
        Assert.Equal("—", ProgressMeter.FormatEta(TimeSpan.Zero));
        Assert.Equal("—", ProgressMeter.FormatEta(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(59, "00:59")]
    [InlineData(61, "01:01")]
    [InlineData(3661, "1:01:01")]
    [InlineData(7325, "2:02:05")]
    public void FormatEta_RendersClockFormat(int seconds, string expected)
    {
        Assert.Equal(expected, ProgressMeter.FormatEta(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ReportItems_ThenFormatStats_ShowsCountsAndDashForInsufficientSamples()
    {
        var meter = new ProgressMeter();
        meter.ReportItems(3, 10);

        Assert.Equal(3, meter.Processed);
        Assert.Equal(10, meter.Total);
        // 样本不足（<2）时速度与 ETA 均为「—」。
        Assert.Null(meter.BytesPerSecond);
        Assert.Equal("3|10|—|—", meter.FormatStats("{0}|{1}|{2}|{3}"));
    }

    [Fact]
    public void Report_AfterSampleGap_ComputesPositiveSpeedAndEta()
    {
        var meter = new ProgressMeter();
        meter.Report(0, 1000);
        // 越过 0.25s 最小样本间隔，使窗口首尾可估算速度。
        Thread.Sleep(300);
        meter.Report(500, 1000);

        Assert.Equal(500, meter.Processed);
        var speed = meter.BytesPerSecond;
        Assert.NotNull(speed);
        Assert.True(speed > 0);
        var eta = meter.EstimatedRemaining;
        Assert.NotNull(eta);
        Assert.True(eta > TimeSpan.Zero);
        Assert.Equal(500, meter.Processed);
    }

    [Fact]
    public void ReportItems_WithSufficientGap_SpeedSegmentContainsPerSecond()
    {
        var meter = new ProgressMeter();
        meter.ReportItems(0, 100);
        Thread.Sleep(300);
        meter.ReportItems(50, 100);

        Assert.NotNull(meter.BytesPerSecond);
        var stats = meter.FormatStats("{0}|{1}|{2}|{3}");
        var segments = stats.Split('|');
        Assert.Equal(4, segments.Length);
        Assert.EndsWith("/s", segments[2]);
    }

    [Fact]
    public void Report_ProcessedRegression_StartsNewStageWithClearedSamples()
    {
        var meter = new ProgressMeter();
        meter.Report(0, 1000);
        Thread.Sleep(300);
        meter.Report(500, 1000);
        Assert.NotNull(meter.BytesPerSecond);

        // processed 回退视为新阶段：清空样本，速度恢复为 null 直至积累新样本。
        meter.Report(100, 1000);
        Assert.Equal(100, meter.Processed);
        Assert.Null(meter.BytesPerSecond);
        Assert.True(meter.EstimatedRemaining is null);

        // 新阶段继续喂样本后速度重新起算。
        Thread.Sleep(300);
        meter.Report(600, 1000);
        var speed = meter.BytesPerSecond;
        Assert.NotNull(speed);
        Assert.True(speed > 0);
        Assert.NotNull(meter.EstimatedRemaining);
    }

    [Fact]
    public void EstimatedRemaining_WhenTotalNotGreaterThanProcessed_IsNull()
    {
        var meter = new ProgressMeter();
        meter.Report(1000, 1000);
        Thread.Sleep(300);
        meter.Report(1000, 1000);

        Assert.Null(meter.EstimatedRemaining);

        meter.Reset();
        meter.Report(500, 400);
        Thread.Sleep(300);
        meter.Report(500, 400);
        Assert.True(meter.EstimatedRemaining is null);
    }
}

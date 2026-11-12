using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// The busy overlay's statistics line: byte samples render their processed/total sizes, a determinate overall
/// fraction without a transfer length renders as a percentage, and an indeterminate run keeps the plain busy caption.
/// <para>忙碌遮罩的统计行：字节样本呈现已处理/总量，只有确定总体比例而没有传输长度时呈现百分比，不确定的运行保持
/// 纯忙碌文案。</para>
/// </summary>
public sealed class FlashBusyProgressTests
{
    [Fact]
    public void ReportBusyTransfer_WithKnownTotal_ShowsProcessedAndTotalSizes()
    {
        var model = new FlashViewModel { IsBusy = true };

        model.ReportBusyTransfer(1024, 4096);

        Assert.True(model.HasBusyStats);
        Assert.Contains("1 KB", model.BusyStatsText, StringComparison.Ordinal);
        Assert.Contains("4 KB", model.BusyStatsText, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateBusyProgress_WithDeterminateOverallFraction_ShowsThePercentage()
    {
        var model = new FlashViewModel { IsBusy = true, OverallPercent = 42, IsOverallIndeterminate = false };

        model.UpdateBusyProgress();

        Assert.True(model.HasBusyStats);
        Assert.Contains("42%", model.BusyStatsText, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateBusyProgress_WhenTheOverallFractionIsIndeterminate_HidesTheStatistics()
    {
        var model = new FlashViewModel { IsBusy = true, OverallPercent = 42, IsOverallIndeterminate = true };

        model.UpdateBusyProgress();

        Assert.False(model.HasBusyStats);
        Assert.Equal(string.Empty, model.BusyStatsText);
    }

    [Fact]
    public void ReportBusyTransfer_OutsideABusyScope_LeavesTheStatisticsEmpty()
    {
        var model = new FlashViewModel();

        model.ReportBusyTransfer(1024, 4096);

        Assert.False(model.HasBusyStats);
        Assert.Equal(string.Empty, model.BusyStatsText);
    }

    [Fact]
    public void ResetBusyProgress_AfterAReport_ClearsTheStatistics()
    {
        var model = new FlashViewModel { IsBusy = true };
        model.ReportBusyTransfer(1024, 4096);

        model.ResetBusyProgress();

        Assert.False(model.HasBusyStats);
        Assert.Equal(string.Empty, model.BusyStatsText);
    }
}
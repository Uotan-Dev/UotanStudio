using UotanStudio.Comm.Diagnostics;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Retention policy of the global log window model: the visible and retained lists share the store budget, the
/// budget is suspended while debug mode is enabled, and the unfiltered list keeps every retained entry for export.
/// <para>全局日志窗口模型的保留策略：可见列表与保留列表共用存储预算，调试模式开启期间暂停预算，未筛选列表为导出
/// 保留全部条目。</para>
/// </summary>
[Collection("Log pipeline")]
public sealed class LogViewModelRetentionTests
{
    [Fact]
    public void FlushPending_OverCharacterBudget_KeepsNewestAndDropsDisplayedPrefix()
    {
        var service = new LogService();
        using var model = new LogViewModel(service, maxEntries: 100, maxCharacters: 1000);

        for (var i = 0; i < 10; i++)
        {
            service.Write(LogLevel.Info, "S", $"m{i}:" + new string('x', 200));
        }

        model.FlushPending();

        Assert.Equal(4, model.AllEntries.Count);
        Assert.Equal(4, model.Entries.Count);
        Assert.StartsWith("m6:", model.AllEntries[0].Message, StringComparison.Ordinal);
        Assert.StartsWith("m9:", model.AllEntries[^1].Message, StringComparison.Ordinal);
        Assert.StartsWith("m6:", model.Entries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushPending_OverEntryGuard_KeepsNewestEntries()
    {
        var service = new LogService();
        using var model = new LogViewModel(service, maxEntries: 5, maxCharacters: long.MaxValue);

        for (var i = 0; i < 12; i++)
        {
            service.Write(LogLevel.Info, "S", $"e{i}");
        }

        model.FlushPending();

        Assert.Equal(5, model.AllEntries.Count);
        Assert.Equal("e7", model.AllEntries[0].Message);
        Assert.Equal("e11", model.AllEntries[^1].Message);
    }

    [Fact]
    public void AllEntries_IgnoresFilters_WhileEntriesFollowsThem()
    {
        var service = new LogService();
        using var model = new LogViewModel(service, maxEntries: 100, maxCharacters: long.MaxValue);
        service.Write(LogLevel.Info, "S", "info-line");
        service.Write(LogLevel.Error, "S", "error-line");
        model.FlushPending();

        model.LevelFilter = LogLevel.Error;

        Assert.Equal(2, model.AllEntries.Count);
        var displayed = Assert.Single(model.Entries);
        Assert.Equal("error-line", displayed.Message);
    }

    [Fact]
    public void Constructor_BackfillsHistoryWithinTheCharacterBudget()
    {
        var service = new LogService();
        for (var i = 0; i < 10; i++)
        {
            service.Write(LogLevel.Info, "S", $"m{i}:" + new string('x', 200));
        }

        using var model = new LogViewModel(service, maxEntries: 100, maxCharacters: 1000);

        Assert.Equal(4, model.AllEntries.Count);
        Assert.Equal(4, model.Entries.Count);
        Assert.StartsWith("m6:", model.AllEntries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushPending_WhileDebugModeEnabled_KeepsEntriesBeyondTheBudget()
    {
        var service = new LogService();
        var commLevel = CommTrace.Level;
        var payloads = CommTrace.CaptureRawPayloads;
        var usbEnabled = UsbTrace.IsEnabled;
        var frames = UsbTrace.CaptureFrames;
        try
        {
            service.ApplyDebugMode(debug: true);
            using var model = new LogViewModel(service, maxEntries: 100, maxCharacters: 1000);

            for (var i = 0; i < 10; i++)
            {
                service.Write(LogLevel.Info, "S", $"m{i}:" + new string('x', 200));
            }

            model.FlushPending();

            Assert.Equal(10, model.AllEntries.Count);
            Assert.Equal(10, model.Entries.Count);
        }
        finally
        {
            CommTrace.Level = commLevel;
            CommTrace.CaptureRawPayloads = payloads;
            UsbTrace.IsEnabled = usbEnabled;
            UsbTrace.CaptureFrames = frames;
        }
    }

    [Fact]
    public void FlushPending_AfterDebugModeIsDisabled_TrimsToTheBudgetAgain()
    {
        var service = new LogService();
        var commLevel = CommTrace.Level;
        var payloads = CommTrace.CaptureRawPayloads;
        var usbEnabled = UsbTrace.IsEnabled;
        var frames = UsbTrace.CaptureFrames;
        try
        {
            service.ApplyDebugMode(debug: true);
            using var model = new LogViewModel(service, maxEntries: 100, maxCharacters: 1000);
            for (var i = 0; i < 10; i++)
            {
                service.Write(LogLevel.Info, "S", $"m{i}:" + new string('x', 200));
            }
            model.FlushPending();
            Assert.Equal(10, model.AllEntries.Count);

            service.ApplyDebugMode(debug: false);
            service.Write(LogLevel.Info, "S", "next");
            model.FlushPending();

            Assert.Equal(5, model.AllEntries.Count);
            Assert.StartsWith("m6:", model.AllEntries[0].Message, StringComparison.Ordinal);
        }
        finally
        {
            CommTrace.Level = commLevel;
            CommTrace.CaptureRawPayloads = payloads;
            UsbTrace.IsEnabled = usbEnabled;
            UsbTrace.CaptureFrames = frames;
        }
    }
}
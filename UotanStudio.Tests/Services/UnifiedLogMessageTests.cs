using UotanStudio.Services;
using UotanStudio.Services.Logging;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Interpolated log messages: the handler formats only when the entry reaches a destination, and the interface
/// default keeps a pass-through target recording the same text as plain string interpolation.
/// <para>插值日志消息：处理器仅在条目会被某个目标接收时才格式化，接口默认实现使直通目标记录与普通字符串插值相同的文本。</para>
/// </summary>
public sealed class UnifiedLogMessageTests
{
    [Fact]
    public void Write_InterpolatedMessage_DiscardedLevelBuildsNoText()
    {
        var service = new LogService();
        service.SetMinimumLevel(LogLevel.Info);
        var probe = new CountingValue();

        service.Write(LogLevel.Debug, "Handler", $"value {probe}");

        Assert.Equal(0, probe.Renders);
        Assert.Empty(service.Entries);
    }

    [Fact]
    public void Write_InterpolatedMessage_RecordedLevelRendersOnce()
    {
        var service = new LogService();
        var probe = new CountingValue();

        service.Write(LogLevel.Info, "Handler", $"value {probe}");

        var entry = Assert.Single(service.Entries);
        Assert.Equal("value probe", entry.Message);
        Assert.Equal(1, probe.Renders);
    }

    [Fact]
    public void Write_ThroughInterface_DiscardedLevelBuildsNoText()
    {
        var service = new LogService();
        service.SetMinimumLevel(LogLevel.Info);
        IUnifiedLog log = service;
        var probe = new CountingValue();

        log.Write(LogLevel.Trace, "Handler", $"value {probe}");

        Assert.Equal(0, probe.Renders);
        Assert.Empty(service.Entries);
    }

    [Fact]
    public void Write_ThroughInterface_OnPassThroughTarget_KeepsTheText()
    {
        var target = new RecordingLog();
        IUnifiedLog log = target;

        log.Write(LogLevel.Debug, "Handler", $"value {41 + 1}");

        Assert.Equal("value 42", Assert.Single(target.Messages));
    }

    [Fact]
    public void Write_InterpolatedMessage_AppliesAlignmentAndFormatSpecifiers()
    {
        var service = new LogService();

        service.Write(LogLevel.Info, "Handler", $"[{3.14159:F2}|{7,3}]");

        Assert.Equal("[3.14|  7]", Assert.Single(service.Entries).Message);
    }

    [Fact]
    public void IsEnabled_WithoutFileTrail_ReportsOnlyLevelsAboveTheMinimum()
    {
        var service = new LogService();
        service.SetMinimumLevel(LogLevel.Info);

        Assert.True(service.IsEnabled(LogLevel.Info));
        Assert.False(service.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public async Task Write_InterpolatedBelowMinimum_StillReachesAnActiveFileTrail()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"uotan-handler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var service = new LogService();
        try
        {
            service.SetMinimumLevel(LogLevel.Info);
            service.ConfigureFileLogging(directory);
            var probe = new CountingValue();

            Assert.True(service.IsEnabled(LogLevel.Debug));
            service.Write(LogLevel.Debug, "Handler", $"value {probe}");
            service.FlushFileLog();
            service.ConfigureFileLogging(null);

            Assert.Equal(1, probe.Renders);
            Assert.Empty(service.Entries);
            var path = Assert.Single(Directory.GetFiles(directory, "UotanStudio-*.log"));
            var entries = await LogFileService.ReadAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(entries, entry => entry.Text.Contains("value probe", StringComparison.Ordinal));
        }
        finally
        {
            service.ConfigureFileLogging(null);
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CountingValue
    {
        public int Renders { get; private set; }

        public override string ToString()
        {
            Renders++;
            return "probe";
        }
    }

    private sealed class RecordingLog : IUnifiedLog
    {
        public List<string> Messages { get; } = [];

        public void Write(LogLevel level, string source, string message) => Messages.Add(message);

        public void Write(LogLevel level, string source, string format, params object[] args)
            => Messages.Add(LogService.FormatMessage(format, args));

        public void SetMinimumLevel(LogLevel level)
        {
        }
    }
}
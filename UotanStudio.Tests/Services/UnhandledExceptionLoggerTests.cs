using UotanStudio.Services;
using UotanStudio.Services.Logging;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Regression tests for <see cref="UnhandledExceptionLogger"/>: a null exception writes nothing, a written
/// exception becomes an Error-level <c>Exception</c>-source entry naming the origin with inner exceptions listed,
/// and oversized messages are truncated so one hostile exception cannot exhaust the retention budget.
/// <para><see cref="UnhandledExceptionLogger"/> 回归测试：null 异常不写出条目；写出的异常成为 Error 级别、来源为
/// <c>Exception</c> 并注明逃出位置且列出内层异常的条目；超长消息被截断，避免单个恶意异常耗尽保留预算。</para>
/// </summary>
[Collection("Log pipeline")]
public sealed class UnhandledExceptionLoggerTests : IDisposable
{
    private readonly List<LogEntry> _entries = [];
    private readonly Action _unsubscribe;

    public UnhandledExceptionLoggerTests()
    {
        LogService.Instance.SetMinimumLevel(LogLevel.Trace);
        _unsubscribe = LogService.Instance.Subscribe((_, entry) => _entries.Add(entry));
    }

    public void Dispose()
    {
        _unsubscribe();
        LogService.Instance.Clear();
    }

    private LogEntry? FindEntry(string contains)
        => _entries.FirstOrDefault(e => e.Source == "Exception" && e.Message.Contains(contains, StringComparison.Ordinal));

    [Fact]
    public void Write_NullException_WritesNoEntry()
    {
        UnhandledExceptionLogger.Write(null, "probe origin");

        Assert.DoesNotContain(_entries, e => e.Source == "Exception");
    }

    [Fact]
    public void Write_AggregateException_WritesErrorEntryWithInnerList()
    {
        var exception = new AggregateException("aggregate-probe",
            new InvalidOperationException("inner-one-probe"),
            new TimeoutException("inner-two-probe"));

        UnhandledExceptionLogger.Write(exception, "probe origin");

        var entry = FindEntry("aggregate-probe");
        Assert.NotNull(entry);
        Assert.Equal(LogLevel.Error, entry!.Level);
        Assert.Contains("Unhandled probe origin exception:", entry.Message, StringComparison.Ordinal);
        Assert.Contains("AggregateException: aggregate-probe", entry.Message, StringComparison.Ordinal);
        Assert.Contains("inner: InvalidOperationException: inner-one-probe", entry.Message, StringComparison.Ordinal);
        Assert.Contains("inner: TimeoutException: inner-two-probe", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_OversizedMessage_TruncatesLine()
    {
        var exception = new TimeoutException(new string('x', 10_000));

        UnhandledExceptionLogger.Write(exception, "probe origin");

        var entry = FindEntry("x");
        Assert.NotNull(entry);
        Assert.Contains("…(truncated)", entry!.Message, StringComparison.Ordinal);
        Assert.True(entry.Message.Length < 4000);
    }
}
using System.Net.Sockets;
using UotanStudio.Services;
using UotanStudio.Services.Logging;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Regression tests for <see cref="FirstChanceExceptionLogger"/>: the three normally-caught
/// exception types are captured as Debug-level log entries containing the call-path text;
/// short-window duplicates of the same key are throttled.
/// <para><see cref="FirstChanceExceptionLogger"/> 回归测试：捕获三类被正常捕获的异常为
/// Debug 级别日志条目，包含调用路径文本；同键短时间重复被节流。</para>
/// </summary>
[Collection("Log pipeline")]
public sealed class FirstChanceExceptionLoggerTests : IDisposable
{
    private readonly List<LogEntry> _entries = [];
    private readonly Action _unsubscribe;

    public FirstChanceExceptionLoggerTests()
    {
        LogService.Instance.SetMinimumLevel(LogLevel.Trace);
        FirstChanceExceptionLogger.Install();
        _unsubscribe = LogService.Instance.Subscribe((_, entry) => _entries.Add(entry));
    }

    public void Dispose()
    {
        _unsubscribe();
        FirstChanceExceptionLogger.Uninstall();
        LogService.Instance.Clear();
    }

    private LogEntry? FindEntry(string contains)
        => _entries.FirstOrDefault(e => e.Source == "Exception" && e.Message.Contains(contains, StringComparison.Ordinal));

    [Fact]
    public void CapturesSocketExceptionWithCallPath()
    {
        void Thrower() => throw new SocketException((int)SocketError.ConnectionRefused);

        try { Thrower(); }
        catch (SocketException) { }

        var entry = FindEntry("SocketException");
        Assert.NotNull(entry);
        Assert.Equal(LogLevel.Debug, entry!.Level);
        Assert.Contains(nameof(Thrower), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesOperationCanceledExceptionWithTokenState()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try { cts.Token.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) { }

        var entry = FindEntry("OperationCanceledException");
        Assert.NotNull(entry);
        Assert.Equal(LogLevel.Debug, entry!.Level);
        Assert.Contains("canceled=", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesTimeoutException()
    {
        try { throw new TimeoutException("ADB read timeout"); }
        catch (TimeoutException) { }

        var entry = FindEntry("ADB read timeout");
        Assert.NotNull(entry);
        Assert.Equal(LogLevel.Debug, entry!.Level);
        Assert.Contains("thrown at:", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatesWithinWindowAreThrottled()
    {
        for (var i = 0; i < 5; i++)
        {
            try { throw new TimeoutException("throttle-probe-unique-message"); }
            catch (TimeoutException) { }
        }

        var fullEntries = _entries.Where(e => e.Message.Contains("throttle-probe-unique-message", StringComparison.Ordinal)).ToList();
        var summaryEntries = fullEntries.Where(e => e.Message.Contains("repeated", StringComparison.Ordinal)).ToList();
        Assert.Equal(1, fullEntries.Count(e => !e.Message.Contains("repeated", StringComparison.Ordinal)));
        Assert.Empty(summaryEntries);
    }

    [Fact]
    public void InstallIsIdempotent()
    {
        FirstChanceExceptionLogger.Install();
        FirstChanceExceptionLogger.Install();

        try { throw new TimeoutException("idempotency-probe"); }
        catch (TimeoutException) { }

        Assert.Equal(1, _entries.Count(e => e.Message.Contains("idempotency-probe", StringComparison.Ordinal)));
    }
}

[CollectionDefinition("Log pipeline", DisableParallelization = true)]
public sealed class LogPipelineCollection;
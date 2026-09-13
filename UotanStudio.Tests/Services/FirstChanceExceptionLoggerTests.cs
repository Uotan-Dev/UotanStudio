using System.Net.Sockets;
using UotanStudio.Services;
using UotanStudio.Services.Logging;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// <para><see cref="FirstChanceExceptionLogger"/> 回归测试：捕获三类被正常捕获的异常为
/// Debug 级别日志条目，包含调用路径文本；同键短时间重复被节流。</para>
/// Regression tests for <see cref="FirstChanceExceptionLogger"/>: the three normally-caught
/// exception types are captured as Debug-level log entries containing the call-path text;
/// short-window duplicates of the same key are throttled.
/// </summary>
public sealed class FirstChanceExceptionLoggerTests : IDisposable
{
    private readonly List<LogEntry> _entries = [];
    private readonly Action _unsubscribe;

    public FirstChanceExceptionLoggerTests()
    {
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
        catch (SocketException) { /* 正常捕获，与 Comm 层探测路径一致 */ }

        var entry = FindEntry("SocketException");
        Assert.NotNull(entry);
        // 级别为 Debug；消息包含抛出点方法名，证明调用路径被记录。
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

        // 5 秒窗口内同键只落一条完整条目（重复计数延迟到窗口后首次出现时补记）。
        var fullEntries = _entries.Where(e => e.Message.Contains("throttle-probe-unique-message", StringComparison.Ordinal)).ToList();
        var summaryEntries = fullEntries.Where(e => e.Message.Contains("repeated", StringComparison.Ordinal)).ToList();
        Assert.Equal(1, fullEntries.Count(e => !e.Message.Contains("repeated", StringComparison.Ordinal)));
        Assert.Empty(summaryEntries);
    }

    [Fact]
    public void InstallIsIdempotent()
    {
        // 重复安装不抛异常且钩子仍只挂一次（重复条目数不翻倍）。
        FirstChanceExceptionLogger.Install();
        FirstChanceExceptionLogger.Install();

        try { throw new TimeoutException("idempotency-probe"); }
        catch (TimeoutException) { }

        Assert.Equal(1, _entries.Count(e => e.Message.Contains("idempotency-probe", StringComparison.Ordinal)));
    }
}

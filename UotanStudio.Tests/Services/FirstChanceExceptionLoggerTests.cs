using System.Net.Sockets;
using UotanStudio.Services;
using UotanStudio.Services.Logging;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Regression tests for <see cref="FirstChanceExceptionLogger"/>: the installed hook stays silent until
/// <c>UOTAN_FIRST_CHANCE_LOG=1</c> is set, the enabled hook writes Debug-level entries carrying the throw-site
/// call path, repeated installs hook only once, and duplicates inside one throttle window collapse into a later
/// summary line.
/// <para><see cref="FirstChanceExceptionLogger"/> 回归测试：已安装的钩子在设置
/// <c>UOTAN_FIRST_CHANCE_LOG=1</c> 前保持静默；启用后写出带抛出点调用路径的 Debug 级别条目；重复安装只挂接
/// 一次；同一节流窗口内的重复在窗口结束后折叠为一条汇总。</para>
/// </summary>
[Collection("Log pipeline")]
public sealed class FirstChanceExceptionLoggerTests : IDisposable
{
    private const string SwitchVariable = "UOTAN_FIRST_CHANCE_LOG";
    private const int ThrottleSettleMilliseconds = 5600;

    private readonly List<LogEntry> _entries = [];
    private readonly Action _unsubscribe;
    private readonly string? _ambientSwitch;

    public FirstChanceExceptionLoggerTests()
    {
        _ambientSwitch = Environment.GetEnvironmentVariable(SwitchVariable);
        LogService.Instance.SetMinimumLevel(LogLevel.Trace);
        FirstChanceExceptionLogger.Uninstall();
        Environment.SetEnvironmentVariable(SwitchVariable, null);
        _unsubscribe = LogService.Instance.Subscribe((_, entry) => _entries.Add(entry));
    }

    public void Dispose()
    {
        _unsubscribe();
        FirstChanceExceptionLogger.Uninstall();
        Environment.SetEnvironmentVariable(SwitchVariable, _ambientSwitch);
        LogService.Instance.Clear();
    }

    private LogEntry? FindEntry(string contains)
        => _entries.FirstOrDefault(e => e.Source == "Exception" && e.Message.Contains(contains, StringComparison.Ordinal));

    private List<LogEntry> FindEntries(string contains)
        => _entries.Where(e => e.Source == "Exception" && e.Message.Contains(contains, StringComparison.Ordinal)).ToList();

    [Fact]
    public void Hook_DefaultSwitch_WritesNoEntries()
    {
        FirstChanceExceptionLogger.Uninstall();

        try
        {
            FirstChanceExceptionLogger.Install();

            try { throw new SocketException((int)SocketError.ConnectionRefused); }
            catch (SocketException) { }

            try { throw new OperationCanceledException("default-switch-probe"); }
            catch (OperationCanceledException) { }

            try { throw new TimeoutException("default-switch-probe"); }
            catch (TimeoutException) { }

            Assert.Empty(FindEntries("default-switch-probe"));
            Assert.Empty(FindEntries("SocketException"));
            Assert.Empty(_entries.Where(e => e.Source == "Exception"));
        }
        finally
        {
            FirstChanceExceptionLogger.Uninstall();
            Environment.SetEnvironmentVariable(SwitchVariable, null);
        }
    }

    [Fact]
    public void Hook_SwitchEnabled_WritesThrowSiteEntry()
    {
        FirstChanceExceptionLogger.Uninstall();
        Environment.SetEnvironmentVariable(SwitchVariable, "1");

        try
        {
            FirstChanceExceptionLogger.Install();

            try { throw new TimeoutException("enabled-switch-probe"); }
            catch (TimeoutException) { }

            var entry = FindEntry("enabled-switch-probe");
            Assert.NotNull(entry);
            Assert.Equal(LogLevel.Debug, entry!.Level);
            Assert.Contains("TimeoutException: enabled-switch-probe", entry.Message, StringComparison.Ordinal);
            Assert.Contains("thrown at:", entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            FirstChanceExceptionLogger.Uninstall();
            Environment.SetEnvironmentVariable(SwitchVariable, null);
        }
    }

    [Fact]
    public void Hook_EnabledCancellation_WritesCanceledTokenState()
    {
        FirstChanceExceptionLogger.Uninstall();
        Environment.SetEnvironmentVariable(SwitchVariable, "1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            FirstChanceExceptionLogger.Install();

            try { throw new OperationCanceledException("canceled-token-probe", cancellation.Token); }
            catch (OperationCanceledException) { }

            var entry = FindEntry("canceled-token-probe");
            Assert.NotNull(entry);
            Assert.Equal(LogLevel.Debug, entry!.Level);
            Assert.Contains("OperationCanceledException", entry.Message, StringComparison.Ordinal);
            Assert.Contains("canceled=True", entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            FirstChanceExceptionLogger.Uninstall();
            Environment.SetEnvironmentVariable(SwitchVariable, null);
        }
    }

    [Fact]
    public void Install_CalledRepeatedlyWhileEnabled_WritesSingleEntry()
    {
        FirstChanceExceptionLogger.Uninstall();
        Environment.SetEnvironmentVariable(SwitchVariable, "1");

        try
        {
            FirstChanceExceptionLogger.Install();
            FirstChanceExceptionLogger.Install();
            FirstChanceExceptionLogger.Install();

            try { throw new TimeoutException("idempotent-install-probe"); }
            catch (TimeoutException) { }

            Assert.Equal(1, FindEntries("idempotent-install-probe").Count);
        }
        finally
        {
            FirstChanceExceptionLogger.Uninstall();
            Environment.SetEnvironmentVariable(SwitchVariable, null);
        }
    }

    [Fact]
    public void Install_SwitchChangedAfterFirstInstall_KeepsCachedSwitchState()
    {
        FirstChanceExceptionLogger.Uninstall();

        try
        {
            FirstChanceExceptionLogger.Install();

            Environment.SetEnvironmentVariable(SwitchVariable, "1");
            FirstChanceExceptionLogger.Install();

            try { throw new TimeoutException("switch-read-once-probe"); }
            catch (TimeoutException) { }

            Assert.Empty(FindEntries("switch-read-once-probe"));
        }
        finally
        {
            FirstChanceExceptionLogger.Uninstall();
            Environment.SetEnvironmentVariable(SwitchVariable, null);
        }
    }

    [Fact]
    public async Task Hook_DuplicatesAcrossThrottleWindow_WritesSummaryEntry()
    {
        FirstChanceExceptionLogger.Uninstall();
        Environment.SetEnvironmentVariable(SwitchVariable, "1");

        static void ThrowThrottleProbe()
        {
            throw new TimeoutException("throttle-summary-probe");
        }

        try
        {
            FirstChanceExceptionLogger.Install();

            for (var i = 0; i < 4; i++)
            {
                try { ThrowThrottleProbe(); }
                catch (TimeoutException) { }
            }

            var throttledEntries = FindEntries("throttle-summary-probe");
            Assert.Equal(1, throttledEntries.Count);
            Assert.Empty(throttledEntries.Where(e => e.Message.Contains("repeated", StringComparison.Ordinal)));

            await Task.Delay(ThrottleSettleMilliseconds, TestContext.Current.CancellationToken);

            try { ThrowThrottleProbe(); }
            catch (TimeoutException) { }

            var probeEntries = FindEntries("throttle-summary-probe");
            var fullEntries = probeEntries.Where(e => !e.Message.Contains("repeated", StringComparison.Ordinal)).ToList();
            var summaryEntries = probeEntries.Where(e => e.Message.Contains("repeated", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, fullEntries.Count);
            Assert.Equal(1, summaryEntries.Count);
            Assert.Contains("repeated 4 more times", summaryEntries[0].Message, StringComparison.Ordinal);
            Assert.Equal(LogLevel.Debug, summaryEntries[0].Level);
        }
        finally
        {
            FirstChanceExceptionLogger.Uninstall();
            Environment.SetEnvironmentVariable(SwitchVariable, null);
        }
    }
}

[CollectionDefinition("Log pipeline", DisableParallelization = true)]
public sealed class LogPipelineCollection;
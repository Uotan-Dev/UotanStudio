using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using UotanStudio.Services;
using UotanStudio.Services.Logging;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Regression tests for <see cref="FirstChanceExceptionLogger"/>: the installed hook captures device- and
/// transport-level exceptions (Win32, COM, I/O, socket, timeout) by default as Debug-level entries carrying the
/// throw-site call path and error codes, skips control-flow exceptions, widens capture to every type only with
/// <c>UOTAN_FIRST_CHANCE_LOG=1</c>, disables capture with <c>UOTAN_FIRST_CHANCE_LOG=0</c>, hooks once across
/// repeated installs with the switch read only at install, and collapses duplicates inside one throttle window
/// into a later summary line.
/// <para><see cref="FirstChanceExceptionLogger"/> 回归测试：已安装的钩子默认捕获设备与传输层异常
/// （Win32、COM、I/O、套接字、超时），写出带抛出点调用路径与错误码的 Debug 级别条目；跳过控制流异常；仅在
/// <c>UOTAN_FIRST_CHANCE_LOG=1</c> 时扩大捕获到全部类型；<c>UOTAN_FIRST_CHANCE_LOG=0</c> 关闭捕获；重复安装只
/// 挂接一次且开关仅在安装时读取；同一节流窗口内的重复在窗口结束后折叠为一条汇总。</para>
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
    public void Hook_DefaultSwitch_WritesDiagnosticExceptions()
    {
        FirstChanceExceptionLogger.Uninstall();

        try
        {
            FirstChanceExceptionLogger.Install();

            try { throw new Win32Exception(23, "crc-win32-probe"); }
            catch (Win32Exception) { }

            try { throw new IOException("crc-io-probe"); }
            catch (IOException) { }

            try { throw new TimeoutException("diagnostic-timeout-probe"); }
            catch (TimeoutException) { }

            try { throw new SocketException((int)SocketError.ConnectionRefused); }
            catch (SocketException) { }

            var win32 = FindEntry("crc-win32-probe");
            Assert.NotNull(win32);
            Assert.Equal(LogLevel.Debug, win32!.Level);
            Assert.Contains("Win32Exception", win32.Message, StringComparison.Ordinal);
            Assert.Contains("code=23", win32.Message, StringComparison.Ordinal);
            Assert.Contains("thrown at:", win32.Message, StringComparison.Ordinal);

            var io = FindEntry("crc-io-probe");
            Assert.NotNull(io);
            Assert.Contains("IOException", io!.Message, StringComparison.Ordinal);
            Assert.Contains("thrown at:", io.Message, StringComparison.Ordinal);

            Assert.NotNull(FindEntry("diagnostic-timeout-probe"));
            Assert.NotNull(FindEntry("SocketException"));
        }
        finally
        {
            FirstChanceExceptionLogger.Uninstall();
            Environment.SetEnvironmentVariable(SwitchVariable, null);
        }
    }

    [Fact]
    public void Hook_DefaultSwitch_SkipsControlFlowExceptions()
    {
        FirstChanceExceptionLogger.Uninstall();

        try
        {
            FirstChanceExceptionLogger.Install();

            try { throw new OperationCanceledException("control-flow-cancellation-probe"); }
            catch (OperationCanceledException) { }

            try { throw new InvalidOperationException("control-flow-invalid-probe"); }
            catch (InvalidOperationException) { }

            Assert.Empty(FindEntries("control-flow-cancellation-probe"));
            Assert.Empty(FindEntries("control-flow-invalid-probe"));
        }
        finally
        {
            FirstChanceExceptionLogger.Uninstall();
            Environment.SetEnvironmentVariable(SwitchVariable, null);
        }
    }

    [Fact]
    public void Hook_DefaultSwitch_AppendsInnerExceptionSummary()
    {
        FirstChanceExceptionLogger.Uninstall();

        try
        {
            FirstChanceExceptionLogger.Install();

            try { throw new IOException("io-outer-probe", new Win32Exception(23, "win32-inner-probe")); }
            catch (IOException) { }

            var entry = FindEntry("io-outer-probe");
            Assert.NotNull(entry);
            Assert.Contains("inner: Win32Exception: win32-inner-probe", entry!.Message, StringComparison.Ordinal);
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
    public void Hook_SwitchEnabled_WritesAllExceptionTypes()
    {
        FirstChanceExceptionLogger.Uninstall();
        Environment.SetEnvironmentVariable(SwitchVariable, "1");

        try
        {
            FirstChanceExceptionLogger.Install();

            try { throw new InvalidOperationException("all-types-probe"); }
            catch (InvalidOperationException) { }

            var entry = FindEntry("all-types-probe");
            Assert.NotNull(entry);
            Assert.Equal(LogLevel.Debug, entry!.Level);
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
    public void Hook_SwitchZero_WritesNoEntries()
    {
        FirstChanceExceptionLogger.Uninstall();
        Environment.SetEnvironmentVariable(SwitchVariable, "0");

        try
        {
            FirstChanceExceptionLogger.Install();

            try { throw new TimeoutException("zero-switch-probe"); }
            catch (TimeoutException) { }

            Assert.DoesNotContain(_entries, e => e.Source == "Exception");
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

            Assert.Single(FindEntries("idempotent-install-probe"));
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

            try { throw new OperationCanceledException("switch-read-once-probe"); }
            catch (OperationCanceledException) { }

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
            Assert.Single(throttledEntries);
            Assert.DoesNotContain(throttledEntries, e => e.Message.Contains("repeated", StringComparison.Ordinal));

            await Task.Delay(ThrottleSettleMilliseconds, TestContext.Current.CancellationToken);

            try { ThrowThrottleProbe(); }
            catch (TimeoutException) { }

            var probeEntries = FindEntries("throttle-summary-probe");
            var fullEntries = probeEntries.Where(e => !e.Message.Contains("repeated", StringComparison.Ordinal)).ToList();
            var summaryEntries = probeEntries.Where(e => e.Message.Contains("repeated", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, fullEntries.Count);
            Assert.Single(summaryEntries);
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
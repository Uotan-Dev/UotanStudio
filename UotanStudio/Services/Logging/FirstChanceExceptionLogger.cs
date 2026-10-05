using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace UotanStudio.Services.Logging;

/// <summary>
/// <para>首次机会异常记录器：挂接 <see cref="AppDomain.FirstChanceException"/>，把调试器输出窗中
/// 「引发的异常」提示捕获为全局日志条目，包含异常抛出点的调用路径。</para>
/// First-chance exception logger: hooks <see cref="AppDomain.FirstChanceException"/> and captures
/// the debugger-output "exception thrown" notices as global log entries, including the call
/// path where the exception was thrown.
/// </summary>
/// <remarks>
/// <para>只处理设备通信路径常见且被正常捕获的三类异常（<see cref="SocketException"/>、
/// <see cref="OperationCanceledException"/>、<see cref="TimeoutException"/>）；其余异常类型
/// 直接放行，避免全局开销。写入级别为 <see cref="LogLevel.Debug"/>（来源 <c>Exception</c>），
/// 开启设置页「调试模式」后在日志窗口可见。高频重复异常按「类型 + 消息 + 顶层帧」节流：
/// 同一键在 5 秒窗口内只记录一条，之后的重复合并为一条「重复 N 次」摘要。</para>
/// <para>Only the three exception types commonly thrown (and normally caught) on device
/// communication paths are handled (<see cref="SocketException"/>,
/// <see cref="OperationCanceledException"/>, <see cref="TimeoutException"/>); all other types
/// pass through untouched to keep global overhead near zero. Entries are written at
/// <see cref="LogLevel.Debug"/> with source <c>Exception</c> and become visible in the log
/// window when the settings-page debug mode is on. High-frequency duplicates are throttled by
/// "type + message + top frame": one entry per key within a 5-second window, then a single
/// "repeated N times" summary.</para>
/// </remarks>
public static class FirstChanceExceptionLogger
{
    private const string Source = "Exception";
    private const int ThrottleWindowMs = 5000;
    private const int MaxStackFrames = 10;
    private const int MaxThrottledKeys = 256;

    private static volatile bool _installed;
    private static readonly Lock InstallLock = new();
    private static readonly Lock ThrottleLock = new();
    private static readonly Dictionary<string, (DateTime WindowStart, int Count)> Throttle = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static bool _reentrant;

    /// <summary>
    /// <para>安装首次机会异常钩子。幂等；重复调用不会重复挂接。</para>
    /// Installs the first-chance exception hook. Idempotent; repeated calls do not re-hook.
    /// </summary>
    public static void Install()
    {
        lock (InstallLock)
        {
            if (_installed) return;
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            _installed = true;
        }
    }

    /// <summary>
    /// <para>卸载首次机会异常钩子（供测试使用）。</para>
    /// Uninstalls the first-chance exception hook (for tests).
    /// </summary>
    public static void Uninstall()
    {
        lock (InstallLock)
        {
            if (!_installed) return;
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            _installed = false;
        }
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        // 防递归：日志管线自身抛出的异常不再进入本处理器。
        // Re-entrancy guard: exceptions thrown by the logging pipeline re-enter nothing.
        if (_reentrant) return;

        var exception = e.Exception;
        if (exception is not (SocketException or OperationCanceledException or TimeoutException)) return;

        _reentrant = true;
        try
        {
            WriteThrottled(exception);
        }
        catch
        {
            // 绝不让诊断日志破坏原始异常传播。 Never let diagnostics break exception flow.
        }
        finally
        {
            _reentrant = false;
        }
    }

    private static void WriteThrottled(Exception exception)
    {
        var topFrame = DescribeTopFrame(exception);
        var key = string.Concat(exception.GetType().Name, "|", exception.Message, "|", topFrame);

        int repeats;
        lock (ThrottleLock)
        {
            var now = DateTime.UtcNow;
            if (Throttle.TryGetValue(key, out var hit) && (now - hit.WindowStart).TotalMilliseconds < ThrottleWindowMs)
            {
                Throttle[key] = (hit.WindowStart, hit.Count + 1);
                return;
            }

            repeats = hit.Count;
            if (Throttle.Count > MaxThrottledKeys)
            {
                Throttle.Clear();
            }
            Throttle[key] = (now, 1);
        }

        // 窗口结束后再次出现时，先补一条重复计数摘要。
        // When the key reappears after the window, first flush the repeat summary.
        if (repeats > 0)
        {
            LogService.Instance.Write(LogLevel.Debug, Source,
                $"{exception.GetType().Name}: {exception.Message} (repeated {repeats} more times within {ThrottleWindowMs / 1000.0:0.#}s)");
        }

        LogService.Instance.Write(LogLevel.Debug, Source, Format(exception, topFrame));
    }

    private static string Format(Exception exception, string topFrame)
    {
        var text = new System.Text.StringBuilder();
        text.Append(exception.GetType().Name)
            .Append(": ").Append(exception.Message)
            .Append(" | thrown at: ").Append(topFrame);

        // OperationCanceledException 额外标注取消令牌状态，区分正常取消与超时取消。
        // Annotate OperationCanceledException with the token state to tell normal
        // cancellation from timeout-driven cancellation.
        if (exception is OperationCanceledException oce)
        {
            text.Append(" | canceled=").Append(oce.CancellationToken.IsCancellationRequested);
        }

        var stack = CaptureStack(exception);
        if (stack.Length > 0)
        {
            text.Append(" | path: ").Append(stack);
        }
        return text.ToString();
    }

    /// <summary>
    /// <para>捕获异常抛出点的调用路径（Debug 构建含文件与行号），最多
    /// <see cref="MaxStackFrames"/> 帧；跳过 Async/Task 基础设施帧。</para>
    /// Captures the call path at the throw site (file and line in Debug builds), up to
    /// <see cref="MaxStackFrames"/> frames; async/task infrastructure frames are skipped.
    /// </summary>
    private static string CaptureStack(Exception exception)
    {
        try
        {
            var lines = exception.StackTrace?
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !IsInfrastructureLine(line))
                .Take(MaxStackFrames)
                .ToArray();
            return lines is { Length: > 0 } ? string.Join(" <- ", lines) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsInfrastructureLine(string line)
    {
        return line.Contains("System.Threading.Tasks.Task", StringComparison.Ordinal)
            || line.Contains("System.Runtime.CompilerServices.", StringComparison.Ordinal)
            || line.Contains("System.Threading.ExecutionContext", StringComparison.Ordinal)
            || line.Contains("System.Threading.ThreadPoolWorkQueue", StringComparison.Ordinal);
    }

    private static string DescribeTopFrame(Exception exception)
    {
        try
        {
            var line = exception.StackTrace?
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => !IsInfrastructureLine(line));
            return string.IsNullOrWhiteSpace(line) ? "unknown" : line;
        }
        catch
        {
            return "unknown";
        }
    }
}
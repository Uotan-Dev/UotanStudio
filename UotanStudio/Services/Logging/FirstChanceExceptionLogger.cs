using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace UotanStudio.Services.Logging;

/// <summary>
/// First-chance exception logger: hooks <see cref="AppDomain.FirstChanceException"/> and captures
/// the debugger-output "exception thrown" notices as global log entries, including the call
/// path where the exception was thrown.
/// <para>首次机会异常记录器：挂接 <see cref="AppDomain.FirstChanceException"/>，把调试器输出窗中
/// 「引发的异常」提示捕获为全局日志条目，包含异常抛出点的调用路径。</para>
/// </summary>
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
    /// Installs the first-chance exception hook. Idempotent; repeated calls do not re-hook.
    /// <para>安装首次机会异常钩子。幂等；重复调用不会重复挂接。</para>
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
    /// Uninstalls the first-chance exception hook.
    /// <para>卸载首次机会异常钩子。</para>
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
    /// Captures the call path at the throw site (file and line in Debug builds), up to
    /// <see cref="MaxStackFrames"/> frames; async/task infrastructure frames are skipped.
    /// <para>捕获异常抛出点的调用路径（Debug 构建含文件与行号），最多
    /// <see cref="MaxStackFrames"/> 帧；跳过 Async/Task 基础设施帧。</para>
    /// </summary>
    /// <param name="exception">The exception whose stack text is read. <para>待读取堆栈文本的异常。</para></param>
    /// <returns>The joined call path, or an empty string when no usable frame remains.
    /// <para>拼接后的调用路径；无可用帧时为空字符串。</para></returns>
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
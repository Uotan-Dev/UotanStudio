using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace UotanStudio.Services.Logging;

/// <summary>
/// First-chance exception logger: hooks <see cref="AppDomain.FirstChanceException"/> and, only when
/// <c>UOTAN_FIRST_CHANCE_LOG=1</c> is set, records the normally-caught socket, cancellation and timeout
/// exceptions as throttled Debug-level global log entries that carry the throw-site call path. The hook is
/// installed by default and stays silent until the switch is set.
/// <para>首次机会异常记录器：挂接 <see cref="AppDomain.FirstChanceException"/>；仅当设置了
/// <c>UOTAN_FIRST_CHANCE_LOG=1</c> 时，才把被正常捕获的套接字、取消与超时异常记录为带抛出点调用路径的
/// Debug 级别全局日志条目，并做节流。钩子默认安装，在设置该开关前保持静默。</para>
/// </summary>
public static class FirstChanceExceptionLogger
{
    private const string EnableVariable = "UOTAN_FIRST_CHANCE_LOG";
    private const string Source = "Exception";
    private const int ThrottleWindowMs = 5000;
    private const int MaxStackFrames = 10;
    private const int MaxThrottledKeys = 256;

    private static volatile bool _installed;
    private static volatile bool _logging;
    private static readonly Lock InstallLock = new();
    private static readonly Lock ThrottleLock = new();
    private static readonly Dictionary<string, (DateTime WindowStart, int Count)> Throttle = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static bool _reentrant;

    /// <summary>
    /// Installs the first-chance exception hook and reads the <c>UOTAN_FIRST_CHANCE_LOG</c> switch once.
    /// Idempotent; repeated calls while installed neither re-hook nor re-read the switch.
    /// <para>安装首次机会异常钩子并读取一次 <c>UOTAN_FIRST_CHANCE_LOG</c> 开关。幂等；已安装时重复调用既不
    /// 重复挂接，也不重新读取开关。</para>
    /// </summary>
    public static void Install()
    {
        lock (InstallLock)
        {
            if (_installed)
            {
                return;
            }

            _logging = IsLoggingRequested();
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            _installed = true;
        }
    }

    /// <summary>
    /// Uninstalls the first-chance exception hook and clears the switch cached by <see cref="Install"/>.
    /// <para>卸载首次机会异常钩子，并清除 <see cref="Install"/> 缓存的开关值。</para>
    /// </summary>
    public static void Uninstall()
    {
        lock (InstallLock)
        {
            if (!_installed)
            {
                return;
            }

            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            _installed = false;
            _logging = false;
        }
    }

    /// <summary>
    /// Reads the opt-in switch: only the literal <c>1</c> enables first-chance logging.
    /// <para>读取显式开关：只有字面量 <c>1</c> 才启用首次机会异常日志。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the switch requests logging.
    /// <para>开关要求写日志时为 <see langword="true"/>。</para></returns>
    private static bool IsLoggingRequested()
    {
        return string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal);
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (_reentrant || !_logging)
        {
            return;
        }

        var exception = e.Exception;
        if (exception is not (SocketException or OperationCanceledException or TimeoutException))
        {
            return;
        }

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
using System.IO;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace UotanStudio.Services.Logging;

/// <summary>
/// First-chance exception logger: hooks <see cref="AppDomain.FirstChanceException"/> and records caught
/// device- and transport-level exceptions (Win32, COM, I/O, socket, timeout) as throttled Debug-level global log
/// entries carrying the throw-site call path, so failures swallowed inside subsystems stay diagnosable.
/// Setting <c>UOTAN_FIRST_CHANCE_LOG=1</c> widens capture to every exception type and
/// <c>UOTAN_FIRST_CHANCE_LOG=0</c> disables capture entirely; the hook is installed by default and the switch is
/// read once at install.
/// <para>首次机会异常记录器：挂接 <see cref="AppDomain.FirstChanceException"/>，把被捕获的设备与传输层异常
/// （Win32、COM、I/O、套接字、超时）记录为带抛出点调用路径、按窗口节流的 Debug 级别全局日志条目，使子系统内部
/// 被吞掉的故障仍可诊断。设置 <c>UOTAN_FIRST_CHANCE_LOG=1</c> 把捕获范围扩大到全部异常类型，
/// <c>UOTAN_FIRST_CHANCE_LOG=0</c> 完全关闭捕获；钩子默认安装，开关在安装时读取一次。</para>
/// </summary>
public static class FirstChanceExceptionLogger
{
    private const string EnableVariable = "UOTAN_FIRST_CHANCE_LOG";
    private const string Source = "Exception";
    private const int ThrottleWindowMs = 5000;
    private const int MaxStackFrames = ExceptionLogText.FirstChanceStackFrames;
    private const int MaxThrottledKeys = 256;

    /// <summary>Capture coverage read once at install. <para>安装时读取一次的捕获范围。</para></summary>
    private enum CaptureMode
    {
        /// <summary>No entries are written. <para>不写出任何条目。</para></summary>
        Off,

        /// <summary>Device- and transport-level exception types are written.
        /// <para>写出设备与传输层异常类型。</para></summary>
        Diagnostic,

        /// <summary>Every exception type is written. <para>写出全部异常类型。</para></summary>
        All,
    }

    private static volatile bool _installed;
    private static volatile CaptureMode _mode;
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

            _mode = ReadMode();
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
            _mode = CaptureMode.Off;
        }
    }

    /// <summary>
    /// Reads the capture switch: <c>1</c> captures every exception type, <c>0</c> disables capture and any other
    /// value selects the default device- and transport-level set.
    /// <para>读取捕获开关：<c>1</c> 捕获全部异常类型，<c>0</c> 关闭捕获，其他取值选择默认的设备与传输层集合。</para>
    /// </summary>
    /// <returns>The mode requested by the switch. <para>开关要求的捕获模式。</para></returns>
    private static CaptureMode ReadMode()
    {
        var value = Environment.GetEnvironmentVariable(EnableVariable);
        if (string.Equals(value, "0", StringComparison.Ordinal))
        {
            return CaptureMode.Off;
        }

        return string.Equals(value, "1", StringComparison.Ordinal) ? CaptureMode.All : CaptureMode.Diagnostic;
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (_reentrant || _mode == CaptureMode.Off)
        {
            return;
        }

        var exception = e.Exception;
        if (_mode == CaptureMode.Diagnostic && exception is not (ExternalException or IOException or TimeoutException))
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
        // Guards the stack walk, the throttle key and the rendered stack below: none of them can be deferred by
        // the interpolated handler because the text is assembled before the call.
        if (!LogService.Instance.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var topFrame = ExceptionLogText.DescribeTopFrame(exception);
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

        var text = ExceptionLogText.Format(exception, MaxStackFrames);
        if (exception is OperationCanceledException canceled)
        {
            text = text.Insert(0, $"[canceled={canceled.CancellationToken.IsCancellationRequested}] ");
        }

        LogService.Instance.Write(LogLevel.Debug, Source, text);
    }
}
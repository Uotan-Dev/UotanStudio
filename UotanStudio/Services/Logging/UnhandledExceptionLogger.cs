using Avalonia.Threading;

namespace UotanStudio.Services.Logging;

/// <summary>
/// Unhandled exception logger: records exceptions that escape the UI dispatcher, the AppDomain and the task
/// scheduler as Error-level global log entries carrying the type, message, error codes, inner exceptions and the
/// call path, so a crash that takes down a window or an operation leaves its cause in the global log.
/// <para>未处理异常记录器：把逃出 UI 调度器、AppDomain 与任务调度器的异常记录为携带类型、消息、错误码、内层异常
/// 与调用路径的 Error 级别全局日志条目，使拖垮窗口或操作的崩溃原因留在全局日志中。</para>
/// </summary>
public static class UnhandledExceptionLogger
{
    private const string Source = "Exception";
    private const int MaxStackFrames = ExceptionLogText.UnhandledStackFrames;
    private const int MaxMessageLength = 2048;

    private static volatile bool _installed;
    private static readonly Lock InstallLock = new();

    /// <summary>
    /// Installs the UI dispatcher, AppDomain and task-scheduler unhandled exception hooks. Idempotent; repeated
    /// calls while installed neither re-hook nor duplicate entries.
    /// <para>安装 UI 调度器、AppDomain 与任务调度器的未处理异常钩子。幂等；已安装时重复调用既不重复挂接，
    /// 也不会写出重复条目。</para>
    /// </summary>
    public static void Install()
    {
        lock (InstallLock)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            Dispatcher.UIThread.UnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }
    }

    /// <summary>
    /// Removes the hooks installed by <see cref="Install"/>.
    /// <para>移除 <see cref="Install"/> 安装的钩子。</para>
    /// </summary>
    public static void Uninstall()
    {
        lock (InstallLock)
        {
            if (!_installed)
            {
                return;
            }

            _installed = false;
            Dispatcher.UIThread.UnhandledException -= OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        }
    }

    private static void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        => Write(e.Exception, "ui dispatcher");

    private static void OnAppDomainException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Write(exception, e.IsTerminating ? "terminating appdomain" : "appdomain");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Write(e.Exception, "unobserved task");
        e.SetObserved();
    }

    /// <summary>
    /// Writes one unhandled exception as an Error-level global log entry; oversized messages are truncated so a
    /// hostile exception cannot exhaust the retention budget with one line.
    /// <para>把一个未处理异常写成 Error 级别全局日志条目；超长消息被截断，避免恶意异常用一行耗尽保留预算。</para>
    /// </summary>
    /// <param name="exception">The unhandled exception. <para>未处理异常。</para></param>
    /// <param name="origin">Where the exception escaped, such as <c>ui dispatcher</c>.
    /// <para>异常逃出的位置，例如 <c>ui dispatcher</c>。</para></param>
    internal static void Write(Exception? exception, string origin)
    {
        if (exception is null)
        {
            return;
        }

        var rendered = ExceptionLogText.Format(exception, MaxStackFrames);
        if (rendered.Length > MaxMessageLength)
        {
            rendered = string.Concat(rendered.AsSpan(0, MaxMessageLength), " …(truncated)");
        }

        LogService.Instance.Write(LogLevel.Error, Source, $"Unhandled {origin} exception: {rendered}");
    }
}
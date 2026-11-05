using System.Runtime.InteropServices;

namespace UotanStudio.Services.Logging;

/// <summary>
/// Shared renderer for exception log lines: type, message, error codes, inner exceptions and a bounded
/// throw-site call path.
/// <para>异常日志行的共享渲染器：类型、消息、错误码、内层异常与有界的抛出点调用路径。</para>
/// </summary>
internal static class ExceptionLogText
{
    /// <summary>Stack-frame budget for first-chance (caught) exception entries.
    /// <para>首次机会（被捕获）异常条目的堆栈帧预算。</para></summary>
    internal const int FirstChanceStackFrames = 10;

    /// <summary>Stack-frame budget for unhandled exception entries.
    /// <para>未处理异常条目的堆栈帧预算。</para></summary>
    internal const int UnhandledStackFrames = 16;

    /// <summary>Maximum inner exceptions listed for one entry; deeper chains still reach the log through their
    /// own first-chance entries.
    /// <para>单条条目最多列出的内层异常数；更深的链路仍会经各自的首次机会条目进入日志。</para></summary>
    private const int MaxInnerExceptions = 5;

    /// <summary>
    /// Renders one exception as a single-line diagnostic: type and message, the HResult (plus the native or COM
    /// error code for external exceptions), the listed inner exceptions, the throw site and the filtered call
    /// path with file and line numbers in Debug builds.
    /// <para>把一个异常渲染为单行诊断：类型与消息、HResult（外部异常附带本机或 COM 错误码）、列出的内层异常、
    /// 抛出点以及过滤后的调用路径（Debug 构建含文件与行号）。</para>
    /// </summary>
    /// <param name="exception">The exception to render. <para>待渲染的异常。</para></param>
    /// <param name="maxStackFrames">Upper bound on listed call-path frames. <para>调用路径的帧数上限。</para></param>
    /// <returns>The rendered single-line text. <para>渲染后的单行文本。</para></returns>
    internal static string Format(Exception exception, int maxStackFrames)
    {
        var text = new System.Text.StringBuilder();
        text.Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        AppendErrorCodes(text, exception);
        AppendInnerExceptions(text, exception);

        var topFrame = DescribeTopFrame(exception);
        if (!string.Equals(topFrame, "unknown", StringComparison.Ordinal))
        {
            text.Append(" | thrown at: ").Append(topFrame);
        }

        var stack = CaptureStack(exception, maxStackFrames);
        if (stack.Length > 0)
        {
            text.Append(" | path: ").Append(stack);
        }

        return text.ToString();
    }

    /// <summary>
    /// Appends the HResult and, for external (Win32 or COM) exceptions, the native error code, so CRC and device
    /// errors are identifiable by number without resolving the localized message.
    /// <para>追加 HResult；外部（Win32 或 COM）异常再附本机错误码，使 CRC 与设备错误无需解析本地化消息即可
    /// 按编号识别。</para>
    /// </summary>
    /// <param name="text">The line under construction. <para>构建中的文本。</para></param>
    /// <param name="exception">The exception being rendered. <para>正在渲染的异常。</para></param>
    private static void AppendErrorCodes(System.Text.StringBuilder text, Exception exception)
    {
        if (exception.HResult != 0)
        {
            text.Append(" | hr=0x").Append(exception.HResult.ToString("X8"));
        }

        if (exception is System.ComponentModel.Win32Exception win32)
        {
            text.Append(" code=").Append(win32.NativeErrorCode);
        }
        else if (exception is ExternalException external)
        {
            text.Append(" code=").Append(external.ErrorCode);
        }
    }

    /// <summary>
    /// Appends the inner exception summaries: the full inner chain for aggregates, otherwise the single
    /// <see cref="Exception.InnerException"/>.
    /// <para>追加内层异常摘要：聚合异常列出完整内层链，否则列单个 <see cref="Exception.InnerException"/>。</para>
    /// </summary>
    /// <param name="text">The line under construction. <para>构建中的文本。</para></param>
    /// <param name="exception">The exception being rendered. <para>正在渲染的异常。</para></param>
    private static void AppendInnerExceptions(System.Text.StringBuilder text, Exception exception)
    {
        var inner = exception is AggregateException aggregate
            ? aggregate.InnerExceptions.Cast<Exception>().Take(MaxInnerExceptions)
            : exception.InnerException is { } single ? [single] : [];
        foreach (var item in inner)
        {
            text.Append(" | inner: ").Append(item.GetType().Name).Append(": ").Append(item.Message);
        }
    }

    /// <summary>
    /// Captures the call path at the throw site (file and line in Debug builds), up to
    /// <paramref name="maxStackFrames"/> frames; async/task infrastructure frames are skipped.
    /// <para>捕获异常抛出点的调用路径（Debug 构建含文件与行号），最多
    /// <paramref name="maxStackFrames"/> 帧；跳过 Async/Task 基础设施帧。</para>
    /// </summary>
    /// <param name="exception">The exception whose stack text is read. <para>待读取堆栈文本的异常。</para></param>
    /// <param name="maxStackFrames">Upper bound on listed frames. <para>列出的帧数上限。</para></param>
    /// <returns>The joined call path, or an empty string when no usable frame remains.
    /// <para>拼接后的调用路径；无可用帧时为空字符串。</para></returns>
    internal static string CaptureStack(Exception exception, int maxStackFrames)
    {
        try
        {
            var lines = exception.StackTrace?
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsUserFrame)
                .Take(maxStackFrames)
                .ToArray();
            return lines is { Length: > 0 } ? string.Join(" <- ", lines) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Describes the first non-infrastructure stack line, or <c>unknown</c> when none remains.
    /// <para>描述第一条非基础设施堆栈行；没有剩余行时为 <c>unknown</c>。</para>
    /// </summary>
    /// <param name="exception">The exception whose stack text is read. <para>待读取堆栈文本的异常。</para></param>
    /// <returns>The top frame text, or <c>unknown</c>. <para>栈顶帧文本；无可用帧时为 <c>unknown</c>。</para></returns>
    internal static string DescribeTopFrame(Exception exception)
    {
        try
        {
            var line = exception.StackTrace?
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(IsUserFrame);
            return string.IsNullOrWhiteSpace(line) ? "unknown" : line;
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool IsUserFrame(string line)
        => !line.Contains("System.Threading.Tasks.Task", StringComparison.Ordinal)
            && !line.Contains("System.Runtime.CompilerServices.", StringComparison.Ordinal)
            && !line.Contains("System.Threading.ExecutionContext", StringComparison.Ordinal)
            && !line.Contains("System.Threading.ThreadPoolWorkQueue", StringComparison.Ordinal);
}
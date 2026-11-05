namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// One-line summary of a caught exception for diagnostics messages: type, message and the throw-site frame, so a
/// swallowed failure keeps its call point next to the reason.
/// <para>被捕获异常的单行诊断摘要：类型、消息与抛出点帧，使被吞掉的失败在原因旁保留调用点。</para>
/// </summary>
internal static class ExceptionSummary
{
    /// <summary>
    /// Renders the exception type, its message and the first user frame of the throw-time stack.
    /// <para>渲染异常类型、消息以及抛出时堆栈的第一条用户帧。</para>
    /// </summary>
    /// <param name="exception">The caught exception to summarize. <para>待摘要的已捕获异常。</para></param>
    /// <returns>Text such as <c>SocketException: refused | at: frame</c>; the frame part is omitted when no usable
    /// frame remains. <para>形如 <c>SocketException: refused | at: frame</c> 的文本；无可用帧时省略帧部分。</para></returns>
    internal static string Describe(Exception exception)
    {
        var text = string.Concat(exception.GetType().Name, ": ", exception.Message);
        var topFrame = DescribeTopFrame(exception);
        return topFrame.Length == 0 ? text : string.Concat(text, " | at: ", topFrame);
    }

    private static string DescribeTopFrame(Exception exception)
    {
        try
        {
            var line = exception.StackTrace?
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(IsUserFrame);
            return string.IsNullOrWhiteSpace(line) ? string.Empty : line;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsUserFrame(string line)
        => !line.Contains("System.Threading.Tasks.Task", StringComparison.Ordinal)
            && !line.Contains("System.Runtime.CompilerServices.", StringComparison.Ordinal)
            && !line.Contains("System.Threading.ExecutionContext", StringComparison.Ordinal)
            && !line.Contains("System.Threading.ThreadPoolWorkQueue", StringComparison.Ordinal);
}
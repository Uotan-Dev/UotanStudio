namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Writes Comm diagnostics to the error stream when the host installs no sink.
/// <para>宿主未安装汇时，把 Comm 诊断写入错误输出流。</para>
/// </summary>
internal sealed class ConsoleCommLogger : ICommLogger
{
    /// <summary>Writes one diagnostic line. <para>写入一行诊断。</para></summary>
    /// <param name="level">Message severity. <para>消息严重级别。</para></param>
    /// <param name="category">Stable category. <para>稳定分类。</para></param>
    /// <param name="message">Rendered message text. <para>已渲染的消息文本。</para></param>
    public void Log(CommLogLevel level, string category, string message)
        => Console.Error.WriteLine($"[{level}] [COMM] {category}: {message}");
}
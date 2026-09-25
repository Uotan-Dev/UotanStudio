namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Writes USB diagnostics to the error stream.
/// <para>将 USB 诊断日志写入错误输出流。</para>
/// </summary>
internal sealed class ConsoleUsbLogger : IUsbLogger
{
    /// <summary>
    /// Writes a diagnostic message to stderr.
    /// <para>将诊断日志写入标准错误输出。</para>
    /// </summary>
    /// <param name="message">The diagnostic message. <para>诊断日志消息内容。</para></param>
    public void Log(string message) => Console.Error.WriteLine($"[USB] {message}");
}
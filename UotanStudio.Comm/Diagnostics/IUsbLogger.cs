namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Receives USB diagnostic log messages.
/// <para>接收 USB 诊断日志消息。</para>
/// </summary>
public interface IUsbLogger
{
    /// <summary>
    /// Writes a diagnostic message.
    /// <para>写入一条诊断日志消息。</para>
    /// </summary>
    /// <param name="message">The diagnostic message. <para>诊断日志消息内容。</para></param>
    void Log(string message);
}
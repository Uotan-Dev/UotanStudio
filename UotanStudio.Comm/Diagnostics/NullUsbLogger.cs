namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Ignores USB diagnostic log messages.
/// <para>忽略 USB 诊断日志消息。</para>
/// </summary>
public sealed class NullUsbLogger : IUsbLogger
{
    /// <summary>
    /// Gets the shared instance.
    /// <para>获取共享实例。</para>
    /// </summary>
    public static readonly NullUsbLogger Instance = new();

    private NullUsbLogger()
    {
    }

    /// <summary>
    /// Ignores the diagnostic message.
    /// <para>忽略该诊断日志消息。</para>
    /// </summary>
    /// <param name="message">The diagnostic message. <para>诊断日志消息内容。</para></param>
    public void Log(string message)
    {
    }
}
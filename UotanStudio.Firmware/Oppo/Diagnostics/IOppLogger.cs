namespace UotanStudio.Firmware.Oppo.Diagnostics;

public interface IOppLogger
{
    /// <summary>
    /// Logs a message at the trace level.
    /// <para>记录跟踪级别的消息。</para>
    /// </summary>
    void Trace(string message);

    /// <summary>
    /// Logs a message at the debug level.
    /// <para>记录调试级别的消息。</para>
    /// </summary>
    void Debug(string message);

    /// <summary>
    /// Logs a message at the information level.
    /// <para>记录信息级别的消息。</para>
    /// </summary>
    void Information(string message);

    /// <summary>
    /// Logs a message at the warning level.
    /// <para>记录警告级别的消息。</para>
    /// </summary>
    void Warning(string message);

    /// <summary>
    /// Logs a message at the warning level along with the associated exception.
    /// <para>记录警告级别的消息及关联的异常信息。</para>
    /// </summary>
    void Warning(string message, Exception exception);

    /// <summary>
    /// Logs a message at the error level.
    /// <para>记录错误级别的消息。</para>
    /// </summary>
    void Error(string message);

    /// <summary>
    /// Logs a message at the error level along with the associated exception.
    /// <para>记录错误级别的消息及关联的异常信息。</para>
    /// </summary>
    void Error(string message, Exception exception);
}

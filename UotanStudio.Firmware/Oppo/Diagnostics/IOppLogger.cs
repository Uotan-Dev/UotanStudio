namespace UotanStudio.Firmware.Oppo.Diagnostics;

/// <summary>
/// <para>定义 OPPO 固件处理库的日志记录接口。</para>
/// Defines the logging interface for the OPPO firmware processing library.
/// </summary>
public interface IOppLogger
{
    /// <summary>
    /// <para>记录跟踪级别的消息。</para>
    /// Logs a message at the trace level.
    /// </summary>
    /// <param name="message"><para>要记录的消息。</para>The message to log.</param>
    void Trace(string message);

    /// <summary>
    /// <para>记录调试级别的消息。</para>
    /// Logs a message at the debug level.
    /// </summary>
    /// <param name="message"><para>要记录的消息。</para>The message to log.</param>
    void Debug(string message);

    /// <summary>
    /// <para>记录信息级别的消息。</para>
    /// Logs a message at the information level.
    /// </summary>
    /// <param name="message"><para>要记录的消息。</para>The message to log.</param>
    void Information(string message);

    /// <summary>
    /// <para>记录警告级别的消息。</para>
    /// Logs a message at the warning level.
    /// </summary>
    /// <param name="message"><para>要记录的消息。</para>The message to log.</param>
    void Warning(string message);

    /// <summary>
    /// <para>记录警告级别的消息及关联的异常信息。</para>
    /// Logs a message at the warning level along with the associated exception.
    /// </summary>
    /// <param name="message"><para>要记录的消息。</para>The message to log.</param>
    /// <param name="exception"><para>与警告关联的异常。</para>The exception associated with the warning.</param>
    void Warning(string message, Exception exception);

    /// <summary>
    /// <para>记录错误级别的消息。</para>
    /// Logs a message at the error level.
    /// </summary>
    /// <param name="message"><para>要记录的消息。</para>The message to log.</param>
    void Error(string message);

    /// <summary>
    /// <para>记录错误级别的消息及关联的异常信息。</para>
    /// Logs a message at the error level along with the associated exception.
    /// </summary>
    /// <param name="message"><para>要记录的消息。</para>The message to log.</param>
    /// <param name="exception"><para>与错误关联的异常。</para>The exception associated with the error.</param>
    void Error(string message, Exception exception);
}
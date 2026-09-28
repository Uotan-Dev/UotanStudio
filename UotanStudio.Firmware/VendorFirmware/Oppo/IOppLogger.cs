namespace UotanStudio.Firmware.VendorFirmware.Oppo;

/// <summary>
/// Destination for diagnostic messages emitted while processing OPPO firmware files.
/// <para>处理 OPPO 固件文件时产生的诊断消息的输出目标。</para>
/// </summary>
public interface IOppLogger
{
    /// <summary>
    /// Logs a message at the trace level.
    /// <para>记录跟踪级别的消息。</para>
    /// </summary>
    /// <param name="message">The trace message. <para>跟踪消息内容。</para></param>
    void Trace(string message);

    /// <summary>
    /// Logs a message at the debug level.
    /// <para>记录调试级别的消息。</para>
    /// </summary>
    /// <param name="message">The debug message. <para>调试消息内容。</para></param>
    void Debug(string message);

    /// <summary>
    /// Logs a message at the information level.
    /// <para>记录信息级别的消息。</para>
    /// </summary>
    /// <param name="message">The informational message. <para>信息性消息内容。</para></param>
    void Information(string message);

    /// <summary>
    /// Logs a message at the warning level.
    /// <para>记录警告级别的消息。</para>
    /// </summary>
    /// <param name="message">The warning message. <para>警告消息内容。</para></param>
    void Warning(string message);

    /// <summary>
    /// Logs a message at the warning level along with the associated exception.
    /// <para>记录警告级别的消息及关联的异常信息。</para>
    /// </summary>
    /// <param name="message">The warning message. <para>警告消息内容。</para></param>
    /// <param name="exception">The exception related to the warning. <para>与该警告关联的异常。</para></param>
    void Warning(string message, Exception exception);

    /// <summary>
    /// Logs a message at the error level.
    /// <para>记录错误级别的消息。</para>
    /// </summary>
    /// <param name="message">The error message. <para>错误消息内容。</para></param>
    void Error(string message);

    /// <summary>
    /// Logs a message at the error level along with the associated exception.
    /// <para>记录错误级别的消息及关联的异常信息。</para>
    /// </summary>
    /// <param name="message">The error message. <para>错误消息内容。</para></param>
    /// <param name="exception">The exception related to the error. <para>与该错误关联的异常。</para></param>
    void Error(string message, Exception exception);
}
namespace UotanStudio.Firmware.OnePlusOps.Logging;

/// <summary>
/// Log level enum for OnePlus operations
/// </summary>
/// <para>OnePlus操作的日志级别枚举</para>
public enum LogLevel
{
    /// <summary>
    /// Trace level for detailed diagnostic information
    /// </summary>
    /// <para>用于详细诊断信息的追踪级别</para>
    Trace,

    /// <summary>
    /// Debug level for development debugging information
    /// </summary>
    /// <para>用于开发调试信息的调试级别</para>
    Debug,

    /// <summary>
    /// Information level for general operational messages
    /// </summary>
    /// <para>用于一般操作消息的信息级别</para>
    Information,

    /// <summary>
    /// Warning level for potentially harmful situations
    /// </summary>
    /// <para>用于潜在有害情况的警告级别</para>
    Warning,

    /// <summary>
    /// Error level for error events that may still allow the application to continue
    /// </summary>
    /// <para>用于可能仍允许应用程序继续运行的错误事件的错误级别</para>
    Error,

    /// <summary>
    /// Critical level for very severe error events
    /// </summary>
    /// <para>用于非常严重的错误事件的严重级别</para>
    Critical
}

/// <summary>
/// Interface for logging operations in OnePlus library
/// </summary>
/// <para>OnePlus库中日志操作的接口</para>
public interface IOnePlusLogger
{
    /// <summary>
    /// Log a trace message
    /// </summary>
    /// <para>记录追踪消息</para>
    /// <param name="message">The message to log</param>
    void Trace(string message);

    /// <summary>
    /// Log a debug message
    /// </summary>
    /// <para>记录调试消息</para>
    /// <param name="message">The message to log</param>
    void Debug(string message);

    /// <summary>
    /// Log an information message
    /// </summary>
    /// <para>记录信息消息</para>
    /// <param name="message">The message to log</param>
    void Information(string message);

    /// <summary>
    /// Log a warning message
    /// </summary>
    /// <para>记录警告消息</para>
    /// <param name="message">The message to log</param>
    void Warning(string message);

    /// <summary>
    /// Log an error message
    /// </summary>
    /// <para>记录错误消息</para>
    /// <param name="message">The message to log</param>
    void Error(string message);

    /// <summary>
    /// Log a critical error message
    /// </summary>
    /// <para>记录严重错误消息</para>
    /// <param name="message">The message to log</param>
    void Critical(string message);

    /// <summary>
    /// Log a message with specified log level
    /// </summary>
    /// <para>记录指定日志级别的消息</para>
    /// <param name="level">The log level</param>
    /// <param name="message">The message to log</param>
    void Log(LogLevel level, string message);
}
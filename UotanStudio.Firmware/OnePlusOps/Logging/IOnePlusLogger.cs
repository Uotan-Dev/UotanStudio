namespace UotanStudio.Firmware.OnePlusOps.Logging;

public enum LogLevel
{
    Trace,

    Debug,

    Information,

    Warning,

    Error,

    Critical
}

public interface IOnePlusLogger
{
    /// <summary>
    /// Log a trace message
    /// <para>记录追踪消息</para>
    /// </summary>
    void Trace(string message);

    /// <summary>
    /// Log a debug message
    /// <para>记录调试消息</para>
    /// </summary>
    void Debug(string message);

    /// <summary>
    /// Log an information message
    /// <para>记录信息消息</para>
    /// </summary>
    void Information(string message);

    /// <summary>
    /// Log a warning message
    /// <para>记录警告消息</para>
    /// </summary>
    void Warning(string message);

    /// <summary>
    /// Log an error message
    /// <para>记录错误消息</para>
    /// </summary>
    void Error(string message);

    /// <summary>
    /// Log a critical error message
    /// <para>记录严重错误消息</para>
    /// </summary>
    void Critical(string message);

    /// <summary>
    /// Log a message with specified log level
    /// <para>记录指定日志级别的消息</para>
    /// </summary>
    void Log(LogLevel level, string message);
}

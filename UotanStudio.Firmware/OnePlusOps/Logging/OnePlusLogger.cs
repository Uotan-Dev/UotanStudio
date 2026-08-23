namespace UotanStudio.Firmware.OnePlusOps.Logging;

/// <summary>
/// Default logger implementation for OnePlus library
/// </summary>
/// <para>OnePlus库的默认日志记录器实现</para>
public class OnePlusLogger : IOnePlusLogger
{
    /// <summary>
    /// Gets or sets the minimum log level to output
    /// </summary>
    /// <para>获取或设置要输出的最小日志级别</para>
    public LogLevel MinLogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Gets or sets the singleton instance of the logger
    /// </summary>
    /// <para>获取或设置日志记录器的单例实例</para>
    public static IOnePlusLogger Instance { get => field ??= new OnePlusLogger(); set; }

    /// <summary>
    /// Log a trace message
    /// </summary>
    /// <para>记录追踪消息</para>
    /// <param name="message">The message to log</param>
    public void Trace(string message) => Log(LogLevel.Trace, message);

    /// <summary>
    /// Log a debug message
    /// </summary>
    /// <para>记录调试消息</para>
    /// <param name="message">The message to log</param>
    public void Debug(string message) => Log(LogLevel.Debug, message);

    /// <summary>
    /// Log an information message
    /// </summary>
    /// <para>记录信息消息</para>
    /// <param name="message">The message to log</param>
    public void Information(string message) => Log(LogLevel.Information, message);

    /// <summary>
    /// Log a warning message
    /// </summary>
    /// <para>记录警告消息</para>
    /// <param name="message">The message to log</param>
    public void Warning(string message) => Log(LogLevel.Warning, message);

    /// <summary>
    /// Log an error message
    /// </summary>
    /// <para>记录错误消息</para>
    /// <param name="message">The message to log</param>
    public void Error(string message) => Log(LogLevel.Error, message);

    /// <summary>
    /// Log a critical error message
    /// </summary>
    /// <para>记录严重错误消息</para>
    /// <param name="message">The message to log</param>
    public void Critical(string message) => Log(LogLevel.Critical, message);

    /// <summary>
    /// Log a message with specified log level
    /// </summary>
    /// <para>记录指定日志级别的消息</para>
    /// <param name="level">The log level</param>
    /// <param name="message">The message to log</param>
    public void Log(LogLevel level, string message)
    {
        if (level < MinLogLevel)
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var levelStr = level.ToString().PadRight(11);

        Console.WriteLine($"[{timestamp}] [{levelStr}] {message}");
    }
}
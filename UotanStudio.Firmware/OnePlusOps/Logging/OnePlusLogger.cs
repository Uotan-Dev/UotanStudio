namespace UotanStudio.Firmware.OnePlusOps.Logging;

public class OnePlusLogger : IOnePlusLogger
{
    public LogLevel MinLogLevel { get; set; } = LogLevel.Information;

    public static IOnePlusLogger Instance { get => field ??= new OnePlusLogger(); set; }

    /// <summary>
    /// Log a trace message
    /// <para>记录追踪消息</para>
    /// </summary>
    public void Trace(string message) => Log(LogLevel.Trace, message);

    /// <summary>
    /// Log a debug message
    /// <para>记录调试消息</para>
    /// </summary>
    public void Debug(string message) => Log(LogLevel.Debug, message);

    /// <summary>
    /// Log an information message
    /// <para>记录信息消息</para>
    /// </summary>
    public void Information(string message) => Log(LogLevel.Information, message);

    /// <summary>
    /// Log a warning message
    /// <para>记录警告消息</para>
    /// </summary>
    public void Warning(string message) => Log(LogLevel.Warning, message);

    /// <summary>
    /// Log an error message
    /// <para>记录错误消息</para>
    /// </summary>
    public void Error(string message) => Log(LogLevel.Error, message);

    /// <summary>
    /// Log a critical error message
    /// <para>记录严重错误消息</para>
    /// </summary>
    public void Critical(string message) => Log(LogLevel.Critical, message);

    /// <summary>
    /// Log a message with specified log level
    /// <para>记录指定日志级别的消息</para>
    /// </summary>
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

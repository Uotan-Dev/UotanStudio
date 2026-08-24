namespace UotanStudio.Firmware.OnePlusOps.Logging;

/// <summary>
/// Null logger implementation that does nothing
/// </summary>
/// <para>空日志记录器实现，不执行任何操作</para>
public class NullLogger : IOnePlusLogger
{
    /// <summary>
    /// Singleton instance of NullLogger
    /// </summary>
    /// <para>NullLogger的单例实例</para>
    public static readonly NullLogger Instance = new();

    /// <summary>
    /// Does nothing
    /// </summary>
    /// <para>不执行任何操作</para>
    /// <param name="message">The message to ignore</param>
    public void Trace(string message) { }

    /// <summary>
    /// Does nothing
    /// </summary>
    /// <para>不执行任何操作</para>
    /// <param name="message">The message to ignore</param>
    public void Debug(string message) { }

    /// <summary>
    /// Does nothing
    /// </summary>
    /// <para>不执行任何操作</para>
    /// <param name="message">The message to ignore</param>
    public void Information(string message) { }

    /// <summary>
    /// Does nothing
    /// </summary>
    /// <para>不执行任何操作</para>
    /// <param name="message">The message to ignore</param>
    public void Warning(string message) { }

    /// <summary>
    /// Does nothing
    /// </summary>
    /// <para>不执行任何操作</para>
    /// <param name="message">The message to ignore</param>
    public void Error(string message) { }

    /// <summary>
    /// Does nothing
    /// </summary>
    /// <para>不执行任何操作</para>
    /// <param name="message">The message to ignore</param>
    public void Critical(string message) { }

    /// <summary>
    /// Does nothing
    /// </summary>
    /// <para>不执行任何操作</para>
    /// <param name="level">The log level to ignore</param>
    /// <param name="message">The message to ignore</param>
    public void Log(LogLevel level, string message) { }
}
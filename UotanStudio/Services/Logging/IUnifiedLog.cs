namespace UotanStudio.Services;

/// <summary>
/// Log level enum.
/// <para>日志级别枚举。</para>
/// </summary>
public enum LogLevel
{
    /// <summary>Trace. <para>跟踪。</para></summary>
    Trace,

    /// <summary>Debug. <para>调试。</para></summary>
    Debug,

    /// <summary>Info. <para>信息。</para></summary>
    Info,

    /// <summary>Warn. <para>警告。</para></summary>
    Warn,

    /// <summary>Error. <para>错误。</para></summary>
    Error,

    /// <summary>Fatal. <para>致命错误。</para></summary>
    Fatal
}

/// <summary>
/// Unified log writer interface. All methods, node executions and device-protocol interactions push text through this interface, collected and displayed by the main log window.
/// <para>统一日志写入接口。所有方法、节点执行、设备协议交互均通过此接口推送文本，由主日志窗口收集显示。</para>
/// </summary>
public interface IUnifiedLog
{
    /// <summary>
    /// Writes a log entry.
    /// <para>写入一条日志条目。</para>
    /// </summary>
    /// <param name="level">
    /// Log level.
    /// <para>日志级别。</para>
    /// </param>
    /// <param name="source">
    /// The log source, such as a module name, node name, or device id.
    /// <para>日志来源，如模块名、节点名或设备 ID。</para>
    /// </param>
    /// <param name="message">
    /// Log text.
    /// <para>日志文本。</para>
    /// </param>
    void Write(LogLevel level, string source, string message);

    /// <summary>
    /// Writes a formatted log entry.
    /// <para>写入一条格式化日志条目。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="format">The composite format string. <para>复合格式字符串。</para></param>
    /// <param name="args">The format arguments. <para>格式化使用的参数数组。</para></param>
    void Write(LogLevel level, string source, string format, params object[] args);

    /// <summary>
    /// Sets the minimum output level; logs below this level are discarded.
    /// <para>设置最小输出级别，低于此级别的日志将被丢弃。</para>
    /// </summary>
    /// <param name="level">The new minimum level. <para>新的最低级别。</para></param>
    void SetMinimumLevel(LogLevel level);
}
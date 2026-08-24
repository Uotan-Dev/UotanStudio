namespace UotanStudio.Services;

/// <summary>
/// <para>日志级别枚举。</para>
/// Log level enum.
/// </summary>
public enum LogLevel
{
    /// <summary><para>跟踪。</para>Trace.</summary>
    Trace,

    /// <summary><para>调试。</para>Debug.</summary>
    Debug,

    /// <summary><para>信息。</para>Info.</summary>
    Info,

    /// <summary><para>警告。</para>Warn.</summary>
    Warn,

    /// <summary><para>错误。</para>Error.</summary>
    Error,

    /// <summary><para>致命错误。</para>Fatal.</summary>
    Fatal
}

/// <summary>
/// <para>统一日志写入接口。所有方法、节点执行、设备协议交互均通过此接口推送文本，由主日志窗口收集显示。</para>
/// Unified log writer interface. All methods, node executions and device-protocol interactions push text through this interface, collected and displayed by the main log window.
/// </summary>
public interface IUnifiedLog
{
    /// <summary>
    /// <para>写入一条日志条目。</para>
    /// Writes a log entry.
    /// </summary>
    /// <param name="level">
    /// <para>日志级别。</para>
    /// Log level.
    /// </param>
    /// <param name="source">
    /// <para>日志来源（模块名、节点名、设备 ID 等）。</para>
    /// Log source (module name, node name, device id, etc.).
    /// </param>
    /// <param name="message">
    /// <para>日志文本。</para>
    /// Log text.
    /// </param>
    void Write(LogLevel level, string source, string message);

    /// <summary>
    /// <para>写入一条格式化日志条目。</para>
    /// Writes a formatted log entry.
    /// </summary>
    void Write(LogLevel level, string source, string format, params object[] args);

    /// <summary>
    /// <para>设置最小输出级别，低于此级别的日志将被丢弃。</para>
    /// Sets the minimum output level; logs below this level are discarded.
    /// </summary>
    void SetMinimumLevel(LogLevel level);
}
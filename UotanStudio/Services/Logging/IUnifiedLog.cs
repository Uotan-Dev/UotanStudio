using System.Runtime.CompilerServices;
using UotanStudio.Services.Logging;

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
    /// Writes an interpolated log entry, formatting it only when this target accepts <paramref name="level"/>.
    /// <para>写入一条插值日志条目，仅在本目标接受 <paramref name="level"/> 时才执行格式化。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="message">Interpolated message; no text is built when the level is filtered out.
    /// <para>插值消息；级别被过滤时不构建任何文本。</para></param>
    void Write(LogLevel level, string source,
        [InterpolatedStringHandlerArgument("", "level")] ref UnifiedLogMessage message)
    {
        if (!message.IsEnabled)
        {
            return;
        }

        Write(level, source, message.ToStringAndClear());
    }

    /// <summary>
    /// Writes a log entry carrying an optional correlation scope and structured fields.
    /// <para>写入一条可携带关联作用域与结构化字段的日志条目。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="message">The log text. <para>日志文本。</para></param>
    /// <param name="scope">Correlation scope shared by a group of entries, or null.
    /// <para>一组条目共享的关联作用域；无则为 null。</para></param>
    /// <param name="fields">Structured fields rendered beside the message, or null.
    /// <para>渲染在消息旁的结构化字段；无则为 null。</para></param>
    /// <remarks>Targets that keep no metadata still record the text, so a pass-through implementation stays valid.
    /// <para>不保留元数据的目标仍会记录文本，因此直通实现依然有效。</para></remarks>
    void Write(LogLevel level, string source, string message, string? scope, IReadOnlyList<LogField>? fields)
        => Write(level, source, message);

    /// <summary>
    /// Gets whether an entry at <paramref name="level"/> would be recorded, so a caller can skip work the
    /// interpolated handler cannot make lazy.
    /// <para>获取某个级别的条目是否会被记录，使调用方可以跳过插值处理器无法延迟的工作。</para>
    /// </summary>
    /// <param name="level">The level the entry would be written at. <para>条目将要写入的级别。</para></param>
    /// <returns><see langword="true"/> when the entry would be recorded.
    /// <para>条目会被记录时为 <see langword="true"/>。</para></returns>
    /// <remarks>The default accepts every level because pass-through targets record everything; a target with a
    /// minimum level overrides it so the handler can suppress formatting.
    /// <para>默认接受所有级别，因为直通目标记录全部内容；带最低级别的目标会重写它，以便处理器抑制格式化。</para></remarks>
    bool IsEnabled(LogLevel level) => true;

    /// <summary>
    /// Sets the minimum output level; logs below this level are discarded.
    /// <para>设置最小输出级别，低于此级别的日志将被丢弃。</para>
    /// </summary>
    /// <param name="level">The new minimum level. <para>新的最低级别。</para></param>
    void SetMinimumLevel(LogLevel level);
}
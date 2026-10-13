namespace UotanStudio.Services.Logging;

/// <summary>A complete log entry and its optional display columns.<para>完整日志条目及其可选显示列。</para></summary>
public interface ILogLine
{
    /// <summary>Complete searchable and copyable text.<para>可搜索和复制的完整文本。</para></summary>
    /// <value>The whole line of log text.<para>整行日志文本。</para></value>
    string Text { get; }
    /// <summary>Parsed severity, when available.<para>解析得到的等级，未知时为空。</para></summary>
    /// <value>The severity level, or null when unparsed.<para>日志等级，无法解析时为 null。</para></value>
    LogLevel? Level { get; }
    /// <summary>Metadata columns preceding the message.<para>正文前的元数据列。</para></summary>
    /// <value>The display columns of this entry.<para>本条日志的显示列。</para></value>
    IReadOnlyList<LogTextColumn> Columns { get; }
}

/// <summary>A fixed-width display column mapped to complete document text.<para>映射完整文档文本的固定宽度显示列。</para></summary>
/// <param name="Text">Unabridged field value.<para>完整字段值。</para></param>
/// <param name="DocumentLength">Consumed document characters, including separators.<para>包含分隔符的文档字符数。</para></param>
/// <param name="Width">Width in monospace characters.<para>以等宽字符数表示的宽度。</para></param>
/// <param name="IsPriority">Whether to draw a severity background.<para>是否绘制等级背景。</para></param>
/// <param name="TrimMiddle">Whether to preserve both ends when eliding.<para>省略时是否保留两端。</para></param>
public readonly record struct LogTextColumn(string Text, int DocumentLength, int Width,
    bool IsPriority = false, bool TrimMiddle = false);
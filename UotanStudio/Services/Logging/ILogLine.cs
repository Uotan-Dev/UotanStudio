namespace UotanStudio.Services.Logging;

/// <summary>A complete log entry and its optional display columns. 完整日志条目及其可选显示列。</summary>
public interface ILogLine
{
    /// <summary>Complete searchable and copyable text. 可搜索和复制的完整文本。</summary>
    string Text { get; }
    /// <summary>Parsed severity, when available. 解析得到的等级，未知时为空。</summary>
    LogLevel? Level { get; }
    /// <summary>Metadata columns preceding the message. 正文前的元数据列。</summary>
    IReadOnlyList<LogTextColumn> Columns { get; }
}

/// <summary>A fixed-width display column mapped to complete document text. 映射完整文档文本的固定宽度显示列。</summary>
/// <param name="Text">Unabridged field value. 完整字段。</param>
/// <param name="DocumentLength">Consumed document characters, including separators. 包含分隔符的文档字符数。</param>
/// <param name="Width">Width in monospace characters. 以等宽字符数表示的宽度。</param>
/// <param name="IsPriority">Whether to draw a severity background. 是否绘制等级背景。</param>
/// <param name="TrimMiddle">Whether to preserve both ends when eliding. 省略时是否保留两端。</param>
public readonly record struct LogTextColumn(string Text, int DocumentLength, int Width,
    bool IsPriority = false, bool TrimMiddle = false);
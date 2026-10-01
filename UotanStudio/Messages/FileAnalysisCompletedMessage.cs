namespace UotanStudio.Messages;

using System.Collections.Generic;

/// <summary>
/// <para>文件分析完成消息，当文档视图完成固件格式识别与结构分析后发送，
/// 供属性面板等订阅方展示分析结果。</para>
/// File analysis completed message, sent after the document view finishes firmware format detection
/// and structure analysis, allowing subscribers such as the properties panel to display the results.
/// </summary>
public record FileAnalysisCompletedMessage(IReadOnlyList<UotanStudio.ViewModels.Editors.PropertyRow> Analysis, string? DetectedFormat, string? FilePath);
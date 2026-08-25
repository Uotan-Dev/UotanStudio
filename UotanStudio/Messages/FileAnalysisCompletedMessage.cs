namespace UotanStudio.Messages;

using System.Collections.Generic;

/// <summary>
/// <para>文件分析完成消息，当文档视图完成固件格式识别与结构分析后发送，
/// 供属性面板等订阅方展示分析结果。</para>
/// File analysis completed message, sent after the document view finishes firmware format detection
/// and structure analysis, allowing subscribers such as the properties panel to display the results.
/// </summary>
public class FileAnalysisCompletedMessage
{
    /// <summary>
    /// <para>获取分析结果键值对列表。</para>
    /// Gets the analysis result key-value pairs.
    /// </summary>
    public IReadOnlyList<ViewModels.PropertyRow> Analysis { get; }

    /// <summary>
    /// <para>获取检测到的固件格式名称；未识别时为 <c>null</c>。</para>
    /// Gets the detected firmware format name; <c>null</c> when unrecognized.
    /// </summary>
    public string? DetectedFormat { get; }

    /// <summary>
    /// <para>获取被分析的文件路径。</para>
    /// Gets the file path that was analyzed.
    /// </summary>
    public string? FilePath { get; }

    /// <summary>
    /// <para>创建文件分析完成消息。</para>
    /// Creates a file analysis completed message.
    /// </summary>
    public FileAnalysisCompletedMessage(IReadOnlyList<ViewModels.PropertyRow> analysis, string? detectedFormat, string? filePath)
    {
        Analysis = analysis;
        DetectedFormat = detectedFormat;
        FilePath = filePath;
    }
}
namespace UotanStudio.Messages;

using System.Collections.Generic;

/// <summary>
/// Message announcing that format detection and structure analysis finished for a file.
/// <para>宣告某个文件的格式识别与结构分析已经完成的消息。</para>
/// </summary>
/// <param name="Analysis">
/// The property rows produced by the analysis.
/// <para>分析得到的属性行集合。</para>
/// </param>
/// <param name="DetectedFormat">
/// The detected format name, or <see langword="null"/> when no format was detected.
/// <para>识别出的格式名称，未识别出格式时为空。</para>
/// </param>
/// <param name="FilePath">
/// The path of the analyzed file, or <see langword="null"/> when no path is available.
/// <para>被分析文件的路径，没有可用路径时为空。</para>
/// </param>
public record FileAnalysisCompletedMessage(IReadOnlyList<UotanStudio.ViewModels.Editors.PropertyRow> Analysis, string? DetectedFormat, string? FilePath);
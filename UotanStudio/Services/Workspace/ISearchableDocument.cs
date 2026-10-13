using AvaloniaEdit;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// The searchable-document contract recognized by workspace find/replace: text carriers expose a
/// text snapshot and accept replacement write-back; read-only structured documents decline it.
/// <para>工作区查找/替换识别的可搜索文档契约：文本载体暴露文本快照并接受替换写回，只读结构化文档拒绝替换。</para>
/// </summary>
internal interface ISearchableDocument
{
    /// <summary>
    /// The searchable text snapshot; <c>null</c> when there is no text view.
    /// <para>可搜索的文本快照；无文本视图时为 <c>null</c>。</para>
    /// </summary>
    /// <value>
    /// The current text snapshot, or <c>null</c>.
    /// <para>当前文本快照，或为 <c>null</c>。</para>
    /// </value>
    string? SearchText { get; }

    /// <summary>
    /// The document file path.
    /// <para>文档对应的文件路径。</para>
    /// </summary>
    /// <value>
    /// The path of the document's file, or <c>null</c>.
    /// <para>文档对应文件的路径，或为 <c>null</c>。</para>
    /// </value>
    string? FilePath { get; }

    /// <summary>
    /// The text control exposed for selection and scrolling; <c>null</c> when absent.
    /// <para>用于选中与滚动的文本控件；无则为 <c>null</c>。</para>
    /// </summary>
    /// <value>
    /// The text control instance, or <c>null</c>.
    /// <para>文本控件实例，或为 <c>null</c>。</para>
    /// </value>
    TextEditor? Editor { get; }

    /// <summary>
    /// Whether text replacement is supported (<c>false</c> for read-only structured views).
    /// <para>是否支持文本替换写回（只读结构化视图为 <c>false</c>）。</para>
    /// </summary>
    /// <value>
    /// True when a replacement can be written back.
    /// <para>可写回替换结果时为真。</para>
    /// </value>
    bool SupportsSearchReplacement { get; }

    /// <summary>
    /// Updates the text carrier with a replacement result.
    /// <para>用替换结果更新文本载体。</para>
    /// </summary>
    /// <param name="text">
    /// The full replacement text.
    /// <para>替换后的完整文本。</para>
    /// </param>
    void ApplySearchText(string text);
}
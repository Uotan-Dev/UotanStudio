using AvaloniaEdit;

namespace UotanStudio.Services.Workspace;

/// <summary>
/// <para>工作区查找/替换可识别的可搜索文档契约：文本载体（编辑器文本、DTS 源码、反编译
/// 预览、资源表文本）统一经 <see cref="SearchText"/> 暴露，替换写回经 <see cref="ApplySearchText"/>。
/// 只读结构化文档（ARSC/DEX）通过 <see cref="SupportsSearchReplacement"/> 显式拒绝替换。</para>
/// The searchable-document contract recognized by the workspace find/replace: text carriers
/// (editor text, DTS source, decompiled preview, resource-table text) are exposed through
/// <see cref="SearchText"/>, with replacement write-back through <see cref="ApplySearchText"/>.
/// Read-only structured documents (ARSC/DEX) reject replacement through
/// <see cref="SupportsSearchReplacement"/>.
/// </summary>
internal interface ISearchableDocument
{
    /// <summary>
    /// <para>可搜索的文本快照；无文本视图时为 <c>null</c>。</para>
    /// The searchable text snapshot; <c>null</c> when there is no text view.
    /// </summary>
    string? SearchText { get; }

    /// <summary>
    /// <para>文档对应的文件路径。</para>
    /// The document file path.
    /// </summary>
    string? FilePath { get; }

    /// <summary>
    /// <para>文档暴露的文本编辑器（用于选中/滚动）；无则为 <c>null</c>。</para>
    /// The text editor exposed for selection/scroll; <c>null</c> when absent.
    /// </summary>
    TextEditor? Editor { get; }

    /// <summary>
    /// <para>是否支持文本替换写回（只读结构化视图为 <c>false</c>）。</para>
    /// Whether text replacement is supported (<c>false</c> for read-only structured views).
    /// </summary>
    bool SupportsSearchReplacement { get; }

    /// <summary>
    /// <para>用替换结果更新文本载体。</para>
    /// Updates the text carrier with a replacement result.
    /// </summary>
    /// <param name="text">
    /// <para>替换后的完整文本。</para>
    /// The full replacement text.
    /// </param>
    void ApplySearchText(string text);
}
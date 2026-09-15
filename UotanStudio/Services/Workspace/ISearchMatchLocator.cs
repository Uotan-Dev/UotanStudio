namespace UotanStudio.Services.Workspace;

/// <summary>
/// <para>无文本编辑器、但可按文本偏移定位到结构化节点的文档契约（如 ARSC 资源树）。
/// 第一阶段不做编辑器内文本定位，改为选中左栏对应树节点。</para>
/// The contract for documents without a text editor that can locate a structured node from a text
/// offset (e.g. the ARSC resource tree). The first stage skips in-editor text location and selects
/// the matching left-pane tree node instead.
/// </summary>
internal interface ISearchMatchLocator
{
    /// <summary>
    /// <para>按文本偏移选中对应的结构化节点。</para>
    /// Selects the structured node matching a text offset.
    /// </summary>
    /// <param name="offset">
    /// <para>匹配在搜索文本中的起始偏移。</para>
    /// The match start offset within the search text.
    /// </param>
    /// <param name="length">
    /// <para>匹配长度（结构定位忽略该值）。</para>
    /// The match length (ignored by structural location).
    /// </param>
    void LocateSearchMatch(int offset, int length);
}
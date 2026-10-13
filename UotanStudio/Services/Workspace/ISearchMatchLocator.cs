namespace UotanStudio.Services.Workspace;

/// <summary>
/// Contract for documents that locate a structured node from a text offset instead of matching
/// inside text, as the ARSC resource tree does.
/// <para>以文本偏移定位结构化节点、而非文内命中的文档契约，如 ARSC 资源树。</para>
/// </summary>
internal interface ISearchMatchLocator
{
    /// <summary>
    /// Selects the structured node matching a text offset.
    /// <para>按文本偏移选中对应的结构化节点。</para>
    /// </summary>
    /// <param name="offset">
    /// The match start offset within the search text.
    /// <para>匹配在搜索文本中的起始偏移。</para>
    /// </param>
    /// <param name="length">
    /// The match length (ignored by structural location).
    /// <para>匹配长度（结构定位忽略该值）。</para>
    /// </param>
    void LocateSearchMatch(int offset, int length);
}
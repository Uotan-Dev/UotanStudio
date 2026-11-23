namespace UotanStudio.Services.Workspace;

/// <summary>
/// Contract for documents whose searchable text is a projection of raw bytes and whose matches must
/// be revealed in a hex view rather than a text editor.
/// <para>契约：文档的可搜索文本是原始字节的投影，匹配项需在十六进制视图而非文本编辑器中定位。</para>
/// </summary>
internal interface IHexSearchMatchLocator
{
    /// <summary>
    /// Reveals a match in the hex view when the document currently uses a binary/hex representation.
    /// <para>文档当前处于二进制/十六进制表示时，在十六进制视图中显示该匹配项。</para>
    /// </summary>
    /// <param name="offset">
    /// The match offset within <see cref="ISearchableDocument.SearchText"/>.
    /// <para>匹配在 <see cref="ISearchableDocument.SearchText"/> 中的偏移。</para>
    /// </param>
    /// <param name="length">
    /// The match length in projected text characters (equal to the byte count).
    /// <para>匹配在投影文本中的长度（等于字节数）。</para>
    /// </param>
    /// <returns>
    /// True when the hex view handled the match; false when the caller should use the text editor.
    /// <para>十六进制视图已处理该匹配时为真；为假时调用方应改用文本编辑器。</para>
    /// </returns>
    bool TryRevealSearchMatch(int offset, int length);
}
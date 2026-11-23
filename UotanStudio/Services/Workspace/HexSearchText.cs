namespace UotanStudio.Services.Workspace;

/// <summary>
/// A searchable snapshot of raw binary content: one text character per byte, mirroring the ASCII
/// column of the hex view. Character and byte offsets are identical by construction.
/// <para>原始二进制内容的可搜索快照：每字节一个文本字符，与十六进制视图的 ASCII 列一致；
/// 由此字符偏移与字节偏移天然相同。</para>
/// </summary>
/// <param name="Text">
/// The text carrier exposed to find/replace.
/// <para>供查找/替换使用的文本载体。</para>
/// </param>
internal sealed record HexSearchSnapshot(string Text);

/// <summary>
/// Builds text projections of in-memory binary documents without depending on hex view controls.
/// <para>为内存中的二进制文档构建文本投影，不依赖十六进制视图控件。</para>
/// </summary>
internal static class HexSearchText
{
    /// <summary>
    /// Projects each byte to one character: printable ASCII is kept, anything else becomes a dot.
    /// <para>把每个字节投影成一个字符：可打印 ASCII 原样保留，其余投影为点号。</para>
    /// </summary>
    /// <param name="bytes">
    /// The raw bytes of the document.
    /// <para>文档的原始字节。</para>
    /// </param>
    /// <returns>
    /// The text projection and its implicit offset map.
    /// <para>文本投影及其隐含的偏移映射。</para>
    /// </returns>
    internal static HexSearchSnapshot Build(ReadOnlySpan<byte> bytes)
    {
        var characters = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            characters[i] = value is >= 0x20 and < 0x7F ? (char)value : '.';
        }

        return new(new string(characters));
    }

    /// <summary>
    /// Maps a text offset to the byte offset it represents, clamping out-of-range offsets to the
    /// nearest byte so a match can always reveal a position in the hex view.
    /// <para>把文本偏移映射为对应字节偏移；越界偏移钳制到最近的字节，保证匹配总能在十六进制视图中定位。</para>
    /// </summary>
    /// <param name="snapshot">
    /// The snapshot holding the text projection.
    /// <para>持有文本投影的快照。</para>
    /// </param>
    /// <param name="textOffset">
    /// The match offset within <see cref="HexSearchSnapshot.Text"/>.
    /// <para>匹配在 <see cref="HexSearchSnapshot.Text"/> 中的偏移。</para>
    /// </param>
    /// <returns>
    /// The byte offset of the match.
    /// <para>该匹配对应的字节偏移。</para>
    /// </returns>
    internal static int ByteOffset(HexSearchSnapshot snapshot, int textOffset) =>
        snapshot.Text.Length == 0 ? 0 : Math.Clamp(textOffset, 0, snapshot.Text.Length - 1);
}
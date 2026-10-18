using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.AXml;

namespace UotanStudio.Firmware.AndroidMeta.Resources;

/// <summary>
/// Parser and holder for one ResStringPool chunk of an Android binary XML resource container. All chunk fields are little-endian and strings are UTF-8 or UTF-16LE depending on the pool's UTF-8 flag; every header value, offset and string extent is bounds-checked against the caller-supplied chunk window.
/// <para>Android 二进制 XML 资源容器中单个 ResStringPool 块的解析器与持有者。块内字段均为小端，字符串依 UTF-8 标志为 UTF-8 或 UTF-16LE；所有头部值、偏移与字符串范围均按调用方给出的块窗口做上限检查。</para>
/// </summary>
public sealed class AXmlStringPool
{
    private const uint Utf8Flag = 1u << 8;

    private const uint SortedFlag = 1u << 0;

    private readonly List<string> _strings = [];
    private Dictionary<string, int>? _index;
    private bool _isUtf8 = true;
    internal uint[] StyleOffsets { get; private set; } = [];
    internal byte[] StyleData { get; private set; } = [];

    /// <summary>Number of decoded strings currently held. <para>当前已解码字符串的数量。</para></summary>
    /// <value>Entries added by the last <see cref="Parse"/> call plus any strings appended via <see cref="Intern"/>. <para>最近一次 <see cref="Parse"/> 解析出的条目加上随后经 <see cref="Intern"/> 追加的字符串。</para></value>
    public int Count => _strings.Count;

    /// <summary>Encoding flag of the parsed pool. <para>已解析数据池的编码标志。</para></summary>
    /// <value><see langword="true"/> when the pool sets the UTF-8 flag and its strings were decoded
    /// as UTF-8; <see langword="false"/> means UTF-16LE. Before any parse the default is
    /// <see langword="true"/>.
    /// <para>数据池置有 UTF-8 标志并按 UTF-8 解码时为 <see langword="true"/>；<see langword="false"/> 表示
    /// UTF-16LE。未解析时默认为 <see langword="true"/>。</para></value>
    public bool IsUtf8 => _isUtf8;

    /// <summary>Decoded pool strings in index order. <para>按索引顺序排列的已解码字符串。</para></summary>
    /// <value>A read-only view over the internal list; out-of-range pool indices from other chunks
    /// should be resolved via <see cref="GetString"/> instead.
    /// <para>内部列表的只读视图；来自其他数据块的越界池索引应改用 <see cref="GetString"/> 解析。</para></value>
    public IReadOnlyList<string> Strings => _strings;

    /// <summary>Parses a ResStringPool chunk.<para>解析 ResStringPool 数据块。</para></summary>
    /// <param name="data">Buffer containing the chunk; treated as untrusted input.
    /// <para>包含该数据块的缓冲区；按不可信输入处理。</para></param>
    /// <param name="offset">Absolute start of the chunk inside <paramref name="data"/>.
    /// <para>数据块在 <paramref name="data"/> 中的绝对起始位置。</para></param>
    /// <param name="chunkSize">Total chunk length in bytes, including its header; at least 28.
    /// <para>含头部在内的数据块总字节数，至少为 28。</para></param>
    /// <exception cref="AXmlException">The window is outside <paramref name="data"/>, the header or offset-table geometry is inconsistent (header size below 28, counts exceeding the available space, malformed <c>stringsStart</c>/<c>stylesStart</c>), a string or style lies outside its region, or a length prefix, NUL terminator or declared UTF-8 character count does not match. State from a previous parse is cleared only after all header checks pass.
    /// <para>窗口超出 <paramref name="data"/> 范围、头部或偏移表几何不一致（头部小于 28、数量超出可用空间、 <c>stringsStart</c>/<c>stylesStart</c> 非法）、字符串或样式越出其区域，或长度前缀、NUL 结束符、 声明的 UTF-8 字符数不匹配时抛出。仅在全部头部检查通过后才清除上次解析的状态。</para></exception>
    public void Parse(byte[] data, int offset, int chunkSize)
    {
        if (offset < 0 || chunkSize < 28 || offset > data.Length || chunkSize > data.Length - offset)
            throw new AXmlException("Invalid string pool range.");
        var span = data.AsSpan(offset, chunkSize);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        var stringCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..]));
        var styleCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[12..]));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        var stringsStart = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[20..]));
        var stylesStart = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[24..]));
        if (headerSize < 28 || headerSize > chunkSize || stringCount > (chunkSize - headerSize) / 4
            || styleCount > stringCount || styleCount > (chunkSize - headerSize - stringCount * 4) / 4
            || stringsStart < headerSize + (stringCount + styleCount) * 4 || stringsStart > chunkSize
            || (styleCount > 0 && (stylesStart < stringsStart || stylesStart > chunkSize)))
            throw new AXmlException("Invalid string pool layout.");
        _isUtf8 = (flags & Utf8Flag) != 0;

        _strings.Clear();
        _strings.Capacity = stringCount;
        _index = null;


        var offsetsStart = offset + headerSize;
        var stringsEnd = offset + (styleCount > 0 ? stylesStart : chunkSize);
        for (var i = 0; i < stringCount; i++)
        {
            var strOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offsetsStart + i * 4)));
            if (strOffset < 0 || strOffset >= stringsEnd - offset - stringsStart)
                throw new AXmlException("String is outside its pool.");
            var strPos = offset + stringsStart + strOffset;
            var s = _isUtf8 ? ReadUtf8String(data, strPos, stringsEnd) : ReadUtf16String(data, strPos, stringsEnd);
            _strings.Add(s);
        }


        StyleOffsets = new uint[styleCount];
        StyleData = styleCount > 0 ? span[stylesStart..].ToArray() : [];
        for (var i = 0; i < styleCount; i++)
        {
            var styleOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offsetsStart + (stringCount + i) * 4));
            if (styleOffset != uint.MaxValue && styleOffset >= StyleData.Length)
                throw new AXmlException("Style is outside its pool.");
            StyleOffsets[i] = styleOffset;
        }
    }

    /// <summary>
    /// Gets the string at the given index; returns empty for out-of-range indices.
    /// <para>按索引获取字符串；越界返回空字符串。</para>
    /// </summary>
    /// <param name="index">Zero-based pool index; negative values are treated as out of range. <para>从零开始的池索引；负值视为越界。</para></param>
    /// <returns>The pool string, or <see cref="string.Empty"/> when the index is out of range, so
    /// references into other chunks never throw.
    /// <para>池内字符串；索引越界时返回 <see cref="string.Empty"/>，因此指向其他数据块的引用不会抛异常。</para></returns>
    public string GetString(int index) => (uint)index < _strings.Count ? _strings[index] : string.Empty;

    /// <summary>
    /// Appends a string and returns its index (reusing the existing index when already present).
    /// <para>追加字符串并返回其索引（若已存在则复用原索引）。</para>
    /// </summary>
    /// <param name="value">String to intern; compared with ordinal equality. <para>待驻留的字符串；按序数相等比较。</para></param>
    /// <returns>The index of the first entry equal to <paramref name="value"/>, appending it when no
    /// entry matches.
    /// <para>与 <paramref name="value"/> 相等的首个条目索引；无匹配时追加并返回新索引。</para></returns>
    public int Intern(string value)
    {
        var index = _index ??= BuildIndex();
        if (index.TryGetValue(value, out var existing))
        {
            return existing;
        }
        _strings.Add(value);
        index[value] = _strings.Count - 1;
        return _strings.Count - 1;
    }

    private Dictionary<string, int> BuildIndex()
    {
        var index = new Dictionary<string, int>(_strings.Count, StringComparer.Ordinal);
        for (var i = 0; i < _strings.Count; i++)
        {
            index.TryAdd(_strings[i], i);
        }
        return index;
    }

    private static string ReadUtf8String(byte[] data, int pos, int end)
    {


        var charLen = DecodeLength8(data, ref pos, end);
        var byteLen = DecodeLength8(data, ref pos, end);
        if (byteLen >= end - pos || data[pos + byteLen] != 0) throw new AXmlException("Truncated UTF-8 string.");
        var value = Encoding.UTF8.GetString(data, pos, byteLen);
        if (value.Length != charLen) throw new AXmlException("UTF-8 string length mismatch.");
        return value;
    }

    private static string ReadUtf16String(byte[] data, int pos, int end)
    {


        var len = DecodeLength16(data, ref pos, end);
        if (len < 0 || len >= (end - pos) / 2 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + len * 2)) != 0)
            throw new AXmlException("Truncated UTF-16 string.");
        return Encoding.Unicode.GetString(data.AsSpan(pos, len * 2));
    }

    private static int DecodeLength8(byte[] data, ref int pos, int end)
    {
        if (pos >= end) throw new AXmlException("Truncated string length.");
        var b0 = data[pos++];
        if ((b0 & 0x80) != 0)
        {
            if (pos >= end) throw new AXmlException("Truncated string length.");
            return ((b0 & 0x7f) << 8) | data[pos++];
        }
        return b0;
    }

    private static int DecodeLength16(byte[] data, ref int pos, int end)
    {
        if (end - pos < 2) throw new AXmlException("Truncated string length.");
        var b0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        pos += 2;
        if ((b0 & 0x8000) != 0)
        {
            if (end - pos < 2) throw new AXmlException("Truncated string length.");
            var lo = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            pos += 2;
            return ((b0 & 0x7fff) << 16) | lo;
        }
        return b0;
    }
}
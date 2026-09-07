using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.AXml;

public sealed class AXmlStringPool
{
    private const uint Utf8Flag = 1u << 8;

    private const uint SortedFlag = 1u << 0;

    private readonly List<string> _strings = [];
    private bool _isUtf8 = true;
    internal uint[] StyleOffsets { get; private set; } = [];
    internal byte[] StyleData { get; private set; } = [];

    public int Count => _strings.Count;

    public bool IsUtf8 => _isUtf8;

    public IReadOnlyList<string> Strings => _strings;

    /// <summary>
    /// Parses a ResStringPool chunk.
    /// <para>解析 ResStringPool 数据块。</para>
    /// </summary>
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
    public string GetString(int index) => (uint)index < _strings.Count ? _strings[index] : string.Empty;

    /// <summary>
    /// Appends a string and returns its index (reusing the existing index when already present).
    /// <para>追加字符串并返回其索引（若已存在则复用原索引）。</para>
    /// </summary>
    public int Intern(string value)
    {
        for (var i = 0; i < _strings.Count; i++)
        {
            if (string.Equals(_strings[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }
        _strings.Add(value);
        return _strings.Count - 1;
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
        var unitCount = len;
        var sb = new StringBuilder(unitCount);
        for (var i = 0; i < unitCount; i++)
        {
            var unit = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + i * 2));
            sb.Append((char)unit);
        }
        return sb.ToString();
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
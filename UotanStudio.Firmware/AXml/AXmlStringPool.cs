using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.AXml;

public sealed class AXmlStringPool
{
    private const uint Utf8Flag = 1u << 8;

    private const uint SortedFlag = 1u << 0;

    private readonly List<string> _strings = [];
    private bool _isUtf8 = true;

    public int Count => _strings.Count;

    public bool IsUtf8 => _isUtf8;

    public IReadOnlyList<string> Strings => _strings;

    /// <summary>
    /// Parses a ResStringPool chunk.
    /// <para>解析 ResStringPool 数据块。</para>
    /// </summary>
    public void Parse(byte[] data, int offset, int chunkSize)
    {
        var span = data.AsSpan(offset, chunkSize);
        var stringCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..]));
        var styleCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[12..]));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        var stringsStart = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[20..]));
        _isUtf8 = (flags & Utf8Flag) != 0;

        _strings.Clear();
        _strings.Capacity = stringCount;


        var offsetsStart = offset + 28;
        for (var i = 0; i < stringCount; i++)
        {
            var strOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offsetsStart + i * 4)));
            var strPos = offset + stringsStart + strOffset;
            var s = _isUtf8 ? ReadUtf8String(data, strPos) : ReadUtf16String(data, strPos);
            _strings.Add(s);
        }


        if (styleCount > 0)
        {
            var stylesStart = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[24..]));
            _ = stylesStart;
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

    private static string ReadUtf8String(byte[] data, int pos)
    {


        var charLen = DecodeLength8(data, ref pos);
        var byteLen = DecodeLength8(data, ref pos);
        _ = charLen;
        var strLen = Math.Min(byteLen, data.Length - pos);
        var strBytes = data.AsSpan(pos, strLen);

        var nul = strBytes.IndexOf((byte)0);
        if (nul >= 0) strBytes = strBytes[..nul];
        return Encoding.UTF8.GetString(strBytes);
    }

    private static string ReadUtf16String(byte[] data, int pos)
    {


        var len = DecodeLength16(data, ref pos);
        var maxUnits = (data.Length - pos) / 2;
        var unitCount = Math.Min(len, maxUnits);
        var sb = new StringBuilder(unitCount);
        for (var i = 0; i < unitCount; i++)
        {
            var unit = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + i * 2));
            if (unit == 0) break;
            sb.Append((char)unit);
        }
        return sb.ToString();
    }

    private static int DecodeLength8(byte[] data, ref int pos)
    {
        var b0 = data[pos++];
        if ((b0 & 0x80) != 0 && pos < data.Length)
        {
            return ((b0 & 0x7f) << 8) | data[pos++];
        }
        return b0;
    }

    private static int DecodeLength16(byte[] data, ref int pos)
    {
        var b0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        pos += 2;
        if ((b0 & 0x8000) != 0 && pos + 2 <= data.Length)
        {
            var lo = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            pos += 2;
            return ((b0 & 0x7fff) << 16) | lo;
        }
        return b0;
    }
}

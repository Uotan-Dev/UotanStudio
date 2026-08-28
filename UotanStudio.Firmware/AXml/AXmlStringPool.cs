using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.AXml;

/// <summary>
/// <para>AXML 字符串池（<c>ResStringPool</c>）：UTF-8/UTF-16 两种编码的字符串集合，
/// 支持按索引读取与追加（重建用）。</para>
/// AXML string pool (<c>ResStringPool</c>): a collection of UTF-8/UTF-16 strings with
/// index-based reads and appends (for rebuilding).
/// </summary>
public sealed class AXmlStringPool
{
    /// <summary>
    /// <para>字符串池标志位：UTF-8 编码（对应 AOSP <c>UTF8_FLAG</c>）。</para>
    /// String pool flag: UTF-8 encoding (AOSP <c>UTF8_FLAG</c>).
    /// </summary>
    private const uint Utf8Flag = 1u << 8;

    /// <summary>
    /// <para>排序标志位（解析时忽略，重建时不保持）。</para>
    /// Sorted flag (ignored on parse; not preserved on rebuild).
    /// </summary>
    private const uint SortedFlag = 1u << 0;

    private readonly List<string> _strings = [];
    private bool _isUtf8 = true;

    /// <summary>字符串总数。<para>Total string count.</para></summary>
    public int Count => _strings.Count;

    /// <summary>是否为 UTF-8 编码池。<para>Whether the pool is UTF-8 encoded.</para></summary>
    public bool IsUtf8 => _isUtf8;

    /// <summary>只读访问所有字符串（重建用）。<para>Read-only access to all strings (for rebuild).</para></summary>
    public IReadOnlyList<string> Strings => _strings;

    /// <summary>
    /// <para>解析 <c>ResStringPool</c> 数据块。返回消耗的字节数。</para>
    /// Parses a <c>ResStringPool</c> chunk. Returns the consumed byte count.
    /// </summary>
    /// <param name="data">文件字节。<para>File bytes.</para></param>
    /// <param name="offset">数据块起始偏移。<para>Chunk start offset.</para></param>
    /// <param name="chunkSize">数据块总大小（含头部）。<para>Total chunk size (including header).</para></param>
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

        // 字符串偏移表（相对池起始）。
        // String offset table (relative to the pool start).
        var offsetsStart = offset + 28;
        for (var i = 0; i < stringCount; i++)
        {
            var strOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offsetsStart + i * 4)));
            var strPos = offset + stringsStart + strOffset;
            var s = _isUtf8 ? ReadUtf8String(data, strPos) : ReadUtf16String(data, strPos);
            _strings.Add(s);
        }

        // 样式表偏移（仅验证范围，样式内容忽略——AXML 样式不参与展示）。
        // Style offsets are only range-checked; style data is ignored for display.
        if (styleCount > 0)
        {
            var stylesStart = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[24..]));
            _ = stylesStart; // 样式表仅重建时原样保留；解析阶段忽略内容
        }
    }

    /// <summary>
    /// <para>按索引获取字符串；越界返回空字符串。</para>
    /// Gets the string at the given index; returns empty for out-of-range indices.
    /// </summary>
    public string GetString(int index) => (uint)index < _strings.Count ? _strings[index] : string.Empty;

    /// <summary>
    /// <para>追加字符串并返回其索引（若已存在则复用原索引）。</para>
    /// Appends a string and returns its index (reusing the existing index when already present).
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
        // 变长长度头，顺序按 AOSP 规范：先 UTF-16 字符数，后 UTF-8 字节数；
        // 首字节 0x80 高位表示两字节长度（AOSP DecodeLength）。
        // Variable-length header in AOSP order: UTF-16 char count first, UTF-8 byte count second;
        // the 0x80 high bit on the leading byte marks a two-byte length.
        var charLen = DecodeLength8(data, ref pos);
        var byteLen = DecodeLength8(data, ref pos);
        _ = charLen; // UTF-16 字符数仅校验用，解码以字节为准
        var strLen = Math.Min(byteLen, data.Length - pos);
        var strBytes = data.AsSpan(pos, strLen);
        // 终止 NUL 之后不读入
        var nul = strBytes.IndexOf((byte)0);
        if (nul >= 0) strBytes = strBytes[..nul];
        return Encoding.UTF8.GetString(strBytes);
    }

    private static string ReadUtf16String(byte[] data, int pos)
    {
        // UTF-16 池：两字节长度；高位 0x8000 表示四字节长度。
        // UTF-16 pool: a two-byte length; the 0x8000 high bit marks a four-byte length.
        var len = DecodeLength16(data, ref pos);
        var maxUnits = (data.Length - pos) / 2;
        var unitCount = Math.Min(len, maxUnits);
        var sb = new StringBuilder(unitCount);
        for (var i = 0; i < unitCount; i++)
        {
            var unit = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + i * 2));
            if (unit == 0) break; // 终止 NUL
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
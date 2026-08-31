namespace UotanStudio.Firmware.Zucchini;

/// <summary>
/// LEB128-style variable-length integer encoding used by the Zucchini patch data streams.
/// Values are written little-endian, seven payload bits per byte, with the high bit marking a
/// continuation byte. The convention matches standard LEB128 as used by upstream zucchini.
/// <para>Zucchini 补丁数据流使用的小端序变长整数（LEB128 风格）编码。每字节携带 7 位有效载荷，
/// 高位为续字节标记，与上游 zucchini 使用的标准 LEB128 一致。</para>
/// </summary>
internal static class ZucchiniLeb128
{
    /// <summary>
    /// Reads a varint from <paramref name="source"/> starting at <paramref name="offset"/>.
    /// <para>从 <paramref name="source"/> 的 <paramref name="offset"/> 处读取一个 varint。</para>
    /// </summary>
    /// <param name="source">The byte span to read from. <para>要读取的字节范围。</para></param>
    /// <param name="offset">The start offset. <para>起始偏移。</para></param>
    /// <param name="value">The decoded value, valid only when the return value is non-negative.
    /// <para>解码后的值，仅当返回值为非负时有效。</para></param>
    /// <returns>The number of bytes consumed, or -1 when the stream ends early or the value overflows a uint32.
    /// <para>消耗的字节数；流提前结束或值溢出 uint32 时返回 -1。</para></returns>
    public static int TryRead(ReadOnlySpan<byte> source, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset >= source.Length)
        {
            return -1;
        }

        uint result = 0;
        int shift = 0;
        int i = offset;
        while (true)
        {
            if (i >= source.Length)
            {
                return -1;
            }

            byte b = source[i++];
            bool more = (b & 0x80) != 0;
            if (shift == 28)
            {
                // Fifth and final byte: only the low 4 bits carry payload.
                if (more || b > 0x0F)
                {
                    return -1;
                }
            }

            result |= (uint)(b & 0x7F) << shift;
            if (!more)
            {
                value = result;
                return i - offset;
            }

            shift += 7;
            if (shift > 28)
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// Appends the LEB128 encoding of <paramref name="value"/> to <paramref name="buffer"/>.
    /// <para>将 <paramref name="value"/> 的 LEB128 编码追加到 <paramref name="buffer"/>。</para>
    /// </summary>
    /// <param name="value">The value to encode. <para>要编码的值。</para></param>
    /// <param name="buffer">The output buffer. <para>输出缓冲区。</para></param>
    public static void Write(uint value, List<byte> buffer)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }
            buffer.Add(b);
        }
        while (value != 0);
    }
}

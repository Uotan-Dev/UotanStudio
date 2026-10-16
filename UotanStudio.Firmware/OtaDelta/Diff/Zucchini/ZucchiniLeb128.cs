namespace UotanStudio.Firmware.OtaDelta.Diff.Zucchini;

internal static class ZucchiniLeb128
{
    /// <summary>
    /// Reads a varint from source starting at offset.
    /// <para>从 source 的 offset 处读取一个 varint。</para>
    /// </summary>
    /// <param name="source">Buffer holding the encoded bytes. <para>容纳编码字节的缓冲区。</para></param>
    /// <param name="offset">Index of the first varint byte; a negative index or one at or past the end of <paramref name="source"/> reads nothing.
    /// <para>varint 首字节索引；负值或等于、超过 <paramref name="source"/> 末尾时读不出任何内容。</para></param>
    /// <param name="value">Decoded 32-bit number on success; <c>0</c> when the read failed, including a partially decoded sequence.
    /// <para>成功时为解码出的 32 位数值；读取失败时为 <c>0</c>，包括只解码了一半的序列。</para></param>
    /// <returns>Bytes consumed, from 1 to 5, or <c>-1</c> when the offset is outside the buffer, the sequence runs past the end, or the encoding needs more than five bytes or more than 32 bits.
    /// <para>消耗的字节数，取值 1 到 5；偏移越界、序列超出缓冲区末尾，或编码需要超过 5 字节或超出 32 位时返回 <c>-1</c>。</para></returns>
    public static int TryRead(ReadOnlySpan<byte> source, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset >= source.Length)
        {
            return -1;
        }

        uint result = 0;
        var shift = 0;
        var i = offset;
        while (true)
        {
            if (i >= source.Length)
            {
                return -1;
            }

            var b = source[i++];
            var more = (b & 0x80) != 0;
            if (shift == 28)
            {

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
    /// Appends the LEB128 encoding of value to buffer.
    /// <para>将 value 的 LEB128 编码追加到 buffer。</para>
    /// </summary>
    /// <param name="value">Unsigned number to encode; <c>0</c> produces a single zero byte and <see cref="uint.MaxValue"/> produces five.
    /// <para>待编码的无符号数值；<c>0</c> 产生一个零字节，<see cref="uint.MaxValue"/> 产生五个字节。</para></param>
    /// <param name="buffer">List receiving the encoded bytes after its current contents; every byte keeps its low seven bits and sets bit 7 on all but the last.
    /// <para>在当前内容之后接收编码字节的列表；每个字节保留低七位，除最后一个字节外都置位 bit 7。</para></param>
    public static void Write(uint value, List<byte> buffer)
    {
        do
        {
            var b = (byte)(value & 0x7F);
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
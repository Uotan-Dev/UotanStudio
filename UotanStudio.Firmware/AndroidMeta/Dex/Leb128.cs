namespace UotanStudio.Firmware.AndroidMeta.Dex;

/// <summary>
/// Reads and writes the 32-bit unsigned and signed LEB128 encodings used by the DEX binary format.
/// <para>读写 DEX 二进制格式使用的 32 位无符号与有符号 LEB128 编码。</para>
/// </summary>
internal static class Leb128
{
    /// <summary>
    /// Maximum number of bytes in a 32-bit LEB128 encoding (5 × 7 = 35 ≥ 32 bits).
    /// <para>32 位 LEB128 编码的最大字节数（5 × 7 = 35 ≥ 32 位）。</para>
    /// </summary>
    public const int MaxEncodedBytes = 5;

    /// <summary>
    /// Reads an unsigned LEB128 integer, bounded to at most 5 bytes.
    /// On malformed input (truncated, over-long, or trailing continuation) a
    /// <see cref="FormatException"/> is thrown and <paramref name="offset"/> is left
    /// unchanged so callers can distinguish "no data consumed" from partial consumption.
    /// <para>读取一个无符号 LEB128 整数，最多消费 5 字节。遇到截断、超长或多余续位时抛出
    /// <see cref="FormatException"/>，<paramref name="offset"/> 保持不变。</para>
    /// </summary>
    /// <param name="data">
    /// Source bytes; <paramref name="offset"/> must point at the first byte of the encoding.
    /// <para>源字节；<paramref name="offset"/> 必须指向编码的首字节。</para>
    /// </param>
    /// <param name="offset">
    /// Byte index to read from, advanced past the last consumed byte on success and left unchanged on failure.
    /// <para>读取的字节索引；成功时前进到最后消费的字节之后，失败时保持不变。</para>
    /// </param>
    /// <returns>The decoded 32-bit unsigned value.
    /// <para>解码得到的 32 位无符号值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative.
    /// <para><paramref name="offset"/> 为负数。</para>
    /// </exception>
    /// <exception cref="FormatException">
    /// No byte is available at <paramref name="offset"/>, the input ends on a continuation byte,
    /// or the encoding spans more than 5 bytes.
    /// <para><paramref name="offset"/> 处无可用字节、输入以续位字节结束，或编码超过 5 字节。</para>
    /// </exception>
    public static uint ReadUleb128(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must not be negative.");
        }

        if (offset >= data.Length)
        {
            throw new FormatException("ULEB128 input is empty or offset is at end of data.");
        }

        var startOffset = offset;
        uint result = 0;
        for (var i = 0; i < MaxEncodedBytes; i++)
        {
            if (offset >= data.Length)
            {
                offset = startOffset;
                throw new FormatException("ULEB128 input ended with a continuation byte set.");
            }

            var b = data[offset++];
            result |= (uint)(b & 0x7f) << (i * 7);
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        offset = startOffset;
        throw new FormatException("ULEB128 value exceeds 32 bits (more than 5 bytes of continuation).");
    }

    /// <summary>
    /// Reads a signed LEB128 integer, bounded to at most 5 bytes.
    /// On malformed input a <see cref="FormatException"/> is thrown and
    /// <paramref name="offset"/> is left unchanged.
    /// <para>读取一个有符号 LEB128 整数，最多消费 5 字节。遇到畸形输入时抛出
    /// <see cref="FormatException"/>，<paramref name="offset"/> 保持不变。</para>
    /// </summary>
    /// <param name="data">
    /// Source bytes; <paramref name="offset"/> must point at the first byte of the encoding.
    /// <para>源字节；<paramref name="offset"/> 必须指向编码的首字节。</para>
    /// </param>
    /// <param name="offset">
    /// Byte index to read from, advanced past the last consumed byte on success and left unchanged on failure.
    /// <para>读取的字节索引；成功时前进到最后消费的字节之后，失败时保持不变。</para>
    /// </param>
    /// <returns>The decoded 32-bit signed value, sign-extended from the final group.
    /// <para>解码得到的 32 位有符号值，由最后一组进行符号扩展。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative.
    /// <para><paramref name="offset"/> 为负数。</para>
    /// </exception>
    /// <exception cref="FormatException">
    /// No byte is available at <paramref name="offset"/>, the input ends on a continuation byte,
    /// or the encoding spans more than 5 bytes.
    /// <para><paramref name="offset"/> 处无可用字节、输入以续位字节结束，或编码超过 5 字节。</para>
    /// </exception>
    public static int ReadSleb128(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must not be negative.");
        }

        if (offset >= data.Length)
        {
            throw new FormatException("SLEB128 input is empty or offset is at end of data.");
        }

        var startOffset = offset;
        var result = 0;
        for (var i = 0; i < MaxEncodedBytes; i++)
        {
            if (offset >= data.Length)
            {
                offset = startOffset;
                throw new FormatException("SLEB128 input ended with a continuation byte set.");
            }

            var b = data[offset++];
            result |= (b & 0x7f) << (i * 7);
            if ((b & 0x80) == 0)
            {
                if (i < 4 && (b & 0x40) != 0)
                {
                    result |= ~0 << ((i + 1) * 7);
                }
                return result;
            }
        }

        offset = startOffset;
        throw new FormatException("SLEB128 value exceeds 32 bits (more than 5 bytes of continuation).");
    }

    /// <summary>
    /// Encodes an unsigned integer into a LEB128 byte sequence.
    /// <para>把无符号整数编码为 LEB128 字节序列。</para>
    /// </summary>
    /// <param name="buffer">
    /// Destination span; it must have room from <paramref name="offset"/> for <c>Uleb128Size(value)</c> bytes.
    /// <para>目标切片；从 <paramref name="offset"/> 起必须容纳 <c>Uleb128Size(value)</c> 字节。</para>
    /// </param>
    /// <param name="offset">Index of the first byte to write. <para>写入起始位置的索引。</para></param>
    /// <param name="value">Value to encode. <para>待编码的值。</para></param>
    /// <returns>The index just after the last written byte.
    /// <para>最后一个已写入字节之后的索引。</para></returns>
    public static int WriteUleb128(Span<byte> buffer, int offset, uint value)
    {
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0) b |= 0x80;
            buffer[offset++] = b;
        } while (value != 0);
        return offset;
    }

    /// <summary>
    /// Encodes a signed integer into a LEB128 byte sequence.
    /// <para>把有符号整数编码为 LEB128 字节序列。</para>
    /// </summary>
    /// <param name="buffer">
    /// Destination span; it must have room from <paramref name="offset"/> for <c>Sleb128Size(value)</c> bytes.
    /// <para>目标切片；从 <paramref name="offset"/> 起必须容纳 <c>Sleb128Size(value)</c> 字节。</para>
    /// </param>
    /// <param name="offset">Index of the first byte to write. <para>写入起始位置的索引。</para></param>
    /// <param name="value">Value to encode. <para>待编码的值。</para></param>
    /// <returns>The index just after the last written byte.
    /// <para>最后一个已写入字节之后的索引。</para></returns>
    public static int WriteSleb128(Span<byte> buffer, int offset, int value)
    {
        var more = true;
        while (more)
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            var sign = (b & 0x40) != 0;
            more = !((value == 0 && !sign) || (value == -1 && sign));
            if (more) b |= 0x80;
            buffer[offset++] = b;
        }
        return offset;
    }

    /// <summary>
    /// Computes the encoded byte length of an unsigned LEB128 value.
    /// <para>计算无符号 LEB128 编码的字节长度。</para>
    /// </summary>
    /// <param name="value">Value to measure. <para>待测量的值。</para></param>
    /// <returns>Encoded byte count, from 1 to 5. <para>编码字节数，范围 1 到 5。</para></returns>
    public static int Uleb128Size(uint value)
    {
        var count = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Computes the encoded byte length of a signed LEB128 value.
    /// <para>计算有符号 LEB128 编码的字节长度。</para>
    /// </summary>
    /// <param name="value">Value to measure. <para>待测量的值。</para></param>
    /// <returns>Encoded byte count, from 1 to 5. <para>编码字节数，范围 1 到 5。</para></returns>
    public static int Sleb128Size(int value)
    {
        var count = 1;
        while (true)
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            var sign = (b & 0x40) != 0;
            if ((value == 0 && !sign) || (value == -1 && sign)) break;
            count++;
        }
        return count;
    }
}
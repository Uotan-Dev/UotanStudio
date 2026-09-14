namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;

public static class Leb128
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

        // A 6th byte would be needed — over-long for a 32-bit value.
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
                // Sign-extend if the sign bit (0x40) is set and there is room.
                if (i < 4 && (b & 0x40) != 0)
                {
                    result |= ~0 << ((i + 1) * 7);
                }
                return result;
            }
        }

        // A 6th byte would be needed — over-long for a 32-bit value.
        offset = startOffset;
        throw new FormatException("SLEB128 value exceeds 32 bits (more than 5 bytes of continuation).");
    }

    /// <summary>
    /// Encodes an unsigned integer into a LEB128 byte sequence.
    /// <para>把无符号整数编码为 LEB128 字节序列。</para>
    /// </summary>
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
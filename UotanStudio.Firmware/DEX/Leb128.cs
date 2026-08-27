namespace UotanStudio.Firmware.Dex;

/// <summary>
/// <para>LEB128 变长整数读写工具（DEX 文件内部索引与长度的标准编码）。</para>
/// LEB128 variable-length integer read/write helpers (standard encoding for indices and lengths inside DEX files).
/// </summary>
public static class Leb128
{
    /// <summary>
    /// <para>读取一个无符号 LEB128 整数。</para>
    /// Reads an unsigned LEB128 integer.
    /// </summary>
    public static uint ReadUleb128(ReadOnlySpan<byte> data, ref int offset)
    {
        uint result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[offset++];
            result |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    /// <summary>
    /// <para>读取一个有符号 LEB128 整数。</para>
    /// Reads a signed LEB128 integer.
    /// </summary>
    public static int ReadSleb128(ReadOnlySpan<byte> data, ref int offset)
    {
        var result = 0;
        var shift = 0;
        byte b;
        while (true)
        {
            b = data[offset++];
            result |= (b & 0x7f) << shift;
            shift += 7;
            if ((b & 0x80) == 0) break;
        }
        // 与 Android readSignedLeb128 一致：仅当 shift &lt; 32 时才做符号扩展，
        // 否则 5 字节负值（如 int.MinValue）的高位已在移位中溢出进符号位。
        // Match Android's readSignedLeb128: sign-extend only when shift &lt; 32;
        // otherwise a 5-byte negative (e.g. int.MinValue) already overflowed its
        // high bits into the sign bit during the shifts.
        if (shift < 32 && (b & 0x40) != 0)
        {
            result |= ~0 << shift;
        }
        return result;
    }

    /// <summary>
    /// <para>把无符号整数编码为 LEB128 字节序列。</para>
    /// Encodes an unsigned integer into a LEB128 byte sequence.
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
    /// <para>把有符号整数编码为 LEB128 字节序列。</para>
    /// Encodes a signed integer into a LEB128 byte sequence.
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
    /// <para>计算无符号 LEB128 编码的字节长度。</para>
    /// Computes the encoded byte length of an unsigned LEB128 value.
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
    /// <para>计算有符号 LEB128 编码的字节长度。</para>
    /// Computes the encoded byte length of a signed LEB128 value.
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
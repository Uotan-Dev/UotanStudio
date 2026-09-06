namespace UotanStudio.Firmware.Dex;

public static class Leb128
{
    /// <summary>
    /// Reads an unsigned LEB128 integer.
    /// <para>读取一个无符号 LEB128 整数。</para>
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
    /// Reads a signed LEB128 integer.
    /// <para>读取一个有符号 LEB128 整数。</para>
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


        if (shift < 32 && (b & 0x40) != 0)
        {
            result |= ~0 << shift;
        }
        return result;
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

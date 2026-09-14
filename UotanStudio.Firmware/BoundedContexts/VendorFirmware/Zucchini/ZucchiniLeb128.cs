namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Zucchini;

internal static class ZucchiniLeb128
{
    /// <summary>
    /// Reads a varint from source starting at offset.
    /// <para>从 source 的 offset 处读取一个 varint。</para>
    /// </summary>
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
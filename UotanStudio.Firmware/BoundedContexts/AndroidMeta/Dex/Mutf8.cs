using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;

public static class Mutf8
{
    /// <summary>
    /// Encodes a .NET string into a MUTF-8 byte sequence (without the terminating NUL).
    /// <para>把 .NET 字符串编码为 MUTF-8 字节序列（不含终止 NUL）。</para>
    /// </summary>
    public static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = new byte[EncodedLength(value)];
        var i = 0;
        foreach (var c in value)
        {
            if (c == 0)
            {
                bytes[i++] = 0xc0;
                bytes[i++] = 0x80;
            }
            else if (c < 0x80)
            {
                bytes[i++] = (byte)c;
            }
            else if (c < 0x800)
            {
                bytes[i++] = (byte)(0xc0 | (c >> 6));
                bytes[i++] = (byte)(0x80 | (c & 0x3f));
            }
            else
            {


                bytes[i++] = (byte)(0xe0 | (c >> 12));
                bytes[i++] = (byte)(0x80 | ((c >> 6) & 0x3f));
                bytes[i++] = (byte)(0x80 | (c & 0x3f));
            }
        }
        return bytes;
    }

    /// <summary>
    /// Decodes a MUTF-8 byte sequence into a .NET string (byteCount bytes, no trailing NUL).
    /// <para>把 MUTF-8 字节序列解码为 .NET 字符串（字节数 byteCount，不含终止 NUL）。</para>
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> data, int byteCount)
    {
        if (byteCount == 0) return string.Empty;
        return DecodeSlow(data, byteCount);
    }

    /// <summary>
    /// Decodes a MUTF-8 byte sequence from a byte[] slice into a .NET string (main parsing path; single allocation, no StringBuilder).
    /// <para>把 byte[] 切片中的 MUTF-8 字节序列解码为 .NET 字符串（解析主路径，一次分配，无 StringBuilder）。</para>
    /// </summary>
    public static string Decode(byte[] data, int start, int byteCount)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (byteCount == 0) return string.Empty;
        var slice = data.AsSpan(start, byteCount);
        var charCount = CountChars(slice, byteCount);
        return string.Create(charCount, (data, start, byteCount), static (span, state) =>
        {
            DecodeInto(state.data.AsSpan(state.start, state.byteCount), state.byteCount, span);
        });
    }

    /// <summary>
    /// Counts the UTF-16 code units a MUTF-8 byte sequence decodes to.
    /// <para>统计 MUTF-8 字节序列解码后的 UTF-16 字符数。</para>
    /// </summary>
    private static int CountChars(ReadOnlySpan<byte> data, int byteCount)
    {
        var count = 0;
        var i = 0;
        while (i < byteCount)
        {
            var b0 = data[i++];
            if (b0 < 0x80) count++;
            else if ((b0 & 0xe0) == 0xc0)
            {
                if (i < byteCount) i++;
                count++;
            }
            else if ((b0 & 0xf0) == 0xe0)
            {
                i += 2;
                count++;
            }
            else
            {


                i += 3;
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Fills a char buffer from a MUTF-8 byte sequence.
    /// <para>把 MUTF-8 字节序列填充进字符缓冲区。</para>
    /// </summary>
    private static void DecodeInto(ReadOnlySpan<byte> data, int byteCount, Span<char> output)
    {
        var i = 0;
        var pos = 0;
        while (i < byteCount)
        {
            var b0 = data[i++];
            if (b0 < 0x80)
            {
                output[pos++] = (char)b0;
            }
            else if ((b0 & 0xe0) == 0xc0)
            {
                var b1 = i < byteCount ? data[i++] : (byte)0;
                output[pos++] = (char)(((b0 & 0x1f) << 6) | (b1 & 0x3f));
            }
            else if ((b0 & 0xf0) == 0xe0)
            {
                var b1 = i < byteCount ? data[i++] : (byte)0;
                var b2 = i < byteCount ? data[i++] : (byte)0;
                output[pos++] = (char)(((b0 & 0x0f) << 12) | ((b1 & 0x3f) << 6) | (b2 & 0x3f));
            }
            else
            {


                i += 3;
                output[pos++] = '\uFFFD';
            }
        }
    }

    /// <summary>
    /// StringBuilder fallback decoder (byte-for-byte identical to the previous behavior).
    /// <para>StringBuilder 回退解码实现（与旧行为逐字节一致）。</para>
    /// </summary>
    private static string DecodeSlow(ReadOnlySpan<byte> data, int byteCount)
    {
        var sb = new StringBuilder(byteCount);
        var i = 0;
        while (i < byteCount)
        {
            var b0 = data[i++];
            if (b0 == 0)
            {
                sb.Append('\0');
            }
            else if (b0 < 0x80)
            {
                sb.Append((char)b0);
            }
            else if ((b0 & 0xe0) == 0xc0)
            {
                var b1 = i < byteCount ? data[i++] : (byte)0;
                var cp = ((b0 & 0x1f) << 6) | (b1 & 0x3f);
                sb.Append((char)cp);
            }
            else if ((b0 & 0xf0) == 0xe0)
            {
                var b1 = i < byteCount ? data[i++] : (byte)0;
                var b2 = i < byteCount ? data[i++] : (byte)0;
                var cp = ((b0 & 0x0f) << 12) | ((b1 & 0x3f) << 6) | (b2 & 0x3f);
                sb.Append((char)cp);
            }
            else
            {


                i += 3;
                sb.Append('\uFFFD');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Computes the MUTF-8 encoded byte length of a string (without the terminating NUL).
    /// <para>计算字符串的 MUTF-8 编码字节长度（不含终止 NUL）。</para>
    /// </summary>
    public static int EncodedLength(string value)
    {
        var length = 0;
        foreach (var c in value)
        {
            if (c == 0) length += 2;
            else if (c < 0x80) length += 1;
            else if (c < 0x800) length += 2;
            else length += 3;
        }
        return length;
    }
}

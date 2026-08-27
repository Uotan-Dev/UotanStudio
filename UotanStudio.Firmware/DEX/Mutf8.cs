using System.Text;

namespace UotanStudio.Firmware.Dex;

/// <summary>
/// <para>Modified UTF-8 编解码（DEX 字符串表专用编码：U+0000 编码为 C0 80，增补字符以代理对形式各编码 3 字节）。</para>
/// Modified UTF-8 codec (the encoding used by the DEX string table: U+0000 encodes as C0 80, supplementary characters
/// are encoded as surrogate pairs, each 3 bytes).
/// </summary>
public static class Mutf8
{
    /// <summary>
    /// <para>把 .NET 字符串编码为 MUTF-8 字节序列（不含终止 NUL）。</para>
    /// Encodes a .NET string into a MUTF-8 byte sequence (without the terminating NUL).
    /// </summary>
    public static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var ms = new MemoryStream(value.Length * 2 + 8);
        foreach (var c in value)
        {
            if (c == 0)
            {
                ms.WriteByte(0xc0);
                ms.WriteByte(0x80);
            }
            else if (c < 0x80)
            {
                ms.WriteByte((byte)c);
            }
            else if (c < 0x800)
            {
                ms.WriteByte((byte)(0xc0 | (c >> 6)));
                ms.WriteByte((byte)(0x80 | (c & 0x3f)));
            }
            else if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c))
            {
                // 代理对已由 .NET 迭代器展开为两个 char，各按 3 字节编码
                // Surrogate halves are enumerated as two chars, each encoded in 3 bytes.
                ms.WriteByte((byte)(0xe0 | (c >> 12)));
                ms.WriteByte((byte)(0x80 | ((c >> 6) & 0x3f)));
                ms.WriteByte((byte)(0x80 | (c & 0x3f)));
            }
            else
            {
                ms.WriteByte((byte)(0xe0 | (c >> 12)));
                ms.WriteByte((byte)(0x80 | ((c >> 6) & 0x3f)));
                ms.WriteByte((byte)(0x80 | (c & 0x3f)));
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// <para>把 MUTF-8 字节序列解码为 .NET 字符串（字节数 <paramref name="byteCount"/>，不含终止 NUL）。</para>
    /// Decodes a MUTF-8 byte sequence into a .NET string (<paramref name="byteCount"/> bytes, no trailing NUL).
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> data, int byteCount)
    {
        if (byteCount == 0) return string.Empty;
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
                // 4 字节序列（非常规 MUTF-8，宽松容忍）
                // 4-byte sequence (non-standard MUTF-8; tolerated leniently).
                i += 3;
                sb.Append('\uFFFD');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// <para>计算字符串的 MUTF-8 编码字节长度（不含终止 NUL）。</para>
    /// Computes the MUTF-8 encoded byte length of a string (without the terminating NUL).
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
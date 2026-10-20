using System.Globalization;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Upstream base.h capability TLV: 16-byte tag, 16-byte decimal byte length, UTF-8 value.</summary>
internal static class HdcCapabilities
{
    /// <summary>
    /// Encodes a capability map into the upstream TLV layout: every entry is a space-padded 16-byte tag, a space-padded
    /// 16-byte decimal UTF-8 byte length and the value itself, concatenated without separators in enumeration order.
    /// <para>把能力映射编码为上游 TLV 布局：每个条目由空格填充的 16 字节标签、空格填充的 16 字节十进制 UTF-8 字节
    /// 长度与值本身组成，按枚举顺序无分隔符拼接。</para>
    /// </summary>
    /// <param name="values">The capabilities to encode. <para>待编码的能力。</para></param>
    /// <returns>The encoded TLV bytes, ready to be carried in a handshake buffer. <para>编码后的 TLV 字节，可直接作为握手缓冲区内容。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.
    /// <para><paramref name="values"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">A tag is empty, longer than 16 characters, or contains a non-ASCII character.
    /// <para>标签为空、超过 16 个字符或包含非 ASCII 字符。</para></exception>
    internal static byte[] Encode(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        using var output = new MemoryStream();
        foreach (var (tag, value) in values)
        {
            if (tag.Length is 0 or > 16 || tag.Any(c => c is < ' ' or > '~'))
            {
                throw new ArgumentException("HDC capability tags must be 1-16 ASCII characters.", nameof(values));
            }

            var payload = Encoding.UTF8.GetBytes(value);
            var header = tag.PadRight(16) + payload.Length.ToString(CultureInfo.InvariantCulture).PadRight(16);
            output.Write(Encoding.ASCII.GetBytes(header));
            output.Write(payload);
        }

        return output.ToArray();
    }
    internal static Dictionary<string, string> Decode(ReadOnlySpan<byte> bytes)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        while (!bytes.IsEmpty)
        {
            if (bytes.Length < 32) throw new InvalidDataException("Truncated HDC capability header.");
            var tag = Encoding.ASCII.GetString(bytes[..16].TrimEnd((byte)' '));
            if (tag.Length == 0 || !int.TryParse(bytes.Slice(16, 16).TrimEnd((byte)' '),
                NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > bytes.Length - 32)
                throw new InvalidDataException("Invalid HDC capability length.");
            if (!values.TryAdd(tag, Encoding.UTF8.GetString(bytes.Slice(32, length))))
                throw new InvalidDataException("Duplicate HDC capability.");
            bytes = bytes[(32 + length)..];
        }
        return values;
    }
}
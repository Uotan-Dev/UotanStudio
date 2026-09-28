using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

public static partial class AvbSignatureVerifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Reads a complete descriptor table, rejecting malformed entries instead of returning a partial list.
    /// <para>读取完整描述符表，拒绝畸形条目而不返回部分列表。</para>
    /// </summary>
    /// <param name="vbmetaData">VBMeta bytes with bounded auxiliary and descriptor regions.
    /// <para>包含有界辅助区和描述符区的 VBMeta 字节。</para></param>
    /// <returns>All descriptors, including unknown tags without interpretation; at most 4096 entries and 16 MiB of descriptor bytes.
    /// <para>全部描述符，包含未解释的未知标签；最多 4096 个条目、16 MiB 描述符字节。</para></returns>
    /// <exception cref="InvalidDataException">A region, entry length, string, terminator or inspection limit is invalid.
    /// <para>区域、条目长度、字符串、终止符或检查限制无效。</para></exception>
    public static IReadOnlyList<AvbDescriptorInfo> ReadDescriptorsStrict(byte[] vbmetaData)
    {
        if (vbmetaData is not { Length: >= HeaderSize } || !vbmetaData.AsSpan(0, 4).SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("Invalid VBMeta descriptor container.");
        }
        var data = vbmetaData.AsSpan();
        var authentication = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(12, 8));
        var auxiliary = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(20, 8));
        var offset = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(96, 8));
        var size = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(104, 8));
        var available = (ulong)data.Length - HeaderSize;
        if (authentication > available || auxiliary > available - authentication
            || offset > auxiliary || size > auxiliary - offset || size > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("VBMeta descriptor region exceeds its container or the 16 MiB limit.");
        }
        var start = HeaderSize + (int)authentication + (int)offset;
        var end = start + (int)size;
        var result = new List<AvbDescriptorInfo>();
        for (var cursor = start; cursor < end;)
        {
            if (end - cursor < 16 || result.Count == 4096)
            {
                throw new InvalidDataException("The descriptor header is truncated or the count exceeds 4096.");
            }
            var tag = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(cursor, 8));
            var length = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(cursor + 8, 8));
            if (length % 8 != 0 || length > (ulong)(end - cursor - 16))
            {
                throw new InvalidDataException("The descriptor body is unaligned or exceeds its declared region.");
            }
            var body = data.Slice(cursor + 16, (int)length);
            ValidateDescriptorBody(tag, body);
            result.Add(ParseDescriptor(tag, (long)length, vbmetaData, cursor + 16));
            cursor += 16 + (int)length;
        }
        return result;
    }

    private static void ValidateDescriptorBody(ulong tag, ReadOnlySpan<byte> body)
    {
        var fixedSize = tag switch { 0 => 16, 1 => 164, 2 => 116, 3 => 8, 4 => 76, _ => 0 };
        if (fixedSize == 0)
        {
            return;
        }
        if (body.Length < fixedSize)
        {
            throw new InvalidDataException("The descriptor body is smaller than its fixed fields.");
        }
        ulong first;
        ulong second = 0;
        ulong third = 0;
        switch (tag)
        {
            case 0:
                first = BinaryPrimitives.ReadUInt64BigEndian(body);
                second = BinaryPrimitives.ReadUInt64BigEndian(body.Slice(8, 8));
                break;
            case 1:
                first = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(88, 4));
                second = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(92, 4));
                third = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(96, 4));
                break;
            case 2:
                first = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(40, 4));
                second = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(44, 4));
                third = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(48, 4));
                break;
            case 3:
                first = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
                break;
            default:
                first = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
                second = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
                break;
        }
        var available = (ulong)(body.Length - fixedSize);
        var terminators = tag == 0 ? 2UL : 0UL;
        if (first > available || second > available - first || third > available - first - second
            || terminators > available - first - second - third)
        {
            throw new InvalidDataException("Variable descriptor fields exceed their own descriptor body.");
        }
        var used = fixedSize + (int)(first + second + third + terminators);
        if (((used + 7) & ~7) != body.Length)
        {
            throw new InvalidDataException("The descriptor length does not match its fields and alignment padding.");
        }
        if (tag == 0 && (body[fixedSize + (int)first] != 0 || body[fixedSize + (int)first + 1 + (int)second] != 0))
        {
            throw new InvalidDataException("Property keys and values require NUL terminators.");
        }
        var name = body.Slice(fixedSize, (int)first);
        try
        {
            _ = StrictUtf8.GetCharCount(name);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The descriptor string is not valid UTF-8.", ex);
        }
        if (name.Contains((byte)0))
        {
            throw new InvalidDataException("The descriptor string contains an embedded NUL.");
        }
    }
}
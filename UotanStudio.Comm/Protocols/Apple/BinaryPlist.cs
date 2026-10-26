using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple;

/// <summary>
/// Decodes binary property list (bplist00) payloads into the shared object graph:
/// <see cref="Dictionary{TKey, TValue}"/>, <see cref="List{T}"/>, <see cref="string"/>,
/// <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, <c>byte[]</c>,
/// <see cref="DateTime"/> and <see cref="ApplePlistUid"/>.
/// <para>将二进制属性列表（bplist00）负载解码为共享对象图：<see cref="Dictionary{TKey, TValue}"/>、
/// <see cref="List{T}"/>、<see cref="string"/>、<see cref="long"/>、<see cref="double"/>、
/// <see cref="bool"/>、<c>byte[]</c>、<see cref="DateTime"/> 与 <see cref="ApplePlistUid"/>。</para>
/// </summary>
internal sealed class BinaryPlistReader(ReadOnlyMemory<byte> data, ApplePlistLimits limits)
{
    /// <summary>Seconds between the Unix epoch (1970) and the plist date epoch (2001-01-01).
    /// <para>Unix 纪元（1970）与属性列表日期纪元（2001-01-01）之间的秒数。</para></summary>
    internal const long PlistDateEpochOffsetSeconds = 978307200;

    private readonly ReadOnlyMemory<byte> _data = data;
    private readonly ApplePlistLimits _limits = limits;
    private long _elements;

    private int _offsetSize;
    private int _referenceSize;
    private long _objectCount;
    private long _topObject;
    private long _offsetTableOffset;

    /// <summary>
    /// Parses the payload and returns the root object.
    /// <para>解析负载并返回根对象。</para>
    /// </summary>
    /// <returns>The decoded root value. <para>解码后的根值。</para></returns>
    /// <exception cref="InvalidDataException">The payload is not a well-formed binary plist or violates the configured limits.
    /// <para>负载不是合法的二进制属性列表，或超出配置的上限时抛出。</para></exception>
    public object Parse()
    {
        if (_data.Length < 8 + 32 || !_data.Span[..8].SequenceEqual("bplist00"u8))
        {
            throw new InvalidDataException("The payload is not a binary property list.");
        }

        if (_data.Length > _limits.MaxTotalBytes)
        {
            throw new InvalidDataException($"The binary plist exceeds {_limits.MaxTotalBytes} bytes.");
        }

        var trailer = _data.Span[^32..];
        _offsetSize = trailer[6];
        _referenceSize = trailer[7];
        _objectCount = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(8, 8));
        _topObject = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(16, 8));
        _offsetTableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(24, 8));

        if (_offsetSize is < 1 or > 8 || _referenceSize is < 1 or > 8)
        {
            throw new InvalidDataException("The binary plist uses an invalid offset or reference size.");
        }

        if (_objectCount < 1 || _objectCount > _limits.MaxCollectionCount ||
            _offsetTableOffset < 9 || _offsetTableOffset > _data.Length - 32 ||
            _offsetTableOffset + _objectCount * _offsetSize > _data.Length - 32)
        {
            throw new InvalidDataException("The binary plist offset table is inconsistent.");
        }

        if (_topObject < 0 || _topObject >= _objectCount)
        {
            throw new InvalidDataException("The binary plist top object index is out of range.");
        }

        var value = ParseObject(_topObject, depth: 0);
        return value ?? throw new InvalidDataException("The binary plist root object is null.");
    }

    private object? ParseObject(long index, int depth)
    {
        if (depth > _limits.MaxDepth)
        {
            throw new InvalidDataException($"The binary plist nesting exceeds {_limits.MaxDepth} levels.");
        }

        if (++_elements > _limits.MaxElements)
        {
            throw new InvalidDataException($"The binary plist exceeds {_limits.MaxElements} values.");
        }

        var offset = ReadOffset(index);
        if (offset < 8 || offset >= _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist object offset is out of range.");
        }

        var marker = _data.Span[(int)offset];
        var kind = marker >> 4;
        var info = marker & 0x0F;
        var position = (int)offset + 1;

        switch (kind)
        {
            case 0x0:
                return info switch
                {
                    0x0 => null,
                    0x8 => false,
                    0x9 => true,
                    _ => throw new InvalidDataException("The binary plist contains an unknown singleton marker."),
                };
            case 0x1:
                return ParseInteger(ref position, info);
            case 0x2:
                return ParseReal(ref position, info);
            case 0x3 when info == 0x3:
                return ParseDate(ref position);
            case 0x4:
                return ParseData(ref position, info);
            case 0x5:
                return ParseAsciiString(ref position, info);
            case 0x6:
                return ParseUtf16String(ref position, info);
            case 0x8:
                return ParseUid(ref position, info);
            case 0xA:
                return ParseArray(ref position, info, depth);
            case 0xC:
                return ParseArray(ref position, info, depth);
            case 0xD:
                return ParseDictionary(ref position, info, depth);
            default:
                throw new InvalidDataException("The binary plist contains an unsupported object marker.");
        }
    }

    private long ParseInteger(ref int position, int info)
    {
        var size = 1 << info;
        if (size is not (1 or 2 or 4 or 8) || position + size > _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist integer is out of range.");
        }

        var span = _data.Span.Slice(position, size);
        position += size;
        return size switch
        {
            1 => span[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(span),
            4 => BinaryPrimitives.ReadUInt32BigEndian(span),
            _ => unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(span)),
        };
    }

    private double ParseReal(ref int position, int info)
    {
        var size = info switch
        {
            0x0 => 4,
            0x2 => 4,
            0x4 => 8,
            _ => throw new InvalidDataException("The binary plist real uses an unsupported size."),
        };

        if (position + size > _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist real is out of range.");
        }

        var span = _data.Span.Slice(position, size);
        position += size;
        return size == 4
            ? BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(span))
            : BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64BigEndian(span));
    }

    private DateTime ParseDate(ref int position)
    {
        if (position + 8 > _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist date is out of range.");
        }

        var seconds = BitConverter.UInt64BitsToDouble(
            BinaryPrimitives.ReadUInt64BigEndian(_data.Span.Slice(position, 8)));
        position += 8;
        return DateTime.UnixEpoch.AddSeconds(seconds + PlistDateEpochOffsetSeconds);
    }

    private byte[] ParseData(ref int position, int info)
    {
        var length = (int)ReadLength(ref position, info);
        if (length < 0 || position + length > _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist data blob is out of range.");
        }

        var bytes = _data.Span.Slice(position, length).ToArray();
        position += length;
        return bytes;
    }

    private string ParseAsciiString(ref int position, int info)
    {
        var length = (int)ReadLength(ref position, info);
        if (length < 0 || position + length > _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist string is out of range.");
        }

        var text = Encoding.UTF8.GetString(_data.Span.Slice(position, length));
        position += length;
        return text;
    }

    private string ParseUtf16String(ref int position, int info)
    {
        var units = (int)ReadLength(ref position, info);
        var byteLength = units * 2;
        if (units < 0 || byteLength < 0 || position + byteLength > _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist string is out of range.");
        }

        var text = Encoding.BigEndianUnicode.GetString(_data.Span.Slice(position, byteLength));
        position += byteLength;
        return text;
    }

    private ApplePlistUid ParseUid(ref int position, int info)
    {
        var size = info + 1;
        if (position + size > _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist UID is out of range.");
        }

        var span = _data.Span.Slice(position, size);
        position += size;
        ulong value = 0;
        foreach (var octet in span)
        {
            value = (value << 8) | octet;
        }

        return new ApplePlistUid(value);
    }

    private List<object> ParseArray(ref int position, int info, int depth)
    {
        var count = (int)ReadLength(ref position, info);
        var references = ReadReferences(ref position, count);
        var items = new List<object>(count);
        foreach (var reference in references)
        {
            var item = ParseObject(reference, depth + 1);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private Dictionary<string, object> ParseDictionary(ref int position, int info, int depth)
    {
        var count = (int)ReadLength(ref position, info);
        var keyReferences = ReadReferences(ref position, count);
        var valueReferences = ReadReferences(ref position, count);
        var dictionary = new Dictionary<string, object>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ParseObject(keyReferences[i], depth + 1) as string
                ?? throw new InvalidDataException("The binary plist dictionary key is not a string.");
            if (dictionary.ContainsKey(key))
            {
                throw new InvalidDataException("The binary plist dictionary contains a duplicate key.");
            }

            var value = ParseObject(valueReferences[i], depth + 1);
            if (value is not null)
            {
                dictionary[key] = value;
            }
        }

        return dictionary;
    }

    private long[] ReadReferences(ref int position, int count)
    {
        if (count < 0 || count > _limits.MaxCollectionCount ||
            position + count * _referenceSize > _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist collection is out of range.");
        }

        var references = new long[count];
        for (var i = 0; i < count; i++)
        {
            references[i] = ReadOffsetFrom(position + i * _referenceSize);
        }

        position += count * _referenceSize;
        return references;
    }

    private long ReadLength(ref int position, int info) =>
        info == 0xF ? ParseLengthInteger(ref position) : info;

    private long ParseLengthInteger(ref int position)
    {
        if (position >= _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist length marker is out of range.");
        }

        var info = _data.Span[position++] & 0x0F;
        var size = 1 << info;
        if (size is not (1 or 2 or 4 or 8) || position + size > _offsetTableOffset)
        {
            throw new InvalidDataException("The binary plist length is out of range.");
        }

        var span = _data.Span.Slice(position, size);
        position += size;
        return size switch
        {
            1 => span[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(span),
            4 => BinaryPrimitives.ReadUInt32BigEndian(span),
            _ => (long)BinaryPrimitives.ReadUInt64BigEndian(span),
        };
    }

    private long ReadOffset(long index) =>
        ReadOffsetFrom((int)(_offsetTableOffset + index * _offsetSize));

    private long ReadOffsetFrom(int position)
    {
        if (position < 8 || position + _offsetSize > _data.Length)
        {
            throw new InvalidDataException("The binary plist offset is out of range.");
        }

        var span = _data.Span.Slice(position, _offsetSize);
        return _offsetSize switch
        {
            1 => span[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(span),
            4 => BinaryPrimitives.ReadUInt32BigEndian(span),
            _ => checked((long)BinaryPrimitives.ReadUInt64BigEndian(span)),
        };
    }
}

/// <summary>
/// Encodes the shared plist object graph into a binary property list (bplist00) payload.
/// <para>将共享属性列表对象图编码为二进制属性列表（bplist00）负载。</para>
/// </summary>
internal static class BinaryPlistWriter
{
    /// <summary>
    /// Serializes <paramref name="root"/> into a complete bplist00 payload.
    /// <para>将 <paramref name="root"/> 序列化为完整的 bplist00 负载。</para>
    /// </summary>
    /// <param name="root">The root value; only the shared plist object graph types are accepted. <para>根值；仅接受共享属性列表对象图类型。</para></param>
    /// <returns>The encoded payload including header and trailer. <para>含头部与尾部的编码负载。</para></returns>
    /// <exception cref="ArgumentException">The root graph contains an unsupported value type.
    /// <para>根对象图包含不受支持的值类型时抛出。</para></exception>
    public static byte[] Write(object root)
    {
        var objects = new List<object>();
        Flatten(root, objects, 0);
        var referenceSize = SizeForCount(objects.Count - 1);

        var index = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < objects.Count; i++)
        {
            index.TryAdd(objects[i], i);
        }

        var payload = new List<byte[]>();
        var offsets = new List<long>();
        var offsetSize = 1;
        var total = 8L;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            payload.Clear();
            offsets.Clear();
            total = 8L;
            foreach (var value in objects)
            {
                offsets.Add(total);
                var encoded = Encode(value, referenceSize, index);
                payload.Add(encoded);
                total += encoded.Length;
            }

            var tableStart = total;
            var newOffsetSize = SizeForCount((int)Math.Min(int.MaxValue, tableStart));
            if (newOffsetSize == offsetSize)
            {
                break;
            }

            offsetSize = newOffsetSize;
        }

        using var output = new MemoryStream();
        output.Write("bplist00"u8);
        foreach (var chunk in payload)
        {
            output.Write(chunk);
        }

        var offsetTableOffset = output.Length;
        foreach (var offset in offsets)
        {
            WriteBigEndian(output, offset, offsetSize);
        }

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)referenceSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.Slice(8, 8), (ulong)offsets.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.Slice(16, 8), (ulong)(objects.Count - 1));
        BinaryPrimitives.WriteUInt64BigEndian(trailer.Slice(24, 8), (ulong)offsetTableOffset);
        output.Write(trailer);
        return output.ToArray();
    }

    private static void Flatten(object value, List<object> objects, int depth)
    {
        if (depth > 64)
        {
            throw new ArgumentException("The plist graph is nested too deeply.", nameof(value));
        }

        switch (value)
        {
            case Dictionary<string, object> dictionary:
                foreach (var entry in dictionary)
                {
                    Flatten(entry.Key, objects, depth + 1);
                    Flatten(entry.Value, objects, depth + 1);
                }

                break;
            case List<object> list:
                foreach (var item in list)
                {
                    Flatten(item, objects, depth + 1);
                }

                break;
        }

        objects.Add(value);
    }

    private static byte[] Encode(object value, int referenceSize, Dictionary<object, int> index)
    {
        switch (value)
        {
            case bool flag:
                return [flag ? (byte)0x09 : (byte)0x08];
            case long integer:
                return EncodeInteger(integer);
            case int integer:
                return EncodeInteger(integer);
            case double real:
                var realBytes = new byte[9];
                realBytes[0] = 0x23;
                BinaryPrimitives.WriteUInt64BigEndian(realBytes.AsSpan(1), BitConverter.DoubleToUInt64Bits(real));
                return realBytes;
            case float real:
                var floatBytes = new byte[5];
                floatBytes[0] = 0x22;
                BinaryPrimitives.WriteUInt32BigEndian(floatBytes.AsSpan(1), BitConverter.SingleToUInt32Bits(real));
                return floatBytes;
            case DateTime date:
                var dateBytes = new byte[9];
                dateBytes[0] = 0x33;
                var seconds = (date.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds
                    - BinaryPlistReader.PlistDateEpochOffsetSeconds;
                BinaryPrimitives.WriteUInt64BigEndian(dateBytes.AsSpan(1), BitConverter.DoubleToUInt64Bits(seconds));
                return dateBytes;
            case ApplePlistUid uid:
                {
                    var size = SizeForCount((int)Math.Min(int.MaxValue, uid.Value));
                    var uidBytes = new byte[1 + size];
                    uidBytes[0] = (byte)(0x80 | (size - 1));
                    for (var i = 0; i < size; i++)
                    {
                        uidBytes[1 + i] = (byte)(uid.Value >> (8 * (size - 1 - i)));
                    }

                    return uidBytes;
                }
            case byte[] data:
                return EncodeWithLength(0x40, data.Length, data);
            case string text:
                {
                    var utf8 = Encoding.UTF8.GetBytes(text);
                    if (IsAscii(utf8))
                    {
                        return EncodeWithLength(0x50, utf8.Length, utf8);
                    }

                    var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
                    return EncodeWithLength(0x60, utf16.Length / 2, utf16);
                }
            case List<object> list:
                return EncodeCollection(0xA0, list, referenceSize, index);
            case Dictionary<string, object> dictionary:
                return EncodeDictionary(dictionary, referenceSize, index);
            default:
                throw new ArgumentException($"Unsupported plist value type {value.GetType().Name}.", nameof(value));
        }
    }

    private static byte[] EncodeInteger(long integer)
    {
        if (integer is >= 0 and <= byte.MaxValue)
        {
            return [(byte)0x10, (byte)integer];
        }

        if (integer is >= 0 and <= ushort.MaxValue)
        {
            var bytes = new byte[3];
            bytes[0] = 0x11;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1), (ushort)integer);
            return bytes;
        }

        if (integer is >= 0 and <= int.MaxValue)
        {
            var bytes = new byte[5];
            bytes[0] = 0x12;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), (uint)integer);
            return bytes;
        }

        var wide = new byte[9];
        wide[0] = 0x13;
        BinaryPrimitives.WriteUInt64BigEndian(wide.AsSpan(1), (ulong)integer);
        return wide;
    }

    private static byte[] EncodeWithLength(byte markerBase, int length, byte[] content)
    {
        using var output = new MemoryStream();
        WriteMarker(output, markerBase, length);
        output.Write(content);
        return output.ToArray();
    }

    private static byte[] EncodeCollection(byte markerBase, List<object> items, int referenceSize, Dictionary<object, int> index)
    {
        using var output = new MemoryStream();
        WriteMarker(output, markerBase, items.Count);
        foreach (var item in items)
        {
            WriteBigEndian(output, index[item], referenceSize);
        }

        return output.ToArray();
    }

    private static byte[] EncodeDictionary(Dictionary<string, object> dictionary, int referenceSize, Dictionary<object, int> index)
    {
        using var output = new MemoryStream();
        WriteMarker(output, 0xD0, dictionary.Count);
        foreach (var key in dictionary.Keys)
        {
            WriteBigEndian(output, index[key], referenceSize);
        }

        foreach (var value in dictionary.Values)
        {
            WriteBigEndian(output, index[value], referenceSize);
        }

        return output.ToArray();
    }

    private static void WriteMarker(MemoryStream output, byte markerBase, int count)
    {
        if (count < 0x0F)
        {
            output.WriteByte((byte)(markerBase | (byte)count));
            return;
        }

        output.WriteByte((byte)(markerBase | 0x0F));
        output.Write(EncodeInteger(count));
    }

    private static void WriteBigEndian(MemoryStream output, long value, int size)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)value);
        output.Write(bytes[^size..]);
    }

    private static int SizeForCount(int count) => count switch
    {
        < 0x100 => 1,
        < 0x10000 => 2,
        _ => 4,
    };

    private static bool IsAscii(byte[] bytes)
    {
        foreach (var octet in bytes)
        {
            if (octet > 0x7F)
            {
                return false;
            }
        }

        return true;
    }
}
using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// Marks the end of a terminated OPACK container when building.
/// <para>构造时标记被终结的 OPACK 容器的结束。</para>
/// </summary>
public sealed class OpackTerminator
{
    /// <summary>Gets the shared terminator instance. <para>获取共享的终结符实例。</para></summary>
    public static OpackTerminator Instance { get; } = new();

    private OpackTerminator()
    {
    }
}

/// <summary>
/// Minimal OPACK binary encoder/decoder for the values RemotePairing exchanges (dictionaries with
/// string keys, strings, integers, byte arrays, booleans and lists). Decoding is bounded.
/// <para>RemotePairing 交换值（字符串键字典、字符串、整数、字节数组、布尔与列表）所需的最小 OPACK
/// 编解码器。解码有界。</para>
/// </summary>
public static class Opack
{
    private const int MaxDepth = 64;
    private const long MaxElements = 1 << 20;

    /// <summary>
    /// Decodes one OPACK object.
    /// <para>解码一个 OPACK 对象。</para>
    /// </summary>
    /// <param name="payload">The OPACK bytes. <para>OPACK 字节。</para></param>
    /// <returns>The decoded value (object graph of string/long/bool/byte[]/List/Dictionary). <para>解码值（string/long/bool/byte[]/List/Dictionary 对象图）。</para></returns>
    /// <exception cref="InvalidDataException">The payload is malformed or exceeds the bounds. <para>负载畸形或超界时抛出。</para></exception>
    public static object? Decode(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);
        var value = reader.ReadValue(0);
        return value;
    }

    /// <summary>
    /// Encodes one OPACK object.
    /// <para>编码一个 OPACK 对象。</para>
    /// </summary>
    /// <param name="value">The value to encode. <para>待编码值。</para></param>
    /// <returns>The encoded bytes. <para>编码字节。</para></returns>
    /// <exception cref="ArgumentException">The value graph contains an unsupported type. <para>对象图包含不受支持类型时抛出。</para></exception>
    public static byte[] Encode(object? value)
    {
        using var output = new MemoryStream();
        WriteValue(output, value);
        return output.ToArray();
    }

    private static void WriteValue(MemoryStream output, object? value)
    {
        switch (value)
        {
            case null:
                output.WriteByte(0x03);
                break;
            case true:
                output.WriteByte(0x01);
                break;
            case false:
                output.WriteByte(0x02);
                break;
            case OpackTerminator:
                output.WriteByte(0x03);
                break;
            case byte[] data:
                WriteHeader(output, 0x70, data.Length);
                output.Write(data);
                break;
            case string text:
                var utf8 = Encoding.UTF8.GetBytes(text);
                WriteHeader(output, 0x40, utf8.Length);
                output.Write(utf8);
                break;
            case long integer:
                WriteInteger(output, integer);
                break;
            case int integer:
                WriteInteger(output, integer);
                break;
            case List<object> list:
                if (list.Count < 15)
                {
                    WriteHeader(output, 0xD0, list.Count);
                    foreach (var item in list)
                    {
                        WriteValue(output, item);
                    }
                }
                else
                {
                    output.WriteByte(0xDF);
                    foreach (var item in list)
                    {
                        WriteValue(output, item);
                    }

                    WriteValue(output, OpackTerminator.Instance);
                }

                break;
            case Dictionary<string, object?> dictionary:
                {
                    var entries = new List<KeyValuePair<object?, object?>>(dictionary.Count);
                    foreach (var entry in dictionary)
                    {
                        entries.Add(new KeyValuePair<object?, object?>(entry.Key, entry.Value));
                    }

                    WriteContainer(output, 0xE0, entries);
                    break;
                }
            case IReadOnlyDictionary<string, object> dictionary:
                {
                    var entries = new List<KeyValuePair<object?, object?>>(dictionary.Count);
                    foreach (var entry in dictionary)
                    {
                        entries.Add(new KeyValuePair<object?, object?>(entry.Key, entry.Value));
                    }

                    WriteContainer(output, 0xE0, entries);
                    break;
                }
            default:
                throw new ArgumentException($"Unsupported OPACK value type {value!.GetType().Name}.");
        }
    }

    private static void WriteContainer(MemoryStream output, byte baseMarker, List<KeyValuePair<object?, object?>> entries)
    {
        if (entries.Count < 15)
        {
            WriteHeader(output, baseMarker, entries.Count);
            foreach (var (key, item) in entries)
            {
                WriteValue(output, key);
                WriteValue(output, item);
            }
        }
        else
        {
            output.WriteByte((byte)(baseMarker + 0x0F));
            foreach (var (key, item) in entries)
            {
                WriteValue(output, key);
                WriteValue(output, item);
            }

            WriteValue(output, OpackTerminator.Instance);
            WriteValue(output, OpackTerminator.Instance);
        }
    }

    private static void WriteInteger(MemoryStream output, long integer)
    {
        Span<byte> bytes = stackalloc byte[8];
        if (integer is >= 0 and <= 0x27)
        {
            output.WriteByte((byte)(integer + 8));
            return;
        }

        if (integer is >= 0 and <= byte.MaxValue)
        {
            output.WriteByte(0x30);
            output.WriteByte((byte)integer);
            return;
        }

        if (integer is >= 0 and <= uint.MaxValue)
        {
            output.WriteByte(0x32);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)integer);
            output.Write(bytes[..4]);
            return;
        }

        output.WriteByte(0x33);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)integer);
        output.Write(bytes);
    }

    private static void WriteHeader(MemoryStream output, byte baseMarker, int length)
    {
        if (length <= 0x20)
        {
            output.WriteByte((byte)(baseMarker + length));
            return;
        }

        Span<byte> bytes = stackalloc byte[8];
        switch (length)
        {
            case <= 0xFF:
                output.WriteByte((byte)(baseMarker + 0x21));
                output.WriteByte((byte)length);
                break;
            case <= 0xFFFF:
                output.WriteByte((byte)(baseMarker + 0x22));
                BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)length);
                output.Write(bytes[..2]);
                break;
            default:
                output.WriteByte((byte)(baseMarker + 0x23));
                BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)length);
                output.Write(bytes[..4]);
                break;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> payload)
    {
        private readonly ReadOnlySpan<byte> _payload = payload;
        private long _elements;
        private int _position;

        public object? ReadValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidDataException("The OPACK nesting exceeds the depth limit.");
            }

            if (++_elements > MaxElements)
            {
                throw new InvalidDataException("The OPACK payload exceeds the element limit.");
            }

            var type = ReadByte();
            switch (type)
            {
                case 0x01:
                    return true;
                case 0x02:
                    return false;
                case 0x03:
                    return null;
                case 0x06:
                    return ReadLittleEndian(8);
                case >= 0x08 and <= 0x2F:
                    return (long)(type - 8);
                case 0x30:
                    return (long)ReadByte();
                case 0x32:
                    return (long)ReadLittleEndian(4);
                case 0x33:
                    return unchecked((long)ReadLittleEndian(8));
                case 0x35 or 0x36:
                    return ReadLittleEndian(8);
                case >= 0x40 and <= 0x64:
                    {
                        var data = ReadData(type, 0x40);
                        return Encoding.UTF8.GetString(data);
                    }
                case >= 0x70 and <= 0x94:
                    return ReadData(type, 0x70);
                case >= 0xD0 and <= 0xDF:
                    {
                        var items = new List<object?>();
                        if (type == 0xDF)
                        {
                            while (ReadTypeByte() != 0x03)
                            {
                                _position--;
                                items.Add(ReadValue(depth + 1));
                            }
                        }
                        else
                        {
                            for (var i = 0; i < type - 0xD0; i++)
                            {
                                items.Add(ReadValue(depth + 1));
                            }
                        }

                        return items;
                    }
                case >= 0xE0 and <= 0xEF:
                    {
                        var dictionary = new Dictionary<string, object?>();
                        if (type == 0xEF)
                        {
                            while (true)
                            {
                                if (PeekTypeByte() == 0x03)
                                {
                                    _position++;
                                    ReadValue(depth + 1);
                                    break;
                                }

                                var key = ReadValue(depth + 1) as string
                                    ?? throw new InvalidDataException("The OPACK dictionary key is not a string.");
                                var value = ReadValue(depth + 1);
                                dictionary[key] = value;
                            }
                        }
                        else
                        {
                            for (var i = 0; i < type - 0xE0; i++)
                            {
                                var key = ReadValue(depth + 1) as string
                                    ?? throw new InvalidDataException("The OPACK dictionary key is not a string.");
                                var value = ReadValue(depth + 1);
                                dictionary[key] = value;
                            }
                        }

                        return dictionary;
                    }
                default:
                    throw new InvalidDataException($"The OPACK object type 0x{type:X2} is unsupported.");
            }
        }

        private byte ReadTypeByte() => ReadByte();

        private byte PeekTypeByte() => _position < _payload.Length ? _payload[_position] : throw new InvalidDataException("The OPACK payload ends unexpectedly.");

        private byte ReadByte()
        {
            if (_position >= _payload.Length)
            {
                throw new InvalidDataException("The OPACK payload ends unexpectedly.");
            }

            return _payload[_position++];
        }

        private ulong ReadLittleEndian(int size)
        {
            if (_position + size > _payload.Length)
            {
                throw new InvalidDataException("The OPACK scalar ends unexpectedly.");
            }

            ulong value = 0;
            for (var i = size - 1; i >= 0; i--)
            {
                value = (value << 8) | _payload[_position + i];
            }

            _position += size;
            return value;
        }

        private byte[] ReadData(int type, int inlineBase)
        {
            var inlineMax = inlineBase + 0x20;
            var size = type <= inlineMax
                ? type - inlineBase
                : type switch
                {
                    0x61 or 0x91 => ReadByte(),
                    0x62 or 0x92 => (int)ReadLittleEndian(2),
                    0x63 or 0x93 => (int)ReadLittleEndian(4),
                    0x64 or 0x94 => (int)ReadLittleEndian(8),
                    _ => throw new InvalidDataException($"The OPACK length type 0x{type:X2} is unsupported."),
                };
            if (size < 0 || _position + size > _payload.Length)
            {
                throw new InvalidDataException("The OPACK data blob ends unexpectedly.");
            }

            var data = _payload.Slice(_position, size).ToArray();
            _position += size;
            return data;
        }
    }
}
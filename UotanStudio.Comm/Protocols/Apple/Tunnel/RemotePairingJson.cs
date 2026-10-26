using System.Globalization;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// Minimal JSON encoder/decoder for the RemotePairing message shapes (dictionaries of strings,
/// numbers, booleans, base64-encoded byte arrays, nested dictionaries and lists). Self-contained so
/// the code stays reflection-free for NativeAOT.
/// <para>RemotePairing 消息形态（字符串、数字、布尔、base64 字节数组、嵌套字典与列表的字典）所需的
/// 最小 JSON 编解码器。自包含，保证 NativeAOT 无反射。</para>
/// </summary>
public static class RemotePairingJson
{
    /// <summary>
    /// Encodes the object graph into compact JSON.
    /// <para>把对象图编码为紧凑 JSON。</para>
    /// </summary>
    /// <param name="value">The value; supported types are dictionaries, lists, strings, longs, bools,
    /// <see langword="null"/> and byte arrays (encoded as base64 strings). <para>待编码值；支持字典、列表、
    /// 字符串、long、布尔、<see langword="null"/> 与字节数组（编码为 base64 字符串）。</para></param>
    /// <returns>The JSON bytes. <para>JSON 字节。</para></returns>
    /// <exception cref="ArgumentException">The graph contains unsupported types. <para>对象图包含不受支持类型时抛出。</para></exception>
    public static byte[] Encode(object? value)
    {
        using var output = new MemoryStream();
        WriteValue(output, value);
        return output.ToArray();
    }

    /// <summary>
    /// Decodes a JSON document into dictionaries and lists with string keys and long/bool/string
    /// values.
    /// <para>把 JSON 文档解码为以字符串为键、long/bool/string 为值的字典与列表。</para>
    /// </summary>
    /// <param name="payload">The JSON bytes. <para>JSON 字节。</para></param>
    /// <returns>The decoded root value. <para>解码后的根值。</para></returns>
    /// <exception cref="InvalidDataException">The payload is malformed. <para>负载畸形时抛出。</para></exception>
    public static object? Decode(ReadOnlySpan<byte> payload)
    {
        var reader = new Parser(payload);
        var value = reader.ParseValue(0);
        reader.SkipWhitespace();
        if (reader.Position < payload.Length)
        {
            throw new InvalidDataException("The JSON document carries trailing content.");
        }

        return value;
    }

    private static void WriteValue(MemoryStream output, object? value)
    {
        switch (value)
        {
            case null:
                output.Write("null"u8);
                break;
            case true:
                output.Write("true"u8);
                break;
            case false:
                output.Write("false"u8);
                break;
            case string text:
                WriteString(output, text);
                break;
            case byte[] data:
                WriteString(output, Convert.ToBase64String(data));
                break;
            case XpcCodec.XpcUInt64 unsigned:
                output.Write(Encoding.ASCII.GetBytes(unsigned.Value.ToString(CultureInfo.InvariantCulture)));
                break;
            case XpcCodec.XpcInt64 signed:
                output.Write(Encoding.ASCII.GetBytes(signed.Value.ToString(CultureInfo.InvariantCulture)));
                break;
            case int or long:
                output.Write(Encoding.ASCII.GetBytes(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture)));
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                output.WriteByte((byte)'{');
                var first = true;
                foreach (var entry in dictionary)
                {
                    if (!first)
                    {
                        output.WriteByte((byte)',');
                    }

                    first = false;
                    WriteString(output, entry.Key);
                    output.WriteByte((byte)':');
                    WriteValue(output, entry.Value);
                }

                output.WriteByte((byte)'}');
                break;
            case List<object?> list:
                output.WriteByte((byte)'[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                    {
                        output.WriteByte((byte)',');
                    }

                    WriteValue(output, list[i]);
                }

                output.WriteByte((byte)']');
                break;
            default:
                throw new ArgumentException($"Unsupported JSON value type {value!.GetType().Name}.");
        }
    }

    private static void WriteString(MemoryStream output, string text)
    {
        output.WriteByte((byte)'"');
        var utf8 = Encoding.UTF8.GetBytes(text);
        foreach (var octet in utf8)
        {
            switch (octet)
            {
                case (byte)'"':
                    output.Write("\\\""u8);
                    break;
                case (byte)'\\':
                    output.Write("\\\\"u8);
                    break;
                case (byte)'\n':
                    output.Write("\\n"u8);
                    break;
                case (byte)'\r':
                    output.Write("\\r"u8);
                    break;
                case (byte)'\t':
                    output.Write("\\t"u8);
                    break;
                case < 0x20:
                    output.Write(Encoding.ASCII.GetBytes($"\\u{octet:x4}"));
                    break;
                default:
                    output.WriteByte(octet);
                    break;
            }
        }

        output.WriteByte((byte)'"');
    }

    private ref struct Parser(ReadOnlySpan<byte> payload)
    {
        private readonly ReadOnlySpan<byte> _payload = payload;
        private long _elements;

        public int Position { get; private set; }

        public void SkipWhitespace()
        {
            while (Position < _payload.Length && _payload[Position] is 0x20 or 0x09 or 0x0A or 0x0D)
            {
                Position++;
            }
        }

        public object? ParseValue(int depth)
        {
            if (depth > 64 || ++_elements > 1 << 20)
            {
                throw new InvalidDataException("The JSON payload exceeds its structural limits.");
            }

            SkipWhitespace();
            if (Position >= _payload.Length)
            {
                throw new InvalidDataException("The JSON payload ends unexpectedly.");
            }

            switch (_payload[Position])
            {
                case (byte)'{':
                    {
                        Position++;
                        var dictionary = new Dictionary<string, object?>();
                        SkipWhitespace();
                        if (Position < _payload.Length && _payload[Position] == (byte)'}')
                        {
                            Position++;
                            return dictionary;
                        }

                        while (true)
                        {
                            SkipWhitespace();
                            var key = ParseString();
                            SkipWhitespace();
                            Expect((byte)':');
                            dictionary[key] = ParseValue(depth + 1);
                            SkipWhitespace();
                            var separator = Take();
                            if (separator == (byte)'}')
                            {
                                return dictionary;
                            }

                            if (separator != (byte)',')
                            {
                                throw new InvalidDataException("The JSON object separator is invalid.");
                            }
                        }
                    }
                case (byte)'[':
                    {
                        Position++;
                        var list = new List<object?>();
                        SkipWhitespace();
                        if (Position < _payload.Length && _payload[Position] == (byte)']')
                        {
                            Position++;
                            return list;
                        }

                        while (true)
                        {
                            list.Add(ParseValue(depth + 1));
                            SkipWhitespace();
                            var separator = Take();
                            if (separator == (byte)']')
                            {
                                return list;
                            }

                            if (separator != (byte)',')
                            {
                                throw new InvalidDataException("The JSON array separator is invalid.");
                            }
                        }
                    }
                case (byte)'"':
                    return ParseString();
                case (byte)'t':
                    ExpectLiteral("true");
                    return true;
                case (byte)'f':
                    ExpectLiteral("false");
                    return false;
                case (byte)'n':
                    ExpectLiteral("null");
                    return null;
                default:
                    return ParseNumber();
            }
        }

        private string ParseString()
        {
            Expect((byte)'"');
            using var output = new MemoryStream();
            while (true)
            {
                if (Position >= _payload.Length)
                {
                    throw new InvalidDataException("The JSON string ends unexpectedly.");
                }

                var octet = _payload[Position++];
                if (octet == (byte)'"')
                {
                    return Encoding.UTF8.GetString(output.ToArray());
                }

                if (octet != (byte)'\\')
                {
                    output.WriteByte(octet);
                    continue;
                }

                var escape = Take();
                switch (escape)
                {
                    case (byte)'"':
                        output.WriteByte((byte)'"');
                        break;
                    case (byte)'\\':
                        output.WriteByte((byte)'\\');
                        break;
                    case (byte)'/':
                        output.WriteByte((byte)'/');
                        break;
                    case (byte)'b':
                        output.WriteByte(0x08);
                        break;
                    case (byte)'f':
                        output.WriteByte(0x0C);
                        break;
                    case (byte)'n':
                        output.WriteByte((byte)'\n');
                        break;
                    case (byte)'r':
                        output.WriteByte((byte)'\r');
                        break;
                    case (byte)'t':
                        output.WriteByte((byte)'\t');
                        break;
                    case (byte)'u':
                        {
                            if (Position + 4 > _payload.Length)
                            {
                                throw new InvalidDataException("The JSON unicode escape is truncated.");
                            }

                            var code = int.Parse(Encoding.ASCII.GetString(_payload.Slice(Position, 4)),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            Position += 4;
                            output.Write(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(code)));
                            break;
                        }
                    default:
                        throw new InvalidDataException($"The JSON escape \\{(char)escape} is invalid.");
                }
            }
        }

        private object ParseNumber()
        {
            var start = Position;
            while (Position < _payload.Length && _payload[Position] is >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E')
            {
                Position++;
            }

            var text = Encoding.ASCII.GetString(_payload.Slice(start, Position - start));
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return integer;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
            {
                return real;
            }

            throw new InvalidDataException($"The JSON number '{text}' is not representable.");
        }

        private void ExpectLiteral(string literal)
        {
            foreach (var character in literal)
            {
                Expect((byte)character);
            }
        }

        private void Expect(byte octet)
        {
            if (Take() != octet)
            {
                throw new InvalidDataException($"The JSON payload expected '{(char)octet}'.");
            }
        }

        private byte Take()
        {
            if (Position >= _payload.Length)
            {
                throw new InvalidDataException("The JSON payload ends unexpectedly.");
            }

            return _payload[Position++];
        }
    }
}
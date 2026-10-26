using System.Globalization;
using System.Text;
using System.Xml;

namespace UotanStudio.Comm.Protocols.Apple;

/// <summary>
/// Parses and serializes Apple property lists in both the XML and binary (bplist00) forms over the
/// shared object graph: <see cref="Dictionary{TKey, TValue}"/>, <see cref="List{T}"/>, <see cref="string"/>,
/// <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, <c>byte[]</c>, <see cref="DateTime"/>
/// and <see cref="ApplePlistUid"/>. All decoding is bounded by <see cref="ApplePlistLimits"/> because device
/// payloads are untrusted input.
/// <para>以 XML 与二进制（bplist00）两种形式解析与序列化 Apple 属性列表，使用共享对象图：
/// <see cref="Dictionary{TKey, TValue}"/>、<see cref="List{T}"/>、<see cref="string"/>、<see cref="long"/>、
/// <see cref="double"/>、<see cref="bool"/>、<c>byte[]</c>、<see cref="DateTime"/> 与
/// <see cref="ApplePlistUid"/>。设备负载属不可信输入，全部解码均受 <see cref="ApplePlistLimits"/> 约束。</para>
/// </summary>
public static class ApplePlist
{
    /// <summary>
    /// Reports whether the payload starts with the binary property list magic.
    /// <para>报告负载是否以二进制属性列表魔数开头。</para>
    /// </summary>
    /// <param name="payload">The encoded plist bytes. <para>编码后的属性列表字节。</para></param>
    /// <returns><see langword="true"/> when the payload is a binary plist. <para>负载为二进制属性列表时返回 <see langword="true"/>。</para></returns>
    public static bool LooksBinary(ReadOnlySpan<byte> payload) =>
        payload.Length >= 6 && payload[..6].SequenceEqual("bplist"u8);

    /// <summary>
    /// Parses an encoded property list, auto-detecting the XML or binary form.
    /// <para>解析编码属性列表，自动识别 XML 或二进制形式。</para>
    /// </summary>
    /// <param name="payload">The encoded plist bytes. <para>编码后的属性列表字节。</para></param>
    /// <param name="limits">Optional decoding bounds; defaults apply when omitted. <para>可选解码上限；缺省时使用默认值。</para></param>
    /// <returns>The decoded root object. <para>解码后的根对象。</para></returns>
    /// <exception cref="InvalidDataException">The payload is malformed or exceeds the limits. <para>负载畸形或超出上限时抛出。</para></exception>
    public static object Parse(ReadOnlySpan<byte> payload, ApplePlistLimits? limits = null)
    {
        var effective = (limits ?? ApplePlistLimits.Default);
        if (payload.Length > effective.MaxTotalBytes)
        {
            throw new InvalidDataException($"The plist exceeds {effective.MaxTotalBytes} bytes.");
        }

        if (LooksBinary(payload))
        {
            return new BinaryPlistReader(payload.ToArray(), effective).Parse();
        }

        if (!LooksLikeXml(payload))
        {
            throw new InvalidDataException("The payload is neither a binary nor an XML property list.");
        }

        return ParseXml(payload.ToArray(), effective);
    }

    /// <summary>
    /// Serializes the root object into a binary property list payload.
    /// <para>将根对象序列化为二进制属性列表负载。</para>
    /// </summary>
    /// <param name="root">The root value. <para>根值。</para></param>
    /// <returns>The encoded binary plist. <para>编码后的二进制属性列表。</para></returns>
    /// <exception cref="ArgumentException">The graph contains unsupported types. <para>对象图包含不受支持类型时抛出。</para></exception>
    public static byte[] ToBinary(object root) => BinaryPlistWriter.Write(root);

    /// <summary>
    /// Serializes the root object into an XML property list payload (UTF-8, no BOM).
    /// <para>将根对象序列化为 XML 属性列表负载（UTF-8，无 BOM）。</para>
    /// </summary>
    /// <param name="root">The root value. <para>根值。</para></param>
    /// <returns>The encoded XML plist. <para>编码后的 XML 属性列表。</para></returns>
    /// <exception cref="ArgumentException">The graph contains unsupported types. <para>对象图包含不受支持类型时抛出。</para></exception>
    public static byte[] ToXml(object root)
    {
        using var output = new MemoryStream();
        using (var writer = CreateXmlWriter(output))
        {
            WriteValue(writer, root);
        }

        return output.ToArray();
    }

    private static XmlWriter CreateXmlWriter(MemoryStream output)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = true,
            CloseOutput = false,
            Async = false,
        };
        var writer = XmlWriter.Create(output, settings);
        writer.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
        writer.WriteStartElement("plist");
        writer.WriteAttributeString("version", "1.0");
        return writer;
    }

    private static void WriteValue(XmlWriter writer, object value)
    {
        switch (value)
        {
            case Dictionary<string, object> dictionary:
                writer.WriteStartElement("dict");
                foreach (var entry in dictionary)
                {
                    writer.WriteElementString("key", entry.Key);
                    WriteValue(writer, entry.Value);
                }

                writer.WriteEndElement();
                break;
            case List<object> list:
                writer.WriteStartElement("array");
                foreach (var item in list)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndElement();
                break;
            case string text:
                writer.WriteElementString("string", text);
                break;
            case long integer:
                writer.WriteElementString("integer", integer.ToString(CultureInfo.InvariantCulture));
                break;
            case int integer:
                writer.WriteElementString("integer", integer.ToString(CultureInfo.InvariantCulture));
                break;
            case double real:
                writer.WriteElementString("real", real.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float real:
                writer.WriteElementString("real", real.ToString("R", CultureInfo.InvariantCulture));
                break;
            case bool flag:
                writer.WriteStartElement(flag ? "true" : "false");
                writer.WriteEndElement();
                break;
            case byte[] data:
                writer.WriteElementString("data", Convert.ToBase64String(data));
                break;
            case DateTime date:
                writer.WriteElementString("date", date.ToUniversalTime().ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                break;
            case ApplePlistUid:
                throw new ArgumentException("A UID value cannot be written to an XML property list.", nameof(value));
            default:
                throw new ArgumentException($"Unsupported plist value type {value.GetType().Name}.", nameof(value));
        }
    }

    private static bool LooksLikeXml(ReadOnlySpan<byte> payload)
    {
        var start = payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF ? 3 : 0;
        while (start < payload.Length && payload[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }

        return start < payload.Length && payload[start] == (byte)'<';
    }

    private static byte[] StripDoctypeDeclaration(byte[] payload)
    {
        var start = payload.AsSpan().IndexOf("<!DOCTYPE"u8);
        if (start < 0)
        {
            return payload;
        }

        var end = payload.AsSpan(start).IndexOf((byte)'>');
        if (end < 0)
        {
            return payload;
        }

        var declaration = payload.AsSpan(start, end + 1);
        if (declaration.Contains((byte)'['))
        {
            return payload;
        }

        var stripped = new byte[payload.Length - declaration.Length];
        payload.AsSpan(0, start).CopyTo(stripped);
        payload.AsSpan(start + declaration.Length).CopyTo(stripped.AsSpan(start));
        return stripped;
    }

    private static object ParseXml(byte[] payload, ApplePlistLimits limits)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CheckCharacters = true,
            CloseInput = true,
        };
        using var stream = new MemoryStream(StripDoctypeDeclaration(payload));
        using var reader = XmlReader.Create(stream, settings);

        var budget = new XmlBudget(limits);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "plist")
            {
                if (!reader.Read() || reader.NodeType != XmlNodeType.Element)
                {
                    throw new InvalidDataException("The XML plist has no root value.");
                }

                return ReadValue(reader, budget, depth: 0);
            }
        }

        throw new InvalidDataException("The payload is not an XML property list.");
    }

    private static object ReadValue(XmlReader reader, XmlBudget budget, int depth)
    {
        budget.Enter(depth);
        var name = reader.LocalName;
        try
        {
            switch (name)
            {
                case "dict":
                    {
                        var dictionary = new Dictionary<string, object>();
                        if (reader.IsEmptyElement)
                        {
                            reader.Read();
                            return dictionary;
                        }

                        reader.Read();
                        while (reader.NodeType != XmlNodeType.EndElement || reader.LocalName != "dict")
                        {
                            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "key")
                            {
                                throw new InvalidDataException("The XML plist dictionary key element is missing.");
                            }

                            var key = reader.ReadElementContentAsString();
                            budget.Count();
                            if (reader.NodeType != XmlNodeType.Element)
                            {
                                throw new InvalidDataException("The XML plist dictionary value element is missing.");
                            }

                            var value = ReadValue(reader, budget, depth + 1);
                            if (!dictionary.TryAdd(key, value))
                            {
                                throw new InvalidDataException("The XML plist dictionary contains a duplicate key.");
                            }
                        }

                        reader.Read();
                        return dictionary;
                    }
                case "array":
                    {
                        var list = new List<object>();
                        if (reader.IsEmptyElement)
                        {
                            reader.Read();
                            return list;
                        }

                        reader.Read();
                        while (reader.NodeType != XmlNodeType.EndElement || reader.LocalName != "array")
                        {
                            if (reader.NodeType != XmlNodeType.Element)
                            {
                                reader.Read();
                                continue;
                            }

                            list.Add(ReadValue(reader, budget, depth + 1));
                        }

                        reader.Read();
                        return list;
                    }
                case "string":
                    budget.Count();
                    return reader.ReadElementContentAsString();
                case "integer":
                    {
                        budget.Count();
                        var text = reader.ReadElementContentAsString();
                        if (text.Length == 0)
                        {
                            text = "0";
                        }

                        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                        {
                            return value;
                        }

                        throw new InvalidDataException("The XML plist integer is not representable.");
                    }
                case "real":
                    {
                        budget.Count();
                        var text = reader.ReadElementContentAsString();
                        if (text.Length == 0)
                        {
                            text = "0";
                        }

                        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        {
                            return value;
                        }

                        throw new InvalidDataException("The XML plist real is not representable.");
                    }
                case "true":
                    budget.Count();
                    reader.Skip();
                    return true;
                case "false":
                    budget.Count();
                    reader.Skip();
                    return false;
                case "data":
                    {
                        budget.Count();
                        var text = reader.ReadElementContentAsString();
                        try
                        {
                            return Convert.FromBase64String(text);
                        }
                        catch (FormatException exception)
                        {
                            throw new InvalidDataException("The XML plist data element is not valid base64.", exception);
                        }
                    }
                case "date":
                    {
                        budget.Count();
                        var text = reader.ReadElementContentAsString();
                        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                                out var value))
                        {
                            return value.ToUniversalTime();
                        }

                        throw new InvalidDataException("The XML plist date is not representable.");
                    }
                default:
                    throw new InvalidDataException($"The XML plist contains an unsupported element '{name}'.");
            }
        }
        finally
        {
            budget.Leave();
        }
    }

    private sealed class XmlBudget
    {
        private readonly ApplePlistLimits _limits;
        private long _elements;
        private int _maxDepth;

        public XmlBudget(ApplePlistLimits limits) => _limits = limits;

        public void Enter(int depth)
        {
            _maxDepth = Math.Max(_maxDepth, depth);
            if (_maxDepth > _limits.MaxDepth)
            {
                throw new InvalidDataException($"The XML plist nesting exceeds {_limits.MaxDepth} levels.");
            }
        }

        public void Leave()
        {
        }

        public void Count()
        {
            if (++_elements > _limits.MaxElements)
            {
                throw new InvalidDataException($"The XML plist exceeds {_limits.MaxElements} values.");
            }
        }
    }
}
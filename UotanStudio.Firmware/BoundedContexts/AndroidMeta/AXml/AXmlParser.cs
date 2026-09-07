using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml;

public static class AXmlParser
{


    private const ushort ResXmlType = 0x0003;
    private const ushort ResStringPoolType = 0x0001;
    private const ushort ResXmlResourceMapType = 0x0180;
    private const ushort ResXmlStartNamespaceType = 0x0100;
    private const ushort ResXmlEndNamespaceType = 0x0101;
    private const ushort ResXmlStartElementType = 0x0102;
    private const ushort ResXmlEndElementType = 0x0103;
    private const ushort ResXmlCdataType = 0x0104;

    private const uint NoIndex = 0xFFFFFFFF;

    /// <summary>
    /// Parses AXML bytes into a document model.
    /// <para>解析 AXML 字节为文档模型。</para>
    /// </summary>
    public static AXmlDocument Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 8)
        {
            throw new AXmlException("File too small to be AXML (header is 8 bytes)");
        }

        var span = data.AsSpan(0, 8);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(span);
        if (type != ResXmlType)
        {
            throw new AXmlException($"Invalid AXML root chunk type 0x{type:X4} (expected 0x0003)");
        }
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        var fileSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..]));
        if (fileSize > data.Length)
        {
            throw new AXmlException($"AXML header claims {fileSize} bytes but only {data.Length} are available");
        }

        var doc = new AXmlDocument { HeaderSize = headerSize };

        var pos = (int)headerSize;
        var stack = new List<AXmlElement>();
        string? pendingText = null;

        while (pos + 8 <= fileSize)
        {
            var chunkType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            var chunkHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            var chunkSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4)));
            if (chunkSize < 8 || pos + chunkSize > fileSize)
            {
                throw new AXmlException($"Invalid chunk at 0x{pos:X} (type 0x{chunkType:X4}, size {chunkSize})");
            }

            switch (chunkType)
            {
                case ResStringPoolType:
                    doc.StringPool.Parse(data, pos, chunkSize);
                    break;

                case ResXmlResourceMapType:
                    ParseResourceMap(data, pos, chunkSize, doc);
                    break;

                case ResXmlStartNamespaceType:
                    ParseNamespace(data, pos, doc, isEnd: false);
                    break;

                case ResXmlEndNamespaceType:
                    ParseNamespace(data, pos, doc, isEnd: true);
                    break;

                case ResXmlStartElementType:
                    {
                        var element = ParseStartElement(data, pos, doc, pendingText);
                        pendingText = null;
                        if (stack.Count == 0)
                        {
                            doc.Root = element;
                        }
                        else
                        {
                            stack[^1].Children.Add(element);
                        }
                        element.Depth = stack.Count;
                        stack.Add(element);
                        break;
                    }

                case ResXmlEndElementType:
                    {
                        if (stack.Count > 0)
                        {
                            var element = stack[^1];
                            if (pendingText is not null)
                            {
                                element.Text = pendingText;
                            }
                            stack.RemoveAt(stack.Count - 1);
                        }
                        pendingText = null;
                        break;
                    }

                case ResXmlCdataType:
                    pendingText = ParseCdata(data, pos, doc);
                    break;

                default:


                    break;
            }

            pos += chunkSize;
        }

        if (stack.Count != 0)
        {
            throw new AXmlException("Unbalanced elements: AXML ends inside an element");
        }

        return doc;
    }

    /// <summary>
    /// Checks whether the header looks like AXML magic (for content sniffing).
    /// <para>判断头部是否为 AXML 魔数（用于内容探测）。</para>
    /// </summary>
    public static bool IsAxml(byte[] data)
    {
        if (data is null || data.Length < 8) return false;
        var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2));
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2));
        return type == ResXmlType && headerSize == 8;
    }

    private static void ParseResourceMap(byte[] data, int offset, int chunkSize, AXmlDocument doc)
    {


        var count = (chunkSize - 8) / 4;
        for (var i = 0; i < count; i++)
        {
            doc.ResourceMap.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8 + i * 4)));
        }
    }

    private static void ParseNamespace(byte[] data, int offset, AXmlDocument doc, bool isEnd)
    {


        var prefixIdx = ReadIndex(data, offset + 16);
        var uriIdx = ReadIndex(data, offset + 20);
        var prefix = prefixIdx >= 0 ? doc.StringPool.GetString(prefixIdx) : string.Empty;
        var uri = uriIdx >= 0 ? doc.StringPool.GetString(uriIdx) : string.Empty;

        if (!isEnd && !string.IsNullOrEmpty(uri))
        {
            doc.Namespaces.Add(new AXmlNamespace { Prefix = prefix, Uri = uri });
        }
    }

    private static AXmlElement ParseStartElement(byte[] data, int offset, AXmlDocument doc, string? pendingText)
    {


        var nsIdx = ReadIndex(data, offset + 16);
        var nameIdx = ReadIndex(data, offset + 20);
        var attributeStart = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 24));
        var attributeSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 26));
        var attributeCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 28));

        var element = new AXmlElement
        {
            Namespace = nsIdx >= 0 ? doc.StringPool.GetString(nsIdx) : null,
            Name = nameIdx >= 0 ? doc.StringPool.GetString(nameIdx) : string.Empty,
            LineNumber = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8)))
        };
        if (pendingText is not null)
        {
            element.Text = pendingText;
        }


        var attrBase = offset + 16 + attributeStart;
        for (var i = 0; i < attributeCount; i++)
        {
            var aOff = attrBase + i * attributeSize;
            var aNs = ReadIndex(data, aOff);
            var aName = ReadIndex(data, aOff + 4);
            var rawIdx = ReadIndex(data, aOff + 8);


            var valueType = data[aOff + 15];
            var valueData = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(aOff + 16));

            var attr = new AXmlAttribute
            {
                Namespace = aNs >= 0 ? doc.StringPool.GetString(aNs) : null,
                Name = aName >= 0 ? doc.StringPool.GetString(aName) : string.Empty,
                OriginalNameIndex = aName,
                RawValue = rawIdx >= 0 ? doc.StringPool.GetString(rawIdx) : null,
                ValueType = (AXmlValueType)valueType,
                ValueData = valueData
            };
            element.Attributes.Add(attr);
        }

        return element;
    }

    private static string? ParseCdata(byte[] data, int offset, AXmlDocument doc)
    {

        var dataIdx = ReadIndex(data, offset + 16);
        return dataIdx >= 0 ? doc.StringPool.GetString(dataIdx) : null;
    }

    private static int ReadIndex(byte[] data, int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        return value == NoIndex ? -1 : checked((int)value);
    }
}

public sealed class AXmlException : Exception
{
    /// <summary>
    /// Creates an AXML exception.
    /// <para>创建 AXML 异常。</para>
    /// </summary>
    public AXmlException(string message) : base(message) { }
}
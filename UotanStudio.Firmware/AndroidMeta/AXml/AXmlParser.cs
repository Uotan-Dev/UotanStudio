using System.Buffers.Binary;
using UotanStudio.Firmware.AndroidMeta.AXml;

namespace UotanStudio.Firmware.AndroidMeta.AXml;

/// <summary>
/// Reads Android binary XML (AXML) from an in-memory byte buffer into an <see cref="AXmlDocument"/>. Every chunk offset and length is validated against the buffer before it is read; the parser holds the whole image at once and supports neither streaming nor cancellation.
/// <para>从内存字节缓冲区读取 Android 二进制 XML（AXML）并生成 <see cref="AXmlDocument"/>。每个数据块的偏移与长度都在读取前对照缓冲区校验；解析器一次性持有整个镜像，既不支持流式处理，也不支持取消。</para>
/// </summary>
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
    /// <param name="data">
    /// Full contents of an AXML file. The declared file size must not exceed <paramref name="data"/>, and each chunk header and body is bounds-checked against it before reading.
    /// <para>AXML 文件的完整内容。声明的文件大小不得超过 <paramref name="data"/>，且每个数据块的头与正文在读取前都按其做边界校验。</para>
    /// </param>
    /// <returns>
    /// The parsed document with root element, string pool, resource map and namespaces populated. Attribute values remain encoded as their raw <see cref="AXmlValueType"/> and data until formatted.
    /// <para>解析后的文档，已填充根元素、字符串池、资源映射与命名空间。属性值在格式化前仍保留其原始 <see cref="AXmlValueType"/> 与数据编码。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>. <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="AXmlException">
    /// The buffer is shorter than the 8-byte header, the root chunk type is not <c>0x0003</c>, the declared file size exceeds the buffer, a chunk is smaller than its header or runs past the file size, the string-pool layout is invalid, or elements are left unbalanced at end of input.
    /// <para>缓冲区短于 8 字节头部、根块类型不是 <c>0x0003</c>、声明的文件大小超出缓冲区、某个数据块小于其头部或越过文件大小、字符串池布局无效，或输入结束时元素未配平。</para>
    /// </exception>
    /// <exception cref="OverflowException">
    /// A declared file or chunk size exceeds <see cref="System.Int32.MaxValue"/>. <para>声明的文件或数据块大小超过 <see cref="System.Int32.MaxValue"/>。</para></exception>
    /// <example>
    /// <code>
    /// var doc = AXmlParser.Parse(File.ReadAllBytes("AndroidManifest.xml"));
    /// Console.WriteLine(doc.Root.Name);
    /// </code>
    /// </example>
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
    /// <param name="data">
    /// Candidate bytes, typically the first bytes of a file; only the first 8 bytes are inspected. A <see langword="null"/> or short buffer simply fails the check.
    /// <para>候选字节，通常为文件的前若干字节；仅检查前 8 个字节。<see langword="null"/> 或过短的缓冲区只会导致检查失败。</para>
    /// </param>
    /// <returns><see langword="true"/> when the root type is <c>0x0003</c> and the header size is <c>8</c>; <see langword="false"/> otherwise. This is a shallow sniff and does not validate the body.
    /// <para>当根类型为 <c>0x0003</c> 且头部大小为 <c>8</c> 时返回 <see langword="true"/>，否则为 <see langword="false"/>。这只是浅层探测，不校验正文。</para></returns>
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

/// <summary>
/// Raised when an AXML buffer fails validation: bad magic, out-of-range offsets or sizes, an invalid string-pool layout, or unbalanced elements.
/// <para>当 AXML 缓冲区校验失败时抛出：魔数错误、越界的偏移或大小、无效的字符串池布局，或元素未配平。</para>
/// </summary>
internal sealed class AXmlException : Exception
{
    /// <summary>
    /// Creates an AXML exception.
    /// <para>创建 AXML 异常。</para>
    /// </summary>
    /// <param name="message">Description of the failed validation, surfaced to the caller. <para>对所失败校验的描述，会呈现给调用方。</para></param>
    public AXmlException(string message) : base(message) { }
}
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.AXml;

namespace UotanStudio.Firmware.AndroidMeta.AXml;

/// <summary>
/// Serializes an <c>AXmlDocument</c> model back to Android binary XML bytes, rebuilding the string pool and emitting the resource-map, namespace and element chunks in little-endian order.
/// <para>将 <c>AXmlDocument</c> 模型序列化回 Android 二进制 XML 字节，重建字符串池，并按小端序输出资源映射、命名空间与元素块。</para>
/// </summary>
public static class AXmlWriter
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
    /// Serializes the model into AXML bytes. The string pool is rebuilt with the source pool entries kept at the head in their original order (later duplicates collapse onto the first ordinal match), followed by any namespace, element, attribute and text strings not already present; pool flags are always written as UTF-8 sorted and the source style offsets and style data are copied verbatim.
    /// <para>把模型序列化为 AXML 字节。字符串池重建时保留源池条目在头部及其原有顺序（后出现的重复项按序数相等合并到首次出现的位置），其后追加命名空间、元素、属性与文本中尚未出现的字符串；池标志始终写为 UTF-8 排序，源样式偏移与样式数据原样复制。</para>
    /// </summary>
    /// <param name="doc">
    /// Document model to serialize; its <c>Root</c> must be a parsed or hand-built element tree.
    /// <para>要序列化的文档模型；其 <c>Root</c> 必须是已解析或手工构建的元素树。</para>
    /// </param>
    /// <returns>
    /// The complete little-endian AXML image, starting with the 8-byte root header. Chunk line numbers reuse each element's source line when positive, otherwise the tree depth.
    /// <para>完整的 little-endian AXML 镜像，以 8 字节根头部开始。块行号在元素源行号为正时复用源行号，否则使用树深度。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="doc"/> is <see langword="null"/>.
    /// <para><paramref name="doc"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="AXmlException">
    /// The document has no root element, or a string referenced by the tree is absent from the rebuilt pool. Resource ids are preserved instead of being renumbered: the resource-map chunk and each attribute's raw value payload are copied byte-for-byte from the model, and a parsed attribute keeps its original name-pool index when that index still holds an equal name in the rebuilt pool. Only modeled constructs are emitted — text becomes a CDATA chunk solely on childless elements, so an element carrying both text and children emits its children and drops the text, and chunk types the parser did not model (they were skipped when reading) cannot be re-emitted.
    /// <para>文档没有根元素，或元素树引用的字符串在重建后的池中缺失。资源 id 通过原样保留而非重新编号：资源映射块与每个属性的原始值载荷都按字节从模型复制；已解析的属性在其原名池索引在重建池中仍保存相等名称时保留该索引。只发射已建模的结构——文本仅在无子元素的元素上成为 CDATA 块，因此同时带有文本和子元素的元素会发射子元素并丢弃文本；解析器未建模的块类型（读取时已被跳过）无法再次发射。</para>
    /// </exception>
    /// <example>
    /// <code language="csharp">
    /// byte[] source = File.ReadAllBytes("AndroidManifest.xml");
    /// var doc = AXmlParser.Parse(source);
    /// byte[] rebuilt = AXmlWriter.Write(doc);
    /// </code>
    /// </example>
    public static byte[] Write(AXmlDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Root is null) throw new AXmlException("AXML document has no root element");

        var pool = Enumerable.Range(0, doc.StringPool.Count)
            .Select(doc.StringPool.GetString).ToList();
        int Intern(string s)
        {
            for (var i = 0; i < pool.Count; i++)
            {
                if (string.Equals(pool[i], s, StringComparison.Ordinal)) return i;
            }
            pool.Add(s);
            return pool.Count - 1;
        }

        foreach (var ns in doc.Namespaces)
        {
            Intern(ns.Prefix);
            Intern(ns.Uri);
        }

        for (var i = 0; i < doc.StringPool.Count; i++)
        {
            Intern(doc.StringPool.GetString(i));
        }

        var stack = new Stack<AXmlElement>();
        stack.Push(doc.Root);
        while (stack.Count > 0)
        {
            var el = stack.Pop();
            if (el.Namespace is { Length: > 0 }) Intern(el.Namespace);
            Intern(el.Name);
            if (el.Text is not null) Intern(el.Text);
            foreach (var attr in el.Attributes)
            {
                if (attr.Namespace is { Length: > 0 }) Intern(attr.Namespace);
                Intern(attr.Name);
                if (attr.RawValue is not null) Intern(attr.RawValue);
            }

            for (var i = el.Children.Count - 1; i >= 0; i--) stack.Push(el.Children[i]);
        }

        var poolHeaderSize = 28;
        var stringOffsetsStart = poolHeaderSize;
        var stringsStart = stringOffsetsStart + (pool.Count + doc.StringPool.StyleOffsets.Length) * 4;
        var stringOffsets = new int[pool.Count];
        var stringBytes = new byte[pool.Count][];
        var poolDataSize = stringsStart;
        for (var i = 0; i < pool.Count; i++)
        {
            stringOffsets[i] = poolDataSize - stringsStart;
            var (payload, total) = EncodeUtf8String(pool[i]);
            stringBytes[i] = payload;
            poolDataSize += total;
        }

        var hasResourceMap = doc.ResourceMap.Count > 0;
        var resourceMapSize = hasResourceMap ? 8 + doc.ResourceMap.Count * 4 : 0;
        var namespaceOpenSize = doc.Namespaces.Count > 0 ? doc.Namespaces.Count * 24 : 0;
        var namespaceCloseSize = doc.Namespaces.Count > 0 ? doc.Namespaces.Count * 24 : 0;

        var treeSize = ElementTreeSize(doc.Root);

        var poolSize = poolDataSize + doc.StringPool.StyleData.Length;
        var totalSize = 8 + poolSize + resourceMapSize + namespaceOpenSize + treeSize + namespaceCloseSize;

        var buffer = new byte[totalSize];
        var pos = 0;

        WriteChunkHeader(buffer, ref pos, ResXmlType, 8, totalSize);

        WriteChunkHeader(buffer, ref pos, ResStringPoolType, (ushort)poolHeaderSize, poolSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)pool.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)doc.StringPool.StyleOffsets.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), 0x100u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)stringsStart);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), doc.StringPool.StyleOffsets.Length == 0 ? 0 : (uint)poolDataSize);

        pos += 20;
        for (var i = 0; i < pool.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)stringOffsets[i]);
            pos += 4;
        }
        foreach (var styleOffset in doc.StringPool.StyleOffsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), styleOffset);
            pos += 4;
        }
        for (var i = 0; i < pool.Count; i++)
        {
            stringBytes[i].CopyTo(buffer, pos);
            pos += stringBytes[i].Length;
        }
        doc.StringPool.StyleData.CopyTo(buffer, pos);
        pos += doc.StringPool.StyleData.Length;

        if (hasResourceMap)
        {
            WriteChunkHeader(buffer, ref pos, ResXmlResourceMapType, 8, resourceMapSize);
            foreach (var id in doc.ResourceMap)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), id);
                pos += 4;
            }
        }

        foreach (var ns in doc.Namespaces)
        {
            WriteChunkHeader(buffer, ref pos, ResXmlStartNamespaceType, 16, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), NoIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)Intern(ns.Prefix));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)Intern(ns.Uri));
            pos += 16;
        }

        WriteElementTree(buffer, ref pos, doc.Root, pool, 0);

        foreach (var ns in doc.Namespaces)
        {
            WriteChunkHeader(buffer, ref pos, ResXmlEndNamespaceType, 16, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), NoIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)Intern(ns.Prefix));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)Intern(ns.Uri));
            pos += 16;
        }

        return buffer;
    }

    private static void WriteStartElement(byte[] buffer, ref int pos, AXmlElement el, List<string> pool, int line)
    {
        var attrCount = el.Attributes.Count;
        var attrStart = 20;
        var attrSize = 20;
        var size = 16 + attrStart + attrCount * attrSize;
        WriteChunkHeader(buffer, ref pos, ResXmlStartElementType, 16, size);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)(line > 0 ? line : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), NoIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)PoolIndex(pool, el.Namespace));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)PoolIndex(pool, el.Name));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 16), (ushort)attrStart);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 18), (ushort)attrSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 20), (ushort)attrCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 22), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 24), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 26), 0);
        pos += 28;

        foreach (var attr in el.Attributes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)PoolIndex(pool, attr.Namespace));
            var nameIndex = attr.OriginalNameIndex is int original && original >= 0
                && original < pool.Count && pool[original] == attr.Name ? original : PoolIndex(pool, attr.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)nameIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8),
                attr.RawValue is null ? NoIndex : (uint)PoolIndex(pool, attr.RawValue));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 12), 8);
            buffer[pos + 14] = 0;
            buffer[pos + 15] = (byte)attr.ValueType;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), attr.ValueData);
            pos += 20;
        }
    }

    private static void WriteEndElement(byte[] buffer, ref int pos, AXmlElement el, List<string> pool, int line)
    {
        WriteChunkHeader(buffer, ref pos, ResXmlEndElementType, 16, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)(line > 0 ? line : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), NoIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)PoolIndex(pool, el.Namespace));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)PoolIndex(pool, el.Name));
        pos += 16;
    }

    private static void WriteElementTree(byte[] buffer, ref int pos, AXmlElement el, List<string> pool, int depth)
    {
        WriteStartElement(buffer, ref pos, el, pool, el.LineNumber > 0 ? el.LineNumber : depth + 1);

        if (el.Text is not null && el.Children.Count == 0)
        {
            WriteCdata(buffer, ref pos, el.Text, pool, el.LineNumber > 0 ? el.LineNumber : depth + 1);
        }
        else
        {
            foreach (var child in el.Children)
            {
                WriteElementTree(buffer, ref pos, child, pool, depth + 1);
            }
        }

        WriteEndElement(buffer, ref pos, el, pool, el.LineNumber > 0 ? el.LineNumber : depth + 1);
    }

    private static void WriteCdata(byte[] buffer, ref int pos, string text, List<string> pool, int line)
    {
        WriteChunkHeader(buffer, ref pos, ResXmlCdataType, 16, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)(line > 0 ? line : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), NoIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)PoolIndex(pool, text));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 12), 8);
        buffer[pos + 14] = 0;
        buffer[pos + 15] = (byte)AXmlValueType.String;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), (uint)PoolIndex(pool, text));
        pos += 20;
    }

    private static int ElementTreeSize(AXmlElement el)
    {
        var size = 16 + 20 + el.Attributes.Count * 20;
        size += 24;
        if (el.Text is not null && el.Children.Count == 0)
        {
            size += 28;
        }
        else
        {
            foreach (var child in el.Children)
            {
                size += ElementTreeSize(child);
            }
        }
        return size;
    }

    private static int PoolIndex(List<string> pool, string? value)
    {
        if (value is null) return -1;
        for (var i = 0; i < pool.Count; i++)
        {
            if (string.Equals(pool[i], value, StringComparison.Ordinal)) return i;
        }
        throw new AXmlException($"String '{value}' not found in the rebuilt pool");
    }

    /// <summary>
    /// Encodes one UTF-8 pool string (variable-length length header + UTF-8 bytes + NUL, 4-byte aligned).
    /// <para>编码一个 UTF-8 池字符串（变长长度头 + UTF-8 字节 + 终止 NUL，4 字节对齐）。</para>
    /// </summary>
    private static (byte[] Payload, int Total) EncodeUtf8String(string value)
    {
        var utf16Len = value.Length;
        var utf8Bytes = Encoding.UTF8.GetBytes(value);

        var utf16Header = EncodeLength(utf16Len);
        var utf8Header = EncodeLength(utf8Bytes.Length);
        var payloadLen = utf16Header.Length + utf8Header.Length + utf8Bytes.Length + 1;
        var padded = (payloadLen + 3) & ~3;
        var payload = new byte[padded];
        utf16Header.CopyTo(payload, 0);
        utf8Header.CopyTo(payload, utf16Header.Length);
        utf8Bytes.CopyTo(payload, utf16Header.Length + utf8Header.Length);

        return (payload, padded);
    }

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80)
        {
            return [(byte)length];
        }
        return [(byte)(0x80 | (length >> 8)), (byte)(length & 0xFF)];
    }

    private static void WriteChunkHeader(byte[] buffer, ref int pos, ushort type, ushort headerSize, int size)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)size);
        pos += 8;
    }
}
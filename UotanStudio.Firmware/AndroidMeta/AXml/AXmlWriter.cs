using System.Buffers.Binary;
using System.Text;

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
    /// Serializes the model into AXML bytes. The string pool is rebuilt with source entries kept at the head in their original order, followed by strings not already present.
    /// <para>把模型序列化为 AXML 字节。字符串池重建时保留源条目在头部及其原有顺序，其后追加尚未出现的字符串。</para>
    /// </summary>
    /// <param name="doc">
    /// Document model to serialize; its <c>Root</c> must be a parsed or hand-built element tree.
    /// <para>要序列化的文档模型；其 <c>Root</c> 必须是已解析或手工构建的元素树。</para>
    /// </param>
    /// <returns>
    /// The complete little-endian AXML image, starting with the 8-byte root header.
    /// <para>完整的 little-endian AXML 镜像，以 8 字节根头部开始。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="doc"/> is <see langword="null"/>.
    /// <para><paramref name="doc"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="AXmlException">
    /// The document has no root element, or a string referenced by the tree is absent from the rebuilt pool.
    /// <para>文档没有根元素，或元素树引用的字符串在重建后的池中缺失。</para>
    /// </exception>
    /// <example>
    /// <code language="csharp">
    /// byte[] bytes = AXmlWriter.Write(doc);
    /// </code>
    /// <para>把模型写出为 AXML 字节。</para>
    /// </example>
    public static byte[] Write(AXmlDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Root is null) throw new AXmlException("AXML document has no root element");

        var pool = new List<string>(doc.StringPool.Count);
        var poolIndex = new Dictionary<string, int>(doc.StringPool.Count, StringComparer.Ordinal);
        for (var i = 0; i < doc.StringPool.Count; i++)
        {
            var value = doc.StringPool.GetString(i);
            poolIndex.TryAdd(value, pool.Count);
            pool.Add(value);
        }
        int Intern(string s)
        {
            if (poolIndex.TryGetValue(s, out var existing)) return existing;
            poolIndex[s] = pool.Count;
            pool.Add(s);
            return pool.Count - 1;
        }

        foreach (var ns in doc.Namespaces)
        {
            Intern(ns.Prefix);
            Intern(ns.Uri);
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

        WriteElementTree(buffer, ref pos, doc.Root, pool, poolIndex, 0);

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

    private static void WriteStartElement(byte[] buffer, ref int pos, AXmlElement el, List<string> pool,
        Dictionary<string, int> poolIndex, int line)
    {
        var attrCount = el.Attributes.Count;
        var attrStart = 20;
        var attrSize = 20;
        var size = 16 + attrStart + attrCount * attrSize;
        WriteChunkHeader(buffer, ref pos, ResXmlStartElementType, 16, size);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)(line > 0 ? line : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), NoIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)PoolIndex(poolIndex, el.Namespace));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)PoolIndex(poolIndex, el.Name));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 16), (ushort)attrStart);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 18), (ushort)attrSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 20), (ushort)attrCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 22), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 24), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 26), 0);
        pos += 28;

        foreach (var attr in el.Attributes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)PoolIndex(poolIndex, attr.Namespace));
            var nameIndex = attr.OriginalNameIndex is int original && original >= 0
                && original < pool.Count && pool[original] == attr.Name ? original : PoolIndex(poolIndex, attr.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)nameIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8),
                attr.RawValue is null ? NoIndex : (uint)PoolIndex(poolIndex, attr.RawValue));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 12), 8);
            buffer[pos + 14] = 0;
            buffer[pos + 15] = (byte)attr.ValueType;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), attr.ValueData);
            pos += 20;
        }
    }

    private static void WriteEndElement(byte[] buffer, ref int pos, AXmlElement el, Dictionary<string, int> poolIndex, int line)
    {
        WriteChunkHeader(buffer, ref pos, ResXmlEndElementType, 16, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)(line > 0 ? line : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), NoIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)PoolIndex(poolIndex, el.Namespace));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)PoolIndex(poolIndex, el.Name));
        pos += 16;
    }

    private static void WriteElementTree(byte[] buffer, ref int pos, AXmlElement el, List<string> pool,
        Dictionary<string, int> poolIndex, int depth)
    {
        WriteStartElement(buffer, ref pos, el, pool, poolIndex, el.LineNumber > 0 ? el.LineNumber : depth + 1);

        if (el.Text is not null && el.Children.Count == 0)
        {
            WriteCdata(buffer, ref pos, el.Text, poolIndex, el.LineNumber > 0 ? el.LineNumber : depth + 1);
        }
        else
        {
            foreach (var child in el.Children)
            {
                WriteElementTree(buffer, ref pos, child, pool, poolIndex, depth + 1);
            }
        }

        WriteEndElement(buffer, ref pos, el, poolIndex, el.LineNumber > 0 ? el.LineNumber : depth + 1);
    }

    private static void WriteCdata(byte[] buffer, ref int pos, string text, Dictionary<string, int> poolIndex, int line)
    {
        WriteChunkHeader(buffer, ref pos, ResXmlCdataType, 16, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)(line > 0 ? line : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), NoIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)PoolIndex(poolIndex, text));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 12), 8);
        buffer[pos + 14] = 0;
        buffer[pos + 15] = (byte)AXmlValueType.String;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), (uint)PoolIndex(poolIndex, text));
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

    private static int PoolIndex(Dictionary<string, int> poolIndex, string? value)
    {
        if (value is null) return -1;
        return poolIndex.TryGetValue(value, out var index)
            ? index
            : throw new AXmlException($"String '{value}' not found in the rebuilt pool");
    }

    /// <summary>
    /// Encodes one UTF-8 pool string (variable-length length header + UTF-8 bytes + NUL, 4-byte aligned).
    /// <para>编码一个 UTF-8 池字符串（变长长度头 + UTF-8 字节 + 终止 NUL，4 字节对齐）。</para>
    /// </summary>
    /// <param name="value">The pool string to encode. <para>待编码的池字符串。</para></param>
    /// <returns>The padded payload and its total byte count. <para>补齐后的载荷及其总字节数。</para></returns>
    private static (byte[] Payload, int Total) EncodeUtf8String(string value)
    {
        var utf16Length = value.Length;
        var utf8ByteCount = Encoding.UTF8.GetByteCount(value);
        var utf16HeaderLength = utf16Length < 0x80 ? 1 : 2;
        var utf8HeaderLength = utf8ByteCount < 0x80 ? 1 : 2;
        var payloadLength = utf16HeaderLength + utf8HeaderLength + utf8ByteCount + 1;
        var padded = (payloadLength + 3) & ~3;
        var payload = new byte[padded];
        var cursor = WriteLength(payload, 0, utf16Length);
        cursor = WriteLength(payload, cursor, utf8ByteCount);
        Encoding.UTF8.GetBytes(value, payload.AsSpan(cursor));

        return (payload, padded);
    }

    private static int WriteLength(byte[] buffer, int offset, int length)
    {
        if (length < 0x80)
        {
            buffer[offset] = (byte)length;
            return offset + 1;
        }
        buffer[offset] = (byte)(0x80 | (length >> 8));
        buffer[offset + 1] = (byte)(length & 0xFF);
        return offset + 2;
    }

    private static void WriteChunkHeader(byte[] buffer, ref int pos, ushort type, ushort headerSize, int size)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)size);
        pos += 8;
    }
}
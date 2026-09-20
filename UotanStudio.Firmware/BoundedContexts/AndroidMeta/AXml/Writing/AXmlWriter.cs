using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Parsing;
using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Writing;

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
    /// Serializes the model into AXML bytes.
    /// <para>把模型序列化为 AXML 字节。</para>
    /// </summary>
    public static byte[] Write(AXmlDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Root is null) throw new AXmlException("AXML document has no root element");


        // Existing indices also address typed string values and the resource map.
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
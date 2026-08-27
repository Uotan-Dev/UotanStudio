using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.AXml;

/// <summary>
/// <para>AXML 序列化器：把 <see cref="AXmlDocument"/> 模型重建为二进制 AXML 字节
/// （字符串池、资源映射、命名空间与元素树按 AOSP 规范写出）。纯托管实现。</para>
/// AXML serializer: rebuilds an <see cref="AXmlDocument"/> model into binary AXML bytes
/// (string pool, resource map, namespaces and element tree per the AOSP spec). Fully managed.
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
    /// <para>把模型序列化为 AXML 字节。</para>
    /// Serializes the model into AXML bytes.
    /// </summary>
    public static byte[] Write(AXmlDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Root is null) throw new AXmlException("AXML document has no root element");

        // 1) 收集字符串池：元素/属性名、命名空间、原始值与文本，保持解析顺序并去重。
        // 1) Collect the string pool: element/attribute names, namespaces, raw values and text,
        //    preserving parse order and de-duplicating.
        var pool = new List<string>();
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

        // 元素/属性收集：显式栈遍历（避免递归过深）。
        // Element/attribute collection via an explicit stack (avoids deep recursion).
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
            // 逆序压栈保证输出顺序与 Children 一致。
            // Push children in reverse so the output order matches the Children list.
            for (var i = el.Children.Count - 1; i >= 0; i--) stack.Push(el.Children[i]);
        }

        // 2) 计算字符串池字节布局。
        // 2) Compute the string pool byte layout.
        var poolHeaderSize = 28;
        var stringOffsetsStart = poolHeaderSize;
        var stringsStart = stringOffsetsStart + pool.Count * 4;
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

        // 3) 计算各 chunk 大小并一次性分配输出缓冲。
        // 3) Compute chunk sizes and allocate the output buffer once.
        var hasResourceMap = doc.ResourceMap.Count > 0;
        var resourceMapSize = hasResourceMap ? 8 + doc.ResourceMap.Count * 4 : 0;
        var namespaceOpenSize = doc.Namespaces.Count > 0 ? doc.Namespaces.Count * 24 : 0;
        var namespaceCloseSize = doc.Namespaces.Count > 0 ? doc.Namespaces.Count * 24 : 0;

        // 元素树大小：递归计算（Start/End 各 36+attr*20 / 24；CDATA 28）。
        // Element tree size via recursion (Start/End are 36+attr*20 / 24; CDATA is 28).
        var treeSize = 0;
        foreach (var el in doc.Root.Children) treeSize += ElementTreeSize(el);
        treeSize += 16 + 20 + doc.Root.Attributes.Count * 20; // 根元素 Start
        treeSize += 24;                                        // 根元素 End

        var poolSize = poolHeaderSize + pool.Count * 4 + (poolDataSize - stringsStart);
        var totalSize = 8 + poolSize + resourceMapSize + namespaceOpenSize + treeSize + namespaceCloseSize;

        // 4) 写出。
        // 4) Write out.
        var buffer = new byte[totalSize];
        var pos = 0;

        // 文件头。
        // File header.
        WriteChunkHeader(buffer, ref pos, ResXmlType, 8, totalSize);

        // 字符串池。
        // String pool.
        WriteChunkHeader(buffer, ref pos, ResStringPoolType, (ushort)poolHeaderSize, poolSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)pool.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), 0u);   // styleCount
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), 0x100u); // UTF8_FLAG
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)stringsStart);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), 0u);   // stylesStart
        // 池头共 28 字节，但 WriteChunkHeader 已推进 8 字节，这里只需再推进 20。
        // The pool header is 28 bytes total; WriteChunkHeader already advanced 8, so advance 20 here.
        pos += 20;
        for (var i = 0; i < pool.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)stringOffsets[i]);
            pos += 4;
        }
        for (var i = 0; i < pool.Count; i++)
        {
            stringBytes[i].CopyTo(buffer, pos);
            pos += stringBytes[i].Length;
        }

        // 资源映射。
        // Resource map.
        if (hasResourceMap)
        {
            WriteChunkHeader(buffer, ref pos, ResXmlResourceMapType, 8, resourceMapSize);
            foreach (var id in doc.ResourceMap)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), id);
                pos += 4;
            }
        }

        // 命名空间开始。
        // Namespace starts.
        foreach (var ns in doc.Namespaces)
        {
            WriteChunkHeader(buffer, ref pos, ResXmlStartNamespaceType, 16, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), 1u); // lineNumber
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), NoIndex); // comment
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)Intern(ns.Prefix));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)Intern(ns.Uri));
            pos += 16;
        }

        // 根元素。
        // Root element.
        WriteStartElement(buffer, ref pos, doc.Root, pool, 1);
        foreach (var child in doc.Root.Children)
        {
            WriteElementTree(buffer, ref pos, child, pool, 1);
        }
        WriteEndElement(buffer, ref pos, doc.Root, pool, 1);

        // 命名空间结束。
        // Namespace ends.
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
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 22), (ushort)0); // idIndex
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 24), (ushort)0); // classIndex
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 26), (ushort)0); // styleIndex
        pos += 28;

        foreach (var attr in el.Attributes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)PoolIndex(pool, attr.Namespace));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)PoolIndex(pool, attr.Name));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8),
                attr.RawValue is null ? NoIndex : (uint)PoolIndex(pool, attr.RawValue));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 12), 8); // Res_value.size
            buffer[pos + 14] = 0;                                                 // res0
            buffer[pos + 15] = (byte)attr.ValueType;                              // dataType
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
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 12), 8); // Res_value.size
        buffer[pos + 14] = 0;
        buffer[pos + 15] = (byte)AXmlValueType.String;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), (uint)PoolIndex(pool, text));
        pos += 20;
    }

    private static int ElementTreeSize(AXmlElement el)
    {
        var size = 16 + 20 + el.Attributes.Count * 20; // Start
        size += 24;                                     // End
        if (el.Text is not null && el.Children.Count == 0)
        {
            size += 28; // CDATA
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
    /// <para>编码一个 UTF-8 池字符串（变长长度头 + UTF-8 字节 + 终止 NUL，4 字节对齐）。
    /// 长度头顺序按 AOSP 规范：先 UTF-16 长度后 UTF-8 长度。</para>
    /// Encodes one UTF-8 pool string (variable-length length header + UTF-8 bytes + NUL, 4-byte
    /// aligned). Header order follows the AOSP spec: UTF-16 length first, then UTF-8 length.
    /// </summary>
    private static (byte[] Payload, int Total) EncodeUtf8String(string value)
    {
        var utf16Len = value.Length;
        var utf8Bytes = Encoding.UTF8.GetBytes(value);

        // 长度头：utf16 长度在前、utf8 长度在后，各为 1~2 字节（高位标志表示两字节）。
        // Length header: utf16 length first, then utf8 length; each is 1-2 bytes (high bit marks two bytes).
        var utf16Header = EncodeLength(utf16Len);
        var utf8Header = EncodeLength(utf8Bytes.Length);
        var payloadLen = utf16Header.Length + utf8Header.Length + utf8Bytes.Length + 1; // + NUL
        var padded = (payloadLen + 3) & ~3;
        var payload = new byte[padded];
        utf16Header.CopyTo(payload, 0);
        utf8Header.CopyTo(payload, utf16Header.Length);
        utf8Bytes.CopyTo(payload, utf16Header.Length + utf8Header.Length);
        // 末尾 NUL 与填充字节均为 0
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

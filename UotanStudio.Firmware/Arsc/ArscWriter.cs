using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AXml;

namespace UotanStudio.Firmware.Arsc;

/// <summary>
/// <para>ARSC 序列化器：把 <see cref="ResourceTable"/> 模型重建为 resources.arsc 字节
/// （全局字符串池、包、typeStrings/keyStrings、类型规范与类型表按 AOSP 规范写出）。
/// 纯托管实现，支持值级编辑后的重建写回。</para>
/// ARSC serializer: rebuilds a <see cref="ResourceTable"/> model into resources.arsc bytes
/// (global string pool, packages, typeStrings/keyStrings, type specs and type tables per the AOSP
/// spec). Fully managed; supports value-level edits and rebuild-write.
/// </summary>
public static class ArscWriter
{
    // ResChunk_header.type 值（AOSP ResourceTypes.h）
    // ResChunk_header.type values (AOSP ResourceTypes.h).
    private const ushort ResTableType = 0x0002;
    private const ushort ResStringPoolType = 0x0001;
    private const ushort ResTablePackageType = 0x0200;
    private const ushort ResTableTypeSpecType = 0x0202;
    private const ushort ResTableTypeType = 0x0201;

    private const uint NoEntry = 0xFFFFFFFF;
    private const ushort EntryFlagComplex = 0x0001;

    // 固定大小（AOSP）
    // Fixed sizes (AOSP).
    private const int PackageHeaderSize = 288;     // 8 + id(4) + name(256) + 5*4
    private const int TypeSpecHeaderSize = 16;     // 8 + id(1)+res0(1)+res1(2) + entryCount(4)
    private const int TypeHeaderSize = 84;         // 8 + id(1)+res0(1)+res1(2)+entryCount(4)+entriesStart(4)+config(64)
    private const int ConfigSize = 64;             // ResTable_config

    /// <summary>
    /// <para>把模型序列化为 ARSC 字节。</para>
    /// Serializes the model into ARSC bytes.
    /// </summary>
    public static byte[] Write(ResourceTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        // 1) 全局字符串池：按原序 Intern，保持 String 型值引用的 ValueData 索引稳定。
        // 1) Global string pool: intern in order to keep ValueData indices of String-typed values stable.
        var global = BuildPool(table.GlobalPool);

        // 2) 计算各 chunk 大小。
        // 2) Compute chunk sizes.
        var packageSizes = new int[table.Packages.Count];
        for (var p = 0; p < table.Packages.Count; p++)
        {
            packageSizes[p] = ComputePackageSize(table.Packages[p]);
        }

        var totalSize = 12; // ResTableHeader: header(8) + packageCount(4)
        totalSize += global.Size;
        foreach (var size in packageSizes) totalSize += size;

        // 3) 写出。
        // 3) Write out.
        var buffer = new byte[totalSize];
        var pos = 0;

        WriteChunkHeader(buffer, ref pos, ResTableType, 12, totalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)table.Packages.Count);
        pos += 4;

        WriteStringPool(buffer, ref pos, global);

        foreach (var pkg in table.Packages)
        {
            WritePackage(buffer, ref pos, pkg);
        }

        return buffer;
    }

    private static void WritePackage(byte[] buffer, ref int pos, ResPackage pkg)
    {
        // ResTable_package 头：header(8) + id(4) + name[128*2] + typeStrings(4) + lastPublicType(4)
        //                     + keyStrings(4) + lastPublicKey(4) + typeIdOffset(4)
        var typeStringsOffset = PackageHeaderSize;
        var keyStringsOffset = typeStringsOffset + ComputePoolSize(pkg.TypeStrings);
        WriteChunkHeader(buffer, ref pos, ResTablePackageType, PackageHeaderSize, 0); // size 稍后回填
        var packageStart = pos - 8;

        buffer[pos] = pkg.Id;
        pos += 4; // id + 3 保留字节（高 24 位为 0）

        // name：编码为 UTF-8 后截断到 256 字节（128 个 UTF-16 单元宽），避免越界。
        // name: encode to UTF-8, then truncate to 256 bytes (the 128-UTF-16-unit wide field).
        var nameBytes = Encoding.UTF8.GetBytes(pkg.Name);
        if (nameBytes.Length > 256)
        {
            nameBytes = nameBytes[..256];
        }
        nameBytes.CopyTo(buffer, pos);
        pos += 256;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)typeStringsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)pkg.TypeStrings.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)keyStringsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)pkg.KeyStrings.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), 0u); // typeIdOffset
        pos += 20;

        WriteStringPool(buffer, ref pos, BuildPool(pkg.TypeStrings));
        WriteStringPool(buffer, ref pos, BuildPool(pkg.KeyStrings));

        // 类型规范与类型表：按类型 ID 分组，每组先写 TypeSpec 再写该 ID 的各 config 表。
        // Type specs and tables: group by type ID; each group writes one TypeSpec then its config tables.
        foreach (var group in pkg.Types.GroupBy(t => t.Id).OrderBy(g => g.Key))
        {
            WriteTypeSpec(buffer, ref pos, group.Key, group, pkg);
            foreach (var type in group)
            {
                WriteTypeTable(buffer, ref pos, type, pkg);
            }
        }

        // 回填包 chunk 大小（packageStart 是 chunk 起始，已含 8 字节头）。
        // Back-fill the package chunk size (packageStart is the chunk start, header included).
        var packageSize = pos - packageStart;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(packageStart + 4), (uint)packageSize);
    }

    private static void WriteTypeSpec(byte[] buffer, ref int pos, byte typeId, IGrouping<byte, ResType> group, ResPackage pkg)
    {
        // 条目数取该组最大 entry index + 1（各 config 表条目数一致，取最大者）。
        // Entry count = max entry index + 1 across the group's tables (all config tables agree).
        var entryCount = group.Max(t => t.Entries.Count);
        pkg.TypeSpecFlags.TryGetValue(typeId, out var originalFlags);

        var size = TypeSpecHeaderSize + entryCount * 4;
        WriteChunkHeader(buffer, ref pos, ResTableTypeSpecType, TypeSpecHeaderSize, size);
        buffer[pos] = typeId;
        buffer[pos + 1] = 0; // res0
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), 0); // res1
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)entryCount);
        pos += TypeSpecHeaderSize - 8; // 头 8 字节已由 WriteChunkHeader 写入

        for (var i = 0; i < entryCount; i++)
        {
            var flag = originalFlags is not null && i < originalFlags.Length ? originalFlags[i] : 0u;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), flag);
            pos += 4;
        }
    }

    private static void WriteTypeTable(byte[] buffer, ref int pos, ResType type, ResPackage pkg)
    {
        var chunkStart = pos; // WriteChunkHeader 之前的 chunk 起始（entriesStart 相对它计算）
        var entryCount = type.Entries.Count;
        var entriesStart = TypeHeaderSize + entryCount * 4;
        var size = entriesStart + ComputeEntriesSize(type);
        WriteChunkHeader(buffer, ref pos, ResTableTypeType, TypeHeaderSize, size);

        buffer[pos] = type.Id;
        buffer[pos + 1] = 0; // res0
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), 0); // res1
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)entriesStart);

        // config：原样写回解析时的 64 字节（ConfigHex 十六进制）。
        // config: write back the 64 bytes captured at parse time (from ConfigHex).
        var configBytes = Convert.FromHexString(type.ConfigHex);
        configBytes.CopyTo(buffer, pos + 12);
        pos += TypeHeaderSize - 8; // 头 8 字节已由 WriteChunkHeader 写入

        // 条目偏移表：先占位，随条目写出逐个回填（相对 chunk 起始）。
        // Entry offset table: placeholder first, back-filled per entry (relative to the chunk start).
        var offsetsBase = pos;
        pos += entryCount * 4;

        for (var i = 0; i < entryCount; i++)
        {
            var entry = i < type.Entries.Count ? type.Entries[i] : null;
            if (entry is null)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offsetsBase + i * 4), NoEntry);
                continue;
            }

            // 条目偏移相对 entriesStart（AOSP 规范），而非 chunk 头。
            // Entry offsets are relative to entriesStart (AOSP spec), not the chunk header.
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offsetsBase + i * 4),
                (uint)(pos - (chunkStart + entriesStart)));
            WriteEntry(buffer, ref pos, entry, pkg);
        }
    }

    private static void WriteEntry(byte[] buffer, ref int pos, ResEntry entry, ResPackage pkg)
    {
        var value = entry.Value;
        if (value is null || !value.IsComplex)
        {
            // 简单条目：ResTable_entry(size=8, flags=0, key) + Res_value(8)。
            // Simple entry: ResTable_entry(size=8, flags=0, key) + Res_value(8).
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)entry.KeyIndex);
            WriteResValue(buffer, pos + 8, value ?? new ResValue());
            pos += 16;
            return;
        }

        // 复杂条目：ResTable_map_entry(size=16, flags=1, key, parent, count) + maps。
        // Complex entry: ResTable_map_entry(size=16, flags=1, key, parent, count) + maps.
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), EntryFlagComplex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)entry.KeyIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), value.Parent);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)value.Maps.Count);
        pos += 16;
        foreach (var map in value.Maps)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), map.Name);
            WriteResValue(buffer, pos + 4, map.Value);
            pos += 12;
        }
    }

    private static void WriteResValue(byte[] buffer, int offset, ResValue value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), 8); // size
        buffer[offset + 2] = 0;                                            // res0
        buffer[offset + 3] = (byte)value.Type;                             // dataType
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), value.Data);
    }

    private static int ComputePackageSize(ResPackage pkg)
    {
        var size = PackageHeaderSize;
        size += ComputePoolSize(pkg.TypeStrings);
        size += ComputePoolSize(pkg.KeyStrings);
        foreach (var group in pkg.Types.GroupBy(t => t.Id))
        {
            var entryCount = group.Max(t => t.Entries.Count);
            size += TypeSpecHeaderSize + entryCount * 4;
            foreach (var type in group)
            {
                size += TypeHeaderSize + type.Entries.Count * 4 + ComputeEntriesSize(type);
            }
        }
        return size;
    }

    private static int ComputeEntriesSize(ResType type)
    {
        var size = 0;
        foreach (var entry in type.Entries)
        {
            if (entry is null) continue;
            if (entry.Value is { IsComplex: true } complex)
            {
                size += 16 + complex.Maps.Count * 12;
            }
            else
            {
                size += 16;
            }
        }
        return size;
    }

    private static void WriteChunkHeader(byte[] buffer, ref int pos, ushort type, ushort headerSize, int size)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)size);
        pos += 8;
    }

    private static void WriteStringPool(byte[] buffer, ref int pos, PoolLayout pool)
    {
        WriteChunkHeader(buffer, ref pos, ResStringPoolType, 28, pool.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)pool.Offsets.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), 0u);       // styleCount
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), 0x100u);   // UTF8_FLAG
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)pool.StringsStart);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), 0u);      // stylesStart
        pos += 20; // 池头 28 字节，WriteChunkHeader 已写 8

        foreach (var offset in pool.Offsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)offset);
            pos += 4;
        }
        foreach (var payload in pool.Payloads)
        {
            payload.CopyTo(buffer, pos);
            pos += payload.Length;
        }
    }

    private static PoolLayout BuildPool(AXmlStringPool pool)
    {
        var offsets = new int[pool.Count];
        var payloads = new byte[pool.Count][];
        var stringsStart = 28 + pool.Count * 4;
        var dataSize = stringsStart;
        for (var i = 0; i < pool.Count; i++)
        {
            offsets[i] = dataSize - stringsStart;
            payloads[i] = EncodeUtf8String(pool.GetString(i));
            dataSize += payloads[i].Length;
        }
        return new PoolLayout(offsets, payloads, stringsStart, dataSize);
    }

    private static int ComputePoolSize(AXmlStringPool pool)
    {
        var size = 28 + pool.Count * 4;
        for (var i = 0; i < pool.Count; i++)
        {
            size += EncodeUtf8String(pool.GetString(i)).Length;
        }
        return size;
    }

    /// <summary>
    /// <para>编码一个 UTF-8 池字符串（AOSP 长度头：先 UTF-16 长度后 UTF-8 长度，4 字节对齐）。</para>
    /// Encodes one UTF-8 pool string (AOSP length header: UTF-16 length then UTF-8 length, 4-byte aligned).
    /// </summary>
    private static byte[] EncodeUtf8String(string value)
    {
        var utf16Len = value.Length;
        var utf8Bytes = Encoding.UTF8.GetBytes(value);
        var utf16Header = EncodeLength(utf16Len);
        var utf8Header = EncodeLength(utf8Bytes.Length);
        var payloadLen = utf16Header.Length + utf8Header.Length + utf8Bytes.Length + 1; // + NUL
        var padded = (payloadLen + 3) & ~3;
        var payload = new byte[padded];
        utf16Header.CopyTo(payload, 0);
        utf8Header.CopyTo(payload, utf16Header.Length);
        utf8Bytes.CopyTo(payload, utf16Header.Length + utf8Header.Length);
        return payload;
    }

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80)
        {
            return [(byte)length];
        }
        return [(byte)(0x80 | (length >> 8)), (byte)(length & 0xFF)];
    }

    /// <summary>
    /// <para>字符串池布局（偏移表 + 编码负载 + 头信息）。</para>
    /// String pool layout (offset table + encoded payloads + header info).
    /// </summary>
    private sealed record PoolLayout(int[] Offsets, byte[][] Payloads, int StringsStart, int Size);
}
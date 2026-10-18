using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Resources;

namespace UotanStudio.Firmware.AndroidMeta.Arsc;

/// <summary>
/// Serializes a <see cref="ResourceTable"/> back into little-endian ARSC bytes.
/// <para>将 <see cref="ResourceTable"/> 序列化为小端序的 ARSC 字节。</para>
/// </summary>
/// <remarks>
/// String pools are emitted with UTF-8 payloads and a header flag field of 0x100, packages use a 288-byte
/// header, and every chunk whose bytes could not be interpreted by the parser is copied verbatim so a parse/write
/// round trip keeps its unknown structures.
/// <para>字符串池以 UTF-8 负载写出、头部标志字段固定为 0x100，包使用 288 字节头部，解析器无法解释的块会被逐字节复制，
/// 从而保证解析/写出的往返仍保留这些未知结构。</para>
/// </remarks>
public static class ArscWriter
{


    private const ushort ResTableType = 0x0002;
    private const ushort ResStringPoolType = 0x0001;
    private const ushort ResTablePackageType = 0x0200;
    private const ushort ResTableTypeSpecType = 0x0202;
    private const ushort ResTableTypeType = 0x0201;

    private const ushort EntryFlagComplex = 0x0001;


    private const int PackageHeaderSize = 288;
    private const int TypeSpecHeaderSize = 16;
    private const int ConfigSize = 64;

    /// <summary>Serializes the model into ARSC bytes.<para>把模型序列化为 ARSC 字节。</para></summary>
    /// <param name="table">Table to write. Types are grouped by their <see cref="ResType.Id"/> and emitted in ascending id order, one type specification chunk per group followed by that group's type chunks; null entries inside <see cref="ResType.Entries"/> are written as the 0xFFFFFFFF offset sentinel.
    /// <para>待写出的资源表。类型按 <see cref="ResType.Id"/> 分组并按 id 升序写出，每组先写一个类型规格块， 再写该组的类型块；<see cref="ResType.Entries"/> 中的 null 条目会写成 0xFFFFFFFF 偏移哨兵值。</para></param>
    /// <returns>A self-contained buffer whose declared total size, package count and chunk sizes match the produced bytes.
    /// <para>自包含的缓冲区，其声明的总大小、包数量与各块大小均与实际写出的字节一致。</para></returns>
    /// <example>
    /// <code>
    /// ResourceTable table = ArscParser.Parse(File.ReadAllBytes("resources.arsc"));
    /// File.WriteAllBytes("copy.arsc", ArscWriter.Write(table));
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.
    /// <para><paramref name="table"/> 为 null。</para></exception>
    /// <exception cref="ArscException">A package name is longer than 127 UTF-16 code units or contains a NUL, or a type configuration decodes to fewer than 4 bytes, more than 65515 bytes, or a length that is not a multiple of 4.
    /// <para>包名超过 127 个 UTF-16 编码单元或包含 NUL；或某个类型的配置字节少于 4 字节、多于 65515 字节、或长度不是 4 的倍数。</para></exception>
    public static byte[] Write(ResourceTable table)
    {
        ArgumentNullException.ThrowIfNull(table);


        var global = BuildPool(table.GlobalPool);

        var layouts = new PackageLayout[table.Packages.Count];
        var totalSize = 12 + global.Size;
        for (var p = 0; p < table.Packages.Count; p++)
        {
            layouts[p] = BuildPackageLayout(table.Packages[p]);
            totalSize += layouts[p].Size;
        }
        foreach (var chunk in table.ExtraChunks) totalSize += chunk.Length;


        var buffer = new byte[totalSize];
        var pos = 0;

        WriteChunkHeader(buffer, ref pos, ResTableType, 12, totalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)table.Packages.Count);
        pos += 4;

        WriteStringPool(buffer, ref pos, global);

        for (var p = 0; p < table.Packages.Count; p++)
        {
            WritePackage(buffer, ref pos, table.Packages[p], layouts[p]);
        }

        foreach (var chunk in table.ExtraChunks)
        {
            chunk.CopyTo(buffer, pos);
            pos += chunk.Length;
        }
        return buffer;
    }

    private static void WritePackage(byte[] buffer, ref int pos, ResPackage pkg, PackageLayout layout)
    {


        var typeStringsOffset = PackageHeaderSize;
        var keyStringsOffset = typeStringsOffset + layout.TypeStrings.Size;
        WriteChunkHeader(buffer, ref pos, ResTablePackageType, PackageHeaderSize, 0);
        var packageStart = pos - 8;

        buffer[pos] = pkg.Id;
        pos += 4;


        if (pkg.Name.Length > 127 || pkg.Name.Contains('\0'))
            throw new ArscException("Package name must fit 127 UTF-16 code units without NUL.");
        var nameBytes = Encoding.Unicode.GetBytes(pkg.Name);
        nameBytes.CopyTo(buffer, pos);
        pos += 256;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)typeStringsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)pkg.TypeStrings.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)keyStringsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)pkg.KeyStrings.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), pkg.TypeIdOffset);
        pos += 20;

        WriteStringPool(buffer, ref pos, layout.TypeStrings);
        WriteStringPool(buffer, ref pos, layout.KeyStrings);


        foreach (var group in pkg.Types.GroupBy(t => t.Id).OrderBy(g => g.Key))
        {
            WriteTypeSpec(buffer, ref pos, group.Key, group, pkg);
            foreach (var type in group)
            {
                WriteTypeTable(buffer, ref pos, type, layout.Configs[type]);
            }
        }
        foreach (var typeId in pkg.TypeSpecFlags.Keys.Except(pkg.Types.Select(t => t.Id)).Order())
            WriteTypeSpec(buffer, ref pos, typeId, [], pkg);


        foreach (var chunk in pkg.ExtraChunks)
        {
            chunk.CopyTo(buffer, pos);
            pos += chunk.Length;
        }
        var packageSize = pos - packageStart;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(packageStart + 4), (uint)packageSize);
    }

    private static void WriteTypeSpec(byte[] buffer, ref int pos, byte typeId, IEnumerable<ResType> group, ResPackage pkg)
    {


        pkg.TypeSpecFlags.TryGetValue(typeId, out var originalFlags);
        var entryCount = Math.Max(group.Select(t => t.EntryCount).DefaultIfEmpty().Max(), originalFlags?.Length ?? 0);

        var size = TypeSpecHeaderSize + entryCount * 4;
        WriteChunkHeader(buffer, ref pos, ResTableTypeSpecType, TypeSpecHeaderSize, size);
        buffer[pos] = typeId;
        buffer[pos + 1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)entryCount);
        pos += TypeSpecHeaderSize - 8;

        for (var i = 0; i < entryCount; i++)
        {
            var flag = originalFlags is not null && i < originalFlags.Length ? originalFlags[i] : 0u;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), flag);
            pos += 4;
        }
    }

    private static void WriteTypeTable(byte[] buffer, ref int pos, ResType type, byte[] configBytes)
    {
        var chunkStart = pos;
        var entryCount = type.EntryCount;
        var typeHeaderSize = checked((ushort)(20 + configBytes.Length));
        var entriesStart = typeHeaderSize + entryCount * 4;
        var size = entriesStart + ComputeEntriesSize(type);
        WriteChunkHeader(buffer, ref pos, ResTableTypeType, typeHeaderSize, size);

        buffer[pos] = type.Id;
        buffer[pos + 1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), (uint)entriesStart);


        configBytes.CopyTo(buffer, pos + 12);
        pos += typeHeaderSize - 8;


        var offsetsBase = pos;
        buffer.AsSpan(offsetsBase, entryCount * 4).Fill(0xFF);
        pos += entryCount * 4;

        if (type.MaterializedEntries is { } entries)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry is null)
                {
                    continue;
                }
                WriteEntryOffsetAndBody(buffer, ref pos, chunkStart, entriesStart, offsetsBase, i, entry);
            }
            return;
        }

        var compactCount = type.CompactEntryCount;
        for (var i = 0; i < compactCount; i++)
        {
            WriteEntryOffsetAndBody(buffer, ref pos, chunkStart, entriesStart, offsetsBase,
                type.CompactEntryIndex(i), type.CompactEntryValue(i));
        }
    }

    private static void WriteEntryOffsetAndBody(byte[] buffer, ref int pos, int chunkStart, int entriesStart,
        int offsetsBase, int index, ResEntry entry)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offsetsBase + index * 4),
            (uint)(pos - (chunkStart + entriesStart)));
        WriteEntry(buffer, ref pos, entry);
    }

    private static void WriteEntry(byte[] buffer, ref int pos, ResEntry entry)
    {
        var value = entry.Value;
        if (value is null || !value.IsComplex)
        {



            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), (ushort)(entry.Flags & 6));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)entry.KeyIndex);
            WriteResValue(buffer, pos + 8, value ?? new ResValue());
            pos += 16;
            return;
        }


        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2), (ushort)(EntryFlagComplex | (entry.Flags & 6)));
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
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), 8);
        buffer[offset + 2] = 0;
        buffer[offset + 3] = (byte)value.Type;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), value.Data);
    }

    private static PackageLayout BuildPackageLayout(ResPackage pkg)
    {
        var typeStrings = BuildPool(pkg.TypeStrings);
        var keyStrings = BuildPool(pkg.KeyStrings);
        var configs = new Dictionary<ResType, byte[]>(pkg.Types.Count);
        foreach (var type in pkg.Types)
        {
            configs[type] = PrepareConfig(type);
        }

        var size = PackageHeaderSize + typeStrings.Size + keyStrings.Size;
        foreach (var group in pkg.Types.GroupBy(t => t.Id))
        {
            var entryCount = Math.Max(group.Max(t => t.EntryCount),
                pkg.TypeSpecFlags.TryGetValue(group.Key, out var flags) ? flags.Length : 0);
            size += TypeSpecHeaderSize + entryCount * 4;
            foreach (var type in group)
            {
                size += 20 + configs[type].Length + type.EntryCount * 4 + ComputeEntriesSize(type);
            }
        }
        foreach (var typeId in pkg.TypeSpecFlags.Keys.Except(pkg.Types.Select(t => t.Id)))
            size += TypeSpecHeaderSize + pkg.TypeSpecFlags[typeId].Length * 4;
        foreach (var chunk in pkg.ExtraChunks) size += chunk.Length;
        return new PackageLayout(typeStrings, keyStrings, configs, size);
    }

    private static byte[] PrepareConfig(ResType type)
    {
        var stored = type.ConfigBytes;
        if (stored.Length == 0)
        {
            var bytes = new byte[ConfigSize];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
            return bytes;
        }
        if (stored.Length < 4 || stored.Length > ushort.MaxValue - 20 || (stored.Length & 3) != 0)
            throw new ArscException("Invalid resource config length.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(stored) == (uint)stored.Length)
            return stored;
        var patched = (byte[])stored.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patched, (uint)patched.Length);
        return patched;
    }

    private static int ComputeEntriesSize(ResType type)
    {
        var size = 0;
        if (type.MaterializedEntries is { } entries)
        {
            foreach (var entry in entries)
            {
                if (entry is not null)
                {
                    size += EntrySize(entry);
                }
            }
            return size;
        }
        for (var i = 0; i < type.CompactEntryCount; i++)
        {
            size += EntrySize(type.CompactEntryValue(i));
        }
        return size;
    }

    private static int EntrySize(ResEntry entry) =>
        entry.Value is { IsComplex: true } complex ? 16 + complex.Maps.Count * 12 : 16;

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
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), (uint)pool.StyleOffsets.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), 0x100u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), (uint)pool.StringsStart);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), pool.StyleOffsets.Length == 0 ? 0 : (uint)(pool.Size - pool.StyleData.Length));
        pos += 20;

        foreach (var offset in pool.Offsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), (uint)offset);
            pos += 4;
        }
        foreach (var offset in pool.StyleOffsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos), offset);
            pos += 4;
        }
        foreach (var payload in pool.Payloads)
        {
            payload.CopyTo(buffer, pos);
            pos += payload.Length;
        }
        pool.StyleData.CopyTo(buffer, pos);
        pos += pool.StyleData.Length;
    }

    private static PoolLayout BuildPool(AXmlStringPool pool)
    {
        var offsets = new int[pool.Count];
        var payloads = new byte[pool.Count][];
        var stringsStart = 28 + (pool.Count + pool.StyleOffsets.Length) * 4;
        var dataSize = stringsStart;
        for (var i = 0; i < pool.Count; i++)
        {
            offsets[i] = dataSize - stringsStart;
            payloads[i] = EncodeUtf8String(pool.GetString(i));
            dataSize += payloads[i].Length;
        }
        return new PoolLayout(offsets, payloads, stringsStart, dataSize + pool.StyleData.Length, pool.StyleOffsets, pool.StyleData);
    }

    /// <summary>
    /// Encodes one UTF-8 pool string (AOSP length header: UTF-16 length then UTF-8 length, 4-byte aligned).
    /// <para>编码一个 UTF-8 池字符串（AOSP 长度头：先 UTF-16 长度后 UTF-8 长度，4 字节对齐）。</para>
    /// </summary>
    private static byte[] EncodeUtf8String(string value)
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
        return payload;
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

    private sealed record PackageLayout(PoolLayout TypeStrings, PoolLayout KeyStrings,
        Dictionary<ResType, byte[]> Configs, int Size);

    private sealed record PoolLayout(int[] Offsets, byte[][] Payloads, int StringsStart, int Size, uint[] StyleOffsets, byte[] StyleData);
}
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AXml;

namespace UotanStudio.Firmware.Arsc;

public static class ArscWriter
{


    private const ushort ResTableType = 0x0002;
    private const ushort ResStringPoolType = 0x0001;
    private const ushort ResTablePackageType = 0x0200;
    private const ushort ResTableTypeSpecType = 0x0202;
    private const ushort ResTableTypeType = 0x0201;

    private const uint NoEntry = 0xFFFFFFFF;
    private const ushort EntryFlagComplex = 0x0001;


    private const int PackageHeaderSize = 288;
    private const int TypeSpecHeaderSize = 16;
    private const int ConfigSize = 64;

    /// <summary>
    /// Serializes the model into ARSC bytes.
    /// <para>把模型序列化为 ARSC 字节。</para>
    /// </summary>
    public static byte[] Write(ResourceTable table)
    {
        ArgumentNullException.ThrowIfNull(table);


        var global = BuildPool(table.GlobalPool);


        var packageSizes = new int[table.Packages.Count];
        for (var p = 0; p < table.Packages.Count; p++)
        {
            packageSizes[p] = ComputePackageSize(table.Packages[p]);
        }

        var totalSize = 12;
        totalSize += global.Size;
        foreach (var size in packageSizes) totalSize += size;
        foreach (var chunk in table.ExtraChunks) totalSize += chunk.Length;


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

        foreach (var chunk in table.ExtraChunks)
        {
            chunk.CopyTo(buffer, pos);
            pos += chunk.Length;
        }
        return buffer;
    }

    private static void WritePackage(byte[] buffer, ref int pos, ResPackage pkg)
    {


        var typeStringsOffset = PackageHeaderSize;
        var keyStringsOffset = typeStringsOffset + ComputePoolSize(pkg.TypeStrings);
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

        WriteStringPool(buffer, ref pos, BuildPool(pkg.TypeStrings));
        WriteStringPool(buffer, ref pos, BuildPool(pkg.KeyStrings));


        foreach (var group in pkg.Types.GroupBy(t => t.Id).OrderBy(g => g.Key))
        {
            WriteTypeSpec(buffer, ref pos, group.Key, group, pkg);
            foreach (var type in group)
            {
                WriteTypeTable(buffer, ref pos, type, pkg);
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
        var entryCount = Math.Max(group.Select(t => t.Entries.Count).DefaultIfEmpty().Max(), originalFlags?.Length ?? 0);

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

    private static void WriteTypeTable(byte[] buffer, ref int pos, ResType type, ResPackage pkg)
    {
        var chunkStart = pos;
        var entryCount = type.Entries.Count;
        var configBytes = GetConfig(type);
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
        pos += entryCount * 4;

        for (var i = 0; i < entryCount; i++)
        {
            var entry = i < type.Entries.Count ? type.Entries[i] : null;
            if (entry is null)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offsetsBase + i * 4), NoEntry);
                continue;
            }


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

    private static int ComputePackageSize(ResPackage pkg)
    {
        var size = PackageHeaderSize;
        size += ComputePoolSize(pkg.TypeStrings);
        size += ComputePoolSize(pkg.KeyStrings);
        foreach (var group in pkg.Types.GroupBy(t => t.Id))
        {
            var entryCount = Math.Max(group.Max(t => t.Entries.Count),
                pkg.TypeSpecFlags.TryGetValue(group.Key, out var flags) ? flags.Length : 0);
            size += TypeSpecHeaderSize + entryCount * 4;
            foreach (var type in group)
            {
                size += 20 + GetConfig(type).Length + type.Entries.Count * 4 + ComputeEntriesSize(type);
            }
        }
        foreach (var typeId in pkg.TypeSpecFlags.Keys.Except(pkg.Types.Select(t => t.Id)))
            size += TypeSpecHeaderSize + pkg.TypeSpecFlags[typeId].Length * 4;
        foreach (var chunk in pkg.ExtraChunks) size += chunk.Length;
        return size;
    }

    private static byte[] GetConfig(ResType type)
    {
        var bytes = string.IsNullOrEmpty(type.ConfigHex) ? new byte[ConfigSize] : Convert.FromHexString(type.ConfigHex);
        if (bytes.Length < 4 || bytes.Length > ushort.MaxValue - 20 || (bytes.Length & 3) != 0)
            throw new ArscException("Invalid resource config length.");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        return bytes;
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

    private static int ComputePoolSize(AXmlStringPool pool)
    {
        var size = 28 + (pool.Count + pool.StyleOffsets.Length) * 4 + pool.StyleData.Length;
        for (var i = 0; i < pool.Count; i++)
        {
            size += EncodeUtf8String(pool.GetString(i)).Length;
        }
        return size;
    }

    /// <summary>
    /// Encodes one UTF-8 pool string (AOSP length header: UTF-16 length then UTF-8 length, 4-byte aligned).
    /// <para>编码一个 UTF-8 池字符串（AOSP 长度头：先 UTF-16 长度后 UTF-8 长度，4 字节对齐）。</para>
    /// </summary>
    private static byte[] EncodeUtf8String(string value)
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

    private sealed record PoolLayout(int[] Offsets, byte[][] Payloads, int StringsStart, int Size, uint[] StyleOffsets, byte[] StyleData);
}
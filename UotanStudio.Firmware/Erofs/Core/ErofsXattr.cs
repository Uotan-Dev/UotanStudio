using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

public sealed class ErofsXattrEntry
{
    public string Name { get; init; } = string.Empty;

    public byte[] Value { get; init; } = [];

    public byte NameIndex { get; init; }
}

internal static class ErofsXattr
{
    public const int HeaderSize = 12;

    public const int EntryHeaderSize = 4;

    public const int EntryAlignment = 4;

    public const byte LongPrefixFlag = 0x80;

    public const byte LongPrefixMask = 0x7f;

    private const int SharedIdUnitSize = sizeof(uint);


    public const byte NameIndexUser = 1;
    public const byte NameIndexPosixAclAccess = 2;
    public const byte NameIndexPosixAclDefault = 3;
    public const byte NameIndexTrusted = 4;
    public const byte NameIndexLustre = 5;
    public const byte NameIndexSecurity = 6;
    public const byte NameIndexSystem = 7;

    private static readonly string[] DefaultPrefixes =
    [
        string.Empty,
        "user.",
        "system.posix_acl_access",
        "system.posix_acl_default",
        "trusted.",
        "lustre.",
        "security.",
        "system.",
    ];

    /// <summary>
    /// Total inline-xattr ibody size in bytes, matching the kernel's erofs_xattr_ibody_size: 12 + 4 * (icount - 1); 0 when icount is 0 (no inline xattr).
    /// <para>inline xattr ibody 总大小（字节），与内核 erofs_xattr_ibody_size 一致： 12 + 4 * (icount - 1)；icount 为 0 时返回 0（无 inline xattr）。</para>
    /// </summary>
    public static int GetIbodySize(int icount)
    {
        if (icount <= 0)
            return 0;
        return HeaderSize + SharedIdUnitSize * (icount - 1);
    }

    /// <summary>
    /// Default short-prefix lookup: maps a name index to its default prefix string (e.g. 1 → "user.").
    /// <para>默认短前缀查找：将名称索引映射到默认前缀字符串（如 1 → "user."）。</para>
    /// </summary>
    public static bool TryGetDefaultPrefix(byte nameIndex, out string prefix)
    {
        if (nameIndex < DefaultPrefixes.Length)
        {
            prefix = DefaultPrefixes[nameIndex];
            return prefix.Length > 0;
        }

        prefix = string.Empty;
        return false;
    }

    /// <summary>
    /// Reverse lookup of the default prefix table: splits a full xattr name into a (short-prefix name index + remaining short name).
    /// <para>反查默认前缀表：把完整 xattr 名拆为（短前缀名称索引 + 剩余短名）。</para>
    /// </summary>
    public static bool TrySplitShortName(string fullName, out byte nameIndex, out string shortName)
    {
        nameIndex = 0;
        shortName = string.Empty;
        if (string.IsNullOrEmpty(fullName))
        {
            return false;
        }

        for (byte i = 1; i < DefaultPrefixes.Length; i++)
        {
            var prefix = DefaultPrefixes[i];
            if (prefix.Length > 0 && fullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                nameIndex = i;
                shortName = fullName.Substring(prefix.Length);
                return shortName.Length > 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Encodes a list of entries into an inline xattr ibody (including the 12-byte header; h_shared_count = 0 when no shared entries are present).
    /// <para>将条目列表编码为 inline xattr ibody（含 12 字节头；无共享条目时 h_shared_count = 0）。</para>
    /// </summary>
    public static byte[] EncodeInline(IReadOnlyList<ErofsXattrEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var body = new List<byte>(HeaderSize + entries.Count * 8);
        body.AddRange(new byte[HeaderSize]);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!TrySplitShortName(entry.Name, out var nameIndex, out var shortName))
            {
                throw new InvalidDataException(
                    $"EROFS xattr name '{entry.Name}' does not match a built-in short prefix " +
                    "(user./trusted./security./system. etc.); long-prefix tables are not supported by the writer.");
            }

            var nameBytes = Encoding.UTF8.GetBytes(shortName);
            if (nameBytes.Length > byte.MaxValue)
            {
                throw new InvalidDataException($"EROFS xattr name '{entry.Name}' exceeds 255 bytes.");
            }

            var value = entry.Value ?? [];
            if (value.Length > ushort.MaxValue)
            {
                throw new InvalidDataException($"EROFS xattr value for '{entry.Name}' exceeds 65535 bytes.");
            }

            var start = body.Count;
            body.Add((byte)nameBytes.Length);
            body.Add(nameIndex);
            body.Add((byte)value.Length);
            body.Add((byte)(value.Length >> 8));
            body.AddRange(nameBytes);
            body.AddRange(value);

            while ((body.Count - start) % EntryAlignment != 0)
            {
                body.Add(0);
            }
        }

        var ibody = body.ToArray();

        if (ibody.Length % 4 != 0)
        {
            throw new InvalidDataException("EROFS xattr ibody must be 4-byte aligned.");
        }

        return ibody;
    }

    /// <summary>
    /// Derives the ErofsInode.XattrICount value from the ibody byte length (0 when there is no xattr).
    /// <para>由 ibody 总字节推导 ErofsInode.XattrICount 值（无 xattr 时返回 0）。</para>
    /// </summary>
    public static ushort GetICountForIbodyLength(int ibodyLength)
        => ibodyLength <= 0 ? (ushort)0 : checked((ushort)(ibodyLength / SharedIdUnitSize - 2));

    /// <summary>
    /// Parses the long xattr name prefix table located in the metadata area pointed to by superblock.xattr_prefix_start, holding XattrPrefixCount entries of {__u8 base_index; char infix[]} (infix NUL-terminated), i.e. the kernel's struct erofs_xattr_long_prefix. base_index is the short-prefix index and infix is the part of the prefix between the short prefix and the name.
    /// <para>解析长 xattr 名称前缀表。</para>
    /// </summary>
    public static List<(byte BaseIndex, string Infix)> ParsePrefixTable(
        ReadOnlySpan<byte> tableBytes, int prefixCount)
    {
        var result = new List<(byte BaseIndex, string Infix)>(Math.Max(0, prefixCount));
        var position = 0;
        for (var i = 0; i < prefixCount; i++)
        {
            if (position >= tableBytes.Length)
                break;

            var baseIndex = tableBytes[position];
            var infixStart = position + 1;
            var end = -1;
            for (var j = infixStart; j < tableBytes.Length; j++)
            {
                if (tableBytes[j] == 0)
                {
                    end = j;
                    break;
                }
            }

            if (end < 0)
                end = tableBytes.Length;

            var infix = Encoding.UTF8.GetString(tableBytes.Slice(infixStart, end - infixStart));
            result.Add((baseIndex, infix));
            position = end + 1;
        }

        return result;
    }

    /// <summary>
    /// Byte offset of the long-prefix table within the image.
    /// <para>长前缀表在镜像内的字节偏移。</para>
    /// </summary>
    public static long GetPrefixTableByteOffset(uint xattrPrefixStart)
        => (long)xattrPrefixStart << 2;

    /// <summary>
    /// Parses the shared xattr id array of the inline xattr ibody: h_shared_count u32s following the 12-byte header.
    /// <para>解析 inline xattr ibody 的共享 xattr id 数组：h_shared_count 个 u32，紧随 12 字节 头之后。</para>
    /// </summary>
    public static uint[] ParseSharedXattrIds(ReadOnlySpan<byte> inodeData, int icount)
    {
        var ibodySize = GetIbodySize(icount);
        if (ibodySize <= 0)
            return [];

        if (inodeData.Length < ibodySize)
            throw new FormatException($"xattr ibody too short: need {ibodySize} bytes, got {inodeData.Length}.");

        var sharedCount = inodeData[4];
        if (HeaderSize + SharedIdUnitSize * sharedCount > ibodySize)
            throw new FormatException($"Invalid h_shared_count {sharedCount} for ibody size {ibodySize}.");

        var ids = new uint[sharedCount];
        for (var i = 0; i < sharedCount; i++)
        {
            ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(
                inodeData.Slice(HeaderSize + SharedIdUnitSize * i, SharedIdUnitSize));
        }

        return ids;
    }

    /// <summary>
    /// Byte offset of a shared xattr entry within the shared xattr area = sharedId * 4 (kernel erofs_xattr_iter_shared: pos = xattr_blkaddr * block_size + xattr_shared_xattrs[i] * sizeof(__le32)).
    /// <para>共享 xattr 条目在共享 xattr 区内的字节偏移 = sharedId * 4（内核 erofs_xattr_iter_shared：pos = xattr_blkaddr * block_size + xattr_shared_xattrs[i] * sizeof(__le32)）。</para>
    /// </summary>
    public static long GetSharedXattrByteOffset(uint sharedId)
        => (long)sharedId * SharedIdUnitSize;

    /// <summary>
    /// Resolves the full xattr name, falling back to the raw name for unknown prefixes.
    /// <para>解析完整扩展属性名称，前缀无法解析时返回原始名称。</para>
    /// </summary>
    public static string ResolveName(
        byte nameIndex,
        ReadOnlySpan<byte> rawName,
        IReadOnlyList<(byte BaseIndex, string Infix)>? prefixTable)
    {
        var name = Encoding.UTF8.GetString(rawName);

        if ((nameIndex & LongPrefixFlag) != 0)
        {
            var tableIndex = nameIndex & LongPrefixMask;
            if (prefixTable is not null && tableIndex < prefixTable.Count)
            {
                var (baseIndex, infix) = prefixTable[tableIndex];
                if (TryGetDefaultPrefix(baseIndex, out var basePrefix))
                    return basePrefix + infix + name;
                return infix + name;
            }

            return name;
        }

        return TryGetDefaultPrefix(nameIndex, out var prefix) ? prefix + name : name;
    }

    /// <summary>
    /// Reads one erofs_xattr_entry (4-byte header + e_name + e_value) at offset in data and resolves its full name.
    /// <para>在 data 的 offset 处读取一个 erofs_xattr_entry（4 字节头 + e_name + e_value），并解析出完整名称。</para>
    /// </summary>
    public static bool TryReadEntry(
        ReadOnlySpan<byte> data,
        int offset,
        IReadOnlyList<(byte BaseIndex, string Infix)>? prefixTable,
        out ErofsXattrEntry entry,
        out int entrySize)
    {
        entry = null!;
        entrySize = 0;

        if (offset < 0 || offset + EntryHeaderSize > data.Length)
            return false;

        var nameLen = data[offset];
        var nameIndex = data[offset + 1];
        var valueSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2));

        var payloadSize = nameLen + valueSize;
        if (offset + EntryHeaderSize + payloadSize > data.Length)
            return false;

        var name = data.Slice(offset + EntryHeaderSize, nameLen);
        var value = data.Slice(offset + EntryHeaderSize + nameLen, valueSize).ToArray();

        entry = new ErofsXattrEntry
        {
            Name = ResolveName(nameIndex, name, prefixTable),
            Value = value,
            NameIndex = nameIndex,
        };

        entrySize = (EntryHeaderSize + payloadSize + (EntryAlignment - 1)) & ~(EntryAlignment - 1);
        return true;
    }

    /// <summary>
    /// Parses inline extended attributes from the inode's xattr body.
    /// <para>从 inode 的扩展属性区解析内联扩展属性。</para>
    /// </summary>
    public static List<ErofsXattrEntry> ParseInline(ReadOnlySpan<byte> inodeData, int icount)
        => ParseInline(inodeData, icount, null);

    /// <summary>
    /// Parses the inline xattr list (with a long-prefix table). prefixTable comes from ParsePrefixTable; when null, long-prefix entries fall back to their raw names.
    /// <para>解析 inline xattr 列表（带长前缀表）。</para>
    /// </summary>
    public static List<ErofsXattrEntry> ParseInline(
        ReadOnlySpan<byte> inodeData,
        int icount,
        IReadOnlyList<(byte BaseIndex, string Infix)>? prefixTable)
    {
        var result = new List<ErofsXattrEntry>();

        var ibodySize = GetIbodySize(icount);
        if (ibodySize <= 0)
            return result;

        if (inodeData.Length < ibodySize)
            throw new FormatException($"xattr ibody too short: need {ibodySize} bytes, got {inodeData.Length}.");


        var ibody = inodeData.Slice(0, ibodySize);

        var sharedCount = ibody[4];
        var entriesStart = HeaderSize + SharedIdUnitSize * sharedCount;
        if (entriesStart > ibodySize)
            throw new FormatException($"Invalid h_shared_count {sharedCount} for ibody size {ibodySize}.");

        var position = entriesStart;
        while (position + EntryHeaderSize <= ibodySize)
        {
            if (!TryReadEntry(ibody, position, prefixTable, out var entry, out var entrySize))
                break;
            result.Add(entry);
            position += entrySize;
        }

        return result;
    }

    /// <summary>
    /// Reads and resolves the shared xattr entry identified by sharedId from the shared xattr area (bytes starting at the superblock.xattr_blkaddr block).
    /// <para>在共享 xattr 区（自 superblock.xattr_blkaddr 块起始的字节区）中按 sharedId 读取并解析 一个共享条目。</para>
    /// </summary>
    public static bool TryParseSharedEntry(
        ReadOnlySpan<byte> sharedArea,
        uint sharedId,
        IReadOnlyList<(byte BaseIndex, string Infix)>? prefixTable,
        out ErofsXattrEntry entry)
    {
        var offset = (int)GetSharedXattrByteOffset(sharedId);
        return TryReadEntry(sharedArea, offset, prefixTable, out entry, out _);
    }
}

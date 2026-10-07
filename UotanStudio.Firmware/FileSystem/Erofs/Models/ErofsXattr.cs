using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

/// <summary>
/// One resolved EROFS extended attribute: full name, raw value bytes and the on-disk name index.
/// <para>一条已解析的 EROFS 扩展属性：完整名称、原始值字节与磁盘上的名称索引。</para>
/// </summary>
public sealed class ErofsXattrEntry
{
    /// <summary>Full attribute name with its short or long prefix resolved (e.g. <c>user.caps</c>).
    /// <para>解析短/长前缀后的完整属性名（例如 <c>user.caps</c>）。</para></summary>
    /// <value>Never null; empty only when the decoded entry carried an empty name and no prefix.
    /// <para>永不为 null；仅当解码出的条目名称为空且无前缀时为空字符串。</para></value>
    public string Name { get; init; } = string.Empty;

    /// <summary>Raw attribute value bytes as stored on disk.
    /// <para>按磁盘存储原样的属性值字节。</para></summary>
    /// <value>Empty array for a zero-length value; the bytes are not interpreted.
    /// <para>零长度值时为空数组；字节内容不做解释。</para></value>
    public byte[] Value { get; init; } = [];

    /// <summary>The on-disk <c>e_name_index</c> byte identifying the name's prefix origin.
    /// <para>磁盘上的 <c>e_name_index</c> 字节，标识名称前缀的来源。</para></summary>
    /// <value>Default prefix index (1..7), or a long-prefix index with the <c>0x80</c> flag set.
    /// <para>默认前缀索引（1..7），或置有 <c>0x80</c> 标志位的长前缀索引。</para></value>
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
    /// <param name="icount">Inline xattr entry count read from the inode; zero or a negative value means the inode carries no inline xattr area.
    /// <para>取自 inode 的 inline xattr 条目数；为 0 或负数表示该 inode 没有 inline xattr 区。</para></param>
    /// <returns>Ibody size in bytes, or <c>0</c> when <paramref name="icount"/> is not positive.
    /// <para>ibody 字节数；<paramref name="icount"/> 非正数时为 <c>0</c>。</para></returns>
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
    /// <param name="nameIndex">On-disk name index with no long-prefix flag applied; values below eight address the built-in table.
    /// <para>未置长前缀标志的磁盘名称索引；小于 8 时指向内置表。</para></param>
    /// <param name="prefix">Matched prefix text, or <c>string.Empty</c> when the index is unknown or refers to the empty slot 0.
    /// <para>匹配到的前缀文本；索引未知或指向空槽 0 时为 <c>string.Empty</c>。</para></param>
    /// <returns><see langword="true"/> when a non-empty default prefix was found, which is index 1 through 7.
    /// <para>找到非空默认前缀（索引 1 到 7）时返回 <see langword="true"/>。</para></returns>
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
    /// <param name="fullName">Complete attribute name matched ordinally against the table from index 1 upward; <see langword="null"/> and an empty string never match.
    /// <para>从索引 1 起按序数方式比对的完整属性名；<see langword="null"/> 与空字符串永不匹配。</para></param>
    /// <param name="nameIndex">Table index of the matched prefix, left at 0 when nothing matched.
    /// <para>匹配到的前缀表索引；未匹配时保持 0。</para></param>
    /// <param name="shortName">Bytes remaining after the matched prefix, left empty when nothing matched.
    /// <para>去掉匹配前缀后剩余的短名；未匹配时为空字符串。</para></param>
    /// <returns><see langword="true"/> when the remainder is non-empty, or when the name equals the POSIX ACL entries at index 2 or 3 whose remainder is empty.
    /// <para>剩余短名非空时返回 <see langword="true"/>；名称等于剩余为空的索引 2 或 3 的 POSIX ACL 条目时同样成立。</para></returns>
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
                return shortName.Length > 0 || i is NameIndexPosixAclAccess or NameIndexPosixAclDefault;
            }
        }

        return false;
    }

    /// <summary>
    /// Encodes a list of entries into an inline xattr ibody (including the 12-byte header; h_shared_count = 0 when no shared entries are present).
    /// <para>将条目列表编码为 inline xattr ibody（含 12 字节头；无共享条目时 h_shared_count = 0）。</para>
    /// </summary>
    /// <param name="entries">Attributes written in list order; every element must be non-null and every name must match a built-in short prefix,
    /// with a UTF-8 short name of at most 255 bytes and a value of at most 65535 bytes. An empty list produces the header alone.
    /// <para>按列表顺序写入的属性；每个元素不得为 null，名称必须命中内置短前缀，UTF-8 短名最长 255 字节，值最长 65535 字节。空列表只产生 ibody 头部。</para></param>
    /// <returns>4-byte aligned ibody: twelve zero header bytes followed by one aligned record per entry, each record holding the UTF-8 short name and the raw value.
    /// <para>4 字节对齐的 ibody：12 个零字节头部，随后每个条目一条对齐记录，记录内含 UTF-8 短名与原始值。</para></returns>
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
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS xattr name '{entry.Name}' does not match a built-in short prefix " +
                    "(user./trusted./security./system. etc.); long-prefix tables are not supported by the writer.");
            }

            var nameBytes = Encoding.UTF8.GetBytes(shortName);
            if (nameBytes.Length > byte.MaxValue)
            {
                throw new FileSystemCorruptedException($"EROFS xattr name '{entry.Name}' exceeds 255 bytes.");
            }

            var value = entry.Value ?? [];
            if (value.Length > ushort.MaxValue)
            {
                throw new FileSystemCorruptedException($"EROFS xattr value for '{entry.Name}' exceeds 65535 bytes.");
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
            throw new FileSystemCorruptedException("EROFS xattr ibody must be 4-byte aligned.");
        }

        return ibody;
    }

    /// <summary>
    /// Derives the ErofsInode.XattrICount value from the ibody byte length (0 when there is no xattr).
    /// <para>由 ibody 总字节推导 ErofsInode.XattrICount 值（无 xattr 时返回 0）。</para>
    /// </summary>
    /// <param name="ibodyLength">Ibody byte length as produced by <see cref="EncodeInline"/>; zero or a negative value means no inline xattr.
    /// <para><see cref="EncodeInline"/> 产生的 ibody 字节长度；0 或负数表示没有 inline xattr。</para></param>
    /// <returns>The entry count <c>ibodyLength / 4 - 2</c>, or 0 when the length is not positive; the conversion traps on overflow.
    /// <para>条目数 <c>ibodyLength / 4 - 2</c>；长度非正数时为 0，转换溢出时抛出异常。</para></returns>
    public static ushort GetICountForIbodyLength(int ibodyLength)
        => ibodyLength <= 0 ? (ushort)0 : checked((ushort)(ibodyLength / SharedIdUnitSize - 2));

    /// <summary>
    /// Parses four-byte-aligned records containing an LE16 length followed by base_index and infix bytes.
    /// <para>解析长 xattr 名称前缀表。</para>
    /// </summary>
    /// <param name="tableBytes">Table bytes laid out as LE16 record length, one base-index byte and (length - 1) infix bytes, each record padded to a multiple of 4.
    /// <para>表字节的排布为 LE16 记录长度、1 个 base-index 字节与 (length - 1) 个 infix 字节，每条记录补齐到 4 的倍数。</para></param>
    /// <param name="prefixCount">Record count to read, in the range 0 to 128 which the seven-bit on-disk index allows.
    /// <para>要读取的记录数，取值范围 0 到 128，与磁盘七位索引允许的范围一致。</para></param>
    /// <returns>One (base index, infix) pair per record in table order; the infix is decoded with the file-name encoding and sits between the default prefix named by the base index and the stored short name.
    /// <para>按表顺序返回每条记录的（base index、infix）；infix 用文件名编码解码，位于 base index 所指默认前缀与磁盘短名之间。</para></returns>
    public static List<(byte BaseIndex, string Infix)> ParsePrefixTable(
        ReadOnlySpan<byte> tableBytes, int prefixCount)
    {
        if (prefixCount is < 0 or > 128)
        {
            throw new FileSystemCorruptedException("EROFS xattr prefix count exceeds its seven-bit index.");
        }
        var result = new List<(byte BaseIndex, string Infix)>(Math.Max(0, prefixCount));
        var position = 0;
        for (var i = 0; i < prefixCount; i++)
        {
            if (position > tableBytes.Length - 2)
            {
                throw new FileSystemCorruptedException("Truncated EROFS xattr prefix record.");
            }
            var length = BinaryPrimitives.ReadUInt16LittleEndian(tableBytes[position..]);
            if (length is < 1 or > 256 || length > tableBytes.Length - position - 2)
            {
                throw new FileSystemCorruptedException("Invalid EROFS xattr prefix record length.");
            }
            var baseIndex = tableBytes[position + 2];
            var infix = Extraction.PortableExtractionPaths.NameEncoding.GetString(tableBytes.Slice(position + 3, length - 1));
            result.Add((baseIndex, infix));
            position = (position + 2 + length + 3) & ~3;
        }

        return result;
    }

    /// <summary>
    /// Byte offset of the long-prefix table within the image.
    /// <para>长前缀表在镜像内的字节偏移。</para>
    /// </summary>
    /// <param name="xattrPrefixStart">Superblock <c>xattr_prefix_start</c> field, stored in 4-byte units.
    /// <para>超级块的 <c>xattr_prefix_start</c> 字段，以 4 字节为单位存储。</para></param>
    /// <returns>Image byte offset of the table, <paramref name="xattrPrefixStart"/> scaled by 4 and widened to 64 bits.
    /// <para>表的镜像字节偏移，即 <paramref name="xattrPrefixStart"/> 乘以 4 并扩展为 64 位。</para></returns>
    public static long GetPrefixTableByteOffset(uint xattrPrefixStart)
        => (long)xattrPrefixStart << 2;

    /// <summary>
    /// Parses the shared xattr id array of the inline xattr ibody: h_shared_count u32s following the 12-byte header.
    /// <para>解析 inline xattr ibody 的共享 xattr id 数组：h_shared_count 个 u32，紧随 12 字节 头之后。</para>
    /// </summary>
    /// <param name="inodeData">Ibody bytes starting at the xattr header; must be at least the ibody size derived from <paramref name="icount"/>.
    /// <para>从 xattr 头部起始的 ibody 字节；长度不得小于由 <paramref name="icount"/> 推导出的 ibody 大小。</para></param>
    /// <param name="icount">Inline xattr entry count, used to compute the ibody size; zero or a negative value means there is no ibody.
    /// <para>inline xattr 条目数，用于计算 ibody 大小；0 或负数表示没有 ibody。</para></param>
    /// <returns>The <c>h_shared_count</c> little-endian u32 ids that follow the 12-byte header, empty when there is no ibody or the header declares no shared entries.
    /// <para>12 字节头之后的 <c>h_shared_count</c> 个小端 u32 id；没有 ibody 或头部未声明共享条目时为空。</para></returns>
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
    /// <param name="sharedId">One id read from the ibody's shared id array, which is itself a slot index counted in 4-byte units.
    /// <para>取自 ibody 共享 id 数组的单个 id，它本身就是以 4 字节为单位的槽号。</para></param>
    /// <returns>Offset in bytes from the start of the shared area, <paramref name="sharedId"/> multiplied by 4.
    /// <para>相对共享区起始处的字节偏移，即 <paramref name="sharedId"/> 乘以 4。</para></returns>
    public static long GetSharedXattrByteOffset(uint sharedId)
        => (long)sharedId * SharedIdUnitSize;

    /// <summary>
    /// Resolves the full xattr name from the default prefix, the long-prefix table, or both; an index the image does not define is reported as corruption.
    /// <para>由默认前缀、长前缀表或两者组合解析完整扩展属性名；镜像未定义的索引按损坏上报。</para>
    /// </summary>
    /// <param name="nameIndex">On-disk <c>e_name_index</c>; the <c>0x80</c> bit selects the long-prefix table and the remaining seven bits are its index.
    /// <para>磁盘上的 <c>e_name_index</c>；<c>0x80</c> 位表示改用长前缀表，其余七位是该表的索引。</para></param>
    /// <param name="rawName">Stored short-name bytes decoded with the file-name encoding; an empty span contributes nothing to the result.
    /// <para>用文件名编码解码的磁盘短名字节；空区间不会为结果增加内容。</para></param>
    /// <param name="prefixTable">Table from <see cref="ParsePrefixTable"/>, or <see langword="null"/> when the image defines none, in which case a long-prefixed name cannot be resolved.
    /// <para>由 <see cref="ParsePrefixTable"/> 得到的表；镜像未定义时为 <see langword="null"/>，此时带长前缀的名称无法解析。</para></param>
    /// <returns>The default prefix of the resolved base index, then the long-prefix infix when present, then the decoded short name.
    /// <para>解析出的基准索引对应的默认前缀，其次是长前缀 infix（存在时），最后是解码后的短名。</para></returns>
    public static string ResolveName(
        byte nameIndex,
        ReadOnlySpan<byte> rawName,
        IReadOnlyList<(byte BaseIndex, string Infix)>? prefixTable)
    {
        var name = Extraction.PortableExtractionPaths.NameEncoding.GetString(rawName);

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

            throw new FileSystemCorruptedException("EROFS xattr refers to an unknown long prefix.");
        }

        return TryGetDefaultPrefix(nameIndex, out var prefix)
            ? prefix + name : throw new FileSystemCorruptedException("EROFS xattr refers to an unknown namespace.");
    }

    /// <summary>
    /// Reads one erofs_xattr_entry (4-byte header + e_name + e_value) at offset in data and resolves its full name.
    /// <para>在 data 的 offset 处读取一个 erofs_xattr_entry（4 字节头 + e_name + e_value），并解析出完整名称。</para>
    /// </summary>
    /// <param name="data">Buffer holding the record, an ibody or the shared xattr area.
    /// <para>容纳该记录的缓冲区，即 ibody 或共享 xattr 区。</para></param>
    /// <param name="offset">Byte offset of the 4-byte entry header inside <paramref name="data"/>; a negative offset, or one leaving fewer than 4 header bytes or fewer than name plus value bytes, fails the read.
    /// <para><paramref name="data"/> 中 4 字节条目头的偏移；偏移为负、不足 4 字节头或名称加值字节越界时读取失败。</para></param>
    /// <param name="prefixTable">Long-prefix table forwarded to name resolution, or <see langword="null"/> when the image defines none.
    /// <para>传给名称解析的长前缀表；镜像未定义时为 <see langword="null"/>。</para></param>
    /// <param name="entry">Decoded attribute whose name is fully resolved, or <see langword="null"/> when the read failed.
    /// <para>名称已完整解析的属性；读取失败时为 <see langword="null"/>。</para></param>
    /// <param name="entrySize">Bytes the record occupies after 4-byte alignment, or 0 when the read failed.
    /// <para>4 字节对齐后该记录占用的字节数；读取失败时为 0。</para></param>
    /// <returns><see langword="true"/> when the header, the <c>e_name_inline</c> bytes and the <c>e_value_size</c> value bytes all fit in <paramref name="data"/>.
    /// <para>头部、<c>e_name_inline</c> 字节与 <c>e_value_size</c> 长度的值字节都落在 <paramref name="data"/> 内时返回 <see langword="true"/>。</para></returns>
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
    /// <param name="inodeData">Inode bytes whose inline xattr ibody is read from the start of the span.
    /// <para>inode 字节，其开头的 inline xattr ibody 即解析来源。</para></param>
    /// <param name="icount">Inline xattr entry count read from the inode; zero or a negative value means
    /// there is no ibody and the result is empty.
    /// <para>取自 inode 的 inline xattr 条目数；为 0 或负数表示没有 ibody，结果为空。</para></param>
    /// <returns>The inline entries in on-disk order, resolved without a long-prefix table.
    /// <para>按磁盘顺序得到的内联条目；不带长前缀表解析。</para></returns>
    public static List<ErofsXattrEntry> ParseInline(ReadOnlySpan<byte> inodeData, int icount)
        => ParseInline(inodeData, icount, null);

    /// <summary>
    /// Parses the inline xattr list (with a long-prefix table). prefixTable comes from ParsePrefixTable; when null, long-prefix entries fall back to their raw names.
    /// <para>解析 inline xattr 列表（带长前缀表）。</para>
    /// </summary>
    /// <param name="inodeData">Inode bytes whose inline xattr ibody is read from the start of the span;
    /// a span shorter than the ibody derived from <paramref name="icount"/> is reported as malformed.
    /// <para>inode 字节，其开头的 inline xattr ibody 即解析来源；跨度短于由 <paramref name="icount"/> 推导的
    /// ibody 时按格式错误上报。</para></param>
    /// <param name="icount">Inline xattr entry count read from the inode; zero or a negative value means
    /// there is no ibody and the result is empty.
    /// <para>取自 inode 的 inline xattr 条目数；为 0 或负数表示没有 ibody，结果为空。</para></param>
    /// <param name="prefixTable">Table from <see cref="ParsePrefixTable"/>, or <see langword="null"/> when
    /// the image defines none.
    /// <para>由 <see cref="ParsePrefixTable"/> 得到的表；镜像未定义时为 <see langword="null"/>。</para></param>
    /// <returns>The inline entries in on-disk order, empty when <paramref name="icount"/> is not positive.
    /// <para>按磁盘顺序得到的内联条目；<paramref name="icount"/> 非正数时为空。</para></returns>
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
            if (!TryReadEntry(ibody, position, prefixTable, out var entry, out var entrySize)
                || entrySize > ibodySize - position)
            {
                throw new FileSystemCorruptedException("Truncated EROFS inline extended attribute.");
            }
            result.Add(entry);
            position += entrySize;
        }

        return result;
    }

    /// <summary>
    /// Reads and resolves the shared xattr entry identified by sharedId from the shared xattr area (bytes starting at the superblock.xattr_blkaddr block).
    /// <para>在共享 xattr 区（自 superblock.xattr_blkaddr 块起始的字节区）中按 sharedId 读取并解析 一个共享条目。</para>
    /// </summary>
    /// <param name="sharedArea">Shared xattr area bytes, read from the block addressed by the superblock's <c>xattr_blkaddr</c>.
    /// <para>共享 xattr 区字节，取自超级块 <c>xattr_blkaddr</c> 所指的数据块。</para></param>
    /// <param name="sharedId">Id slot from the ibody's shared id array; the record starts at <paramref name="sharedId"/> multiplied by 4 bytes.
    /// <para>来自 ibody 共享 id 数组的槽值；记录起始于 <paramref name="sharedId"/> 乘以 4 的字节处。</para></param>
    /// <param name="prefixTable">Long-prefix table forwarded to name resolution, or <see langword="null"/> when the image defines none.
    /// <para>传给名称解析的长前缀表；镜像未定义时为 <see langword="null"/>。</para></param>
    /// <param name="entry">Decoded shared attribute, or <see langword="null"/> when the id addresses a record that does not fit in <paramref name="sharedArea"/>.
    /// <para>已解析的共享属性；id 指向的记录超出 <paramref name="sharedArea"/> 范围时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when a complete record was read at the id's offset.
    /// <para>在 id 对应偏移处读到了完整记录时返回 <see langword="true"/>。</para></returns>
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
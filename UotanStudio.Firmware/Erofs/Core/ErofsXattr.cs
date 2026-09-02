using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// 单个已解析的 erofs xattr 条目：完整 xattr 名称（前缀已解析）、原始值字节与磁盘上的
/// e_name_index（含 EROFS_XATTR_LONG_PREFIX 位）。不持有文件流，仅承载解析结果。
/// <para>A single resolved EROFS xattr entry: the full xattr name (prefix already resolved),
/// the raw value bytes, and the on-disk e_name_index (including the EROFS_XATTR_LONG_PREFIX
/// bit). Holds no file stream; it only carries the parsing result.</para>
/// </summary>
public sealed class ErofsXattrEntry
{
    /// <summary>
    /// 完整的 xattr 名称，前缀已解析（例如 "user.foo"、"security.selinux" 或
    /// "system.posix_acl_access"）。前缀解析规则与内核 fs/erofs/xattr.c 的
    /// <c>erofs_listxattr_foreach</c> 一致。
    /// <para>The full xattr name with the prefix already resolved (e.g. "user.foo",
    /// "security.selinux" or "system.posix_acl_access"). The prefix resolution matches the
    /// kernel's <c>erofs_listxattr_foreach</c> in fs/erofs/xattr.c.</para>
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>原始值字节（e_value_size 字节，未解码）。Raw value bytes (e_value_size bytes, undecoded).</summary>
    public byte[] Value { get; init; } = [];

    /// <summary>
    /// 磁盘上的 e_name_index。bit7（0x80，EROFS_XATTR_LONG_PREFIX）置位表示长前缀，
    /// 低 7 位为长前缀表索引。
    /// <para>The on-disk e_name_index. Bit 7 (0x80, EROFS_XATTR_LONG_PREFIX) set means a long
    /// xattr name prefix; the low 7 bits are the long-prefix table index.</para>
    /// </summary>
    public byte NameIndex { get; init; }
}

/// <summary>
/// erofs xattr 磁盘结构解析器，严格对齐 Linux 内核主线 <c>fs/erofs/erofs_fs.h</c> 的
/// <c>struct erofs_xattr_ibody_header</c>、<c>struct erofs_xattr_entry</c> 与
/// <c>struct erofs_xattr_long_prefix</c>。仅做纯结构解析，不持有文件流——上层 Parser
/// 按需读入字节区域后调用本类型。
/// <para>Parser for EROFS on-disk xattr structures, strictly aligned with the mainline Linux
/// kernel <c>fs/erofs/erofs_fs.h</c>: <c>struct erofs_xattr_ibody_header</c>,
/// <c>struct erofs_xattr_entry</c> and <c>struct erofs_xattr_long_prefix</c>. Pure structural
/// parsing only; holds no file stream — the upper Parser reads byte regions on demand and calls
/// into this type.</para>
/// </summary>
internal static class ErofsXattr
{
    /// <summary>
    /// <c>struct erofs_xattr_ibody_header</c> 的固定大小（12 字节：h_name_filter +
    /// h_shared_count + h_reserved2[7]）。
    /// <para>Fixed size of <c>struct erofs_xattr_ibody_header</c> (12 bytes: h_name_filter +
    /// h_shared_count + h_reserved2[7]).</para>
    /// </summary>
    public const int HeaderSize = 12;

    /// <summary>
    /// <c>struct erofs_xattr_entry</c> 的头大小（4 字节：e_name_len + e_name_index +
    /// e_value_size）。条目头之后依次为 e_name 与 e_value。
    /// <para>Header size of <c>struct erofs_xattr_entry</c> (4 bytes: e_name_len + e_name_index +
    /// e_value_size). e_name and e_value follow the header.</para>
    /// </summary>
    public const int EntryHeaderSize = 4;

    /// <summary>
    /// EROFS_XATTR_ALIGN 对齐字节数（<c>sizeof(struct erofs_xattr_entry)</c> = 4）。
    /// 条目总大小 = round_up(EntryHeaderSize + e_name_len + e_value_size, EntryAlignment)。
    /// <para>Alignment bytes of EROFS_XATTR_ALIGN (<c>sizeof(struct erofs_xattr_entry)</c> = 4).
    /// The total entry size is round_up(EntryHeaderSize + e_name_len + e_value_size,
    /// EntryAlignment).</para>
    /// </summary>
    public const int EntryAlignment = 4;

    /// <summary>
    /// 长前缀标记位（EROFS_XATTR_LONG_PREFIX = 0x80）：e_name_index 的 bit7 置位表示该条目
    /// 使用长 xattr 名称前缀，低 7 位为前缀表索引。
    /// <para>The long-prefix marker bit (EROFS_XATTR_LONG_PREFIX = 0x80): when bit 7 of
    /// e_name_index is set the entry uses a long xattr name prefix and the low 7 bits are the
    /// prefix table index.</para>
    /// </summary>
    public const byte LongPrefixFlag = 0x80;

    /// <summary>
    /// 长前缀表索引掩码（EROFS_XATTR_LONG_PREFIX_MASK = 0x7f）。
    /// <para>The long-prefix table index mask (EROFS_XATTR_LONG_PREFIX_MASK = 0x7f).</para>
    /// </summary>
    public const byte LongPrefixMask = 0x7f;

    /// <summary>共享 xattr id 的磁盘单位大小（__le32 = 4 字节）。On-disk unit size of a shared xattr id (__le32 = 4 bytes).</summary>
    private const int SharedIdUnitSize = sizeof(uint);

    // ---- 名称索引（与内核 erofs_fs.h 的 EROFS_XATTR_INDEX_* 一致）。Name indexes. ----
    /// <summary>EROFS_XATTR_INDEX_USER = 1。</summary>
    public const byte NameIndexUser = 1;
    /// <summary>EROFS_XATTR_INDEX_POSIX_ACL_ACCESS = 2。</summary>
    public const byte NameIndexPosixAclAccess = 2;
    /// <summary>EROFS_XATTR_INDEX_POSIX_ACL_DEFAULT = 3。</summary>
    public const byte NameIndexPosixAclDefault = 3;
    /// <summary>EROFS_XATTR_INDEX_TRUSTED = 4。</summary>
    public const byte NameIndexTrusted = 4;
    /// <summary>EROFS_XATTR_INDEX_LUSTRE = 5。</summary>
    public const byte NameIndexLustre = 5;
    /// <summary>EROFS_XATTR_INDEX_SECURITY = 6。</summary>
    public const byte NameIndexSecurity = 6;
    /// <summary>system 命名空间索引（系统 xattr，前缀 "system."，EROFS_XATTR_INDEX_SYSTEM = 7）。System namespace index.</summary>
    public const byte NameIndexSystem = 7;

    /// <summary>
    /// 默认短前缀名表，下标与名称索引一一对应（0 为保留占位）。与内核/erofs-utils 的
    /// <c>erofs_xattr_prefixes[]</c> 一致：user. / system.posix_acl_access /
    /// system.posix_acl_default / trusted. / lustre. / security. / system.。
    /// <para>Default short prefix table, indexed by the name index (index 0 is a reserved
    /// placeholder). Matches the kernel/erofs-utils <c>erofs_xattr_prefixes[]</c>: user. /
    /// system.posix_acl_access / system.posix_acl_default / trusted. / lustre. / security. /
    /// system.</para>
    /// </summary>
    private static readonly string[] DefaultPrefixes =
    [
        string.Empty,                // 0 — 保留 / reserved
        "user.",                     // 1 — EROFS_XATTR_INDEX_USER
        "system.posix_acl_access",   // 2 — EROFS_XATTR_INDEX_POSIX_ACL_ACCESS
        "system.posix_acl_default",  // 3 — EROFS_XATTR_INDEX_POSIX_ACL_DEFAULT
        "trusted.",                  // 4 — EROFS_XATTR_INDEX_TRUSTED
        "lustre.",                   // 5 — EROFS_XATTR_INDEX_LUSTRE
        "security.",                 // 6 — EROFS_XATTR_INDEX_SECURITY
        "system.",                   // 7 — EROFS_XATTR_INDEX_SYSTEM
    ];

    /// <summary>
    /// inline xattr ibody 总大小（字节），与内核 <c>erofs_xattr_ibody_size</c> 一致：
    /// <c>12 + 4 * (icount - 1)</c>；icount 为 0 时返回 0（无 inline xattr）。
    /// <para>Total inline-xattr ibody size in bytes, matching the kernel's
    /// <c>erofs_xattr_ibody_size</c>: <c>12 + 4 * (icount - 1)</c>; 0 when icount is 0 (no inline
    /// xattr).</para>
    /// </summary>
    public static int GetIbodySize(int icount)
    {
        if (icount <= 0)
            return 0;
        return HeaderSize + SharedIdUnitSize * (icount - 1);
    }

    /// <summary>
    /// 默认短前缀查找：将名称索引映射到默认前缀字符串（如 1 → "user."）。
    /// 索引 0、越界或保留值返回 false。
    /// <para>Default short-prefix lookup: maps a name index to its default prefix string
    /// (e.g. 1 → "user."). Returns false for index 0, out-of-range or reserved values.</para>
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
    /// 反查默认前缀表：把完整 xattr 名拆为（短前缀名称索引 + 剩余短名）。
    /// 仅匹配内置短前缀（user. / trusted. / security. / system. 等）；不匹配返回 false。
    /// <para>Reverse lookup of the default prefix table: splits a full xattr name into a
    /// (short-prefix name index + remaining short name). Only built-in short prefixes match
    /// (user. / trusted. / security. / system. etc.); returns false when nothing matches.</para>
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
    /// 将条目列表编码为 inline xattr ibody（含 12 字节头；无共享条目时 h_shared_count = 0）。
    /// 返回的字节区直接写在 inode 记录之后；调用方须把长度计入 inode 记录大小并设置
    /// <see cref="ErofsInode.XattrICount"/>（= <c>(ibody 总字节 / 4) - 2</c>，满足
    /// <c>12 + 4 * (icount - 1) == ibody 长度</c>）。
    /// 名称须能拆为内置短前缀（见 <see cref="TrySplitShortName"/>），否则抛
    /// <see cref="InvalidDataException"/>。
    /// <para>Encodes a list of entries into an inline xattr ibody (including the 12-byte header;
    /// h_shared_count = 0 when no shared entries are present). The returned bytes are written right
    /// after the inode record; callers must count the length into the inode record size and set
    /// <see cref="ErofsInode.XattrICount"/> (= <c>ibody length / 4 - 2</c> so that
    /// <c>12 + 4 * (icount - 1) == ibody length</c>). Names must split into a built-in short
    /// prefix (see <see cref="TrySplitShortName"/>), otherwise an
    /// <see cref="InvalidDataException"/> is thrown.</para>
    /// </summary>
    public static byte[] EncodeInline(IReadOnlyList<ErofsXattrEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var body = new List<byte>(HeaderSize + entries.Count * 8);
        body.AddRange(new byte[HeaderSize]); // 头：h_name_filter + h_shared_count(0) + 保留。
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
            body.Add((byte)nameBytes.Length); // e_name_len
            body.Add(nameIndex);              // e_name_index
            body.Add((byte)value.Length);     // e_value_size (low)
            body.Add((byte)(value.Length >> 8));
            body.AddRange(nameBytes);
            body.AddRange(value);
            // 对齐到 4 字节（EROFS_XATTR_ALIGN）。Pad to 4 bytes (EROFS_XATTR_ALIGN).
            while ((body.Count - start) % EntryAlignment != 0)
            {
                body.Add(0);
            }
        }

        var ibody = body.ToArray();
        // icount = ibody 总字节 / 4 - 2 ⇒ 12 + 4*(icount-1) = ibody 长度。
        if (ibody.Length % 4 != 0)
        {
            throw new InvalidDataException("EROFS xattr ibody must be 4-byte aligned.");
        }

        return ibody;
    }

    /// <summary>
    /// 由 ibody 总字节推导 <see cref="ErofsInode.XattrICount"/> 值（无 xattr 时返回 0）。
    /// <para>Derives the <see cref="ErofsInode.XattrICount"/> value from the ibody byte length
    /// (0 when there is no xattr).</para>
    /// </summary>
    public static ushort GetICountForIbodyLength(int ibodyLength)
        => ibodyLength <= 0 ? (ushort)0 : checked((ushort)(ibodyLength / SharedIdUnitSize - 2));

    /// <summary>
    /// 解析长 xattr 名称前缀表。表位于 superblock.xattr_prefix_start 指向的元数据区，共
    /// XattrPrefixCount 个条目，每个条目为 <c>{__u8 base_index; char infix[]}</c>（infix 以 NUL
    /// 结尾），即内核的 <c>struct erofs_xattr_long_prefix</c>。base_index 为短前缀索引，
    /// infix 为前缀中位于短前缀与名称之间的部分。
    /// <para>Parses the long xattr name prefix table located in the metadata area pointed to by
    /// superblock.xattr_prefix_start, holding XattrPrefixCount entries of
    /// <c>{__u8 base_index; char infix[]}</c> (infix NUL-terminated), i.e. the kernel's
    /// <c>struct erofs_xattr_long_prefix</c>. base_index is the short-prefix index and infix is
    /// the part of the prefix between the short prefix and the name.</para>
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
    /// 长前缀表在镜像内的字节偏移。内核 fs/erofs/xattr.c 的 <c>erofs_xattr_prefixes_init</c>
    /// 将 <c>s_xattr_prefix_start</c> 视作 u32 单位偏移：<c>pos = xattr_prefix_start &lt;&lt; 2</c>，
    /// 因此磁盘上的字段值需要左移 2 位才是字节偏移（erofs-utils mkfs 写入时亦为 offset &gt;&gt; 2）。
    /// <para>Byte offset of the long-prefix table within the image. The kernel's
    /// <c>erofs_xattr_prefixes_init</c> in fs/erofs/xattr.c treats <c>s_xattr_prefix_start</c> as a
    /// u32-unit offset: <c>pos = xattr_prefix_start &lt;&lt; 2</c>; the on-disk field is therefore
    /// shifted left by 2 to yield the byte offset (erofs-utils mkfs likewise writes offset &gt;&gt; 2).</para>
    /// </summary>
    public static long GetPrefixTableByteOffset(uint xattrPrefixStart)
        => (long)xattrPrefixStart << 2;

    /// <summary>
    /// 解析 inline xattr ibody 的共享 xattr id 数组：<c>h_shared_count</c> 个 u32，紧随 12 字节
    /// 头之后。每个 id 是共享 xattr 区内以 4 字节为单位的偏移，指向一个 erofs_xattr_entry
    /// （内核 <c>erofs_xattr_iter_shared</c> / erofs-utils <c>xattrblock_addr</c>）。
    /// <para>Parses the shared xattr id array of the inline xattr ibody: <c>h_shared_count</c> u32s
    /// following the 12-byte header. Each id is an offset in 4-byte units within the shared xattr
    /// area, pointing at an erofs_xattr_entry (kernel <c>erofs_xattr_iter_shared</c> / erofs-utils
    /// <c>xattrblock_addr</c>).</para>
    /// </summary>
    public static uint[] ParseSharedXattrIds(ReadOnlySpan<byte> inodeData, int icount)
    {
        var ibodySize = GetIbodySize(icount);
        if (ibodySize <= 0)
            return [];

        if (inodeData.Length < ibodySize)
            throw new FormatException($"xattr ibody too short: need {ibodySize} bytes, got {inodeData.Length}.");

        var sharedCount = inodeData[4]; // h_shared_count
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
    /// 共享 xattr 条目在共享 xattr 区内的字节偏移 = sharedId * 4（内核
    /// <c>erofs_xattr_iter_shared</c>：<c>pos = xattr_blkaddr * block_size +
    /// xattr_shared_xattrs[i] * sizeof(__le32)</c>）。上层将本偏移叠加到
    /// <c>xattr_blkaddr * block_size</c> 上得到镜像字节位置。
    /// <para>Byte offset of a shared xattr entry within the shared xattr area = sharedId * 4
    /// (kernel <c>erofs_xattr_iter_shared</c>: <c>pos = xattr_blkaddr * block_size +
    /// xattr_shared_xattrs[i] * sizeof(__le32)</c>). The upper layer adds this offset to
    /// <c>xattr_blkaddr * block_size</c> to get the image byte position.</para>
    /// </summary>
    public static long GetSharedXattrByteOffset(uint sharedId)
        => (long)sharedId * SharedIdUnitSize;

    /// <summary>
    /// 将条目名称解析为完整的 xattr 名称，与内核 fs/erofs/xattr.c 的
    /// <c>erofs_listxattr_foreach</c> 一致：
    /// <list type="bullet">
    /// <item>短前缀：完整名 = 默认前缀[name_index] + e_name；</item>
    /// <item>长前缀：按 name_index 低 7 位查前缀表，完整名 = 默认前缀[base_index] + infix + e_name。</item>
    /// </list>
    /// 前缀无法解析（未知索引 / 表越界 / 无表）时退化为原始 e_name。
    /// <para>Resolves an entry name to the full xattr name, matching the kernel's
    /// <c>erofs_listxattr_foreach</c> in fs/erofs/xattr.c:</para>
    /// <list type="bullet">
    /// <item>Short prefix: full name = DefaultPrefixes[name_index] + e_name;</item>
    /// <item>Long prefix: look up the table by the low 7 bits of name_index, full name =
    /// DefaultPrefixes[base_index] + infix + e_name.</item>
    /// </list>
    /// Falls back to the raw e_name when the prefix cannot be resolved (unknown index, table
    /// out of range, or no table provided).</para>
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
    /// 在 <paramref name="data"/> 的 <paramref name="offset"/> 处读取一个
    /// erofs_xattr_entry（4 字节头 + e_name + e_value），并解析出完整名称。条目总大小 =
    /// round_up(4 + e_name_len + e_value_size, 4)（EROFS_XATTR_ALIGN）。数据不足或布局非法时
    /// 返回 false（不抛出，供上层做尽力而为的遍历）。
    /// <para>Reads one erofs_xattr_entry (4-byte header + e_name + e_value) at
    /// <paramref name="offset"/> in <paramref name="data"/> and resolves its full name. The total
    /// entry size is round_up(4 + e_name_len + e_value_size, 4) (EROFS_XATTR_ALIGN). Returns false
    /// (without throwing) when the data is too short or the layout is invalid, enabling the upper
    /// layer to do best-effort traversal.</para>
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

        var nameLen = data[offset];            // e_name_len
        var nameIndex = data[offset + 1];      // e_name_index
        var valueSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2)); // e_value_size

        var payloadSize = (int)nameLen + valueSize;
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
    /// 解析 inline xattr 列表。inodeData 必须从 xattr ibody 起始处传入，即 inode 起始 +
    /// inode 磁盘尺寸（compact 32 / extended 64），也就是 <see cref="ErofsInode.InlineDataOffset"/>
    /// 减去 <c>XattrTotalSize</c> 的位置；长度至少为 <see cref="ErofsInode.XattrTotalSize"/>。
    /// icount 为 inode 的 i_xattr_icount。ibody 布局：
    /// 12 字节头 + h_shared_count * 4 字节共享 id 数组 + 依次排列的 inline 条目。
    /// <para>Parses the inline xattr list. inodeData must start at the xattr ibody, i.e. inode start
    /// + on-disk inode size (compact 32 / extended 64), equivalently
    /// <see cref="ErofsInode.InlineDataOffset"/> minus <c>XattrTotalSize</c>, and must be at least
    /// <see cref="ErofsInode.XattrTotalSize"/> bytes long. icount is the inode's i_xattr_icount.
    /// ibody layout: 12-byte header + h_shared_count * 4-byte shared id array + inline entries.</para>
    /// </summary>
    public static List<ErofsXattrEntry> ParseInline(ReadOnlySpan<byte> inodeData, int icount)
        => ParseInline(inodeData, icount, null);

    /// <summary>
    /// 解析 inline xattr 列表（带长前缀表）。<paramref name="prefixTable"/> 由
    /// <see cref="ParsePrefixTable"/> 解析得到；为 null 时长前缀条目退化为原始名称。
    /// <para>Parses the inline xattr list (with a long-prefix table).
    /// <paramref name="prefixTable"/> comes from <see cref="ParsePrefixTable"/>; when null,
    /// long-prefix entries fall back to their raw names.</para>
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

        // 只允许在 ibody 范围内遍历，防止越入随后的 inline 数据区。
        // Bound the traversal to the ibody so we never walk into the inline data that follows.
        var ibody = inodeData.Slice(0, ibodySize);

        var sharedCount = ibody[4]; // h_shared_count
        var entriesStart = HeaderSize + SharedIdUnitSize * sharedCount;
        if (entriesStart > ibodySize)
            throw new FormatException($"Invalid h_shared_count {sharedCount} for ibody size {ibodySize}.");

        var position = entriesStart;
        while (position + EntryHeaderSize <= ibodySize)
        {
            if (!TryReadEntry(ibody, position, prefixTable, out var entry, out var entrySize))
                break; // 越界或损坏：尽力而为，跳过剩余部分。Out of range / corrupt: stop best-effort.
            result.Add(entry);
            position += entrySize;
        }

        return result;
    }

    /// <summary>
    /// 在共享 xattr 区（自 superblock.xattr_blkaddr 块起始的字节区）中按 sharedId 读取并解析
    /// 一个共享条目。条目可能跨块，sharedArea 需覆盖整条（上层负责拼接足够的字节）。
    /// <para>Reads and resolves the shared xattr entry identified by sharedId from the shared
    /// xattr area (bytes starting at the superblock.xattr_blkaddr block). The entry may span
    /// blocks, so sharedArea must cover it entirely (the upper layer concatenates enough bytes).</para>
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

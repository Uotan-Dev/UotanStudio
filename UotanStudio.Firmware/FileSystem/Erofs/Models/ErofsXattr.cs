using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.Xxh32;

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
    /// <value>Default prefix index (1..6), or a long-prefix index with the <c>0x80</c> flag set.
    /// <para>默认前缀索引（1..6），或置有 <c>0x80</c> 标志位的长前缀索引。</para></value>
    public byte NameIndex { get; init; }
}

/// <summary>
/// Result of encoding one inode's extended attributes: the inline ibody that follows the inode record and
/// the long-prefix table records the image must carry when any name needed a long prefix. The table is
/// shared per image, so the writer flushes the union of every inode's records once.
/// <para>编码单个 inode 扩展属性的结果：紧随 inode 记录的 inline ibody，以及任一名称需要长前缀时镜像
/// 必须携带的长前缀表记录。该表为镜像级共享，写出方把每个 inode 的记录取并集后一次写出。</para>
/// </summary>
public sealed class ErofsXattrEncoding
{
    /// <summary>Gets the 4-byte aligned inline ibody, including its 12-byte header.
    /// <para>获取 4 字节对齐的 inline ibody，含 12 字节头部。</para></summary>
    public required byte[] Ibody { get; init; }

    /// <summary>Gets the long-prefix records the encoded names registered, in table order; empty when
    /// every name matched a built-in short prefix.
    /// <para>获取编码名称登记的长前缀记录，按表序；全部名称命中内置短前缀时为空。</para></summary>
    public required IReadOnlyList<(byte BaseIndex, string Infix)> PrefixTable { get; init; }

    /// <summary>Gets the name-filter word written into the ibody header, or zero when filtering was not
    /// requested or no name contributed a bit.
    /// <para>获取写入 ibody 头部的名称过滤字；未请求过滤或无名称贡献位时为零。</para></summary>
    public uint NameFilter { get; init; }
}

internal static class ErofsXattr
{
    public const int HeaderSize = 12;

    public const int EntryHeaderSize = 4;

    public const int EntryAlignment = 4;

    public const byte LongPrefixFlag = 0x80;

    public const byte LongPrefixMask = 0x7f;

    /// <summary>Unit the shared-xattr id counts in: one id is a slot of four bytes inside the shared area.
    /// <para>共享 xattr id 的计数单位：一个 id 即共享区内一个四字节槽位。</para></summary>
    internal const int SharedIdUnitSize = sizeof(uint);


    public const byte NameIndexUser = 1;
    public const byte NameIndexPosixAclAccess = 2;
    public const byte NameIndexPosixAclDefault = 3;
    public const byte NameIndexTrusted = 4;

    /// <summary>Name index 5 is declared by <c>EROFS_XATTR_INDEX_LUSTRE</c> but upstream
    /// <c>xattr_types[]</c> leaves its slot empty, so it names no namespace and must neither be written
    /// nor resolved. <para>名称索引 5 虽由 <c>EROFS_XATTR_INDEX_LUSTRE</c> 声明，但上游
    /// <c>xattr_types[]</c> 让其槽位为空，因此它不指名任何命名空间，既不可写出也不可解析。</para></summary>
    public const byte ReservedNameIndex = 5;

    public const byte NameIndexSecurity = 6;

    /// <summary>Highest name index the default prefix table defines, matching
    /// <c>EROFS_XATTR_INDEX_SECURITY</c> (<c>include/erofs_fs.h</c>).
    /// <para>默认前缀表定义的名称索引上限，与 <c>EROFS_XATTR_INDEX_SECURITY</c>
    /// （<c>include/erofs_fs.h</c>）一致。</para></summary>
    public const byte MaxNameIndex = NameIndexSecurity;

    private static readonly string[] DefaultPrefixes =
    [
        string.Empty,
        "user.",
        "system.posix_acl_access",
        "system.posix_acl_default",
        "trusted.",
        string.Empty,
        "security.",
    ];

    /// <summary>
    /// Reconstructs the full prefix text a table record stands for: the built-in prefix its base index
    /// names, followed by its infix. This is the text an attribute name must start with to be encoded
    /// through that record.
    /// <para>重建表记录所代表的完整前缀文本：其 base index 所指名的内置前缀后接其 infix。属性名必须以该
    /// 文本开头才会经该记录编码。</para>
    /// </summary>
    /// <param name="record">Table record as stored on disk.<para>磁盘上存储的表记录。</para></param>
    /// <returns>The full prefix text.<para>完整前缀文本。</para></returns>
    internal static string PrefixText((byte BaseIndex, string Infix) record)
        => (TryGetDefaultPrefix(record.BaseIndex, out var basePrefix) ? basePrefix : string.Empty) + record.Infix;

    /// <summary>
    /// Splits a whole prefix text into the table record that stores it: the built-in prefix the text
    /// starts with becomes the base index and everything after it the infix, or base index zero for the
    /// empty namespace when no built-in prefix matches. A caller that already knows the exact
    /// prefix uses this to register it verbatim, because the automatic splitter only registers prefixes
    /// that leave a name behind and would otherwise shorten a configured prefix.
    /// <para>把完整前缀文本拆成存储它的表记录：文本开头命中的内置前缀成为 base index，其后全部成为
    /// infix；未命中任何内置前缀时 base index 为零，即内部隐藏命名空间。已确切知道前缀的调用方借此逐字
    /// 注册它，因为自动拆分只登记会留下名称的前缀，否则会缩短配置的前缀。</para>
    /// </summary>
    /// <param name="prefix">Whole prefix text to store.<para>待存储的完整前缀文本。</para></param>
    /// <returns>The record that reconstructs <paramref name="prefix"/> through <see cref="PrefixText"/>.
    /// <para>经 <see cref="PrefixText"/> 可还原 <paramref name="prefix"/> 的记录。</para></returns>
    internal static (byte BaseIndex, string Infix) SplitPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        for (byte i = 1; i < DefaultPrefixes.Length; i++)
        {
            var candidate = DefaultPrefixes[i];
            if (candidate.Length > 0 && prefix.StartsWith(candidate, StringComparison.Ordinal))
            {
                return (i, prefix[candidate.Length..]);
            }
        }

        return (0, prefix);
    }

    /// <summary>Seed mixed with an entry's base prefix index before hashing its name into
    /// <c>h_name_filter</c> (<c>EROFS_XATTR_FILTER_SEED</c>).
    /// <para>把条目的基准前缀索引混入 <c>h_name_filter</c> 之前用于哈希其名称的种子
    /// （<c>EROFS_XATTR_FILTER_SEED</c>）。</para></summary>
    internal const uint NameFilterSeed = 0x25BBE08F;

    /// <summary>Bit count of the name filter, so the hash is taken modulo this many bits
    /// (<c>EROFS_XATTR_FILTER_BITS</c>).
    /// <para>名称过滤器的位数，哈希即依此取模（<c>EROFS_XATTR_FILTER_BITS</c>）。</para></summary>
    internal const int NameFilterBits = 32;

    /// <summary>
    /// Builds the <c>h_name_filter</c> word for one inode's xattr names. A bit is raised for every name
    /// the inode can answer, then complemented, because the kernel reads a raised bit as "definitely
    /// absent" and uses that to skip lookups (<c>lib/xattr.c</c>).
    /// <para>为单个 inode 的 xattr 名称构造 <c>h_name_filter</c> 字。可应答的每个名称置一位，随后取反，
    /// 因为内核把置起的位读作"一定不存在"并据此跳过查找（<c>lib/xattr.c</c>）。</para>
    /// </summary>
    /// <param name="namesAndBaseIndexes">Each xattr's full name paired with the built-in prefix index whose
    /// text its hash skips. <para>每个 xattr 的完整名称，以及其哈希所跳过的内置前缀文本的索引。</para></param>
    /// <returns>The filter word, or zero when no name contributed a bit.
    /// <para>过滤字；无名称贡献位时为零。</para></returns>
    internal static uint ComputeNameFilter(IReadOnlyList<(string Name, byte BaseIndex)> namesAndBaseIndexes)
    {
        ArgumentNullException.ThrowIfNull(namesAndBaseIndexes);
        var bits = 0u;
        foreach (var (name, baseIndex) in namesAndBaseIndexes)
        {
            var basePrefix = TryGetDefaultPrefix(baseIndex, out var prefix) ? prefix : string.Empty;
            var tail = name.Length >= basePrefix.Length ? name[basePrefix.Length..] : name;
            var hash = Xxh32.Compute(Encoding.UTF8.GetBytes(tail), NameFilterSeed + baseIndex);
            bits |= 1u << (int)(hash & (NameFilterBits - 1));
        }

        return ~bits;
    }

    /// <summary>
    /// Returns the total inline-xattr ibody size in bytes for the given entry count.
    /// <para>返回给定条目数下 inline xattr ibody 的总字节大小。</para>
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
    /// Index 0 maps to the empty prefix, which upstream <c>xattr_types[0] = {""}</c> defines as a namespace
    /// of its own, so a name carrying it is stored and resolved verbatim.
    /// <para>默认短前缀查找：将名称索引映射到默认前缀字符串（如 1 → "user."）。索引 0 映射到空前缀，
    /// 上游 <c>xattr_types[0] = {""}</c> 把它定义为一个自有命名空间，因此携带它的名称按原样存储与解析。</para>
    /// </summary>
    /// <param name="nameIndex">On-disk name index with no long-prefix flag applied; values below seven address the built-in table.
    /// <para>未置长前缀标志的磁盘名称索引；小于 7 时指向内置表。</para></param>
    /// <param name="prefix">Matched prefix text, which is <c>string.Empty</c> for index 0, or empty when the index names no table slot.
    /// <para>匹配到的前缀文本；索引 0 为 <c>string.Empty</c>，索引不指名任何表槽时也为空。</para></param>
    /// <returns><see langword="true"/> when the index addresses a defined table slot, including the empty namespace 0 and the non-empty prefixes 1 through 6.
    /// <para>索引指向已定义表槽时返回 <see langword="true"/>，包含空命名空间 0 与非空的 1 到 6。</para></returns>
    public static bool TryGetDefaultPrefix(byte nameIndex, out string prefix)
    {
        if (nameIndex < DefaultPrefixes.Length)
        {
            prefix = DefaultPrefixes[nameIndex];
            return nameIndex == 0 || prefix.Length > 0;
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
        var encoding = Encode(entries, null);
        if (encoding.PrefixTable.Count > 0)
        {
            throw new FileSystemUnsupportedFeatureException(
                $"EROFS xattr name requires a long-prefix table, which this build does not carry; " +
                $"first offending name: '{encoding.PrefixTable[0].Infix}'.");
        }
        return encoding.Ibody;
    }

    /// <summary>
    /// Encodes entries into an inline xattr ibody and the long-prefix table the image must carry.
    /// <para>把条目列表编码为 inline xattr ibody，并给出镜像必须携带的长前缀表。</para>
    /// </summary>
    public static ErofsXattrEncoding Encode(IReadOnlyList<ErofsXattrEntry> entries,
        IReadOnlyList<(byte BaseIndex, string Infix)>? inheritedPrefixes, bool nameFilter = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var prefixes = new List<(byte BaseIndex, string Infix)>(inheritedPrefixes ?? []);
        var registered = new List<string>(prefixes.Select(PrefixText));
        var filtered = new List<(string Name, byte BaseIndex)>(nameFilter ? entries.Count : 0);
        var body = new List<byte>(HeaderSize + entries.Count * 8);
        body.AddRange(new byte[HeaderSize]);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ValidateEntryNameIndex(entry.NameIndex);
            byte nameIndex;
            string shortName;
            byte filterBaseIndex;
            if (TrySplitShortName(entry.Name, out var defaultIndex, out var defaultShort))
            {
                nameIndex = defaultIndex;
                shortName = defaultShort;
                filterBaseIndex = defaultIndex;
            }
            else if (FindLongPrefix(entry.Name, registered, prefixes, out var longIndex, out var longShort,
                out var longBaseIndex))
            {
                nameIndex = (byte)(LongPrefixFlag | longIndex);
                shortName = longShort;
                filterBaseIndex = longBaseIndex;
            }
            else if (RegisterLongPrefix(entry.Name, prefixes, registered, out var newIndex, out var newShort,
                out var newBaseIndex))
            {
                nameIndex = (byte)(LongPrefixFlag | newIndex);
                shortName = newShort;
                filterBaseIndex = newBaseIndex;
            }
            else
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS xattr name '{entry.Name}' cannot be encoded: it matches no built-in short prefix " +
                    "and its longest registerable prefix exceeds the on-disk infix limit of 255 bytes.");
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

            if (nameFilter)
            {
                filtered.Add((entry.Name, filterBaseIndex));
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

        var nameFilterWord = nameFilter ? ComputeNameFilter(filtered) : 0u;
        var ibody = body.ToArray();
        if (nameFilterWord != 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ibody, nameFilterWord);
        }

        if (ibody.Length % 4 != 0)
        {
            throw new FileSystemCorruptedException("EROFS xattr ibody must be 4-byte aligned.");
        }

        return new ErofsXattrEncoding
        {
            Ibody = ibody,
            PrefixTable = prefixes,
            NameFilter = nameFilterWord,
        };
    }

    /// <summary>
    /// Serializes the long-prefix table: each record is an LE16 size followed by the base-index byte and the
    /// infix bytes, each padded to the next multiple of four, matching what
    /// <see cref="ParsePrefixTable"/> reads back.
    /// <para>序列化长前缀表：每条记录为 LE16 尺寸、base-index 字节与 infix 字节，补齐到 4 的倍数，与
    /// <see cref="ParsePrefixTable"/> 的读取互逆。</para>
    /// </summary>
    public static byte[] EncodePrefixTable(IReadOnlyList<(byte BaseIndex, string Infix)> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        if (prefixes.Count > 128)
        {
            throw new FileSystemCorruptedException("EROFS xattr prefix count exceeds its seven-bit index.");
        }

        var table = new List<byte>(prefixes.Count * 8);
        foreach (var (baseIndex, infix) in prefixes)
        {
            var infixBytes = Encoding.UTF8.GetBytes(infix);
            if (infixBytes.Length > byte.MaxValue)
            {
                throw new FileSystemCorruptedException($"EROFS xattr prefix '{infix}' exceeds 255 bytes.");
            }

            var size = 1 + infixBytes.Length;
            var start = table.Count;
            table.Add((byte)size);
            table.Add((byte)(size >> 8));
            table.Add(baseIndex);
            table.AddRange(infixBytes);
            while ((table.Count - start) % EntryAlignment != 0)
            {
                table.Add(0);
            }
        }

        return table.ToArray();
    }

    /// <summary>
    /// Encodes an inline ibody that names one shared xattr slot instead of carrying entries itself.
    /// <para>编码一条只指名一个共享 xattr 槽位、不自带条目的 inline ibody。</para>
    /// </summary>
    /// <param name="sharedId">Slot the inode's xattrs were packed into.
    /// <para>该 inode 的 xattr 被打包进的槽位。</para></param>
    /// <param name="nameFilter">Name-filter word for the shared entry set, or zero when the set is not
    /// filtered. <para>该共享条目集合的名称过滤字；集合未过滤时为零。</para></param>
    /// <returns>The ibody bytes for a single shared reference.
    /// <para>单条共享引用的 ibody 字节。</para></returns>
    public static byte[] EncodeSharedIbody(uint sharedId, uint nameFilter = 0)
    {
        var ibody = new byte[GetIbodySize(2)];
        BinaryPrimitives.WriteUInt32LittleEndian(ibody, nameFilter);
        ibody[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(ibody.AsSpan(HeaderSize), sharedId);
        return ibody;
    }

    /// <summary>
    /// Finds the longest registered long prefix the name starts with; a longer record wins so the stored
    /// short name stays the shortest the table allows.
    /// <para>查找名称所匹配的已注册长前缀中最长者；记录越长优先级越高，使存储的短名尽量短。</para>
    /// </summary>
    /// <param name="name">Full attribute name.<para>完整属性名。</para></param>
    /// <param name="registered">Full prefix texts the table already carries, in table order.
    /// <para>表中已携带的完整前缀文本，按表序。</para></param>
    /// <param name="prefixes">Table records the registered texts came from, so the winning record's base
    /// index can be reported for the name filter.<para>已注册文本所来源的表记录，以便为名称过滤器报告
    /// 命中记录的 base index。</para></param>
    /// <param name="index">Table index of the winning record.<para>命中记录的表索引。</para></param>
    /// <param name="shortName">Name remainder the inode stores.<para>inode 存储的名称余部。</para></param>
    /// <param name="baseIndex">Built-in prefix index the winning record extends, which is what the name
    /// filter hashes past.<para>命中记录所扩展的内置前缀索引，名称过滤器即哈希其后的部分。</para></param>
    /// <returns><see langword="true"/> when a registered prefix matched.
    /// <para>某个已注册前缀命中时为 <see langword="true"/>。</para></returns>
    private static bool FindLongPrefix(string name, List<string> registered,
        List<(byte BaseIndex, string Infix)> prefixes, out int index, out string shortName, out byte baseIndex)
    {
        index = -1;
        shortName = string.Empty;
        baseIndex = 0;
        var matched = 0;
        for (var i = 0; i < registered.Count; i++)
        {
            var prefix = registered[i];
            if (prefix.Length > matched && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                index = i;
                matched = prefix.Length;
                baseIndex = i < prefixes.Count ? prefixes[i].BaseIndex : (byte)0;
            }
        }

        if (index < 0)
        {
            return false;
        }

        shortName = name.Substring(matched);
        return true;
    }

    /// <summary>
    /// Registers the longest prefix of an unmatched name that the on-disk record can still hold: the
    /// record extends the built-in short prefix the name carries (or the hidden internal namespace when
    /// it carries none), and the infix after that base stays below the 255-byte limit. A name whose
    /// infix cannot fit registers nothing.
    /// <para>为未命中名称注册其磁盘记录仍能容纳的最长前缀：记录扩展名称所携带的内置短前缀（无则
    /// 使用内部隐藏命名空间），其后 infix 不超过 255 字节。infix 放不下的名称不做注册。</para>
    /// </summary>
    private static bool RegisterLongPrefix(string name, List<(byte BaseIndex, string Infix)> prefixes,
        List<string> registered, out int index, out string shortName, out byte baseIndex)
    {
        index = -1;
        shortName = string.Empty;
        baseIndex = 0;
        var baseLength = 0;
        for (byte i = 1; i < DefaultPrefixes.Length; i++)
        {
            var prefix = DefaultPrefixes[i];
            if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                baseIndex = i;
                baseLength = prefix.Length;
                break;
            }
        }

        var lastDot = name.LastIndexOf('.');
        var limit = Math.Min(lastDot < 0 ? name.Length : lastDot + 1, baseLength + byte.MaxValue - 1);
        for (var end = Math.Min(name.Length, limit); end > baseLength; end--)
        {
            var candidate = name.Substring(0, end);
            var infix = candidate.Substring(baseLength);
            if (Encoding.UTF8.GetByteCount(infix) > byte.MaxValue - 1)
            {
                continue;
            }

            prefixes.Add((baseIndex, infix));
            registered.Add(candidate);
            index = prefixes.Count - 1;
            shortName = name.Substring(end);
            return shortName.Length > 0;
        }

        return false;
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
    /// Returns the byte offset of a shared xattr entry within the shared xattr area.
    /// <para>返回共享 xattr 条目在共享 xattr 区内的字节偏移。</para>
    /// </summary>
    /// <param name="sharedId">One id read from the ibody's shared id array, which is itself a slot index counted in 4-byte units.
    /// <para>取自 ibody 共享 id 数组的单个 id，它本身就是以 4 字节为单位的槽号。</para></param>
    /// <returns>Offset in bytes from the start of the shared area, <paramref name="sharedId"/> multiplied by 4.
    /// <para>相对共享区起始处的字节偏移，即 <paramref name="sharedId"/> 乘以 4。</para></returns>
    public static long GetSharedXattrByteOffset(uint sharedId)
        => (long)sharedId * SharedIdUnitSize;

    /// <summary>Resolves the full xattr name from the default prefix, the long-prefix table, or both; an index the image does not define is reported as corruption.
    /// <para>由默认前缀、长前缀表或两者组合解析完整扩展属性名；镜像未定义的索引按损坏上报。</para></summary>
    /// <param name="nameIndex">On-disk <c>e_name_index</c>; the <c>0x80</c> bit selects the long-prefix table and the remaining seven bits are its index.
    /// <para>磁盘上的 <c>e_name_index</c>；<c>0x80</c> 位表示改用长前缀表，其余七位是该表的索引。</para></param>
    /// <param name="rawName">Stored short-name bytes decoded with the file-name encoding; an empty span contributes nothing to the result.
    /// <para>用文件名编码解码的磁盘短名字节；空区间不会为结果增加内容。</para></param>
    /// <param name="prefixTable">Table from <see cref="ParsePrefixTable"/>, or <see langword="null"/> when the image defines none, in which case a long-prefixed name cannot be resolved.
    /// <para>由 <see cref="ParsePrefixTable"/> 得到的表；镜像未定义时为 <see langword="null"/>，此时带长前缀的名称无法解析。</para></param>
    /// <returns>The default prefix of the resolved base index, then the long-prefix infix when present, then the decoded short name. Index 0 contributes no prefix.
    /// <para>解析出的基准索引对应的默认前缀，其次是长前缀 infix（存在时），最后是解码后的短名；索引 0 不贡献前缀。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The name index is 5, above 6, names an unknown long prefix, or names a table slot the format does not define.
    /// <para>名称索引为 5、大于 6、指名未知长前缀，或指名格式未定义的表槽。</para></exception>
    public static string ResolveName(
        byte nameIndex,
        ReadOnlySpan<byte> rawName,
        IReadOnlyList<(byte BaseIndex, string Infix)>? prefixTable)
    {
        ValidateNameIndex(nameIndex);
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
    /// Rejects an <c>e_name_index</c> that names no default namespace: the format defines 0 through 6
    /// (<c>include/erofs_fs.h</c>), where 0 is the empty namespace upstream <c>xattr_types[]</c> declares for
    /// names stored without a prefix, and 5 is the empty slot neither side may use. A long-prefix index is a
    /// table position, so only its table bounds are checked, by the resolver that owns the table.
    /// <para>拒绝不指名任何默认命名空间的 <c>e_name_index</c>：格式定义 0 到 6
    /// （<c>include/erofs_fs.h</c>），其中 0 是上游 <c>xattr_types[]</c> 为无前缀名称声明的空命名空间，5 是
    /// 双方都不得使用的空槽。长前缀索引是表内位置，只由持有该表的解析方检查其表边界。</para>
    /// </summary>
    /// <param name="nameIndex">On-disk <c>e_name_index</c>.<para>磁盘上的 <c>e_name_index</c>。</para></param>
    /// <exception cref="FileSystemCorruptedException">A plain index is 5 or above 7.
    /// <para>非长前缀索引为 5 或大于等于 7。</para></exception>
    internal static void ValidateNameIndex(byte nameIndex)
    {
        if ((nameIndex & LongPrefixFlag) != 0)
        {
            return;
        }

        if (nameIndex > MaxNameIndex || nameIndex == ReservedNameIndex)
        {
            throw new FileSystemCorruptedException(
                $"EROFS xattr name index {nameIndex} does not name a defined namespace.");
        }
    }

    /// <summary>
    /// Rejects a caller-declared <see cref="ErofsXattrEntry.NameIndex"/> that names no default namespace,
    /// so an entry cannot silently be encoded under an index the format does not define. Zero means the
    /// caller did not declare one, and a long-prefix index is a table position the encoder re-derives from
    /// the name, so neither is checked here.
    /// <para>拒绝调用方声明的不指名任何默认命名空间的 <see cref="ErofsXattrEntry.NameIndex"/>，使条目不会在
    /// 格式未定义的索引下被静默编码。零表示调用方未声明，长前缀索引是编码器按名称重新推导的表内位置，
    /// 两者都不在此检查。</para>
    /// </summary>
    /// <param name="nameIndex">Declared <c>e_name_index</c>.<para>声明的 <c>e_name_index</c>。</para></param>
    /// <exception cref="FileSystemUnsupportedFeatureException">A plain declared index is 5 or above 7.
    /// <para>声明的非长前缀索引为 5 或大于等于 7。</para></exception>
    private static void ValidateEntryNameIndex(byte nameIndex)
    {
        if (nameIndex == 0 || (nameIndex & LongPrefixFlag) != 0)
        {
            return;
        }

        if (nameIndex > MaxNameIndex || nameIndex == ReservedNameIndex)
        {
            throw new FileSystemUnsupportedFeatureException(
                $"EROFS xattr name index {nameIndex} does not name a defined namespace.");
        }
    }

    /// <summary>Reads one erofs_xattr_entry (4-byte header + e_name + e_value) at offset in data and resolves its full name.
    /// <para>在 data 的 offset 处读取一个 erofs_xattr_entry（4 字节头 + e_name + e_value），并解析出完整名称。</para></summary>
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

    /// <summary>Parses the inline xattr list (with a long-prefix table). prefixTable comes from ParsePrefixTable; when null, long-prefix entries fall back to their raw names.
    /// <para>解析 inline xattr 列表（带长前缀表）。</para></summary>
    /// <param name="inodeData">Inode bytes whose inline xattr ibody is read from the start of the span; a span shorter than the ibody derived from <paramref name="icount"/> is reported as malformed.
    /// <para>inode 字节，其开头的 inline xattr ibody 即解析来源；跨度短于由 <paramref name="icount"/> 推导的 ibody 时按格式错误上报。</para></param>
    /// <param name="icount">Inline xattr entry count read from the inode; zero or a negative value means there is no ibody and the result is empty.
    /// <para>取自 inode 的 inline xattr 条目数；为 0 或负数表示没有 ibody，结果为空。</para></param>
    /// <param name="prefixTable">Table from <see cref="ParsePrefixTable"/>, or <see langword="null"/> when the image defines none.
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

    /// <summary>Reads and resolves the shared xattr entry identified by sharedId from the shared xattr area (bytes starting at the superblock.xattr_blkaddr block).
    /// <para>在共享 xattr 区（自 superblock.xattr_blkaddr 块起始的字节区）中按 sharedId 读取并解析 一个共享条目。</para></summary>
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
using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.F2fs.Models;

internal sealed record F2fsInode
{
    /// <summary>
    /// Extra attribute words the kernel reserves at the head of <c>i_addr</c>: <c>i_extra_isize</c> through <c>i_padding</c>, matching <c>F2FS_TOTAL_EXTRA_ATTR_SIZE</c>.
    /// <para>内核在 <c>i_addr</c> 开头预留的扩展属性字数：从 <c>i_extra_isize</c> 到 <c>i_padding</c>，与 <c>F2FS_TOTAL_EXTRA_ATTR_SIZE</c> 相同。</para>
    /// </summary>
    public const int TotalExtraAttrWords = 9;

    /// <summary>
    /// Words the kernel keeps clear of the inline xattr region so a directory can still hold its dot entries, matching <c>MIN_INLINE_DENTRY_SIZE</c>.
    /// <para>内核为避免内联 xattr 区占用而留给目录点条目的字数，与 <c>MIN_INLINE_DENTRY_SIZE</c> 相同。</para>
    /// </summary>
    public const int MinInlineDentryWords = 10;

    /// <summary>
    /// Largest inline xattr region expressible in <c>i_inline_xattr_size</c>, matching the kernel's <c>MAX_INLINE_XATTR_SIZE</c>.
    /// <para><c>i_inline_xattr_size</c> 可表示的最大内联 xattr 区，与内核的 <c>MAX_INLINE_XATTR_SIZE</c> 相同。</para>
    /// <para>The cap already subtracts <see cref="TotalExtraAttrWords"/>, the reserved word and the dot-entry floor, so an inode claiming it still leaves eleven address slots; xattr reservation can never empty the address array.</para>
    /// <para>该上限已扣除 <see cref="TotalExtraAttrWords"/>、保留字与点条目下限，故按此预留仍余 11 个地址槽，xattr 预留不可能清空地址数组。</para>
    /// </summary>
    public const int MaxInlineXattrWords = F2fsNode.DefAddrsPerInode - TotalExtraAttrWords
        - F2fsNode.DefInlineReservedSize - MinInlineDentryWords;

    public const byte InlineXattrFlag = 0x01;

    public const byte InlineDataFlag = 0x02;

    public const byte InlineDentryFlag = 0x04;

    public const byte DataExistFlag = 0x08;

    public const byte ExtraAttrFlag = 0x20;

    public const uint ComprFlag = 0x0000_0004;


    private const int OffsetMode = 0;
    private const int OffsetAdvise = 2;
    private const int OffsetInline = 3;
    private const int OffsetUid = 4;
    private const int OffsetGid = 8;
    private const int OffsetLinks = 12;
    private const int OffsetSize = 16;
    private const int OffsetBlocks = 24;
    private const int OffsetAtime = 32;
    private const int OffsetCtime = 40;
    private const int OffsetMtime = 48;
    private const int OffsetAtimeNsec = 56;
    private const int OffsetCtimeNsec = 60;
    private const int OffsetMtimeNsec = 64;
    private const int OffsetGeneration = 68;
    private const int OffsetCurrentDepth = 72;
    private const int OffsetXattrNid = 76;
    private const int OffsetFlags = 80;
    private const int OffsetPino = 84;
    private const int OffsetNamelen = 88;
    private const int OffsetName = 92;
    private const int OffsetExtraIsize = 360;
    private const int OffsetInlineXattrSize = 362;

    public ushort Mode { get; init; }

    public byte Advise { get; init; }

    public byte Inline { get; init; }

    public uint Uid { get; init; }

    public uint Gid { get; init; }

    public uint Links { get; init; }

    public ulong Size { get; init; }

    public ulong Blocks { get; init; }

    public uint XattrNid { get; init; }

    public uint Flags { get; init; }

    public uint CurrentDepth { get; init; }

    public uint Pino { get; init; }

    public uint Namelen { get; init; }

    public byte[] Name { get; init; } = [];

    public F2fsExtent Extent { get; init; } = new F2fsExtent();

    public uint[] Addr { get; init; } = [];

    public uint[] Nid { get; init; } = [];

    public ulong Mtime { get; init; }

    public uint MtimeNsec { get; init; }

    public ulong Ctime { get; init; }

    public uint CtimeNsec { get; init; }

    public ulong Atime { get; init; }
    public uint AtimeNsec { get; init; }
    public uint Generation { get; init; }
    public uint? ProjectId { get; init; }
    public FileSystemTimestamp? BirthTime { get; init; }

    public FileSystemInodeMetadata Metadata => new()
    {
        AccessTime = new(unchecked((long)Atime), AtimeNsec),
        ChangeTime = new(unchecked((long)Ctime), CtimeNsec),
        ModificationTime = new(unchecked((long)Mtime), MtimeNsec),
        BirthTime = BirthTime,
        Generation = Generation,
        ProjectId = ProjectId,
        LinkCount = Links,
        F2fsAdvise = Advise,
        F2fsCompression = IsCompressed ? new(CompressAlgorithm, LogClusterSize, CompressFlags) : null
    };

    public ushort ExtraIsize { get; init; }

    public ushort InlineXattrSize { get; init; }

    public byte CompressAlgorithm { get; init; }

    public byte LogClusterSize { get; init; }

    public ushort CompressFlags { get; init; }

    public ulong CompressedBlocks { get; init; }

    public bool IsCompressed => (Flags & ComprFlag) != 0;

    public bool IsRegularFile => (Mode & InodeTypeBits.FormatMask) == InodeTypeBits.RegularFile;

    public bool IsDirectory => (Mode & InodeTypeBits.FormatMask) == InodeTypeBits.Directory;

    public bool IsSymlink => (Mode & InodeTypeBits.FormatMask) == InodeTypeBits.SymbolicLink;

    public bool HasInlineData => (Inline & InlineDataFlag) != 0;

    public bool HasInlineDentry => (Inline & InlineDentryFlag) != 0;

    public int ExtraIsizeWords => (Inline & ExtraAttrFlag) != 0 ? ExtraIsize / 4 : 0;

    public int AddressCount => AlignAddressCount(F2fsNode.DefAddrsPerInode - ExtraIsizeWords - InlineXattrAddrs);

    public int DataAddressesPerNode => AlignAddressCount(F2fsNode.AddrsPerBlock);

    private int AlignAddressCount(int count) => IsCompressed ? count & ~((1 << LogClusterSize) - 1) : count;

    public int InlineXattrAddrs => (Inline & InlineXattrFlag) != 0 ? InlineXattrSize : 0;

    public int MaxInlineData => (F2fsNode.DefAddrsPerInode - ExtraIsizeWords - InlineXattrAddrs - F2fsNode.DefInlineReservedSize) * 4;

    /// <summary>
    /// Byte offset of the inline data area inside the inode node page: the kernel anchors it right after the extra attributes and one reserved word, so it starts at the head of the usable <c>i_addr</c> range.
    /// <para>inode 节点页内联数据区的字节偏移：内核把它锚定在扩展属性之后并跳过一个保留字，因此它位于可用 <c>i_addr</c> 区间的开头。</para>
    /// </summary>
    /// <value>Offset of <c>i_addr[i_extra_isize / 4 + 1]</c>, matching the kernel's <c>inline_data_addr()</c>; block addresses for a non-inline inode share the same base, so word <c>i_extra_isize / 4</c> is the first file block.
    /// <para><c>i_addr[i_extra_isize / 4 + 1]</c> 的偏移，与内核 <c>inline_data_addr()</c> 一致；非内联 inode 的块地址使用同一基准，因此字 <c>i_extra_isize / 4</c> 即首个文件块。</para></value>
    public int InlineDataOffset => F2fsNode.AddrArrayOffset + (ExtraIsizeWords + F2fsNode.DefInlineReservedSize) * 4;

    /// <summary>
    /// Byte offset of the inline xattr payload inside the inode node page: the kernel anchors it at the tail of <c>i_addr</c>, not next to the inline data, so reserving xattr space never moves block addresses.
    /// <para>inode 节点页内联 xattr 载荷的字节偏移：内核把它锚定在 <c>i_addr</c> 的末尾，而非紧贴内联数据，因此预留 xattr 空间不会移动块地址。</para>
    /// </summary>
    /// <value>Offset of <c>i_addr[923 - i_inline_xattr_size]</c>, matching the kernel's <c>inline_xattr_addr()</c>, or the node size when the inode carries no inline xattr region.
    /// <para><c>i_addr[923 - i_inline_xattr_size]</c> 的偏移，与内核 <c>inline_xattr_addr()</c> 一致；inode 没有内联 xattr 区时返回节点大小。</para></value>
    public int InlineXattrOffset => InlineXattrAddrs == 0
        ? F2fsNode.NodeSize
        : F2fsNode.AddrArrayOffset + (F2fsNode.DefAddrsPerInode - InlineXattrAddrs) * 4;

    /// <summary>
    /// Parses the inode from a node page (the footer must be validated separately: nid == ino marks an inode node).
    /// <para>从节点页解析 inode（页脚需同时校验：nid == ino 表示 inode 节点）。</para>
    /// </summary>
    /// <param name="node">Complete inode node page of at least the F2FS node size, whose i_addr array of 923 entries starts at <see cref="F2fsNode.AddrArrayOffset"/>, i_nid array at <see cref="F2fsNode.NidArrayOffset"/>, first extent at <see cref="F2fsExtent.InodeOffset"/> and name bytes at the offset following the name length.
    /// <para>完整的 inode 节点页，长度至少为 F2FS 节点大小：923 项的 i_addr 数组起始于 <see cref="F2fsNode.AddrArrayOffset"/>，i_nid 数组起始于 <see cref="F2fsNode.NidArrayOffset"/>，首个 extent 位于 <see cref="F2fsExtent.InodeOffset"/>，名称字节从名称长度之后的偏移开始。</para></param>
    /// <param name="flexibleInlineXattr">When <see langword="false"/> and the inode marks inline xattr, the inline xattr size is taken as 50 address slots instead of the stored field; <see langword="true"/>, the default, always reads the stored value.
    /// <para>为 <see langword="false"/> 且 inode 标记了内联 xattr 时，内联 xattr 大小按 50 个地址槽取值而不读磁盘字段；默认值 <see langword="true"/> 始终读取存储的值。</para></param>
    /// <returns>Decoded inode with the address and nid arrays, the NUL-trimmed name, the first extent and the timestamp fields; project id, birth time and the compression attributes stay absent while the recorded extra-attribute size does not reach their offsets. A short page, an extra size that is not a multiple of four or exceeds the address array, a compressed inode without 36 bytes of extra attributes and a cluster exponent outside 2 to 8 are rejected.
    /// <para>返回已解码的 inode，含地址与 nid 数组、去除尾部 NUL 的名称、首个 extent 与时间戳字段；记录的扩展属性大小未触及相应偏移时，project id、创建时间与压缩属性保持缺失。页长不足、扩展大小不是 4 的倍数或超过地址数组、压缩 inode 缺少 36 字节扩展属性、簇指数不在 2 到 8 范围内都会被拒绝。</para></returns>
    public static F2fsInode FromBytes(ReadOnlySpan<byte> node, bool flexibleInlineXattr = true)
    {
        if (node.Length < F2fsNode.NodeSize)
        {
            throw new FormatException($"F2FS node page too short: {node.Length} bytes.");
        }

        var addrs = new uint[F2fsNode.DefAddrsPerInode];
        for (var i = 0; i < addrs.Length; i++)
        {
            addrs[i] = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(F2fsNode.AddrArrayOffset + i * 4, 4));
        }

        var nids = new uint[F2fsNode.DefNidsPerInode];
        var nidOffset = F2fsNode.NidArrayOffset;
        for (var i = 0; i < nids.Length; i++)
        {
            nids[i] = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(nidOffset + i * 4, 4));
        }

        var namelen = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetNamelen..]);
        var name = new byte[F2fsNode.NodeSize - OffsetName];
        node.Slice(OffsetName).CopyTo(name);

        var advise = node[OffsetAdvise];
        var extraSize = (node[OffsetInline] & ExtraAttrFlag) != 0 ? BinaryPrimitives.ReadUInt16LittleEndian(node[OffsetExtraIsize..]) : 0;
        if (extraSize % 4 != 0 || extraSize > F2fsNode.DefAddrsPerInode * 4)
            throw new FileSystemCorruptedException("Invalid F2FS extra inode attribute size.");
        var compressed = (BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetFlags..]) & ComprFlag) != 0;
        if (compressed && extraSize < 36)
            throw new FileSystemCorruptedException("F2FS compressed inode lacks compression extra attributes.");
        if (compressed && node[393] is < 2 or > 8)
            throw new FileSystemCorruptedException("Invalid F2FS compression cluster size.");

        return new F2fsInode
        {
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(node[OffsetMode..]),
            Advise = advise,
            Inline = node[OffsetInline],
            Uid = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetUid..]),
            Gid = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetGid..]),
            Links = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetLinks..]),
            Size = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetSize..]),
            Blocks = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetBlocks..]),
            Atime = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetAtime..]),
            AtimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetAtimeNsec..]),
            Generation = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetGeneration..]),
            ProjectId = extraSize >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(node[364..]) : null,
            BirthTime = extraSize >= 24 ? new(BinaryPrimitives.ReadInt64LittleEndian(node[372..]), BinaryPrimitives.ReadUInt32LittleEndian(node[380..])) : null,
            Mtime = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetMtime..]),
            MtimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetMtimeNsec..]),
            Ctime = BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetCtime..]),
            CtimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetCtimeNsec..]),
            XattrNid = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetXattrNid..]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetFlags..]),
            Pino = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetPino..]),
            CurrentDepth = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetCurrentDepth..]),
            Namelen = namelen,
            Name = TrimName(name, namelen),
            Extent = F2fsExtent.FromBytes(node.Slice(F2fsExtent.InodeOffset, F2fsExtent.Size)),
            Addr = addrs,
            Nid = nids,
            ExtraIsize = BinaryPrimitives.ReadUInt16LittleEndian(node[OffsetExtraIsize..]),
            InlineXattrSize = !flexibleInlineXattr && (node[OffsetInline] & InlineXattrFlag) != 0 ? (ushort)50 : BinaryPrimitives.ReadUInt16LittleEndian(node[OffsetInlineXattrSize..]),
            CompressAlgorithm = extraSize >= 36 ? node[392] : (byte)0,
            LogClusterSize = extraSize >= 36 ? node[393] : (byte)0,
            CompressFlags = extraSize >= 36 ? BinaryPrimitives.ReadUInt16LittleEndian(node[394..]) : (ushort)0,
            CompressedBlocks = extraSize >= 36 ? BinaryPrimitives.ReadUInt64LittleEndian(node[384..]) : 0,
        };
    }

    /// <summary>
    /// Writes the inode fields into a node page (the footer is written separately by the caller; untouched fields keep their previous values).
    /// <para>将 inode 字段写入节点页（页脚由调用方另行写入；未覆盖字段保持原样）。</para>
    /// </summary>
    /// <param name="node">Destination node page with at least the F2FS node size writable bytes. Mode, ownership, size, timestamps, flags, parent, name length, the name bytes, the first extent and the address and nid arrays are written in place, the extra-attribute block is written only while the inline byte carries the extra-attribute flag, and the name field is cleared over its whole width before the available bytes are copied. A nanosecond component of one second or more stops the write after mode, ownership, size and block count were already stored.
    /// <para>目标节点页，可写字节至少为 F2FS 节点大小。模式、属主、大小、时间戳、标志、父目录 inode、名称长度、名称字节、首个 extent 以及地址与 nid 数组都就地写入；只有 inline 字节带扩展属性标志时才写扩展属性区块；名称字段先按整个宽度清零再复制可用字节。纳秒分量达到 1 秒或以上会中止写入，但此时模式、属主、大小与块数已经写入。</para></param>
    public void WriteTo(Span<byte> node)
    {
        if (node.Length < F2fsNode.NodeSize)
        {
            throw new ArgumentException("Inode destination buffer is too small.", nameof(node));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(node[OffsetMode..], Mode);
        node[OffsetAdvise] = Advise;
        node[OffsetInline] = Inline;
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetUid..], Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetGid..], Gid);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetLinks..], Links);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetSize..], Size);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetBlocks..], Blocks);


        Metadata.Validate();
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetAtime..], Atime);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetCtime..], Ctime);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetMtime..], Mtime);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetAtimeNsec..], AtimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetCtimeNsec..], CtimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetMtimeNsec..], MtimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetGeneration..], Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetCurrentDepth..], CurrentDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetXattrNid..], XattrNid);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetFlags..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetPino..], Pino);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetNamelen..], Namelen);
        node.Slice(OffsetName, Math.Min(F2fsNode.NodeSize - OffsetName, Name.Length)).Clear();
        Name.AsSpan(0, Math.Min(F2fsNode.NodeSize - OffsetName, Name.Length)).CopyTo(node[OffsetName..]);
        Extent.WriteTo(node.Slice(F2fsExtent.InodeOffset, F2fsExtent.Size));
        for (var i = 0; i < Addr.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(F2fsNode.AddrArrayOffset + i * 4, 4), Addr[i]);
        }

        if ((Inline & ExtraAttrFlag) != 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(node[OffsetExtraIsize..], ExtraIsize);
            BinaryPrimitives.WriteUInt16LittleEndian(node[OffsetInlineXattrSize..], InlineXattrSize);
            if (ExtraIsize >= 8)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(node[364..], ProjectId ?? 0);
            }
            if (ExtraIsize >= 24)
            {
                BinaryPrimitives.WriteInt64LittleEndian(node[372..], BirthTime?.Seconds ?? 0);
                BinaryPrimitives.WriteUInt32LittleEndian(node[380..], BirthTime?.Nanoseconds ?? 0);
            }
            if (ExtraIsize >= 36)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(node[384..], CompressedBlocks);
                node[392] = CompressAlgorithm;
                node[393] = LogClusterSize;
                BinaryPrimitives.WriteUInt16LittleEndian(node[394..], CompressFlags);
            }
        }

        for (var i = 0; i < Nid.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(F2fsNode.NidArrayOffset + i * 4, 4), Nid[i]);
        }
    }

    /// <summary>
    /// Reads the file content bytes from the inline area (for inline data and fast symlinks).
    /// <para>从 inline 区域读取文件内容字节（供 inline data 与快速符号链接使用）。</para>
    /// </summary>
    /// <param name="node">Inode node page the payload is sliced from; it must reach the inline area this inode's extra size and inline xattr size select.
    /// <para>用于切出载荷的 inode 节点页；页长必须覆盖由该 inode 的扩展大小与内联 xattr 大小所选中的内联区域。</para></param>
    /// <param name="size">Logical file size in bytes; the smaller of it and <c>MaxInlineData</c> decides how many bytes are returned, and 0 yields an empty array.
    /// <para>逻辑文件大小（字节）；它与 <c>MaxInlineData</c> 中较小者决定返回的字节数，为 0 时返回空数组。</para></param>
    /// <returns>Copied inline bytes, the requested size capped at the inode's inline capacity, and an empty array when that capacity is 0; a range reaching past the inode end offset is reported as corruption.
    /// <para>复制出的内联字节，取请求大小与该 inode 内联容量中的较小值；容量为 0 时返回空数组。范围越过 inode 末尾偏移时按损坏上报。</para></returns>
    public byte[] ReadInlineBytes(ReadOnlySpan<byte> node, ulong size)
    {
        var offset = InlineDataOffset;
        var length = Math.Min((ulong)MaxInlineData, size);
        if (offset < 0 || offset > F2fsNode.InodeEndOffset || (long)length > F2fsNode.InodeEndOffset - offset)
        {
            throw new FileSystemCorruptedException("F2FS inline data exceeds the inode bounds.");
        }

        return node.Slice(offset, (int)length).ToArray();
    }

    private static byte[] TrimName(byte[] raw, uint namelen)
    {
        var length = (int)Math.Min(namelen, (uint)raw.Length);
        var result = new byte[length];
        raw.AsSpan(0, length).CopyTo(result);
        return result;
    }
}
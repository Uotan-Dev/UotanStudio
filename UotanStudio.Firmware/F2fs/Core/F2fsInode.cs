using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// f2fs inode 结构（struct f2fs_inode，小端），位于节点页起始处，提供文件/目录/符号链接
/// 遍历与数据块寻址所需的字段。i_addr[i_nid] 直接指针与节点指针、inline data（xattr 之后）
/// 均按 Linux 内核布局解析。
/// <para>The F2FS inode structure (struct f2fs_inode, little-endian) at the start of a node page,
/// providing the fields needed for file/directory/symlink traversal and data block addressing.
/// The i_addr/i_nid pointers and inline data (after xattr) follow the Linux kernel layout.</para>
/// </summary>
internal sealed record F2fsInode
{
    private const ushort ModeFormatMask = 0xF000;
    private const ushort ModeRegularFile = 0x8000;
    private const ushort ModeDirectory = 0x4000;
    private const ushort ModeSymbolicLink = 0xA000;

    /// <summary>inline xattr 标志（F2FS_INLINE_XATTR）。Inline-xattr flag.</summary>
    public const byte InlineXattrFlag = 0x01;

    /// <summary>inline data 标志（F2FS_INLINE_DATA）。Inline-data flag.</summary>
    public const byte InlineDataFlag = 0x02;

    /// <summary>inline dentry 标志（F2FS_INLINE_DENTRY）。Inline-dentry flag.</summary>
    public const byte InlineDentryFlag = 0x04;

    /// <summary>inline data 已有有效数据标志（F2FS_DATA_EXIST）。Inline-data-exists flag.</summary>
    public const byte DataExistFlag = 0x08;

    /// <summary>extra attr 标志（F2FS_EXTRA_ATTR）。Extra-attribute flag.</summary>
    public const byte ExtraAttrFlag = 0x20;

    /// <summary>
    /// inode 标志位：文件已压缩（F2FS_COMPR_FL = 0x4，内核 fs/f2fs/f2fs.h）。
    /// <para>Inode flag: compressed file (F2FS_COMPR_FL = 0x4, kernel fs/f2fs/f2fs.h).</para>
    /// </summary>
    public const uint ComprFlag = 0x0000_0004;

    // inode 内字段偏移（相对节点页起始，对齐内核 fs/f2fs/f2fs_fs.h 的 struct f2fs_inode）。
    // Field offsets within the node page (matching the kernel struct f2fs_inode).
    private const int OffsetMode = 0;
    private const int OffsetAdvise = 2;
    private const int OffsetInline = 3;
    private const int OffsetUid = 4;
    private const int OffsetGid = 8;
    private const int OffsetLinks = 12;
    private const int OffsetSize = 16;
    private const int OffsetBlocks = 24;
    private const int OffsetAtime = 32;      // __le64 i_atime
    private const int OffsetCtime = 40;      // __le64 i_ctime
    private const int OffsetMtime = 48;      // __le64 i_mtime
    private const int OffsetAtimeNsec = 56;  // __le32 i_atime_nsec
    private const int OffsetCtimeNsec = 60;  // __le32 i_ctime_nsec
    private const int OffsetMtimeNsec = 64;  // __le32 i_mtime_nsec
    private const int OffsetGeneration = 68;
    private const int OffsetCurrentDepth = 72;
    private const int OffsetXattrNid = 76;
    private const int OffsetFlags = 80;
    private const int OffsetPino = 84;
    private const int OffsetNamelen = 88;
    private const int OffsetName = 92;
    private const int OffsetExtraIsize = 360;
    private const int OffsetInlineXattrSize = 362;

    /// <summary>文件模式（i_mode）。File mode.</summary>
    public ushort Mode { get; init; }

    /// <summary>文件提示位（i_advise）。File hints.</summary>
    public byte Advise { get; init; }

    /// <summary>inline 标志位（i_inline）。Inline flags.</summary>
    public byte Inline { get; init; }

    /// <summary>属主 uid。Owner uid.</summary>
    public uint Uid { get; init; }

    /// <summary>属组 gid。Owner gid.</summary>
    public uint Gid { get; init; }

    /// <summary>链接数。Link count.</summary>
    public uint Links { get; init; }

    /// <summary>文件大小（字节）。File size in bytes.</summary>
    public ulong Size { get; init; }

    /// <summary>已分配块数（4 KB 块数，含节点块自身；对齐内核 i_blocks 语义）。
    /// Allocated block count (4 KB blocks, including the node block itself; matches the kernel i_blocks semantics).</summary>
    public ulong Blocks { get; init; }

    /// <summary>xattr nid。Xattr node id.</summary>
    public uint XattrNid { get; init; }

    /// <summary>inode 标志位。Inode flags.</summary>
    public uint Flags { get; init; }

    /// <summary>目录当前深度（i_current_depth，仅目录用）。Current directory depth.</summary>
    public uint CurrentDepth { get; init; }

    /// <summary>父 inode 号。Parent inode number.</summary>
    public uint Pino { get; init; }

    /// <summary>文件名字节长度。Name length in bytes.</summary>
    public uint Namelen { get; init; }

    /// <summary>文件名（SPOR 用，最多 255 字节）。File name (for SPOR, up to 255 bytes).</summary>
    public byte[] Name { get; init; } = [];

    /// <summary>extent cache（i_ext）。Extent cache.</summary>
    public F2fsExtent Extent { get; init; } = new F2fsExtent();

    /// <summary>i_addr[923]：inode 内直接数据块指针。Direct data block pointers inside the inode.</summary>
    public uint[] Addr { get; init; } = [];

    /// <summary>i_nid[5]：direct(2)/indirect(2)/double_indirect(1) 节点指针。Node id pointers.</summary>
    public uint[] Nid { get; init; } = [];

    /// <summary>修改时间（秒，epoch）。Modification time (seconds since epoch).</summary>
    public uint Mtime { get; init; }

    /// <summary>修改时间纳秒部分。Modification time nanoseconds.</summary>
    public uint MtimeNsec { get; init; }

    /// <summary>状态变更时间（秒，epoch）。Change time (seconds since epoch).</summary>
    public uint Ctime { get; init; }

    /// <summary>状态变更时间纳秒部分。Change time nanoseconds.</summary>
    public uint CtimeNsec { get; init; }

    /// <summary>extra inode 属性大小（i_extra_isize，字节）。Extra attribute size in bytes.</summary>
    public ushort ExtraIsize { get; init; }

    /// <summary>inline xattr 大小（i_inline_xattr_size，LE32 单位）。Inline xattr size in LE32 units.</summary>
    public ushort InlineXattrSize { get; init; }

    /// <summary>
    /// 压缩算法（i_advise 低 4 位：0=LZO,1=LZ4,2=ZSTD,3=LZORLE）。
    /// <para>Compression algorithm (low 4 bits of i_advise).</para>
    /// </summary>
    public byte CompressAlgorithm { get; init; }

    /// <summary>
    /// 压缩簇大小的对数（i_advise 高 4 位；簇大小 = 1 &lt;&lt; (log_cluster_size + 12)）。
    /// <para>Log cluster size (high 4 bits of i_advise; cluster = 1 &lt;&lt; (log + 12)).</para>
    /// </summary>
    public byte LogClusterSize { get; init; }

    /// <summary>
    /// 该文件是否为压缩文件（i_flags 含 F2FS_COMPR_FL）。
    /// <para>Whether the file is compressed (F2FS_COMPR_FL set in i_flags).</para>
    /// </summary>
    public bool IsCompressed => (Flags & ComprFlag) != 0;

    /// <summary>是否为普通文件。Whether this is a regular file.</summary>
    public bool IsRegularFile => (Mode & ModeFormatMask) == ModeRegularFile;

    /// <summary>是否为目录。Whether this is a directory.</summary>
    public bool IsDirectory => (Mode & ModeFormatMask) == ModeDirectory;

    /// <summary>是否为符号链接。Whether this is a symbolic link.</summary>
    public bool IsSymlink => (Mode & ModeFormatMask) == ModeSymbolicLink;

    /// <summary>是否含 inline data。Whether the file has inline data.</summary>
    public bool HasInlineData => (Inline & InlineDataFlag) != 0;

    /// <summary>是否含 inline dentry（内联目录）。Whether the directory has inline dentries.</summary>
    public bool HasInlineDentry => (Inline & InlineDentryFlag) != 0;

    /// <summary>extra isize 的 LE32 槽数（get_extra_isize）。Extra isize in LE32 units.</summary>
    public int ExtraIsizeWords => (Inline & ExtraAttrFlag) != 0 ? ExtraIsize / 4 : 0;

    /// <summary>inline xattr 的 LE32 槽数（get_inline_xattr_addrs）。Inline xattr addrs in LE32 units.</summary>
    public int InlineXattrAddrs => (Inline & InlineXattrFlag) != 0 ? InlineXattrSize : 0;

    /// <summary>inline 区域可容纳的最大字节数（MAX_INLINE_DATA）。Maximum inline-data bytes.</summary>
    public int MaxInlineData => (F2fsNode.DefAddrsPerInode - ExtraIsizeWords - InlineXattrAddrs - F2fsNode.DefInlineReservedSize) * 4;

    /// <summary>inline 数据在节点页内的起始字节偏移（i_addr + extra_isize + 1 槽）。
    /// Start byte offset of the inline data within the node page.</summary>
    public int InlineDataOffset => F2fsNode.AddrArrayOffset + (ExtraIsizeWords + F2fsNode.DefInlineReservedSize) * 4;

    /// <summary>
    /// 从节点页解析 inode（页脚需同时校验：nid == ino 表示 inode 节点）。
    /// <para>Parses the inode from a node page (the footer must be validated separately:
    /// nid == ino marks an inode node).</para>
    /// </summary>
    public static F2fsInode FromBytes(ReadOnlySpan<byte> node)
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
            Mtime = (uint)BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetMtime..]),
            MtimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(node[OffsetMtimeNsec..]),
            Ctime = (uint)BinaryPrimitives.ReadUInt64LittleEndian(node[OffsetCtime..]),
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
            InlineXattrSize = BinaryPrimitives.ReadUInt16LittleEndian(node[OffsetInlineXattrSize..]),
            CompressAlgorithm = (byte)(advise & 0x0F),
            LogClusterSize = (byte)((advise >> 4) & 0x0F),
        };
    }

    /// <summary>
    /// 将 inode 字段写入节点页（页脚由调用方另行写入；未覆盖字段保持原样）。
    /// <para>Writes the inode fields into a node page (the footer is written separately by the
    /// caller; untouched fields keep their previous values).</para>
    /// </summary>
    public void WriteTo(Span<byte> node)
    {
        if (node.Length < F2fsNode.NodeSize)
        {
            throw new ArgumentException("Inode destination buffer is too small.", nameof(node));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(node[OffsetMode..], Mode);
        node[OffsetAdvise] = (byte)((CompressAlgorithm & 0x0F) | ((LogClusterSize & 0x0F) << 4));
        node[OffsetInline] = Inline;
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetUid..], Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetGid..], Gid);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetLinks..], Links);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetSize..], Size);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetBlocks..], Blocks);
        // 内核 i_atime/i_ctime/i_mtime 为 __le64 秒（@32/40/48），纳秒为 __le32（@56/60/64）。
        // Kernel i_atime/i_ctime/i_mtime are __le64 seconds (@32/40/48); nanoseconds are __le32 (@56/60/64).
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetAtime..], Mtime);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetCtime..], Ctime);
        BinaryPrimitives.WriteUInt64LittleEndian(node[OffsetMtime..], Mtime);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetAtimeNsec..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetCtimeNsec..], CtimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetMtimeNsec..], MtimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetGeneration..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetCurrentDepth..], CurrentDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetXattrNid..], XattrNid);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetFlags..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetPino..], Pino);
        BinaryPrimitives.WriteUInt32LittleEndian(node[OffsetNamelen..], Namelen);
        node.Slice(OffsetName, Math.Min(F2fsNode.NodeSize - OffsetName, Name.Length)).Clear();
        Name.AsSpan(0, Math.Min(F2fsNode.NodeSize - OffsetName, Name.Length)).CopyTo(node[OffsetName..]);
        Extent.WriteTo(node.Slice(F2fsExtent.InodeOffset, F2fsExtent.Size));
        BinaryPrimitives.WriteUInt16LittleEndian(node[OffsetExtraIsize..], ExtraIsize);
        BinaryPrimitives.WriteUInt16LittleEndian(node[OffsetInlineXattrSize..], InlineXattrSize);
        for (var i = 0; i < Addr.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(F2fsNode.AddrArrayOffset + i * 4, 4), Addr[i]);
        }

        for (var i = 0; i < Nid.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(F2fsNode.NidArrayOffset + i * 4, 4), Nid[i]);
        }
    }

    /// <summary>
    /// 从 inline 区域读取文件内容字节（供 inline data 与快速符号链接使用）。
    /// <para>Reads the file content bytes from the inline area (for inline data and fast symlinks).</para>
    /// </summary>
    public byte[] ReadInlineBytes(ReadOnlySpan<byte> node, ulong size)
    {
        var offset = InlineDataOffset;
        var length = Math.Min((ulong)MaxInlineData, size);
        if (offset < 0 || offset > F2fsNode.InodeEndOffset || (long)length > F2fsNode.InodeEndOffset - offset)
        {
            throw new InvalidDataException("F2FS inline data exceeds the inode bounds.");
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
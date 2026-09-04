using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs inode：compact（32 字节）与 extended（64 字节）两种布局，均为小端。磁盘布局与 Linux
/// 内核主线 <c>fs/erofs/erofs_fs.h</c> 的 <c>struct erofs_inode_compact</c> /
/// <c>struct erofs_inode_extended</c> 严格对齐，包括 i_format 位域、compact 偏移 6 的 i_nb 联合体
/// 以及 chunk 布局的 <c>erofs_inode_chunk_info</c> 语义。
/// <para>EROFS inode: both compact (32 bytes) and extended (64 bytes) layouts, little-endian. The
/// on-disk layout strictly follows the mainline Linux kernel <c>fs/erofs/erofs_fs.h</c>
/// (<c>struct erofs_inode_compact</c> / <c>struct erofs_inode_extended</c>), including the i_format
/// bit fields, the i_nb union at compact offset 6, and the <c>erofs_inode_chunk_info</c> semantics
/// of the chunk-based layout.</para>
/// </summary>
internal sealed class ErofsInode
{
    /// <summary>compact inode 大小。Size of a compact inode.</summary>
    public const int CompactSize = 32;

    /// <summary>
    /// extended inode 大小。按 Linux 内核 fs/erofs/erofs_fs.h 的 BUILD_BUG_ON，
    /// 磁盘上的 extended inode 为 64 字节（compact 为 32 字节）。
    /// <para>Size of an extended inode. Per the BUILD_BUG_ON in Linux fs/erofs/erofs_fs.h,
    /// the on-disk extended inode is 64 bytes (the compact one is 32 bytes).</para>
    /// </summary>
    public const int ExtendedSize = 64;

    // ---- i_format 位域（与内核 erofs_fs.h 的 EROFS_I_* 宏一致）：
    // 位 0 = 版本（EROFS_I_VERSION_MASK = 0x01，1 = extended）；
    // 位 1..3 = 数据布局（EROFS_I_DATALAYOUT_MASK = 0x07，起始位 EROFS_I_DATALAYOUT_BIT = 1）；
    // 位 4 = EROFS_I_NLINK_1_BIT（非目录 compact inode 置位表示 nlink == 1，偏移 6 的 i_nb
    //        改存 blocks_hi/startblk_hi 而非 nlink）与 EROFS_I_DOT_OMITTED_BIT（目录置位表示
    //        省略 "." dirent）共用；
    // EROFS_I_ALL = 0x1F 覆盖上述全部位。
    // ---- i_format bit fields (matching the kernel erofs_fs.h EROFS_I_* macros):
    // bit 0 = version (EROFS_I_VERSION_MASK = 0x01, 1 = extended);
    // bits 1..3 = data layout (EROFS_I_DATALAYOUT_MASK = 0x07, start bit EROFS_I_DATALAYOUT_BIT = 1);
    // bit 4 = EROFS_I_NLINK_1_BIT (non-directory compact inode: nlink == 1, and the i_nb union at
    //         offset 6 holds blocks_hi/startblk_hi instead of nlink) shared with
    //         EROFS_I_DOT_OMITTED_BIT (directory: the "." dirent is omitted);
    // EROFS_I_ALL = 0x1F covers all of the above.

    /// <summary>数据布局位掩码（i_format 位 1..3，EROFS_I_DATALAYOUT_MASK）。Data layout mask
    /// (i_format bits 1..3, EROFS_I_DATALAYOUT_MASK).</summary>
    public const ushort DataLayoutMask = 0x7 << 1;

    /// <summary>扁平布局（未压缩，i_u = raw_blkaddr）。Flat layout (uncompressed, i_u = raw_blkaddr).</summary>
    public const ushort DataLayoutFlat = 0;

    /// <summary>压缩布局（完整索引，i_u = compressed_blocks）。Compressed layout (full indexes, i_u = compressed_blocks).</summary>
    public const ushort DataLayoutCompressed = 1 << 1;

    /// <summary>紧凑索引压缩布局（保留）。Compacted-index compression layout (reserved).</summary>
    public const ushort DataLayoutCompactIndexes = 3 << 1;

    /// <summary>尾部内联布局（保留）。Inline layout (reserved).</summary>
    public const ushort DataLayoutInline = 2 << 1;

    /// <summary>
    /// 基于 chunk 的布局（i_u 为 <c>erofs_inode_chunk_info { __le16 format; __le16 reserved }</c>）。
    /// 对应内核枚举 <c>EROFS_INODE_CHUNK_BASED = 4</c>，掩码后值 <c>4 &lt;&lt; 1</c> = 0x8。
    /// <para>Chunk-based layout (i_u holds an <c>erofs_inode_chunk_info { __le16 format; __le16
    /// reserved }</c>). Matches the kernel enum <c>EROFS_INODE_CHUNK_BASED = 4</c>, masked value
    /// <c>4 &lt;&lt; 1</c> = 0x8.</para>
    /// </summary>
    public const ushort DataLayoutChunkBased = 4 << 1;

    /// <summary>extended inode 版本位（i_format 位 0，EROFS_INODE_LAYOUT_EXTENDED）。Extended-inode version bit (i_format bit 0, EROFS_INODE_LAYOUT_EXTENDED).</summary>
    public const ushort FlagExtended = 1;

    /// <summary>
    /// i_format 位 4 的位掩码：非目录 compact inode 为 nlink==1 位（EROFS_I_NLINK_1_BIT），
    /// 目录则复用为省略 "." dirent 位（EROFS_I_DOT_OMITTED_BIT）。
    /// <para>Bit mask for i_format bit 4: nlink==1 for non-directory compact inodes
    /// (EROFS_I_NLINK_1_BIT), reused as dot-omitted for directories (EROFS_I_DOT_OMITTED_BIT).</para>
    /// </summary>
    private const ushort AdviseBitNlinkOneOrDotOmitted = 1 << 4;

    // ---- chunk 信息位域（与内核 erofs_fs.h 的 EROFS_CHUNK_FORMAT_* 宏一致）。
    // ---- Chunk-info bit fields (matching the kernel erofs_fs.h EROFS_CHUNK_FORMAT_* macros).
    /// <summary>chunk 格式位掩码（EROFS_CHUNK_FORMAT_BLKBITS_MASK）：低 5 位为块大小移位，
    /// chunk 大小 = 块大小 &lt;&lt; blkbits。Chunk-format bit mask (EROFS_CHUNK_FORMAT_BLKBITS_MASK):
    /// the low 5 bits are the block-size shift; chunk size = block size &lt;&lt; blkbits.</summary>
    private const ushort ChunkFormatBlkBitsMask = 0x001F;

    /// <summary>chunk 索引位（EROFS_CHUNK_FORMAT_INDEXES）：置位时 i_u 之后为 chunk 索引表，
    /// 否则为连续的 chunk 块号数组。Chunk-index bit (EROFS_CHUNK_FORMAT_INDEXES): when set, a
    /// chunk-index table follows i_u; otherwise a contiguous chunk block-number array follows.</summary>
    private const ushort ChunkFormatIndexes = 0x0020;

    /// <summary>48 位 chunk 块号位（EROFS_CHUNK_FORMAT_48BIT）：块号由 blocks_hi 与 i_u 拼接。
    /// 48-bit chunk block numbers (EROFS_CHUNK_FORMAT_48BIT): block numbers are built from
    /// blocks_hi plus i_u.</summary>
    private const ushort ChunkFormat48Bit = 0x0040;

    /// <summary>i_mode 文件类型掩码。i_mode file type mask.</summary>
    public const ushort ModeTypeMask = 0xF000;

    /// <summary>目录类型位。Directory type bits.</summary>
    public const ushort ModeDirectory = 0x4000;

    /// <summary>普通文件类型位。Regular-file type bits.</summary>
    public const ushort ModeRegularFile = 0x8000;

    /// <summary>符号链接类型位。Symbolic-link type bits.</summary>
    public const ushort ModeSymlink = 0xA000;

    /// <summary>字符设备类型位（S_IFCHR）。Character-device type bits (S_IFCHR).</summary>
    public const ushort ModeCharacterDevice = 0x2000;

    /// <summary>块设备类型位（S_IFBLK）。Block-device type bits (S_IFBLK).</summary>
    public const ushort ModeBlockDevice = 0x6000;

    /// <summary>命名管道类型位（S_IFIFO）。Named-pipe type bits (S_IFIFO).</summary>
    public const ushort ModeFifo = 0x1000;

    /// <summary>套接字类型位（S_IFSOCK）。Socket type bits (S_IFSOCK).</summary>
    public const ushort ModeSocket = 0xC000;

    /// <summary>dirent 文件类型：未知。Dirent file type: unknown.</summary>
    public const byte FileTypeUnknown = 0;

    /// <summary>dirent 文件类型：普通文件。Dirent file type: regular file.</summary>
    public const byte FileTypeRegularFile = 1;

    /// <summary>dirent 文件类型：目录。Dirent file type: directory.</summary>
    public const byte FileTypeDirectory = 2;

    /// <summary>dirent 文件类型：字符设备（EROFS_FT_CHRDEV = 3）。Dirent file type: char device.</summary>
    public const byte FileTypeCharacterDevice = 3;

    /// <summary>dirent 文件类型：块设备（EROFS_FT_BLKDEV = 4）。Dirent file type: block device.</summary>
    public const byte FileTypeBlockDevice = 4;

    /// <summary>dirent 文件类型：命名管道（EROFS_FT_FIFO = 5）。Dirent file type: named pipe.</summary>
    public const byte FileTypeFifo = 5;

    /// <summary>dirent 文件类型：套接字（EROFS_FT_SOCK = 6）。Dirent file type: socket.</summary>
    public const byte FileTypeSocket = 6;

    /// <summary>dirent 文件类型：符号链接（EROFS_FT_SYMLINK = 7）。Dirent file type: symbolic link.</summary>
    public const byte FileTypeSymlink = 7;

    /// <summary>i_format / i_advise。Format / advise field.</summary>
    public ushort Advise { get; set; }

    /// <summary>xattr 条目数量（以 4 字节计）。Xattr entry count (in 4-byte units).</summary>
    public ushort XattrICount { get; set; }

    /// <summary>i_mode（含文件类型位）。Mode including file type bits.</summary>
    public ushort Mode { get; set; }

    /// <summary>
    /// 链接数。compact 布局中磁盘偏移 6 为 i_nb 联合体：默认存 nlink；当 <see cref="NlinkIsOne"/>
    /// （非目录且 i_format 位 4 置位）时改存 blocks_hi/startblk_hi，此时 nlink 恒为 1。extended
    /// 布局存于偏移 44 的 u32 i_nlink。
    /// <para>Link count. In the compact layout, offset 6 is the i_nb union: it stores nlink by
    /// default; when <see cref="NlinkIsOne"/> (non-directory with i_format bit 4 set) it stores
    /// blocks_hi/startblk_hi instead, and nlink is always 1. In the extended layout it is the u32
    /// i_nlink at offset 44.</para>
    /// </summary>
    public ulong Nlink { get; set; }

    /// <summary>i_size（文件/目录字节大小）。Size in bytes.</summary>
    public ulong Size { get; set; }

    /// <summary>
    /// i_u：扁平布局为数据起始块号（blocks_lo/startblk_lo），压缩布局为压缩块总数，chunk 布局为
    /// <c>erofs_inode_chunk_info { __le16 format; __le16 reserved }</c>，设备节点为 rdev。
    /// <para>i_u: raw block address (blocks_lo/startblk_lo) for flat, compressed block count for
    /// compressed, an <c>erofs_inode_chunk_info { __le16 format; __le16 reserved }</c> for the
    /// chunk-based layout, and rdev for device nodes.</para>
    /// </summary>
    public uint U { get; set; }

    /// <summary>i_ino（32 位 stat 兼容号）。Inode number for 32-bit stat compatibility.</summary>
    public uint Ino { get; set; }

    /// <summary>uid。User id.</summary>
    public uint Uid { get; set; }

    /// <summary>gid。Group id.</summary>
    public uint Gid { get; set; }

    /// <summary>
    /// generation（compact inode 偏移 12 的 u32）。经典内核该字段实为 i_reserved，仅当超级块声明
    /// <c>EROFS_FEATURE_COMPAT_MTIME</c>（见 <see cref="ErofsSuperblock.CompatMtime"/>）时才按
    /// i_mtime 解释；本类在解析时统一按原始值保存，由调用方依据超级块特性位取舍。
    /// <para>Generation (the u32 at compact inode offset 12). In the classic kernel this field is
    /// actually i_reserved; it only carries an mtime when the superblock declares
    /// <c>EROFS_FEATURE_COMPAT_MTIME</c> (see <see cref="ErofsSuperblock.CompatMtime"/>). This
    /// class always keeps the raw value on parse; callers may reinterpret it from the feature bits.</para>
    /// </summary>
    public uint Generation { get; set; }

    /// <summary>
    /// i_ctime（Unix 秒）。extended 布局读自偏移 32 的 u64 i_mtime；compact 布局磁盘上无该字段
    /// （compact 偏移 12 的 u32 见 <see cref="Generation"/>），恒为 0。
    /// <para>Creation/modification time in Unix seconds. For extended inodes read from the u64
    /// i_mtime at offset 32; compact inodes carry no such field on disk (see
    /// <see cref="Generation"/> for the u32 at compact offset 12), so this stays 0.</para>
    /// </summary>
    public ulong CTime { get; set; }

    /// <summary>i_ctime 纳秒部分（extended 偏移 40 的 u32 i_mtime_nsec）。Creation/modification time
    /// nanoseconds (extended u32 i_mtime_nsec at offset 40).</summary>
    public uint CTimeNsec { get; set; }

    /// <summary>是否为 extended inode。Whether this is an extended inode.</summary>
    public bool IsExtended => (Advise & FlagExtended) != 0;

    /// <summary>数据布局值（i_format 位 1..3 掩码后的值，对应内核枚举 EROFS_INODE_*）。
    /// 与 <see cref="DataLayoutFlat"/>/<see cref="DataLayoutCompressed"/>/<see cref="DataLayoutInline"/>
    /// 等常量一致，均为掩码后未右移的值（如 inline = <c>2 &lt;&lt; 1</c> = 4）。
    /// <para>Data layout value (i_format bits 1..3 after masking, matching the kernel EROFS_INODE_*
    /// enum). Consistent with the <see cref="DataLayoutFlat"/>/<see cref="DataLayoutCompressed"/>/
    /// <see cref="DataLayoutInline"/> constants, all being the masked (non-shifted) value
    /// (e.g. inline = <c>2 &lt;&lt; 1</c> = 4).</para></summary>
    public ushort DataLayout => (ushort)(Advise & DataLayoutMask);

    /// <summary>是否为目录。Whether this is a directory.</summary>
    public bool IsDirectory => (Mode & ModeTypeMask) == ModeDirectory;

    /// <summary>是否为普通文件。Whether this is a regular file.</summary>
    public bool IsRegularFile => (Mode & ModeTypeMask) == ModeRegularFile;

    /// <summary>是否为符号链接。Whether this is a symbolic link.</summary>
    public bool IsSymlink => (Mode & ModeTypeMask) == ModeSymlink;

    /// <summary>是否为字符设备。Whether this is a character device.</summary>
    public bool IsCharacterDevice => (Mode & ModeTypeMask) == ModeCharacterDevice;

    /// <summary>是否为块设备。Whether this is a block device.</summary>
    public bool IsBlockDevice => (Mode & ModeTypeMask) == ModeBlockDevice;

    /// <summary>是否为命名管道。Whether this is a named pipe (FIFO).</summary>
    public bool IsFifo => (Mode & ModeTypeMask) == ModeFifo;

    /// <summary>是否为套接字。Whether this is a socket.</summary>
    public bool IsSocket => (Mode & ModeTypeMask) == ModeSocket;

    /// <summary>
    /// 由 i_mode 文件类型位推导 dirent 文件类型（对应内核 <c>erofs_mode_to_ftype</c>）。
    /// 未知/非法类型返回 <see cref="FileTypeUnknown"/>。
    /// <para>Derives the dirent file type from the i_mode type bits (kernel
    /// <c>erofs_mode_to_ftype</c>). Returns <see cref="FileTypeUnknown"/> for unknown types.</para>
    /// </summary>
    public byte FileType
    {
        get
        {
            if (IsRegularFile) return FileTypeRegularFile;
            if (IsDirectory) return FileTypeDirectory;
            if (IsSymlink) return FileTypeSymlink;
            if (IsCharacterDevice) return FileTypeCharacterDevice;
            if (IsBlockDevice) return FileTypeBlockDevice;
            if (IsFifo) return FileTypeFifo;
            if (IsSocket) return FileTypeSocket;
            return FileTypeUnknown;
        }
    }

    /// <summary>是否为压缩布局（完整索引或紧凑索引）。
    /// 内核 <c>erofs_inode_is_data_compressed</c> 同时匹配 <c>EROFS_INODE_COMPRESSED_FULL</c>
    /// 与 <c>EROFS_INODE_COMPRESSED_COMPACT</c>。
    /// <para>Whether the data is compressed (full-index or compacted-index). The kernel
    /// <c>erofs_inode_is_data_compressed</c> matches both <c>EROFS_INODE_COMPRESSED_FULL</c> and
    /// <c>EROFS_INODE_COMPRESSED_COMPACT</c>.</para></summary>
    public bool IsCompressed =>
        DataLayout == DataLayoutCompressed || DataLayout == DataLayoutCompactIndexes;

    /// <summary>
    /// 是否非目录 compact inode 且 i_format 位 4（EROFS_I_NLINK_1_BIT）置位：表示 nlink == 1，
    /// 且磁盘偏移 6 的 i_nb 存 blocks_hi/startblk_hi 而非 nlink（如 48 位 chunk 文件）。
    /// <para>Whether this is a non-directory compact inode with i_format bit 4
    /// (EROFS_I_NLINK_1_BIT) set: nlink is 1 and the i_nb union at offset 6 holds
    /// blocks_hi/startblk_hi instead of nlink (e.g. 48-bit chunk files).</para>
    /// </summary>
    public bool NlinkIsOne => (Advise & AdviseBitNlinkOneOrDotOmitted) != 0 && !IsDirectory;

    /// <summary>
    /// 是否目录且 i_format 位 4（EROFS_I_DOT_OMITTED_BIT）置位：目录数据省略 "." dirent。
    /// <para>Whether this is a directory with i_format bit 4 (EROFS_I_DOT_OMITTED_BIT) set: the
    /// directory data omits the "." dirent.</para>
    /// </summary>
    public bool DotOmitted => (Advise & AdviseBitNlinkOneOrDotOmitted) != 0 && IsDirectory;

    /// <summary>
    /// chunk 格式（仅 chunk 布局有意义）：i_u 低 16 位，即 <c>erofs_inode_chunk_info.format</c>。
    /// 位定义：低 5 位 EROFS_CHUNK_FORMAT_BLKBITS_MASK（块大小移位）、位 5
    /// EROFS_CHUNK_FORMAT_INDEXES、位 6 EROFS_CHUNK_FORMAT_48BIT。
    /// <para>Chunk format (only meaningful for the chunk-based layout): the low 16 bits of i_u, i.e.
    /// <c>erofs_inode_chunk_info.format</c>. Bits: low 5 bits EROFS_CHUNK_FORMAT_BLKBITS_MASK
    /// (block-size shift), bit 5 EROFS_CHUNK_FORMAT_INDEXES, bit 6 EROFS_CHUNK_FORMAT_48BIT.</para>
    /// </summary>
    public ushort ChunkFormat => (ushort)U;

    /// <summary>
    /// chunk 块大小移位（EROFS_CHUNK_FORMAT_BLKBITS_MASK 低 5 位）；chunk 大小 =
    /// 块大小 &lt;&lt; blkbits。
    /// <para>Chunk block-size shift (low 5 bits, EROFS_CHUNK_FORMAT_BLKBITS_MASK); chunk size =
    /// block size &lt;&lt; blkbits.</para>
    /// </summary>
    public int ChunkFormatBlkBits => ChunkFormat & ChunkFormatBlkBitsMask;

    /// <summary>
    /// 是否启用 chunk 索引表（EROFS_CHUNK_FORMAT_INDEXES）：置位时 chunk 数据由跟随 i_u 的
    /// chunk 索引表描述，否则 i_u 之后为连续的 chunk 块号数组。
    /// <para>Whether the chunk-index table is used (EROFS_CHUNK_FORMAT_INDEXES): when set, chunk
    /// data is described by a chunk-index table following i_u; otherwise a contiguous chunk
    /// block-number array follows i_u.</para>
    /// </summary>
    public bool UsesChunkIndexes => (ChunkFormat & ChunkFormatIndexes) != 0;

    /// <summary>
    /// 是否使用 48 位 chunk 块号（EROFS_CHUNK_FORMAT_48BIT）：每个 chunk 块号由 compact inode 的
    /// blocks_hi（i_nb）与 i_u 的低 32 位拼接。
    /// <para>Whether 48-bit chunk block numbers are used (EROFS_CHUNK_FORMAT_48BIT): each chunk
    /// block number is built from the compact inode's blocks_hi (i_nb) plus the low 32 bits of
    /// i_u.</para>
    /// </summary>
    public bool Chunk48Bit => (ChunkFormat & ChunkFormat48Bit) != 0;

    /// <summary>磁盘占用大小（compact 或 extended）。On-disk size (compact or extended).</summary>
    public int OnDiskSize => IsExtended ? ExtendedSize : CompactSize;

    /// <summary>
    /// inline xattr ibody 总大小（字节），与 Linux 内核
    /// <c>erofs_xattr_ibody_size</c> 公式一致：
    /// <c>sizeof(struct erofs_xattr_ibody_header) + sizeof(__u32) * (i_xattr_icount - 1)</c>，
    /// 即 <c>12 + 4 * (i_xattr_icount - 1)</c>。当 <see cref="XattrICount"/> 为 0 时无 inline
    /// xattr，大小为 0。inline 数据紧随 xattr ibody 之后。
    /// <para>Total inline-xattr ibody size in bytes, matching the Linux kernel
    /// <c>erofs_xattr_ibody_size</c> formula:
    /// <c>sizeof(struct erofs_xattr_ibody_header) + sizeof(__u32) * (i_xattr_icount - 1)</c>,
    /// i.e. <c>12 + 4 * (i_xattr_icount - 1)</c>. When <see cref="XattrICount"/> is 0 there is no
    /// inline xattr and the size is 0. Inline data immediately follows the xattr ibody.</para>
    /// </summary>
    public int XattrTotalSize => XattrICount == 0 ? 0 : 12 + 4 * (XattrICount - 1);

    /// <summary>inline 数据起始偏移（相对 inode 起始）：OnDiskSize + XattrTotalSize。
    /// Inline-data start offset relative to the inode start: OnDiskSize + XattrTotalSize.</summary>
    public int InlineDataOffset => OnDiskSize + XattrTotalSize;

    /// <summary>数据起始块号（扁平布局）。Start block of file data (flat layout).</summary>
    public uint RawBlockAddress => U;

    /// <summary>压缩块总数（压缩布局）。Total compressed blocks (compressed layout).</summary>
    public uint CompressedBlocks => U;

    /// <summary>
    /// 从字节解析 inode；根据 i_format 的 extended 标志自动选择 compact/extended 布局。
    /// compact 解析按位 4 的 <see cref="NlinkIsOne"/> 语义解释偏移 6 的 i_nb 联合体。
    /// <para>Parses an inode from bytes; selects compact/extended layout from the extended flag in
    /// i_format. Compact parsing interprets the i_nb union at offset 6 per the
    /// <see cref="NlinkIsOne"/> semantics of bit 4.</para>
    /// </summary>
    public static ErofsInode FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            throw new FormatException("Inode region too short.");
        }

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if ((advise & FlagExtended) != 0)
        {
            if (data.Length < ExtendedSize)
            {
                throw new FormatException($"Extended inode region too short: {data.Length} bytes.");
            }

            return new ErofsInode
            {
                Advise = advise,
                XattrICount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2)),
                Mode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)),
                Size = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8)),
                U = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16)),
                Ino = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20)),
                Uid = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24)),
                Gid = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(28)),
                CTime = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(32)),
                CTimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(40)),
                Nlink = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(44)),
            };
        }

        if (data.Length < CompactSize)
        {
            throw new FormatException($"Compact inode region too short: {data.Length} bytes.");
        }

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4));
        var isDirectory = (mode & ModeTypeMask) == ModeDirectory;
        var nb = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
        var nlinkIsOne = (advise & AdviseBitNlinkOneOrDotOmitted) != 0 && !isDirectory;

        return new ErofsInode
        {
            Advise = advise,
            XattrICount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2)),
            Mode = mode,
            // i_nb 联合体：nlink==1（位 4，非目录）置位时改存 blocks_hi/startblk_hi，nlink 恒为 1。
            // The i_nb union: with the nlink==1 bit (bit 4, non-directory) set it holds
            // blocks_hi/startblk_hi, and nlink is always 1.
            Nlink = nlinkIsOne ? 1u : nb,
            Size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8)),
            Generation = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12)),
            U = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16)),
            Ino = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20)),
            Uid = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(24)),
            Gid = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26)),
        };
    }

    /// <summary>
    /// 将 inode 序列化到缓冲区（长度须至少为 OnDiskSize）。
    /// <para>Serializes the inode into a buffer of at least OnDiskSize bytes.</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < OnDiskSize)
        {
            throw new ArgumentException("Inode destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        if (IsExtended)
        {
            WriteExtended(destination);
            return;
        }

        WriteCompact(destination);
    }

    private void WriteCompact(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, Advise);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], XattrICount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Mode);
        // i_nb 联合体：非目录且位 4 置位（NlinkIsOne）时磁盘上存 blocks_hi/startblk_hi；本类仅
        // 建模 nlink，故写入 nlink（此时恒为 1）。
        // The i_nb union: when NlinkIsOne the on-disk field holds blocks_hi/startblk_hi; this class
        // only models nlink, so nlink (always 1 in that case) is written.
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)Math.Min(Nlink, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)Math.Min(Size, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], U);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], Ino);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[24..], (ushort)Math.Min(Uid, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[26..], (ushort)Math.Min(Gid, ushort.MaxValue));
    }

    private void WriteExtended(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, Advise);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], XattrICount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Mode);
        // extended 偏移 6 的 i_nb 为保留字段，缓冲区已清零，故不写入。
        // The extended i_nb at offset 6 is reserved; the destination buffer is pre-cleared, so it
        // is not written.
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], U);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], Ino);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], Gid);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], CTime);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], CTimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..], (uint)Math.Min(Nlink, uint.MaxValue));
    }
}
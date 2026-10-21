using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

/// <summary>
/// Unified EROFS inode that holds the tree position, the on-disk fields and the build-time source
/// information that erofs-utils keeps in one <c>struct erofs_inode</c>. A decoded inode fills the
/// on-disk fields from the inode record and carries its addressing state in <see cref="Idata"/>; a
/// planner fills the same fields while it assigns layouts, so a preserved rebuild compares source and
/// target strategies on one model instead of three parallel ones.
/// <para>统一的 EROFS inode，把 erofs-utils 保存在单个 <c>struct erofs_inode</c> 中的树位置、
/// 磁盘字段与构建期源信息放在一起。解码得到的 inode 从 inode 记录填充磁盘字段，并在
/// <see cref="Idata"/> 中携带寻址状态；规划器在分配布局时填充同一批字段，使保留式重建可以在一个模型上
/// 比较源与目标的存储策略，而不是在三处并行模型之间手工同步。</para>
/// </summary>
/// <remarks>
/// <para>磁盘字段语义与磁盘布局一致，模型本身不承担序列化策略以外的任何判断。承载保留式重建源策略的
/// <see cref="SourceCompressed"/>、<see cref="SourceAlgorithm"/> 与 <see cref="SourceFragmentShape"/>
/// 三项由解析器在解码时一次写入，之后只被读取，不与任何磁盘字段同步。</para>
/// The on-disk field semantics follow the disk layout and the model itself makes no decision beyond
/// serialization. The three preserved-rebuild source-strategy members <see cref="SourceCompressed"/>,
/// <see cref="SourceAlgorithm"/> and <see cref="SourceFragmentShape"/> are written once by the parser
/// while decoding and only read afterwards; they are never synchronised with an on-disk field.
/// </remarks>
internal sealed class ErofsInode
{
    /// <summary>Size in bytes of the compact inode record.
    /// <para>紧凑 inode 记录的字节数。</para></summary>
    internal const int CompactSize = 32;

    /// <summary>Size in bytes of the extended inode record.
    /// <para>扩展 inode 记录的字节数。</para></summary>
    internal const int ExtendedSize = 64;

    /// <summary>Mask of the <c>EROFS_I_DATALAYOUT</c> bits inside <c>i_format</c>.
    /// <para><c>i_format</c> 内 <c>EROFS_I_DATALAYOUT</c> 位的掩码。</para></summary>
    internal const ushort DataLayoutMask = 0x7 << 1;

    /// <summary>Shift of the <c>EROFS_I_DATALAYOUT</c> field inside <c>i_format</c>.
    /// <para><c>i_format</c> 内 <c>EROFS_I_DATALAYOUT</c> 字段的位移。</para></summary>
    internal const int DataLayoutShift = 1;

    /// <summary>Raw <c>i_format</c> value of the flat layout. <para>平坦布局的原始 <c>i_format</c> 值。</para></summary>
    internal const ushort DataLayoutFlat = 0;

    /// <summary>Raw <c>i_format</c> value of the compressed-full layout.
    /// <para>compressed-full 布局的原始 <c>i_format</c> 值。</para></summary>
    internal const ushort DataLayoutCompressed = 1 << 1;

    /// <summary>Raw <c>i_format</c> value of the inline layout.
    /// <para>内联布局的原始 <c>i_format</c> 值。</para></summary>
    internal const ushort DataLayoutInline = 2 << 1;

    /// <summary>Raw <c>i_format</c> value of the compressed-compact layout.
    /// <para>compressed-compact 布局的原始 <c>i_format</c> 值。</para></summary>
    internal const ushort DataLayoutCompactIndexes = 3 << 1;

    /// <summary>Raw <c>i_format</c> value of the chunk-based layout.
    /// <para>chunk-based 布局的原始 <c>i_format</c> 值。</para></summary>
    internal const ushort DataLayoutChunkBased = 4 << 1;

    /// <summary>Bit marking the 64-byte extended record (<c>EROFS_I_VERSION</c>).
    /// <para>标记 64 字节扩展记录的位（<c>EROFS_I_VERSION</c>）。</para></summary>
    internal const ushort FlagExtended = 1;

    /// <summary>Width of the <c>EROFS_I_DATALAYOUT</c> field inside <c>i_format</c>.
    /// <para><c>i_format</c> 内 <c>EROFS_I_DATALAYOUT</c> 字段的位宽。</para></summary>
    internal const int DataLayoutBits = 3;

    /// <summary>Bit that newer kernels define as <c>EROFS_I_NLINK_1_BIT</c> on a non-directory and
    /// <c>EROFS_I_DOT_OMITTED_BIT</c> on a directory. The kernel derives <c>EROFS_I_ALL</c> from this bit, so an
    /// inode carrying it stays inside what the kernel's <c>ifmt &amp; ~EROFS_I_ALL</c> check accepts; it is still
    /// written only when the declaration targets a kernel that defines it.
    /// <para>较新内核把它定义为非目录的 <c>EROFS_I_NLINK_1_BIT</c>、目录的 <c>EROFS_I_DOT_OMITTED_BIT</c>。内核由该位
    /// 推导出 <c>EROFS_I_ALL</c>，因此携带它的 inode 仍在内核 <c>ifmt &amp; ~EROFS_I_ALL</c> 的接受范围内；但它仍只在
    /// 声明针对定义了该位的内核时才写出。</para></summary>
    internal const ushort AdviseBitNlinkOneOrDotOmitted = 1 << 4;

    /// <summary>Every <c>i_format</c> bit the kernel accepts (<c>EROFS_I_ALL</c>). Upstream computes it from the
    /// nlink-one bit rather than from the data-layout width, as
    /// <c>((1 &lt;&lt; (EROFS_I_NLINK_1_BIT + 1)) - 1)</c>, which is <c>0x1F</c>; the kernel rejects any inode with
    /// a bit outside it through <c>ifmt &amp; ~EROFS_I_ALL</c>, and erofs-utils applies the same test while
    /// reading. Deriving it from <see cref="AdviseBitNlinkOneOrDotOmitted"/> keeps the acceptance mask and the
    /// bit the writer raises from ever disagreeing.
    /// <para>内核接受的全部 <c>i_format</c> 位（<c>EROFS_I_ALL</c>）。上游由 nlink-one 位而非数据布局宽度推导它，即
    /// <c>((1 &lt;&lt; (EROFS_I_NLINK_1_BIT + 1)) - 1)</c>，结果为 <c>0x1F</c>；内核以 <c>ifmt &amp; ~EROFS_I_ALL</c>
    /// 拒绝任何越界的 inode，erofs-utils 在读取时施加同一判定。由 <see cref="AdviseBitNlinkOneOrDotOmitted"/> 推导它，
    /// 使接受掩码与写入方置起的位不可能出现分歧。</para></summary>
    internal const ushort AdviseAll = (AdviseBitNlinkOneOrDotOmitted << 1) - 1;

    /// <summary>Mask of chunk size log2 bits stored in the low half of a chunk-based <c>u</c> field
    /// (<c>EROFS_CHUNK_FORMAT_BLKBITS_MASK</c>).
    /// <para>chunk-based <c>u</c> 低半部存放的 chunk 大小 log2 位掩码
    /// （<c>EROFS_CHUNK_FORMAT_BLKBITS_MASK</c>）。</para></summary>
    internal const ushort ChunkFormatBlkBitsMask = 0x001F;

    /// <summary>Chunk-format bit selecting 8-byte index entries over the 4-byte block map
    /// (<c>EROFS_CHUNK_FORMAT_INDEXES</c>).
    /// <para>选择 8 字节索引表项而非 4 字节块映射的 chunk 格式位
    /// （<c>EROFS_CHUNK_FORMAT_INDEXES</c>）。</para></summary>
    internal const ushort ChunkFormatIndexes = 0x0020;

    /// <summary>Chunk-format bit widening an index entry's start block to 48 bits
    /// (<c>EROFS_CHUNK_FORMAT_48BIT</c>).
    /// <para>把索引表项首块加宽到 48 位的 chunk 格式位（<c>EROFS_CHUNK_FORMAT_48BIT</c>）。</para></summary>
    internal const ushort ChunkFormat48Bit = 0x0040;

    /// <summary>Every defined chunk-format bit (<c>EROFS_CHUNK_FORMAT_ALL</c>). erofs-utils derives it as
    /// <c>((EROFS_CHUNK_FORMAT_48BIT &lt;&lt; 1) - 1)</c> and rejects a chunk-based inode carrying any bit
    /// outside it, so the test is stated once over the whole word instead of per sub-field.
    /// <para>全部已定义的 chunk 格式位（<c>EROFS_CHUNK_FORMAT_ALL</c>）。erofs-utils 按
    /// <c>((EROFS_CHUNK_FORMAT_48BIT &lt;&lt; 1) - 1)</c> 推导它，并拒绝任何携带其外位的 chunk-based inode，
    /// 因此该判定针对整个字声明一次，而不是逐子域声明。</para></summary>
    internal const ushort ChunkFormatAll = (ChunkFormat48Bit << 1) - 1;

    /// <summary>Size in bytes of one wide <c>erofs_inode_chunk_index</c> entry.
    /// <para>一个宽 <c>erofs_inode_chunk_index</c> 表项的字节数。</para></summary>
    internal const int ChunkIndexEntrySize = 8;

    /// <summary>Size in bytes of one narrow block-map entry.
    /// <para>一个窄块映射表项的字节数。</para></summary>
    internal const int BlockMapEntrySize = 4;

    /// <summary>Mask of the file-type nibble in <c>i_mode</c>.
    /// <para><c>i_mode</c> 中文件类型半字节的掩码。</para></summary>
    internal const ushort ModeTypeMask = 0xF000;

    /// <summary>Directory file type in <c>i_mode</c>. <para><c>i_mode</c> 中的目录类型。</para></summary>
    internal const ushort ModeDirectory = 0x4000;

    /// <summary>Regular file type in <c>i_mode</c>. <para><c>i_mode</c> 中的常规文件类型。</para></summary>
    internal const ushort ModeRegularFile = 0x8000;

    /// <summary>Symlink file type in <c>i_mode</c>. <para><c>i_mode</c> 中的符号链接类型。</para></summary>
    internal const ushort ModeSymlink = 0xA000;

    /// <summary>Character device type in <c>i_mode</c>. <para><c>i_mode</c> 中的字符设备类型。</para></summary>
    internal const ushort ModeCharacterDevice = 0x2000;

    /// <summary>Block device type in <c>i_mode</c>. <para><c>i_mode</c> 中的块设备类型。</para></summary>
    internal const ushort ModeBlockDevice = 0x6000;

    /// <summary>FIFO file type in <c>i_mode</c>. <para><c>i_mode</c> 中的 FIFO 类型。</para></summary>
    internal const ushort ModeFifo = 0x1000;

    /// <summary>Socket file type in <c>i_mode</c>. <para><c>i_mode</c> 中的套接字类型。</para></summary>
    internal const ushort ModeSocket = 0xC000;

    /// <summary>Dirent file type of an unrecognised mode. <para>无法识别模式时的 dirent 文件类型。</para></summary>
    internal const byte FileTypeUnknown = 0;

    /// <summary>Dirent file type of a regular file. <para>常规文件的 dirent 文件类型。</para></summary>
    internal const byte FileTypeRegularFile = 1;

    /// <summary>Dirent file type of a directory. <para>目录的 dirent 文件类型。</para></summary>
    internal const byte FileTypeDirectory = 2;

    /// <summary>Dirent file type of a character device. <para>字符设备的 dirent 文件类型。</para></summary>
    internal const byte FileTypeCharacterDevice = 3;

    /// <summary>Dirent file type of a block device. <para>块设备的 dirent 文件类型。</para></summary>
    internal const byte FileTypeBlockDevice = 4;

    /// <summary>Dirent file type of a FIFO. <para>FIFO 的 dirent 文件类型。</para></summary>
    internal const byte FileTypeFifo = 5;

    /// <summary>Dirent file type of a socket. <para>套接字的 dirent 文件类型。</para></summary>
    internal const byte FileTypeSocket = 6;

    /// <summary>Dirent file type of a symbolic link. <para>符号链接的 dirent 文件类型。</para></summary>
    internal const byte FileTypeSymlink = 7;

    /// <summary>Parent inode in the tree, or <see langword="null"/> for the root.
    /// <para>树中的父 inode；根为 <see langword="null"/>。</para></summary>
    /// <value>Set while the tree is built; a decoded leaf stays <see langword="null"/> until it is linked.
    /// <para>构建树时设置；解码得到的叶子在被链接之前保持 <see langword="null"/>。</para></value>
    internal ErofsInode? Parent { get; set; }

    /// <summary>Child inodes of a directory in on-disk order, covering every directory entry
    /// (subdirectories, files, symlinks and special files).
    /// <para>目录的子 inode，按磁盘顺序，覆盖全部目录条目（子目录、文件、符号链接与特殊文件）。</para></summary>
    /// <value>Empty for non-directories. <para>非目录为空。</para></value>
    internal List<ErofsInode> Children { get; } = [];

    /// <summary>Entry name inside its parent directory, empty for the root.
    /// <para>在父目录中的条目名；根为空字符串。</para></summary>
    /// <value>The single path component, without separators. <para>不含分隔符的单个路径分量。</para></value>
    internal string Name { get; set; } = string.Empty;

    /// <summary>Source path this inode was built from (<c>i_srcpath</c>).
    /// <para>构建该 inode 所用的源路径（<c>i_srcpath</c>）。</para></summary>
    /// <value><see langword="null"/> for an inode with no source file. <para>无源文件的 inode 为
    /// <see langword="null"/>。</para></value>
    internal string? SourcePath { get; set; }

    /// <summary>Device this inode's payload belongs to (<c>dev</c>).
    /// <para>该 inode 负载所属设备（<c>dev</c>）。</para></summary>
    /// <value>Zero for the main image; a nonzero id is relative to the declared device table.
    /// <para>主镜像为零；非零 id 相对于已声明的设备表。</para></value>
    internal uint DeviceId { get; set; }

    /// <summary>Whether this inode came from the source image or was added by the build.
    /// <para>该 inode 来自源镜像还是由构建新增。</para></summary>
    /// <value>Fixed when the inode is created. <para>创建 inode 时确定。</para></value>
    internal ErofsInodeLayer Layer { get; init; } = ErofsInodeLayer.Source;

    /// <summary>Inode number of this record. <para>该记录的 inode 编号。</para></summary>
    /// <value>An index into the metadata inode table, or a METABOX-flagged slot for a resident inode.
    /// <para>元数据 inode 表中的索引，或常驻 inode 的 METABOX 标志槽位。</para></value>
    internal ulong Nid { get; set; }

    /// <summary>Raw <c>i_format</c> word. <para>原始 <c>i_format</c> 字。</para></summary>
    /// <value>The version, datalayout and shared nlink-one / dot-omitted bits exactly as stored.
    /// <para>按磁盘原样保存的版本、datalayout 与共用的 nlink-one / 省略点项位。</para></value>
    internal ushort IFormat { get; set; }

    /// <summary>Decoded data layout. <para>解码后的数据布局。</para></summary>
    /// <value>Derived from <see cref="IFormat"/>; kept as an explicit field so planning code does not
    /// re-derive bit offsets. <para>由 <see cref="IFormat"/> 导出；作为显式字段保存，使规划代码不必重复
    /// 推导位偏移。</para></value>
    internal ErofsDataLayout DataLayout { get; set; }

    /// <summary>Addressing state of the <c>u</c> field. <para><c>u</c> 字段的寻址状态。</para></summary>
    /// <value>A flat, compressed or chunk-based instance matching <see cref="DataLayout"/>.
    /// <para>与 <see cref="DataLayout"/> 对应的平坦、压缩或 chunk-based 实例。</para></value>
    internal IErofsIdata Idata { get; set; } = new ErofsFlatIdata();

    /// <summary>File size in bytes (<c>i_size</c>). <para>文件字节大小（<c>i_size</c>）。</para></summary>
    /// <value>32-bit in the compact record and 64-bit in the extended record.
    /// <para>紧凑记录中为 32 位，扩展记录中为 64 位。</para></value>
    internal ulong Size { get; set; }

    /// <summary>Owner user id. <para>属主用户 id。</para></summary>
    /// <value>16-bit in the compact record and 32-bit in the extended record.
    /// <para>紧凑记录中为 16 位，扩展记录中为 32 位。</para></value>
    internal uint Uid { get; set; }

    /// <summary>Owner group id. <para>属主组 id。</para></summary>
    /// <value>16-bit in the compact record and 32-bit in the extended record.
    /// <para>紧凑记录中为 16 位，扩展记录中为 32 位。</para></value>
    internal uint Gid { get; set; }

    /// <summary>On-disk inode number (<c>i_ino</c>). <para>磁盘 inode 编号（<c>i_ino</c>）。</para></summary>
    /// <value>Stored in both record widths. <para>两种记录宽度都存储。</para></value>
    internal uint Ino { get; set; }

    /// <summary>Compact-record mtime delta against the superblock epoch (<c>i_mtime</c>).
    /// <para>紧凑记录的相对超级块 epoch 的 mtime 增量（<c>i_mtime</c>）。</para></summary>
    /// <value>Only meaningful for the compact record; the extended record carries
    /// <see cref="CTime"/> instead. <para>仅紧凑记录有义；扩展记录改为携带 <see cref="CTime"/>。</para></value>
    internal uint Generation { get; set; }

    /// <summary>Extended-record change time seconds. <para>扩展记录的变更时间秒数。</para></summary>
    /// <value>Zero for a compact inode, whose effective time is resolved from
    /// <see cref="Generation"/> plus the superblock epoch. <para>紧凑 inode 为零，其生效时间由
    /// <see cref="Generation"/> 加超级块 epoch 解析得出。</para></value>
    internal ulong CTime { get; set; }

    /// <summary>Extended-record sub-second change time. <para>扩展记录的亚秒变更时间。</para></summary>
    /// <value>Zero for a compact inode, which shares the volume-wide fixed nanoseconds.
    /// <para>紧凑 inode 为零，其共用卷级固定纳秒值。</para></value>
    internal uint CTimeNsec { get; set; }

    /// <summary>Link count (<c>i_nlink</c>). <para>链接数（<c>i_nlink</c>）。</para></summary>
    /// <value>For a compact inode with the shared bit set and a non-directory mode this is one. A freshly
    /// created inode starts at one link, matching upstream's new-inode default; a decoded inode overwrites
    /// it from the record. <para>紧凑 inode 置有共用位且模式为文件时为 1。新建 inode 起始为一个链接，与上游的
    /// 新 inode 默认值一致；解码得到的 inode 会用记录覆盖它。</para></value>
    internal ulong Nlink { get; set; } = 1;

    /// <summary>Mode word (<c>i_mode</c>) including the file-type nibble.
    /// <para>模式字（<c>i_mode</c>），含文件类型半字节。</para></summary>
    /// <value>The full 16-bit mode, not only the permission bits.
    /// <para>完整 16 位模式，而非仅权限位。</para></value>
    internal ushort Mode { get; set; }

    /// <summary>Inline xattr record count (<c>i_xattr_icount</c>).
    /// <para>内联 xattr 记录数（<c>i_xattr_icount</c>）。</para></summary>
    /// <value>Zero when the inode carries no inline xattrs.
    /// <para>inode 不携带内联 xattr 时为零。</para></value>
    internal ushort XattrICount { get; set; }

    /// <summary>Size of the inode record itself. <para>inode 记录自身的大小。</para></summary>
    /// <value>32 for the compact record and 64 for the extended record (<c>inode_isize</c>).
    /// <para>紧凑记录为 32，扩展记录为 64（<c>inode_isize</c>）。</para></value>
    internal int InodeISize { get; set; }

    /// <summary>Size of the inline xattr area that follows the inode record (<c>xattr_isize</c>).
    /// <para>inode 记录之后内联 xattr 区的大小（<c>xattr_isize</c>）。</para></summary>
    /// <value><c>12 + 4 * (i_xattr_icount - 1)</c> for a nonzero count, otherwise zero.
    /// <para>计数非零时为 <c>12 + 4 * (i_xattr_icount - 1)</c>，否则为零。</para></value>
    internal int XattrISize { get; set; }

    /// <summary>Size of the extent index area attached to a compressed-full inode
    /// (<c>extent_isize</c>). <para>附加到 compressed-full inode 的 extent 索引区大小
    /// （<c>extent_isize</c>）。</para></summary>
    /// <value>Zero on a decoded inode, whose extent rows live in the metadata stream rather than in a
    /// counted area. <para>解码得到的 inode 为零，其 extent 行位于元数据流而不在计数区内。</para></value>
    internal int ExtentISize { get; set; }

    /// <summary>Whether the inode record is resident in the METABOX area
    /// (<c>in_metabox</c>). <para>inode 记录是否常驻 METABOX 区（<c>in_metabox</c>）。</para></summary>
    /// <value>Set by the caller that located the record, because the flag is not stored in the record
    /// itself. <para>由定位该记录的调用方设置，因为该标志不存储在记录自身中。</para></value>
    internal bool InMetabox { get; set; }

    /// <summary>Whether a directory omits its <c>.</c> and <c>..</c> entries
    /// (<c>dot_omitted</c>). <para>目录是否省略 <c>.</c> 与 <c>..</c> 条目（<c>dot_omitted</c>）。</para></summary>
    /// <value>Derived from the shared <c>i_format</c> bit on a directory.
    /// <para>由目录上共用的 <c>i_format</c> 位导出。</para></value>
    internal bool DotOmitted { get; set; }

    /// <summary>Whether the compact writer may set the shared nlink-one bit.
    /// <para>紧凑写入方是否可置起共用的 nlink-one 位。</para></summary>
    /// <value><see langword="false"/> makes every nlink value use the wide field, which older kernels that
    /// predate the bit require. The default is off because the bit is only defined by newer kernels, and a
    /// kernel that does not define it rejects the inode even though <see cref="AdviseAll"/> covers the bit;
    /// <c>ErofsBuilder</c> turns it on only for a caller that declares such a target through
    /// <c>ErofsOptions.NlinkOneAdviseSupported</c>. <para>为 <see langword="false"/> 时 nlink 一律使用宽字段，
    /// 这是早于该位的内核所要求的。默认为关闭，因为该位只由较新内核定义，未定义它的内核即使该位落在
    /// <see cref="AdviseAll"/> 之内也会拒绝该 inode；<c>ErofsBuilder</c> 仅对通过
    /// <c>ErofsOptions.NlinkOneAdviseSupported</c> 声明了此类目标的调用方开启它。</para></value>
    internal bool AllowNlinkOneBit { get; set; }

    /// <summary>Compressed map header advise word (<c>z_advise</c>).
    /// <para>压缩映射头 advise 字（<c>z_advise</c>）。</para></summary>
    /// <value>Zero until a map header is attached. <para>附加映射头之前为零。</para></value>
    internal ushort ZAdvise { get; set; }

    /// <summary>HEAD1 algorithm id (<c>z_algorithmtype[0]</c>).
    /// <para>HEAD1 算法 id（<c>z_algorithmtype[0]</c>）。</para></summary>
    /// <value>Zero for an extent-form inode, which carries no algorithm type.
    /// <para>extent 形态的 inode 为零，它不携带算法类型。</para></value>
    internal byte ZAlgorithmTypeHead1 { get; set; }

    /// <summary>HEAD2 algorithm id (<c>z_algorithmtype[1]</c>).
    /// <para>HEAD2 算法 id（<c>z_algorithmtype[1]</c>）。</para></summary>
    /// <value>Zero when no index row names a second algorithm.
    /// <para>没有索引行指名第二算法时为零。</para></value>
    internal byte ZAlgorithmTypeHead2 { get; set; }

    /// <summary>Absolute logical cluster size log2 (<c>z_lclusterbits</c>).
    /// <para>绝对逻辑簇大小 log2（<c>z_lclusterbits</c>）。</para></summary>
    /// <value>The block size shift plus the stored low nibble; zero until a map header is attached.
    /// <para>块大小位移加上存储的低半字节；附加映射头之前为零。</para></value>
    internal byte ZLclusterBits { get; set; }

    /// <summary>Physical blocks one pcluster spans (<c>z_physical_clusterblks</c>).
    /// <para>单个 pcluster 跨越的物理块数（<c>z_physical_clusterblks</c>）。</para></summary>
    /// <value>A build-time planning field that erofs-utils does not store in the map header, so a
    /// decoded inode leaves it zero and the planner fills it.
    /// <para>构建期规划字段，erofs-utils 并不把它写入映射头，因此解码得到的 inode 保持为零，由规划器填充。</para></value>
    internal byte ZPhysicalClusterBlks { get; set; }

    /// <summary>Extent row count of a compressed-full inode (<c>z_extents</c>).
    /// <para>compressed-full inode 的 extent 行数量（<c>z_extents</c>）。</para></summary>
    /// <value>Zero when the inode carries no extent index.
    /// <para>inode 不携带 extent 索引时为零。</para></value>
    internal ulong ZExtents { get; set; }

    /// <summary>Fragment offset, or the packed-inode offset of a whole-file fragment
    /// (<c>z_fragmentoff</c> / <c>fragmentoff</c>).
    /// <para>fragment 偏移，或整文件 fragment 在 packed inode 中的偏移
    /// （<c>z_fragmentoff</c> 与 <c>fragmentoff</c>）。</para></summary>
    /// <value>The full 64-bit packed offset for a whole-file fragment inode, otherwise the 32-bit
    /// <c>h_fragmentoff</c>. <para>整文件 fragment inode 为完整 64 位 packed 偏移，其余为 32 位
    /// <c>h_fragmentoff</c>。</para></value>
    internal long FragmentOff { get; set; }

    /// <summary>Whether the record uses the 64-byte extended form.
    /// <para>记录是否采用 64 字节扩展形式。</para></summary>
    /// <value>Derived from <see cref="IFormat"/>. <para>由 <see cref="IFormat"/> 导出。</para></value>
    internal bool IsExtended => (IFormat & FlagExtended) != 0;

    /// <summary>Size in bytes of the on-disk inode record. <para>磁盘 inode 记录的字节数。</para></summary>
    /// <value><see cref="ExtendedSize"/> or <see cref="CompactSize"/>.
    /// <para><see cref="ExtendedSize"/> 或 <see cref="CompactSize"/>。</para></value>
    internal int OnDiskSize => IsExtended ? ExtendedSize : CompactSize;

    /// <summary>Byte offset of the payload that follows the inode record and its xattrs.
    /// <para>inode 记录及其 xattr 之后负载的字节偏移。</para></summary>
    /// <value><see cref="OnDiskSize"/> plus <see cref="XattrISize"/>.
    /// <para><see cref="OnDiskSize"/> 加 <see cref="XattrISize"/>。</para></value>
    internal int InlineDataOffset => OnDiskSize + XattrISize;

    /// <summary>Whether this inode is a directory, or sets the directory type nibble.
    /// <para>该 inode 是否为目录，或设置目录类型半字节。</para></summary>
    /// <value>Compares or updates the file-type nibble of <see cref="Mode"/>; clearing only the
    /// directory type leaves any other type untouched.
    /// <para>比较或更新 <see cref="Mode"/> 的文件类型半字节；清除时只清除目录类型，不影响其它类型。</para></value>
    internal bool IsDirectory
    {
        get => (Mode & ModeTypeMask) == ModeDirectory;
        set => SetTypeNibble(ModeDirectory, value);
    }

    /// <summary>Whether this inode is a regular file. <para>该 inode 是否为常规文件。</para></summary>
    /// <value>Compares the file-type nibble of <see cref="Mode"/>.
    /// <para>比较 <see cref="Mode"/> 的文件类型半字节。</para></value>
    internal bool IsRegularFile => (Mode & ModeTypeMask) == ModeRegularFile;

    /// <summary>Whether this inode is a symbolic link, or sets the symbolic-link type nibble.
    /// <para>该 inode 是否为符号链接，或设置符号链接类型半字节。</para></summary>
    /// <value>Compares or updates the file-type nibble of <see cref="Mode"/>; clearing only the
    /// symbolic-link type leaves any other type untouched.
    /// <para>比较或更新 <see cref="Mode"/> 的文件类型半字节；清除时只清除符号链接类型，不影响其它类型。</para></value>
    internal bool IsSymlink
    {
        get => (Mode & ModeTypeMask) == ModeSymlink;
        set => SetTypeNibble(ModeSymlink, value);
    }

    /// <summary>Whether this inode is a device, FIFO or socket, or marks it as one.
    /// <para>该 inode 是否为设备、FIFO 或套接字，或把它标记为其中之一。</para></summary>
    /// <value>Compares the file-type nibble of <see cref="Mode"/> against the four special types; setting
    /// it uses the type recorded in <see cref="DeviceMode"/> when present, otherwise a character-device
    /// marker. <para>把 <see cref="Mode"/> 的文件类型半字节与四种特殊类型比较；置位时使用
    /// <see cref="DeviceMode"/> 记录的类型（若有），否则用字符设备标记。</para></value>
    internal bool IsDevice
    {
        get => (Mode & ModeTypeMask) is ModeCharacterDevice or ModeBlockDevice or ModeFifo or ModeSocket;
        set
        {
            if (value)
            {
                var recorded = (ushort)(DeviceMode & ModeTypeMask);
                Mode = (ushort)((Mode & ~ModeTypeMask) | (recorded != 0 ? recorded : ModeCharacterDevice));
            }
            else if (IsDevice)
            {
                Mode = (ushort)(Mode & ~ModeTypeMask);
            }
        }
    }

    /// <summary>Whether this inode is a character device. <para>该 inode 是否为字符设备。</para></summary>
    /// <value>Compares the file-type nibble of <see cref="Mode"/>.
    /// <para>比较 <see cref="Mode"/> 的文件类型半字节。</para></value>
    internal bool IsCharacterDevice => (Mode & ModeTypeMask) == ModeCharacterDevice;

    /// <summary>Whether this inode is a block device. <para>该 inode 是否为块设备。</para></summary>
    /// <value>Compares the file-type nibble of <see cref="Mode"/>.
    /// <para>比较 <see cref="Mode"/> 的文件类型半字节。</para></value>
    internal bool IsBlockDevice => (Mode & ModeTypeMask) == ModeBlockDevice;

    /// <summary>Whether this inode is a FIFO. <para>该 inode 是否为 FIFO。</para></summary>
    /// <value>Compares the file-type nibble of <see cref="Mode"/>.
    /// <para>比较 <see cref="Mode"/> 的文件类型半字节。</para></value>
    internal bool IsFifo => (Mode & ModeTypeMask) == ModeFifo;

    /// <summary>Whether this inode is a socket. <para>该 inode 是否为套接字。</para></summary>
    /// <value>Compares the file-type nibble of <see cref="Mode"/>.
    /// <para>比较 <see cref="Mode"/> 的文件类型半字节。</para></value>
    internal bool IsSocket => (Mode & ModeTypeMask) == ModeSocket;

    /// <summary>Dirent file type derived from <see cref="Mode"/>.
    /// <para>由 <see cref="Mode"/> 导出的 dirent 文件类型。</para></summary>
    /// <value>The matching <c>FileType*</c> constant, or <see cref="FileTypeUnknown"/>.
    /// <para>匹配的 <c>FileType*</c> 常量，或 <see cref="FileTypeUnknown"/>。</para></value>
    internal byte FileType
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

    /// <summary>Whether a compressed payload is stored. <para>是否存储压缩负载。</para></summary>
    /// <value>True for the full and compact-index compressed layouts.
    /// <para>完整与紧凑索引两种压缩布局为 true。</para></value>
    internal bool IsCompressed => DataLayout is ErofsDataLayout.Compressed or ErofsDataLayout.CompactIndexes;

    /// <summary>Whether the shared format bit means nlink == 1 for this inode.
    /// <para>共用的格式位对该 inode 是否表示 nlink == 1。</para></summary>
    /// <value>True when the bit is raised and the inode is not a directory.
    /// <para>该位置起且 inode 不是目录时为 true。</para></value>
    internal bool NlinkIsOne => (IFormat & AdviseBitNlinkOneOrDotOmitted) != 0 && !IsDirectory;

    /// <summary>Chunk format word of a chunk-based inode (<c>u.chunkformat</c>).
    /// <para>chunk-based inode 的 chunk 格式字（<c>u.chunkformat</c>）。</para></summary>
    /// <value>Zero for every other layout. <para>其它布局为零。</para></value>
    internal ushort ChunkFormat => Idata is ErofsChunkIdata chunk ? chunk.ChunkFormat : (ushort)0;

    /// <summary>Chunk size log2 in filesystem blocks of a chunk-based inode.
    /// <para>chunk-based inode 以文件系统块计的 chunk 大小 log2。</para></summary>
    /// <value>Zero for every other layout. <para>其它布局为零。</para></value>
    internal int ChunkFormatBlkBits => ChunkFormat & ChunkFormatBlkBitsMask;

    /// <summary>Whether a chunk-based inode uses 8-byte index entries.
    /// <para>chunk-based inode 是否使用 8 字节索引表项。</para></summary>
    /// <value>False for every other layout. <para>其它布局为 false。</para></value>
    internal bool UsesChunkIndexes => (ChunkFormat & ChunkFormatIndexes) != 0;

    /// <summary>Whether a chunk-based inode widens start blocks to 48 bits.
    /// <para>chunk-based inode 是否把首块加宽到 48 位。</para></summary>
    /// <value>False for every other layout. <para>其它布局为 false。</para></value>
    internal bool Chunk48Bit => (ChunkFormat & ChunkFormat48Bit) != 0;

    /// <summary>Original host inode number the node was collected from, which collapses hardlink
    /// aliases; zero when the node has no host inode identity.
    /// <para>收集节点所用的宿主 inode 编号，用于折叠硬链接别名；节点没有宿主 inode 身份时为零。</para></summary>
    internal ulong OriginalInode { get; set; }

    /// <summary>Host path the node was collected from. <para>收集该节点所用的宿主路径。</para></summary>
    internal string FullPath { get; set; } = string.Empty;

    /// <summary>Decoded target text of a symlink node. <para>符号链接节点解码出的目标文本。</para></summary>
    internal string SymlinkTarget { get; set; } = string.Empty;

    /// <summary>Byte offset of the inode record inside the metadata area once assigned.
    /// <para>分配完成后 inode 记录在元数据区内的字节偏移。</para></summary>
    internal long MetaOffset { get; set; }

    /// <summary>Inline payload tail stored beside the inode record.
    /// <para>存放在 inode 记录旁的内联负载尾部。</para></summary>
    internal byte[] InlineData { get; set; } = [];

    /// <summary>Number of data blocks a directory payload occupies.
    /// <para>目录负载占用的数据块数量。</para></summary>
    internal long DirBlocks { get; set; }

    /// <summary>Byte length of a directory payload. <para>目录负载的字节长度。</para></summary>
    internal long DirPayloadSize { get; set; }

    /// <summary>Whether a directory payload is stored inline beside its inode record.
    /// <para>目录负载是否内联存放在 inode 记录旁。</para></summary>
    internal bool DirectoryInline { get; set; }

    /// <summary>First block assigned to a directory payload, or <c>-1</c> before assignment.
    /// <para>分配给目录负载的首块，分配前为 <c>-1</c>。</para></summary>
    internal long DirBlockAddr { get; set; } = -1;

    /// <summary>First block assigned to flat data, or <c>-1</c> before assignment.
    /// <para>分配给平坦数据的首块，分配前为 <c>-1</c>。</para></summary>
    internal long FlatBlockAddr { get; set; } = -1;

    /// <summary>Number of flat data blocks. <para>平坦数据块数量。</para></summary>
    internal long FlatBlocks { get; set; }

    /// <summary>Compression options the payload planner selected for this node.
    /// <para>负载规划器为该节点选定的压缩选项。</para></summary>
    internal ErofsOptions? CompressionOptions { get; set; }

    /// <summary>Source inode this node replays, when the build is a preserved rebuild. The node itself is
    /// the carrier of the source strategy, so this is a reference to another instance of the same model
    /// rather than a separate record.
    /// <para>该节点重放的源 inode；仅在保留式重建中存在。源策略由该模型自身承载，因此这里是对同一模型另一个
    /// 实例的引用，而不是独立记录。</para></summary>
    /// <value>A decoded or restored source node, or <see langword="null"/> for a fresh build.
    /// <para>解码或还原得到的源节点；全新构建为 <see langword="null"/>。</para></value>
    internal ErofsInode? SourceInode { get; set; }

    /// <summary>Whether the source payload is stored compressed, recorded while a preserved source is
    /// decoded. This is a source-strategy fact written once by the parser and only read afterwards, never
    /// synchronised with <see cref="DataLayout"/>: the fragment form rewrites the flag to the packed
    /// inode's state, so it is deliberately allowed to differ from <see cref="IsCompressed"/>, which
    /// always describes this inode's own record.
    /// <para>源负载是否以压缩形式存放，在解码保留式源时记录。这是由解析器一次写入、之后只被读取的源策略事实，
    /// 绝不同 <see cref="DataLayout"/> 同步：fragment 形态会把它改写为 packed inode 的状态，因此它被有意允许
    /// 与始终描述本 inode 自身记录的 <see cref="IsCompressed"/> 不同。</para></summary>
    /// <value>Only meaningful on a node whose <see cref="Layer"/> is
    /// <see cref="ErofsInodeLayer.Source"/>; a build-added node leaves it at the default and the builder
    /// ignores it there. <para>仅在 <see cref="Layer"/> 为 <see cref="ErofsInodeLayer.Source"/> 的节点上有义；
    /// 构建新增节点保持默认值，构建器在那里忽略它。</para></value>
    internal bool SourceCompressed { get; set; }

    /// <summary>Compression algorithm the source payload was encoded with, recorded while a preserved
    /// source is decoded. The value cannot be derived from the decoded node: an extent-form inode carries
    /// its algorithm in the extent record's format field and never writes it into the map header, so
    /// <see cref="ZAlgorithmTypeHead1"/> stays zero for that form.
    /// <para>源负载编码所用的压缩算法，在解码保留式源时记录。该值无法由解码得到的节点推导：extent 形态的
    /// inode 把算法存放在 extent 记录的 format 字段中，从不写进映射头，因此该形态下
    /// <see cref="ZAlgorithmTypeHead1"/> 保持为零。</para></summary>
    /// <value>Only meaningful on a node whose <see cref="Layer"/> is
    /// <see cref="ErofsInodeLayer.Source"/>. <para>仅在 <see cref="Layer"/> 为
    /// <see cref="ErofsInodeLayer.Source"/> 的节点上有义。</para></value>
    internal ErofsCompressionAlgorithm SourceAlgorithm { get; set; }

    /// <summary>Placement shape of the source payload across the file's own blocks and the packed inode,
    /// recorded while a preserved source is decoded. The value cannot be derived from the decoded node:
    /// upstream decides between the whole-file and tail shapes by whether the packed inode is compressed,
    /// which is not part of this inode's record.
    /// <para>源负载在文件自身块与 packed inode 之间的存放形态，在解码保留式源时记录。该值无法由解码得到的
    /// 节点推导：上游依据 packed inode 是否压缩来区分整文件与尾部两种形态，而这不是本 inode 记录的一部分。</para></summary>
    /// <value>Only meaningful on a node whose <see cref="Layer"/> is
    /// <see cref="ErofsInodeLayer.Source"/>; <see cref="ErofsFragmentShape.None"/> means the payload sits
    /// entirely in the file's own blocks or pclusters.
    /// <para>仅在 <see cref="Layer"/> 为 <see cref="ErofsInodeLayer.Source"/> 的节点上有义；
    /// <see cref="ErofsFragmentShape.None"/> 表示负载完全位于文件自身的块或 pcluster 中。</para></value>
    internal ErofsFragmentShape SourceFragmentShape { get; set; }

    /// <summary>
    /// Whether <see cref="IFormat"/> holds the source inode's recorded format bits rather than a format the
    /// reader or the metadata restore synthesised from the record width. A preserved rebuild mirrors the
    /// per-inode advise bit only while this statement is set, so a profile that predates the recorded format
    /// keeps the image-level advice dimension and rebuilds byte-identically, while a profile that recorded it
    /// is replayed per inode instead of being resolved once for the whole image.
    /// <para>标记 <see cref="IFormat"/> 承载的是源 inode 记录的格式位，而不是读取方或元数据还原按记录宽度合成的
    /// 格式。保留式重建只在该陈述置起时逐 inode 镜像 advise 位，因此早于该记录的档案保持镜像级 advise 维度并
    /// 逐字节重建，而记录了该维度的档案按逐 inode 重放，而不是为整幅镜像解析一次。</para>
    /// </summary>
    /// <value>Only meaningful on a node whose <see cref="Layer"/> is
    /// <see cref="ErofsInodeLayer.Source"/>; a decoded source node keeps it set because the format was read from
    /// the image, and a restored node keeps it clear unless the profile recorded the format word.
    /// <para>仅在 <see cref="Layer"/> 为 <see cref="ErofsInodeLayer.Source"/> 的节点上有义；解码得到的源节点保持
    /// 置起（格式确实读自镜像），还原得到的节点则仅在档案记录了格式字时置起。</para></value>
    internal bool SourceIFormatRecorded { get; set; }

    /// <summary>Metadata creation form of the source record, recorded while a preserved source is decoded:
    /// whether the record lives in the plain metadata area, the METABOX container or the separate metadata
    /// zone, and whether it uses the compact or the extended width. The form cannot be re-derived from the
    /// record fields alone, because the METABOX location carried by nid bit 63 and the metazone relocation
    /// carried by the superblock's metadata block address are both outside the record.
    /// <para>源记录的元数据创建形态，在解码保留式源时记录：记录位于普通元数据区、METABOX 容器还是独立元数据区，
    /// 以及采用紧凑还是扩展宽度。该形态无法仅由记录字段重新推导，因为 nid 第 63 位承载的 METABOX 位置与超级块
    /// 元数据块地址承载的 metazone 迁移都不在记录之内。</para></summary>
    /// <value><see langword="null"/> on a node whose creation form was not recorded, which keeps the rebuild's
    /// previous derivation for that node. <para>未记录创建形态的节点为 <see langword="null"/>，重建对该节点保持
    /// 此前的推导方式。</para></value>
    internal ErofsInodeMetadataCreationType? MetadataCreationType { get; set; }

    /// <summary>Secondary algorithm the node's HEAD2 index rows name, or <see langword="null"/>.
    /// <para>该节点 HEAD2 索引行指名的第二算法，或 <see langword="null"/>。</para></summary>
    internal ErofsCompressionAlgorithm? SecondaryAlgorithm { get; set; }

    /// <summary>Encoded inline xattr body. <para>编码后的内联 xattr body。</para></summary>
    internal byte[] XattrIbody { get; set; } = [];

    /// <summary>Long-prefix records this node's xattr names registered, or <see langword="null"/>.
    /// <para>该节点 xattr 名称登记的长前缀记录，或 <see langword="null"/>。</para></summary>
    internal List<(byte BaseIndex, string Infix)>? XattrPrefixes { get; set; }

    /// <summary>Resolved xattr entries this node carries, or <see langword="null"/>.
    /// <para>该节点携带的已解析 xattr 条目，或 <see langword="null"/>。</para></summary>
    internal List<ErofsXattrEntry>? XattrEntries { get; set; }

    /// <summary>Shared xattr slot this node's ibody names, or <c>-1</c> for inline xattrs.
    /// <para>该节点 ibody 指名的共享 xattr 槽位；内联 xattr 为 <c>-1</c>。</para></summary>
    internal int SharedXattrId { get; set; } = -1;

    /// <summary>Raw <c>i_mode</c> a device node's host record carried.
    /// <para>设备节点的宿主记录携带的原始 <c>i_mode</c>。</para></summary>
    internal ushort DeviceMode { get; set; }

    /// <summary>Device number of a device node. <para>设备节点的设备号。</para></summary>
    internal uint DeviceRdev { get; set; }

    /// <summary>Permission bits recorded by a host metadata override.
    /// <para>宿主元数据覆盖记录的权限位。</para></summary>
    internal ushort ModeOverride { get; set; }

    /// <summary>Whether <see cref="ModeOverride"/> was recorded.
    /// <para>是否记录了 <see cref="ModeOverride"/>。</para></summary>
    internal bool HasModeOverride { get; set; }

    /// <summary>Whether the timestamp forces the 64-byte extended inode record.
    /// <para>时间戳是否强制使用 64 字节扩展 inode 记录。</para></summary>
    internal bool RequiresExtendedTime { get; set; }

    /// <summary>Record width the build was told to emit for this node, resolved from the build options
    /// during layout planning. Every measurement and the final serialization read this one field, so a
    /// forced format is visible to the planner before any index or payload size is derived from it.
    /// <para>构建被要求为该节点写出的记录宽度，在布局规划阶段由构建选项解析。所有度量与最终序列化都读取这一个
    /// 字段，因此强制形态在任何索引或负载尺寸由它推出之前就对规划器可见。</para></summary>
    /// <value><see cref="ErofsInodeFormat.Auto"/> lets the metadata decide; the other members force the
    /// named width or fail loudly when the fields do not fit it.
    /// <para><see cref="ErofsInodeFormat.Auto"/> 由元数据决定；其他成员强制所命名的宽度，字段装不下时显式报错。</para></value>
    internal ErofsInodeFormat InodeFormat { get; set; }

    /// <summary>Planner offset inside the packed inode, or <c>-1</c> when the node is not a fragment.
    /// <para>packed inode 内的规划偏移；节点不是 fragment 时为 <c>-1</c>。</para></summary>
    internal long FragmentOffset { get; set; } = -1;

    /// <summary>Whether the node's payload is placed as a fragment.
    /// <para>节点负载是否按 fragment 放置。</para></summary>
    internal bool IsFragment { get; set; }

    /// <summary>Whether only the node's final pcluster is placed in the shared fragment area while its
    /// remaining clusters stay in its own data blocks. This is the shape <c>mkfs.erofs -E fragments</c>
    /// produces for a file whose last compressed window fits one pcluster; the whole-file shape above is
    /// the separate case its map header marks with bit 63.
    /// <para>是否只把节点的末尾 pcluster 放入共享 fragment 区，其余簇仍留在自身的数据库块中。这是
    /// <c>mkfs.erofs -E fragments</c> 对「末尾压缩窗口能装进一个 pcluster」的文件所产生的形态；上面的整文件
    /// 形态是其映射头以第 63 位标记的另一种情况。</para></summary>
    /// <value><see langword="true"/> only for a node that keeps <see cref="Pclusters"/> non-empty and ends
    /// them with a fragment entry. <para>仅当节点保留非空 <see cref="Pclusters"/> 并以 fragment 条目结尾时为
    /// <see langword="true"/>。</para></value>
    internal bool TailFragment { get; set; }

    /// <summary>Content hash used by flat deduplication, or <see langword="null"/>.
    /// <para>平坦去重所用的内容哈希，或 <see langword="null"/>。</para></summary>
    internal byte[]? ContentHash { get; set; }

    /// <summary>Node whose payload this node reuses through deduplication, or <see langword="null"/>.
    /// <para>该节点经去重复用其负载的节点，或 <see langword="null"/>。</para></summary>
    internal ErofsInode? DedupSource { get; set; }

    /// <summary>Whether this node is the synthetic packed inode.
    /// <para>该节点是否为合成的 packed inode。</para></summary>
    internal bool IsPacked { get; set; }

    /// <summary>Fragments this node packs, or <see langword="null"/>.
    /// <para>该节点打包的 fragment，或 <see langword="null"/>。</para></summary>
    internal List<ErofsPackedFragment>? PackedFragments { get; set; }

    /// <summary>Whether the inode record lives in the METABOX area.
    /// <para>inode 记录是否位于 METABOX 区。</para></summary>
    internal bool IsMetaboxResident { get; set; }

    /// <summary>Whether this node is the synthetic METABOX container.
    /// <para>该节点是否为合成的 METABOX 容器。</para></summary>
    internal bool IsMetaboxContainer { get; set; }

    /// <summary>Slot reserved for a METABOX-resident node, or <c>-1</c> before assignment.
    /// <para>为 METABOX 常驻节点预留的槽位，分配前为 <c>-1</c>。</para></summary>
    internal long MetaboxSlot { get; set; } = -1;

    /// <summary>Planned pclusters of this node, stored in its compressed addressing state.
    /// <para>该节点已规划的 pcluster，存放在其压缩寻址状态中。</para></summary>
    internal ErofsPclusterPlan[]? Pclusters
    {
        get => CompressedState?.Pclusters;
        set
        {
            if (value is null && Idata is not ErofsCompressedIdata)
            {
                return;
            }

            EnsureCompressedState().Pclusters = value;
        }
    }

    /// <summary>Compressed block count of this node, stored in its compressed addressing state.
    /// <para>该节点的压缩块数量，存放在其压缩寻址状态中。</para></summary>
    internal uint CompressedBlocks
    {
        get => CompressedState?.Blocks ?? 0;
        set
        {
            if (value == 0 && Idata is not ErofsCompressedIdata)
            {
                return;
            }

            EnsureCompressedState().Blocks = value;
        }
    }

    /// <summary>Planned logical cluster count of this node.
    /// <para>该节点已规划的逻辑簇数量。</para></summary>
    internal uint ClusterCount
    {
        get => CompressedState?.ClusterCount ?? 0;
        set
        {
            if (value == 0 && Idata is not ErofsCompressedIdata)
            {
                return;
            }

            EnsureCompressedState().ClusterCount = value;
        }
    }

    /// <summary>Logical cluster size in bytes the planner selected.
    /// <para>规划器选定的逻辑簇字节大小。</para></summary>
    internal int LogicalClusterSize
    {
        get => CompressedState?.LogicalClusterSize ?? 0;
        set
        {
            if (value == 0 && Idata is not ErofsCompressedIdata)
            {
                return;
            }

            EnsureCompressedState().LogicalClusterSize = value;
        }
    }

    /// <summary>Whether the node's index uses the compact 4-byte packed form.
    /// <para>该节点的索引是否使用紧凑 4 字节打包形式。</para></summary>
    internal bool CompactedIndexes
    {
        get => CompressedState?.CompactedIndexes ?? false;
        set
        {
            if (value)
            {
                EnsureCompressedState().CompactedIndexes = true;
                DataLayout = ErofsDataLayout.CompactIndexes;
            }
            else if (CompressedState is { } compressed)
            {
                compressed.CompactedIndexes = false;
                DataLayout = ErofsDataLayout.Compressed;
            }
        }
    }

    /// <summary>Whether any plain index row is stored with the interlaced byte rotation.
    /// <para>是否有任一原样索引行按交错字节旋转存放。</para></summary>
    internal bool InterlacedPlainPclusters
    {
        get => CompressedState?.InterlacedPlainPclusters ?? false;
        set
        {
            if (value)
            {
                EnsureCompressedState().InterlacedPlainPclusters = true;
            }
            else if (CompressedState is { } compressed)
            {
                compressed.InterlacedPlainPclusters = false;
            }
        }
    }

    /// <summary>Whether any index row advertises a big pcluster.
    /// <para>是否有任一索引行声明了 big pcluster。</para></summary>
    internal bool BigPcluster
    {
        get => CompressedState?.BigPcluster ?? false;
        set
        {
            if (value)
            {
                EnsureCompressedState().BigPcluster = true;
            }
            else if (CompressedState is { } compressed)
            {
                compressed.BigPcluster = false;
            }
        }
    }

    /// <summary>Whether the final partial pcluster is stored beside the inode record.
    /// <para>最后不完整 pcluster 是否存放在 inode 记录旁。</para></summary>
    internal bool InlineCompressedTail
    {
        get => CompressedState?.InlineCompressedTail ?? false;
        set
        {
            if (value)
            {
                EnsureCompressedState().InlineCompressedTail = true;
            }
            else if (CompressedState is { } compressed)
            {
                compressed.InlineCompressedTail = false;
            }
        }
    }

    /// <summary>Whether the compact index carries the 2-byte packed tail rows.
    /// <para>紧凑索引是否携带 2 字节打包尾部行。</para></summary>
    internal bool Compacted2B
    {
        get => CompressedState?.Compiled2B ?? false;
        set
        {
            if (value)
            {
                EnsureCompressedState().Compiled2B = true;
            }
            else if (CompressedState is { } compressed)
            {
                compressed.Compiled2B = false;
            }
        }
    }

    /// <summary>Chunk index entries of this node, or <see langword="null"/> when it is not chunk-based.
    /// <para>该节点的 chunk 索引表项；不是 chunk-based 时为 <see langword="null"/>。</para></summary>
    internal ulong[]? ChunkIndex
    {
        get => ChunkState?.ChunkIndex;
        set
        {
            if (value is null && Idata is not ErofsChunkIdata)
            {
                return;
            }

            EnsureChunkState().ChunkIndex = value;
        }
    }

    private ErofsCompressedIdata? CompressedState => Idata as ErofsCompressedIdata;

    private ErofsChunkIdata? ChunkState => Idata as ErofsChunkIdata;

    private ErofsCompressedIdata EnsureCompressedState()
    {
        if (Idata is ErofsCompressedIdata compressed)
        {
            return compressed;
        }

        var created = new ErofsCompressedIdata
        {
            CompactedIndexes = DataLayout == ErofsDataLayout.CompactIndexes,
        };
        Idata = created;
        DataLayout = created.CompactedIndexes ? ErofsDataLayout.CompactIndexes : ErofsDataLayout.Compressed;
        return created;
    }

    private ErofsChunkIdata EnsureChunkState()
    {
        if (Idata is ErofsChunkIdata chunk)
        {
            return chunk;
        }

        var created = new ErofsChunkIdata();
        Idata = created;
        DataLayout = ErofsDataLayout.ChunkBased;
        return created;
    }

    /// <summary>Whether the addressing state and the decoded data-layout field agree.
    /// <para>寻址状态与解码出的数据布局字段是否一致。</para></summary>
    /// <value>Flat/inline layouts carry flat addressing state, compressed layouts carry compressed state
    /// whose compact-index flag matches the layout, and the chunk-based layout carries chunk state.
    /// <para>平坦/内联布局携带平坦寻址状态，压缩布局携带压缩状态且其紧凑索引标志与布局一致，
    /// chunk-based 布局携带 chunk 状态。</para></value>
    internal bool HasConsistentDatalayout => Idata switch
    {
        ErofsFlatIdata => DataLayout is ErofsDataLayout.Flat or ErofsDataLayout.Inline,
        ErofsCompressedIdata compressed => DataLayout is ErofsDataLayout.Compressed or ErofsDataLayout.CompactIndexes
            && compressed.CompactedIndexes == (DataLayout == ErofsDataLayout.CompactIndexes),
        ErofsChunkIdata => DataLayout == ErofsDataLayout.ChunkBased,
        _ => false,
    };

    private ushort ComposeFormat()
        => (ushort)((IFormat & ~DataLayoutMask) | (ushort)((byte)DataLayout << DataLayoutShift));
    private void SetTypeNibble(ushort type, bool value)
    {
        if (value)
        {
            Mode = (ushort)((Mode & ~ModeTypeMask) | type);
        }
        else if ((Mode & ModeTypeMask) == type)
        {
            Mode = (ushort)(Mode & ~ModeTypeMask);
        }
    }

    /// <summary>
    /// Decodes one inode record into the unified model, deriving the addressing state from the decoded
    /// data layout. The block size shift is needed for the flat block span and for the absolute logical
    /// cluster size the map header stores relative to the block size.
    /// <para>把一条 inode 记录解码为统一模型，并从解码出的数据布局导出寻址状态。块大小位移用于平坦块跨度，
    /// 以及映射头相对块大小存储的绝对逻辑簇大小。</para>
    /// </summary>
    /// <param name="data">Inode bytes beginning with the two-byte little-endian <c>i_format</c> word; the
    /// extended layout needs <see cref="ExtendedSize"/> bytes and the compact layout
    /// <see cref="CompactSize"/>. <para>起始于 2 字节小端 <c>i_format</c> 字的 inode 字节；扩展布局需要
    /// <see cref="ExtendedSize"/> 字节，紧凑布局需要 <see cref="CompactSize"/> 字节。</para></param>
    /// <param name="nid">Inode number of the record, which the record itself does not store.
    /// <para>该记录的 inode 编号，记录本身不存储它。</para></param>
    /// <param name="blockSizeBits">Log2 of the filesystem block size.
    /// <para>文件系统块大小的 log2。</para></param>
    /// <param name="inMetabox">Whether the caller located the record in the METABOX area.
    /// <para>调用方是否在 METABOX 区定位到该记录。</para></param>
    /// <returns>The decoded inode with its flat, compressed or chunk-based addressing state.
    /// <para>解码得到的 inode，携带平坦、压缩或 chunk-based 寻址状态。</para></returns>
    /// <remarks>A flat-inline inode carries no data block address, and a 48bit source leaves the null-address
    /// high half (<c>0xFFFF</c>) in <c>i_nb</c>; that field is unused by the layout, so a nonzero value is only
    /// rejected for layouts that really address data blocks. <para>flat-inline inode 不携带数据块地址，48bit 源会在
    /// <c>i_nb</c> 中留下空地址的高半部（<c>0xFFFF</c>）；该字段在此布局中不使用，因此只有真正以块地址寻址数据的布局
    /// 才会拒绝非零值。</para></remarks>
    /// <exception cref="FormatException">The region is too short, raises undefined <c>i_format</c> bits, or
    /// carries a nonzero <c>i_nb</c> upper address half outside the 32-bit block window this reader models
    /// on a layout that uses block addresses.
    /// <para>区域过短、置有未定义的 <c>i_format</c> 位，或在使用块地址的布局上携带非零的 <c>i_nb</c> 地址高半部、
    /// 超出本读取器所建模的 32 位块窗口。</para></exception>
    internal static ErofsInode FromBytes(ReadOnlySpan<byte> data, ulong nid, int blockSizeBits, bool inMetabox = false)
    {
        if (data.Length < 2)
        {
            throw new FormatException("Inode region too short.");
        }

        uint rawAddressField;

        var format = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if ((format & ~AdviseAll) != 0)
        {
            throw new FormatException($"EROFS inode uses undefined i_format bits 0x{format & ~AdviseAll:X4}.");
        }

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        var isDirectory = (mode & ModeTypeMask) == ModeDirectory;
        var datalayout = (ErofsDataLayout)((format & DataLayoutMask) >> DataLayoutShift);
        var storesWideAddress = (format & FlagExtended) != 0
            || (format & AdviseBitNlinkOneOrDotOmitted) != 0 && !isDirectory;
        var wideAddressHighHalf = storesWideAddress && data.Length >= 8
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[6..])
            : (ushort)0;

        if (wideAddressHighHalf != 0 && datalayout != ErofsDataLayout.Inline)
        {
            throw new FormatException(
                $"EROFS inode i_nb 0x{wideAddressHighHalf:X4} extends the address past the 32-bit block window this reader models.");
        }

        var inode = new ErofsInode
        {
            Nid = nid,
            IFormat = format,
            DataLayout = datalayout,
            XattrICount = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            Mode = mode,
            InMetabox = inMetabox,
        };

        if ((format & FlagExtended) != 0)
        {
            if (data.Length < ExtendedSize)
            {
                throw new FormatException($"Extended inode region too short: {data.Length} bytes.");
            }

            inode.InodeISize = ExtendedSize;
            inode.Size = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
            rawAddressField = ReadAddressField(data);
            inode.Ino = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
            inode.Uid = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
            inode.Gid = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
            inode.CTime = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);
            inode.CTimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]);
            inode.Nlink = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]);
        }
        else
        {
            if (data.Length < CompactSize)
            {
                throw new FormatException($"Compact inode region too short: {data.Length} bytes.");
            }

            inode.InodeISize = CompactSize;
            var narrowLinkCount = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            var nlinkIsOne = (format & AdviseBitNlinkOneOrDotOmitted) != 0 && !isDirectory;
            inode.Nlink = nlinkIsOne ? 1u : narrowLinkCount;
            inode.Size = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            inode.Generation = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            rawAddressField = ReadAddressField(data);
            inode.Ino = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
            inode.Uid = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
            inode.Gid = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]);
        }

        inode.XattrISize = inode.XattrICount == 0 ? 0 : 12 + 4 * (inode.XattrICount - 1);
        inode.SourceIFormatRecorded = true;
        inode.DotOmitted = (inode.Mode & ModeTypeMask) == ModeDirectory
            && (format & AdviseBitNlinkOneOrDotOmitted) != 0;
        inode.Idata = CreateIdataForLayout(inode.DataLayout, rawAddressField, inode.Size, blockSizeBits);
        return inode;
    }

    /// <summary>
    /// Creates the addressing state an inode with the given data layout carries, so callers that decode
    /// or convert records share one mapping from layout to the <c>u</c> interpretation.
    /// <para>创建给定数据布局的 inode 所携带的寻址状态，使解码或转换记录的调用方共用同一份
    /// 由布局到 <c>u</c> 解释的映射。</para>
    /// </summary>
    /// <param name="layout">Decoded data layout. <para>解码后的数据布局。</para></param>
    /// <param name="addressField">Raw value stored in the four-byte <c>u</c> field.
    /// <para><c>u</c> 四字节字段中存放的原始值。</para></param>
    /// <param name="size">File size in bytes, used to derive the flat block span.
    /// <para>文件字节大小，用于导出平坦块跨度。</para></param>
    /// <param name="blockSizeBits">Log2 of the filesystem block size.
    /// <para>文件系统块大小的 log2。</para></param>
    /// <returns>The flat, compressed or chunk-based addressing state.
    /// <para>平坦、压缩或 chunk-based 寻址状态。</para></returns>
    internal static IErofsIdata CreateIdataForLayout(ErofsDataLayout layout, uint addressField, ulong size, int blockSizeBits)
    {
        switch (layout)
        {
            case ErofsDataLayout.Compressed:
            case ErofsDataLayout.CompactIndexes:
                return new ErofsCompressedIdata { Blocks = addressField };
            case ErofsDataLayout.ChunkBased:
                return new ErofsChunkIdata { ChunkFormat = (ushort)addressField };
            default:
                var blockSize = 1UL << blockSizeBits;
                var blocks = layout == ErofsDataLayout.Inline
                    ? size / blockSize
                    : (size + blockSize - 1) / blockSize;
                return new ErofsFlatIdata
                {
                    BlockAddress = addressField,
                    Blocks = (uint)Math.Min(blocks, uint.MaxValue),
                };
        }
    }

    /// <summary>
    /// Returns the value the addressing state stores back into the four-byte <c>u</c> field.
    /// <para>返回寻址状态写回 <c>u</c> 四字节字段的值。</para>
    /// </summary>
    /// <returns>The block address, compressed block count or chunk format word.
    /// <para>块地址、压缩块数量或 chunk 格式字。</para></returns>
    internal uint AddressField() => Idata switch
    {
        ErofsFlatIdata flat => flat.BlockAddress,
        ErofsCompressedIdata compressed => compressed.Blocks,
        ErofsChunkIdata chunk => chunk.ChunkFormat,
        _ => 0,
    };

    /// <summary>
    /// Writes the on-disk fields into a buffer of at least <see cref="OnDiskSize"/> bytes, selecting the
    /// compact or extended record from <see cref="IFormat"/> exactly as the reader selected it.
    /// <para>把磁盘字段写入至少 <see cref="OnDiskSize"/> 字节的缓冲区，按读取方选择的同一依据从
    /// <see cref="IFormat"/> 选定紧凑或扩展记录。</para>
    /// </summary>
    /// <param name="destination">Destination span of at least <see cref="OnDiskSize"/> bytes; the span is
    /// cleared before the record is written little-endian.
    /// <para>至少 <see cref="OnDiskSize"/> 字节的目标区间；写入小端记录前先整体清零。</para></param>
    /// <exception cref="ArgumentException">The destination is smaller than the record.
    /// <para>目标区间小于记录大小。</para></exception>
    internal void WriteTo(Span<byte> destination)
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

    /// <summary>
    /// Decodes the <c>z_erofs_map_header</c> that follows the inode record and stores it in the
    /// <c>Z*</c> fields, expanding a compressed inode's addressing state with the index-form flags.
    /// <para>解码 inode 记录之后的 <c>z_erofs_map_header</c> 并存入 <c>Z*</c> 字段，同时用索引形态标志
    /// 扩充压缩 inode 的寻址状态。</para>
    /// </summary>
    /// <param name="headerBytes">Raw map-header bytes, at least eight bytes.
    /// <para>原始映射头字节，至少 8 字节。</para></param>
    /// <param name="blockSizeBits">Log2 of the filesystem block size, needed to make the stored logical
    /// cluster nibble absolute. <para>文件系统块大小的 log2，用于把存储的逻辑簇半字节还原为绝对值。</para></param>
    /// <exception cref="FormatException">The header region is too short.
    /// <para>映射头区域过短。</para></exception>
    internal void AttachMapHeader(ReadOnlySpan<byte> headerBytes, int blockSizeBits)
    {
        var header = ZErofsMapHeader.FromBytes(headerBytes);
        ZAdvise = header.Advise;
        ZLclusterBits = (byte)(blockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask));

        if (header.IsFragmentInode)
        {
            ZAdvise = ZErofsMapHeader.AdviseFragmentPcluster;
            FragmentOff = (long)(BinaryPrimitives.ReadUInt64LittleEndian(headerBytes) & ~(1UL << 63));
            return;
        }

        if (DataLayout == ErofsDataLayout.Compressed && header.HasExtents)
        {
            ZExtents = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes)
                | ((ulong)header.ExtentsHi << 32);
            if (Idata is ErofsCompressedIdata extents)
            {
                extents.HasExtents = true;
                extents.ExtentRecordSize = 4 << ((header.Advise >> ZErofsMapHeader.ExtRecSizeBit)
                    & ZErofsMapHeader.ExtRecSizeMask);
                extents.ExtentCount = ZExtents;
            }

            return;
        }

        ZAlgorithmTypeHead1 = (byte)(header.AlgorithmType & ZErofsMapHeader.AlgorithmTypeMask);
        ZAlgorithmTypeHead2 = (byte)((header.AlgorithmType >> 4) & ZErofsMapHeader.AlgorithmTypeMask);
        if (header.HasFragmentPcluster)
        {
            FragmentOff = header.FragmentOff;
        }

        if (Idata is ErofsCompressedIdata compressed)
        {
            compressed.CompactedIndexes = DataLayout == ErofsDataLayout.CompactIndexes;
            compressed.Compiled2B = compressed.CompactedIndexes && header.HasCompacted2B;
            if (compressed.CompactedIndexes && header.HasBigPcluster1 != header.HasBigPcluster2)
            {
                throw new FileSystemCorruptedException(
                    $"EROFS compact-index inode declares inconsistent big-pcluster heads at nid {Nid}.");
            }

            compressed.BigPcluster = header.HasBigPcluster1 || header.HasBigPcluster2;
            compressed.InlineCompressedTail = header.HasInlinePcluster;
        }
    }

    /// <summary>
    /// Decodes a chunk index table into <see cref="ErofsChunkIdata.ChunkIndex"/>; the entry width is
    /// taken from the chunk format word, so the caller only supplies the bytes that follow the aligned
    /// index start. <para>把 chunk 索引表解码进 <see cref="ErofsChunkIdata.ChunkIndex"/>；表项宽度取自
    /// chunk 格式字，因此调用方只需提供对齐索引起点之后的字节。</para>
    /// </summary>
    /// <param name="indexTable">Index table bytes; a trailing partial entry is ignored.
    /// <para>索引表字节；末尾不完整的表项被忽略。</para></param>
    /// <exception cref="InvalidOperationException">The inode does not carry chunk-based addressing.
    /// <para>该 inode 不携带 chunk-based 寻址。</para></exception>
    internal void AttachChunkIndex(ReadOnlySpan<byte> indexTable)
    {
        if (Idata is not ErofsChunkIdata chunk)
        {
            throw new InvalidOperationException("Chunk index table attached to a non-chunk inode.");
        }

        var wide = UsesChunkIndexes;
        var entrySize = wide ? ChunkIndexEntrySize : BlockMapEntrySize;
        var count = indexTable.Length / entrySize;
        var entries = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            var slot = indexTable.Slice(i * entrySize, entrySize);
            var start = wide
                ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(slot[4..])
                    | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(slot) << 32)
                : BinaryPrimitives.ReadUInt32LittleEndian(slot);
            entries[i] = start;
        }

        chunk.ChunkIndex = entries;
    }

    /// <summary>
    /// Returns a shallow copy of this node, used where an edit plan overlays a different timestamp on a
    /// source strategy that is shared with other nodes: the overlay must not rewrite the shared instance
    /// the source dictionaries still hand out.
    /// <para>返回该节点的浅拷贝，用于编辑计划在与其他节点共享的源策略上覆盖不同时间戳的场合：覆盖不得改写
    /// 源字典仍会分发的共享实例。</para>
    /// </summary>
    /// <returns>A copy carrying the same field values and the same child list reference.
    /// <para>携带相同字段值与相同子列表引用的副本。</para></returns>
    internal ErofsInode Clone() => (ErofsInode)MemberwiseClone();

    private static uint ReadAddressField(ReadOnlySpan<byte> data)
        => BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);

    private void WriteCompact(Span<byte> destination)
    {
        var nlinkOneBit = AllowNlinkOneBit && !IsDirectory && Nlink == 1;
        if (nlinkOneBit)
        {
            IFormat |= AdviseBitNlinkOneOrDotOmitted;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, ComposeFormat());
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], XattrICount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Mode);

        var narrowLinkCount = nlinkOneBit ? (ushort)0 : (ushort)Math.Min(Nlink, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], narrowLinkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)Math.Min(Size, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], AddressField());
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], Ino);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[24..], (ushort)Math.Min(Uid, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[26..], (ushort)Math.Min(Gid, ushort.MaxValue));
    }

    private void WriteExtended(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, ComposeFormat());
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], XattrICount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Mode);

        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], AddressField());
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], Ino);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], Gid);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], CTime);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], CTimeNsec);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..], (uint)Math.Min(Nlink, uint.MaxValue));
    }

    private uint RawAddressField() => Idata switch
    {
        ErofsFlatIdata flat => flat.BlockAddress,
        ErofsCompressedIdata compressed => compressed.Blocks,
        ErofsChunkIdata chunk => chunk.ChunkFormat,
        _ => 0,
    };
}
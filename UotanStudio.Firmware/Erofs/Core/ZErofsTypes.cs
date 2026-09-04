using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// erofs 压缩算法标识，与内核 fs/erofs/erofs_fs.h 的 <c>Z_EROFS_COMPRESSION_*</c> 一一对应
/// （编码于 z_erofs_map_header.h_algorithmtype 的低 4 位，掩码 <see cref="ZErofsMapHeader.AlgorithmTypeMask"/>）。
/// <para>EROFS compression algorithm identifiers matching <c>Z_EROFS_COMPRESSION_*</c> in the
/// kernel's fs/erofs/erofs_fs.h (stored in the low 4 bits of
/// <c>z_erofs_map_header.h_algorithmtype</c>, masked by
/// <see cref="ZErofsMapHeader.AlgorithmTypeMask"/>).</para>
/// </summary>
internal enum ErofsAlgorithm : byte
{
    /// <summary>LZ4（<c>Z_EROFS_COMPRESSION_LZ4 = 0</c>）。</summary>
    Lz4 = 0,

    /// <summary>LZMA（<c>Z_EROFS_COMPRESSION_LZMA = 1</c>）。</summary>
    Lzma = 1,

    /// <summary>DEFLATE（<c>Z_EROFS_COMPRESSION_DEFLATE = 2</c>）。</summary>
    Deflate = 2,

    /// <summary>Zstandard（<c>Z_EROFS_COMPRESSION_ZSTD = 3</c>）。</summary>
    Zstd = 3,
}

/// <summary>
/// 8 字节 z_erofs_map_header（内核 fs/erofs/erofs_fs.h 的紧凑定义，仅支持全量/legacy full 索引，
/// 不覆盖 extent 元数据）。
/// <code>
/// struct z_erofs_map_header {
///     union {
///         __le32 h_fragmentoff;              /* bytes 0-3 */
///         struct { __le16 h_reserved1; __le16 h_idata_size; };
///     };
///     __le16 h_advise;                       /* bytes 4-5 */
///     union {
///         struct { __u8 h_algorithmtype; __u8 h_clusterbits; }; /* bytes 6-7 */
///         __le16 h_extents_hi;
///     };
/// } __packed;
/// </code>
/// <para>The 8-byte z_erofs_map_header (the packed definition from the kernel's erofs_fs.h,
/// covering the legacy/full index form; extent metadata is not modelled).</para>
/// </summary>
internal readonly struct ZErofsMapHeader
{
    /// <summary>结构体大小（8 字节）。Struct size (8 bytes).</summary>
    public const int Size = 8;

    // ---- h_advise 位。h_advise bit flags. ----
    /// <summary>紧凑 2B 索引（compact 2-byte index）：<c>Z_EROFS_ADVISE_COMPACTED_2B = 0x0001</c>。</summary>
    public const ushort AdviseCompacted2B = 0x0001;

    /// <summary>extent 元数据（full 索引，big pcluster）：<c>Z_EROFS_ADVISE_EXTENTS = 0x0001</c>
    /// （与 <see cref="AdviseCompacted2B"/> 同值，由上下文区分）。</summary>
    public const ushort AdviseExtents = 0x0001;

    /// <summary>大 pcluster 物理块高 1 位：<c>Z_EROFS_ADVISE_BIG_PCLUSTER_1 = 0x0002</c>。</summary>
    public const ushort AdviseBigPcluster1 = 0x0002;

    /// <summary>大 pcluster 物理块高 2 位：<c>Z_EROFS_ADVISE_BIG_PCLUSTER_2 = 0x0004</c>。</summary>
    public const ushort AdviseBigPcluster2 = 0x0004;

    /// <summary>内联 pcluster（尾部打包）：<c>Z_EROFS_ADVISE_INLINE_PCLUSTER = 0x0008</c>。</summary>
    public const ushort AdviseInlinePcluster = 0x0008;

    /// <summary>交错 pcluster：<c>Z_EROFS_ADVISE_INTERLACED_PCLUSTER = 0x0010</c>。</summary>
    public const ushort AdviseInterlacedPcluster = 0x0010;

    /// <summary>fragment pcluster：<c>Z_EROFS_ADVISE_FRAGMENT_PCLUSTER = 0x0020</c>。</summary>
    public const ushort AdviseFragmentPcluster = 0x0020;

    // ---- extent 记录大小位（zmap.c EXTRECSZ_*）。Extent-record-size bits (zmap.c EXTRECSZ_*). ----
    /// <summary>extent 记录大小位的起始位移：<c>EXTRECSZ_BIT = 1</c>。</summary>
    public const int ExtRecSizeBit = 1;

    /// <summary>extent 记录大小位的掩码：<c>EXTRECSZ_MASK = 0x3</c>。</summary>
    public const int ExtRecSizeMask = 0x3;

    // ---- h_clusterbits 位。h_clusterbits bits. ----
    /// <summary>逻辑簇 bits 相对 blkszbits 偏移量的掩码（低 4 位）。Mask of the lcluster-bits offset
    /// relative to blkszbits (low 4 bits).</summary>
    public const byte LclusterBitsMask = 0x0F;

    /// <summary>整文件打包到 packed inode 的位序号：<c>Z_EROFS_FRAGMENT_INODE_BIT = 7</c>。</summary>
    public const int FragmentInodeBit = 7;

    /// <summary>整文件打包到 packed inode 的位掩码（bit 7）：<c>1 &lt;&lt; Z_EROFS_FRAGMENT_INODE_BIT</c>。</summary>
    public const byte FragmentInodeMask = 0x80;

    // ---- h_algorithmtype 掩码。h_algorithmtype masks. ----
    /// <summary>算法类型掩码（低 4 位）。Algorithm-type mask (low 4 bits).</summary>
    public const byte AlgorithmTypeMask = 0x0F;

    /// <summary>全部已知压缩算法位图：<c>Z_EROFS_ALL_COMPR_ALGS = 0x0F</c>。</summary>
    public const byte AllCompressionAlgorithms = 0x0F;

    private readonly uint _fragmentOff;

    /// <summary>h_fragmentoff（字节 0-3 小端，fragment 偏移；低 16 位即 h_reserved1）。
    /// h_fragmentoff (bytes 0-3, little-endian fragment offset; the low 16 bits double as
    /// h_reserved1).</summary>
    public uint FragmentOff => _fragmentOff;

    /// <summary>h_reserved1（字节 0-1）。h_reserved1 (bytes 0-1).</summary>
    public ushort Reserved1 => (ushort)(_fragmentOff & 0xFFFF);

    /// <summary>h_idata_size（字节 2-3，内联数据大小）。h_idata_size (bytes 2-3, inline data size).</summary>
    public ushort IdataSize => (ushort)(_fragmentOff >> 16);

    /// <summary>h_advise（字节 4-5）。h_advise (bytes 4-5).</summary>
    public ushort Advise { get; }

    /// <summary>h_algorithmtype 原始字节（字节 6，低 4 位为算法）。Raw h_algorithmtype byte
    /// (byte 6; the low 4 bits hold the algorithm).</summary>
    public byte AlgorithmType { get; }

    /// <summary>h_clusterbits（字节 7：低 4 位为逻辑簇 bits 相对 blkszbits 偏移，bit 7 为
    /// fragment-inode 标记）。h_clusterbits (byte 7: low 4 bits = lcluster-bits offset relative to
    /// blkszbits, bit 7 = fragment-inode flag).</summary>
    public byte ClusterBits { get; }

    /// <summary>h_extents_hi（字节 6-7 小端，extent 元数据高位；与 algorithmtype/clusterbits 共用存储）。
    /// h_extents_hi (bytes 6-7, little-endian high bits of the extent table; aliased with
    /// algorithmtype/clusterbits).</summary>
    public ushort ExtentsHi => (ushort)((AlgorithmType << 8) | ClusterBits);

    /// <summary>算法类型（低 4 位掩码后映射到 <see cref="ErofsAlgorithm"/>）。Algorithm type
    /// (low 4 bits masked and mapped to <see cref="ErofsAlgorithm"/>).</summary>
    public ErofsAlgorithm Algorithm => (ErofsAlgorithm)(AlgorithmType & AlgorithmTypeMask);

    // ---- advise 便捷判断。Advise convenience flags. ----
    /// <summary>是否紧凑 2B 索引。Whether the compact 2B index is used.</summary>
    public bool HasCompacted2B => (Advise & AdviseCompacted2B) != 0;

    /// <summary>是否 extent 元数据（full 索引）。Whether extent metadata (full index) is used.</summary>
    public bool HasExtents => (Advise & AdviseExtents) != 0;

    /// <summary>是否大 pcluster（物理块数超 1）。Whether the pcluster spans multiple physical blocks.</summary>
    public bool HasBigPcluster1 => (Advise & AdviseBigPcluster1) != 0;

    /// <summary>是否大 pcluster 的高 2 位标志。Whether the big-pcluster high-2-bits flag is set.</summary>
    public bool HasBigPcluster2 => (Advise & AdviseBigPcluster2) != 0;

    /// <summary>是否内联 pcluster（尾部打包）。Whether the pcluster is inline (tail-packed).</summary>
    public bool HasInlinePcluster => (Advise & AdviseInlinePcluster) != 0;

    /// <summary>是否交错 pcluster。Whether the pcluster is interlaced.</summary>
    public bool HasInterlacedPcluster => (Advise & AdviseInterlacedPcluster) != 0;

    /// <summary>是否 fragment pcluster。Whether the pcluster references a fragment.</summary>
    public bool HasFragmentPcluster => (Advise & AdviseFragmentPcluster) != 0;

    /// <summary>逻辑簇 bits 相对 blkszbits 的偏移（低 4 位）。
    /// Offset of the lcluster bits relative to blkszbits (low 4 bits).</summary>
    public int LclusterBitsOffset => ClusterBits & LclusterBitsMask;

    /// <summary>整文件是否打包到 packed inode（bit 7，Z_EROFS_FRAGMENT_INODE）。
    /// Whether the whole file is packed into the packed inode (bit 7, Z_EROFS_FRAGMENT_INODE).</summary>
    public bool IsFragmentInode => (ClusterBits & FragmentInodeMask) != 0;

    /// <summary>
    /// 以 fragment 偏移形式构造（h_fragmentoff 占满字节 0-3）。
    /// <para>Creates a header using the fragment-offset form (h_fragmentoff occupies bytes 0-3).</para>
    /// </summary>
    public ZErofsMapHeader(uint fragmentOff, ushort advise, byte algorithmType, byte clusterBits)
    {
        _fragmentOff = fragmentOff;
        Advise = advise;
        AlgorithmType = algorithmType;
        ClusterBits = clusterBits;
    }

    /// <summary>
    /// 以内联数据形式构造（h_reserved1 字节 0-1，h_idata_size 字节 2-3）。
    /// <para>Creates a header using the inline-data form (h_reserved1 at bytes 0-1, h_idata_size at
    /// bytes 2-3).</para>
    /// </summary>
    public ZErofsMapHeader(ushort reserved1, ushort idataSize, ushort advise, byte algorithmType, byte clusterBits)
        : this(reserved1 | ((uint)idataSize << 16), advise, algorithmType, clusterBits)
    {
    }

    /// <summary>
    /// 从字节解析（小端，至少 8 字节）。
    /// <para>Parses the header from bytes (little-endian, at least 8 bytes).</para>
    /// </summary>
    public static ZErofsMapHeader FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new FormatException("z_erofs_map_header region is too short.");
        }

        return new ZErofsMapHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)),
            data[6],
            data[7]);
    }

    /// <summary>
    /// 将头部写入缓冲区（至少 8 字节）。
    /// <para>Writes the header into a buffer of at least 8 bytes.</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("z_erofs_map_header destination buffer is too small.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, _fragmentOff);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4), Advise);
        destination[6] = AlgorithmType;
        destination[7] = ClusterBits;
    }
}

/// <summary>
/// 8 字节 z_erofs_lcluster_index（legacy full 索引条目，内核 fs/erofs/erofs_fs.h）。
/// <code>
/// struct z_erofs_lcluster_index {
///     __le16 di_advise;                  /* bytes 0-1 */
///     __le16 di_clusterofs;              /* bytes 2-3 */
///     union {
///         __le32 blkaddr;                /* bytes 4-7, full index: physical block address */
///         __le16 delta[2];               /* bytes 4-7, NONHEAD: delta[0]/delta[1] */
///     };
/// } __packed;
/// </code>
/// <para>The 8-byte z_erofs_lcluster_index (legacy/full index entry, from the kernel's erofs_fs.h).
/// For NONHEAD entries <c>delta[0]</c> is the distance to the head lcluster and <c>delta[1]</c> the
/// distance to the next head lcluster.</para>
/// </summary>
internal readonly struct ZErofsLclusterIndex
{
    /// <summary>条目大小（8 字节）。Entry size (8 bytes).</summary>
    public const int Size = 8;

    // ---- di_advise 位。di_advise bit flags. ----
    /// <summary>部分引用（NONHEAD 引用前一个 pcluster 的共享数据）：<c>Z_EROFS_LI_PARTIAL_REF = 1 &lt;&lt; 15</c>。</summary>
    public const ushort LiPartialRef = 1 << 15;

    /// <summary>空洞（逻辑簇没有对应物理数据）：<c>Z_EROFS_LI_HOLE = 1 &lt;&lt; 14</c>。</summary>
    public const ushort LiHole = 1 << 14;

    /// <summary>逻辑簇类型掩码（低 2 位）：<c>Z_EROFS_LI_LCLUSTER_TYPE_MASK = 0x3</c>。</summary>
    public const ushort LiTypeMask = 0x0003;

    /// <summary>首个 NONHEAD 条目的 delta[0] 记录压缩块数：<c>Z_EROFS_LI_D0_CBLKCNT = 1 &lt;&lt; 11</c>。</summary>
    public const ushort LiD0CblkCnt = 1 << 11;

    // ---- 逻辑簇类型。Lcluster types. ----
    /// <summary>普通逻辑簇（PLAIN，逐簇独立压缩）：<c>Z_EROFS_LCLUSTER_TYPE_PLAIN = 0</c>。</summary>
    public const ushort LclusterTypePlain = 0;

    /// <summary>pcluster 头逻辑簇（HEAD1）：<c>Z_EROFS_LCLUSTER_TYPE_HEAD1 = 1</c>。</summary>
    public const ushort LclusterTypeHead1 = 1;

    /// <summary>非头逻辑簇（NONHEAD，共享所在 pcluster）：<c>Z_EROFS_LCLUSTER_TYPE_NONHEAD = 2</c>。</summary>
    public const ushort LclusterTypeNonHead = 2;

    /// <summary>HEAD2（delta 形式，通常与 INTERLACED 相关）：<c>Z_EROFS_LCLUSTER_TYPE_HEAD2 = 3</c>。</summary>
    public const ushort LclusterTypeHead2 = 3;

    /// <summary>di_advise（字节 0-1）。di_advise (bytes 0-1).</summary>
    public ushort Advise { get; }

    /// <summary>di_clusterofs（字节 2-3，逻辑簇内偏移）。di_clusterofs (bytes 2-3, offset within the
    /// logical cluster).</summary>
    public ushort ClusterOfs { get; }

    /// <summary>联合字段（字节 4-7）：blkaddr 或 delta[0..1]。Union field (bytes 4-7): blkaddr or
    /// delta[0..1].</summary>
    public uint Value { get; }

    /// <summary>物理块地址（full 索引）。Physical block address (full index).</summary>
    public uint Blkaddr => Value;

    /// <summary>NONHEAD 条目：到 HEAD 的距离（delta[0]，低 16 位）。NONHEAD entries: distance to the
    /// head lcluster (delta[0], low 16 bits).</summary>
    public ushort Delta0 => (ushort)(Value & 0xFFFF);

    /// <summary>NONHEAD 条目：到下一个 HEAD 的距离（delta[1]，高 16 位）。NONHEAD entries: distance to
    /// the next head lcluster (delta[1], high 16 bits).</summary>
    public ushort Delta1 => (ushort)(Value >> 16);

    /// <summary>逻辑簇类型（低 2 位）。Logical cluster type (low 2 bits).</summary>
    public ushort LclusterType => (ushort)(Advise & LiTypeMask);

    /// <summary>是否普通逻辑簇（PLAIN）。Whether this is a plain (independently compressed) lcluster.</summary>
    public bool IsPlain => LclusterType == LclusterTypePlain;

    /// <summary>是否 pcluster 头逻辑簇（HEAD1）。Whether this is a head lcluster (HEAD1).</summary>
    public bool IsHead1 => LclusterType == LclusterTypeHead1;

    /// <summary>是否非头逻辑簇（NONHEAD）。Whether this is a non-head lcluster (NONHEAD).</summary>
    public bool IsNonHead => LclusterType == LclusterTypeNonHead;

    /// <summary>是否 HEAD2。Whether this is a HEAD2 lcluster.</summary>
    public bool IsHead2 => LclusterType == LclusterTypeHead2;

    /// <summary>是否部分引用（Z_EROFS_LI_PARTIAL_REF）。Whether the partial-ref flag is set.</summary>
    public bool IsPartial => (Advise & LiPartialRef) != 0;

    /// <summary>是否空洞（Z_EROFS_LI_HOLE）。Whether the hole flag is set.</summary>
    public bool IsHole => (Advise & LiHole) != 0;

    /// <summary>delta[0] 是否记录压缩块数（Z_EROFS_LI_D0_CBLKCNT）。Whether delta[0] stores the
    /// compressed block count.</summary>
    public bool HasD0CblkCnt => (Advise & LiD0CblkCnt) != 0;

    /// <summary>
    /// 以 full 索引形式构造（blkaddr 占字节 4-7）。
    /// <para>Creates the entry in full-index form (blkaddr at bytes 4-7).</para>
    /// </summary>
    public ZErofsLclusterIndex(ushort advise, ushort clusterOfs, uint blkaddr)
    {
        Advise = advise;
        ClusterOfs = clusterOfs;
        Value = blkaddr;
    }

    /// <summary>
    /// 以 NONHEAD delta 形式构造（delta[0] 字节 4-5，delta[1] 字节 6-7）。
    /// <para>Creates the entry in NONHEAD delta form (delta[0] at bytes 4-5, delta[1] at bytes
    /// 6-7).</para>
    /// </summary>
    public ZErofsLclusterIndex(ushort advise, ushort clusterOfs, ushort delta0, ushort delta1)
        : this(advise, clusterOfs, (uint)(delta0 | (delta1 << 16)))
    {
    }

    /// <summary>
    /// 从字节解析（小端，至少 8 字节）。
    /// <para>Parses the entry from bytes (little-endian, at least 8 bytes).</para>
    /// </summary>
    public static ZErofsLclusterIndex FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new FormatException("z_erofs_lcluster_index region is too short.");
        }

        return new ZErofsLclusterIndex(
            BinaryPrimitives.ReadUInt16LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)));
    }

    /// <summary>
    /// 将条目写入缓冲区（至少 8 字节）。
    /// <para>Writes the entry into a buffer of at least 8 bytes.</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("z_erofs_lcluster_index destination buffer is too small.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, Advise);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), ClusterOfs);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4), Value);
    }
}
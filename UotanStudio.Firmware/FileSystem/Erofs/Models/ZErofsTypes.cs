using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Erofs.Models;

internal enum ErofsAlgorithm : byte
{
    Lz4 = 0,

    Lzma = 1,

    Deflate = 2,

    Zstd = 3,
}

internal readonly struct ZErofsMapHeader
{
    public const int Size = 8;

    public const ushort AdviseCompacted2B = 0x0001;

    public const ushort AdviseExtents = 0x0001;

    public const ushort AdviseBigPcluster1 = 0x0002;

    public const ushort AdviseBigPcluster2 = 0x0004;

    public const ushort AdviseInlinePcluster = 0x0008;

    public const ushort AdviseInterlacedPcluster = 0x0010;

    public const ushort AdviseFragmentPcluster = 0x0020;

    public const int ExtRecSizeBit = 1;

    public const int ExtRecSizeMask = 0x3;

    public const uint ExtentPlenPartial = 1u << 27;

    public const int ExtentPlenFmtBit = 28;

    public const uint ExtentPlenMask = (1u << 21) - 1;

    public const byte LclusterBitsMask = 0x0F;

    public const int FragmentInodeBit = 7;

    public const byte FragmentInodeMask = 0x80;

    public const byte AlgorithmTypeMask = 0x0F;

    public const byte AllCompressionAlgorithms = 0x0F;

    private readonly uint _fragmentOff;

    public uint FragmentOff => _fragmentOff;

    public ushort Reserved1 => (ushort)(_fragmentOff & 0xFFFF);

    public ushort IdataSize => (ushort)(_fragmentOff >> 16);

    public ushort Advise { get; }

    public byte AlgorithmType { get; }

    public byte ClusterBits { get; }

    public ushort ExtentsHi => (ushort)(AlgorithmType | ClusterBits << 8);

    public ErofsAlgorithm Algorithm => (ErofsAlgorithm)(AlgorithmType & AlgorithmTypeMask);

    public bool HasCompacted2B => (Advise & AdviseCompacted2B) != 0;

    public bool HasExtents => (Advise & AdviseExtents) != 0;

    public bool HasBigPcluster1 => (Advise & AdviseBigPcluster1) != 0;

    public bool HasBigPcluster2 => (Advise & AdviseBigPcluster2) != 0;

    public bool HasInlinePcluster => (Advise & AdviseInlinePcluster) != 0;

    public bool HasInterlacedPcluster => (Advise & AdviseInterlacedPcluster) != 0;

    public bool HasFragmentPcluster => (Advise & AdviseFragmentPcluster) != 0;

    public int LclusterBitsOffset => ClusterBits & LclusterBitsMask;

    public bool IsFragmentInode => (ClusterBits & FragmentInodeMask) != 0;

    /// <summary>
    /// Creates a header using the fragment-offset form (h_fragmentoff occupies bytes 0-3).
    /// <para>以 fragment 偏移形式构造（h_fragmentoff 占满字节 0-3）。</para>
    /// </summary>
    /// <param name="fragmentOff">h_fragmentoff written to bytes 0-3 and exposed as
    /// <see cref="FragmentOff"/>. <para>写入字节 0-3 并由 <see cref="FragmentOff"/> 暴露的 h_fragmentoff。</para></param>
    /// <param name="advise">h_advise feature-advice flags at bytes 4-5. <para>字节 4-5 的 h_advise 特性建议标志。</para></param>
    /// <param name="algorithmType">h_type at byte 6; the low nibble selects <see cref="Algorithm"/> and the
    /// upper nibble belongs to <see cref="ExtentsHi"/>.
    /// <para>字节 6 的 h_type；低四位决定 <see cref="Algorithm"/>，高四位属于 <see cref="ExtentsHi"/>。</para></param>
    /// <param name="clusterBits">h_clusterbits at byte 7; the low nibble is <see cref="LclusterBitsOffset"/>
    /// and the high bit marks <see cref="IsFragmentInode"/>.
    /// <para>字节 7 的 h_clusterbits；低四位是 <see cref="LclusterBitsOffset"/>，最高位标记 <see cref="IsFragmentInode"/>。</para></param>
    public ZErofsMapHeader(uint fragmentOff, ushort advise, byte algorithmType, byte clusterBits)
    {
        _fragmentOff = fragmentOff;
        Advise = advise;
        AlgorithmType = algorithmType;
        ClusterBits = clusterBits;
    }

    /// <summary>
    /// Creates a header using the inline-data form (h_reserved1 at bytes 0-1, h_idata_size at bytes 2-3).
    /// <para>以内联数据形式构造（h_reserved1 字节 0-1，h_idata_size 字节 2-3）。</para>
    /// </summary>
    /// <param name="reserved1">h_reserved1 written to bytes 0-1 and exposed as <see cref="Reserved1"/>.
    /// <para>写入字节 0-1 并由 <see cref="Reserved1"/> 暴露的 h_reserved1。</para></param>
    /// <param name="idataSize">h_idata_size written to bytes 2-3 and exposed as <see cref="IdataSize"/>.
    /// <para>写入字节 2-3 并由 <see cref="IdataSize"/> 暴露的 h_idata_size。</para></param>
    /// <param name="advise">h_advise feature-advice flags at bytes 4-5. <para>字节 4-5 的 h_advise 特性建议标志。</para></param>
    /// <param name="algorithmType">h_type at byte 6; the low nibble selects <see cref="Algorithm"/> and the
    /// upper nibble belongs to <see cref="ExtentsHi"/>.
    /// <para>字节 6 的 h_type；低四位决定 <see cref="Algorithm"/>，高四位属于 <see cref="ExtentsHi"/>。</para></param>
    /// <param name="clusterBits">h_clusterbits at byte 7; the low nibble is <see cref="LclusterBitsOffset"/>
    /// and the high bit marks <see cref="IsFragmentInode"/>.
    /// <para>字节 7 的 h_clusterbits；低四位是 <see cref="LclusterBitsOffset"/>，最高位标记 <see cref="IsFragmentInode"/>。</para></param>
    public ZErofsMapHeader(ushort reserved1, ushort idataSize, ushort advise, byte algorithmType, byte clusterBits)
        : this(reserved1 | ((uint)idataSize << 16), advise, algorithmType, clusterBits)
    {
    }

    /// <summary>
    /// Parses the header from bytes (little-endian, at least 8 bytes).
    /// <para>从字节解析（小端，至少 8 字节）。</para>
    /// </summary>
    /// <param name="data">Bytes of one <c>z_erofs_map_header</c> record, at least <see cref="Size"/> long.
    /// <para>一条 <c>z_erofs_map_header</c> 记录的字节，长度至少为 <see cref="Size"/>。</para></param>
    /// <returns>Header built from the fragment-offset form, so bytes 0-3 become <see cref="FragmentOff"/>.
    /// <para>以 fragment 偏移形式构造的头部，即字节 0-3 成为 <see cref="FragmentOff"/>。</para></returns>
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
    /// Writes the header into a buffer of at least 8 bytes.
    /// <para>将头部写入缓冲区（至少 8 字节）。</para>
    /// </summary>
    /// <param name="destination">Buffer receiving the record; fewer than <see cref="Size"/> writable bytes are rejected.
    /// <para>接收该记录的缓冲区；可写字节少于 <see cref="Size"/> 时被拒绝。</para></param>
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

internal readonly struct ZErofsLclusterIndex
{
    public const int Size = 8;

    public const ushort LiPartialRef = 1 << 15;

    public const ushort LiHole = 1 << 14;

    public const ushort LiTypeMask = 0x0003;

    public const ushort LiD0CblkCnt = 1 << 11;

    public const ushort LclusterTypePlain = 0;

    public const ushort LclusterTypeHead1 = 1;

    public const ushort LclusterTypeNonHead = 2;

    public const ushort LclusterTypeHead2 = 3;

    public ushort Advise { get; }

    public ushort ClusterOfs { get; }

    public uint Value { get; }

    public uint Blkaddr => Value;

    public ushort Delta0 => (ushort)(Value & 0xFFFF);

    public ushort Delta1 => (ushort)(Value >> 16);

    public ushort LclusterType => (ushort)(Advise & LiTypeMask);

    public bool IsPlain => LclusterType == LclusterTypePlain;

    public bool IsHead1 => LclusterType == LclusterTypeHead1;

    public bool IsNonHead => LclusterType == LclusterTypeNonHead;

    public bool IsHead2 => LclusterType == LclusterTypeHead2;

    public bool IsPartial => (Advise & LiPartialRef) != 0;

    public bool IsHole => (Advise & LiHole) != 0;

    public bool HasD0CblkCnt => (Advise & LiD0CblkCnt) != 0;

    /// <summary>
    /// Creates the entry in full-index form (blkaddr at bytes 4-7).
    /// <para>以 full 索引形式构造（blkaddr 占字节 4-7）。</para>
    /// </summary>
    /// <param name="advise">li_advise at bytes 0-1, carrying the lcluster type, partial-reference and hole
    /// bits. <para>字节 0-1 的 li_advise，携带 lcluster 类型、partial 引用与 hole 标志位。</para></param>
    /// <param name="clusterOfs">li_cluster_ofs at bytes 2-3. <para>字节 2-3 的 li_cluster_ofs。</para></param>
    /// <param name="blkaddr">li_daddr written to bytes 4-7 and exposed as <see cref="Blkaddr"/>.
    /// <para>写入字节 4-7 并由 <see cref="Blkaddr"/> 暴露的 li_daddr。</para></param>
    public ZErofsLclusterIndex(ushort advise, ushort clusterOfs, uint blkaddr)
    {
        Advise = advise;
        ClusterOfs = clusterOfs;
        Value = blkaddr;
    }

    /// <summary>
    /// Creates the entry in NONHEAD delta form (delta[0] at bytes 4-5, delta[1] at bytes 6-7).
    /// <para>以 NONHEAD delta 形式构造（delta[0] 字节 4-5，delta[1] 字节 6-7）。</para>
    /// </summary>
    /// <param name="advise">li_advise at bytes 0-1, carrying the lcluster type, partial-reference and hole
    /// bits. <para>字节 0-1 的 li_advise，携带 lcluster 类型、partial 引用与 hole 标志位。</para></param>
    /// <param name="clusterOfs">li_cluster_ofs at bytes 2-3. <para>字节 2-3 的 li_cluster_ofs。</para></param>
    /// <param name="delta0">li_delta[0] written to bytes 4-5; with <see cref="LiD0CblkCnt"/> set it holds the
    /// compressed block count instead of a delta.
    /// <para>写入字节 4-5 的 li_delta[0]；置位 <see cref="LiD0CblkCnt"/> 时改为存放压缩块数量。</para></param>
    /// <param name="delta1">li_delta[1] written to bytes 6-7. <para>写入字节 6-7 的 li_delta[1]。</para></param>
    public ZErofsLclusterIndex(ushort advise, ushort clusterOfs, ushort delta0, ushort delta1)
        : this(advise, clusterOfs, (uint)(delta0 | (delta1 << 16)))
    {
    }

    /// <summary>
    /// Parses the entry from bytes (little-endian, at least 8 bytes).
    /// <para>从字节解析（小端，至少 8 字节）。</para>
    /// </summary>
    /// <param name="data">Bytes of one <c>z_erofs_lcluster_index</c> record, at least <see cref="Size"/> long.
    /// <para>一条 <c>z_erofs_lcluster_index</c> 记录的字节，长度至少为 <see cref="Size"/>。</para></param>
    /// <returns>Entry whose bytes 0-1 hold the advise flags, bytes 2-3 the cluster offset and bytes 4-7 the full <c>blkaddr</c> value.
    /// <para>字节 0-1 为 advise 标志、字节 2-3 为簇偏移、字节 4-7 为完整 <c>blkaddr</c> 值的条目。</para></returns>
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
    /// Writes the entry into a buffer of at least 8 bytes.
    /// <para>将条目写入缓冲区（至少 8 字节）。</para>
    /// </summary>
    /// <param name="destination">Buffer receiving the record; fewer than <see cref="Size"/> writable bytes are rejected.
    /// <para>接收该记录的缓冲区；可写字节少于 <see cref="Size"/> 时被拒绝。</para></param>
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
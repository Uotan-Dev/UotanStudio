using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;

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


    // z_erofs_extent plen encoding (fs/erofs/erofs_fs.h): low bits hold the encoded length,
    // bit 27 marks a partially-referenced pcluster, bits 28+ encode (algorithm + 1).
    // z_erofs_extent 的 plen 编码（fs/erofs/erofs_fs.h）：低位为编码长度，位 27 表示部分引用，
    // 位 28 起编码（算法号 + 1）。
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

    public ushort ExtentsHi => (ushort)((AlgorithmType << 8) | ClusterBits);

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
    public ZErofsMapHeader(ushort reserved1, ushort idataSize, ushort advise, byte algorithmType, byte clusterBits)
        : this(reserved1 | ((uint)idataSize << 16), advise, algorithmType, clusterBits)
    {
    }

    /// <summary>
    /// Parses the header from bytes (little-endian, at least 8 bytes).
    /// <para>从字节解析（小端，至少 8 字节）。</para>
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
    /// Writes the header into a buffer of at least 8 bytes.
    /// <para>将头部写入缓冲区（至少 8 字节）。</para>
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
    public ZErofsLclusterIndex(ushort advise, ushort clusterOfs, ushort delta0, ushort delta1)
        : this(advise, clusterOfs, (uint)(delta0 | (delta1 << 16)))
    {
    }

    /// <summary>
    /// Parses the entry from bytes (little-endian, at least 8 bytes).
    /// <para>从字节解析（小端，至少 8 字节）。</para>
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
    /// Writes the entry into a buffer of at least 8 bytes.
    /// <para>将条目写入缓冲区（至少 8 字节）。</para>
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
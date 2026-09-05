using System.Buffers;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

public sealed partial class ErofsParser
{
    /// <c>z_erofs_load_full_lcluster</c>.</para>
    /// </summary>
    private LclusterEntry LoadLcluster(
        ulong nid,
        ErofsInode inode,
        long headerOffset,
        ushort advise,
        int lclusterBits,
        int totalIdx,
        long lcn,
        out long ebase,
        out int compacted4bInitial,
        out int compacted2b)
    {
        // ebase：紧凑索引表起始（map header 之后）。ebase: start of the compact index table.
        ebase = headerOffset + ZErofsMapHeader.Size;
        if (inode.DataLayout == ErofsInode.DataLayoutCompactIndexes)
        {
            compacted4bInitial = (int)(((32 - ebase % 32) / 4) & 7);
            compacted2b = 0;
            if ((advise & ZErofsMapHeader.AdviseCompacted2B) != 0 && compacted4bInitial < totalIdx)
            {
                compacted2b = (totalIdx - compacted4bInitial) / 16 * 16;
            }

            return LoadCompactLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, compacted4bInitial, compacted2b, lcn);
        }

        compacted4bInitial = 0;
        compacted2b = 0;
        return LoadFullLcluster(inode, headerOffset, lclusterBits, lcn);
    }

    /// <summary>
    /// 解码紧凑索引中的单个逻辑簇条目（位级），移植自内核
    /// <c>z_erofs_load_compact_lcluster</c> + <c>decode_compactedbits</c>，含 big-pcluster 回看分支。
    /// <para>Decodes a single logical-cluster entry from the compact index (bit-level), ported from the
    /// kernel <c>z_erofs_load_compact_lcluster</c> + <c>decode_compactedbits</c>, including the
    /// big-pcluster lookback branch.</para>
    /// </summary>
    private LclusterEntry LoadCompactLcluster(
        ulong nid,
        ErofsInode inode,
        long headerOffset,
        ushort advise,
        int lclusterBits,
        int totalIdx,
        int compacted4bInitial,
        int compacted2b,
        long lcn)
    {
        var bigPcluster = (advise & (ZErofsMapHeader.AdviseBigPcluster1 | ZErofsMapHeader.AdviseBigPcluster2)) != 0;
        var amortizedShift = 2; // compact_4b：每簇 4 字节
        var pos = headerOffset + ZErofsMapHeader.Size;
        var l = lcn;
        if (l >= compacted4bInitial)
        {
            pos += compacted4bInitial * 4;
            l -= compacted4bInitial;
            if (l < compacted2b)
            {
                amortizedShift = 1; // compact_2b：每簇 2 字节
            }
            else
            {
                pos += compacted2b * 2;
                l -= compacted2b;
            }
        }

        pos += l * (1 << amortizedShift);

        int vcnt;
        if ((1 << amortizedShift) == 4 && lclusterBits <= 14)
        {
            vcnt = 2;
        }
        else if ((1 << amortizedShift) == 2 && lclusterBits <= 12)
        {
            vcnt = 16;
        }
        else
        {
            throw new InvalidDataException(
                $"Unsupported compact index layout (shift {amortizedShift}, lcluster bits {lclusterBits}).");
        }

        var packSize = vcnt << amortizedShift;
        var bytesInPack = (int)(pos & (packSize - 1));
        var i = bytesInPack >> amortizedShift;
        var packStartRel = (int)(pos - (headerOffset + ZErofsMapHeader.Size) - bytesInPack);
        if (packStartRel < 0)
        {
            throw new InvalidDataException("Compact index pack exceeds the image bounds.");
        }

        var ebase = headerOffset + ZErofsMapHeader.Size;
        var indexLength = checked((long)totalIdx * 4 + 64);
        if (indexLength > MaxMetadataComponentSize)
        {
            throw new InvalidDataException(
                $"Compact index table size {indexLength} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }

        var index = ReadBytes(ebase, checked((int)Math.Min(indexLength, _imageSize - ebase)), "compact index table");
        if (packStartRel + packSize > index.Length)
        {
            throw new InvalidDataException("Compact index pack exceeds the image bounds.");
        }

        var lobits = Math.Max(lclusterBits, 12); // ilog2(Z_EROFS_LI_D0_CBLKCNT) + 1 = 11 + 1
        var encodebits = ((packSize - 4) * 8) / vcnt;
        var (lo, type) = DecodeCompactedBits(index, packStartRel, lobits, encodebits * i);
        if (type == LclusterTypeNonHead)
        {
            // NONHEAD：clusterofs = 1<<lclusterbits 哨兵；delta[0] 指向 HEAD 的距离。
            // NONHEAD: clusterofs = 1<<lclusterbits sentinel; delta[0] = distance to the HEAD.
            var clusterOfs = 1 << lclusterBits;
            if ((lo & (1u << 11)) != 0)
            {
                // D0_CBLKCNT 标志（big pcluster 首个 NONHEAD 存储压缩块数）。
                // D0_CBLKCNT flag: the first NONHEAD of a big pcluster stores the compressed block count.
                return new LclusterEntry(type, clusterOfs, delta0: 1, delta1: 0, pblk: 0, compressedBlocks: (int)(lo & ~(1u << 11)));
            }

            if (i + 1 == vcnt)
            {
                // pack 内最后一个条目存的是 delta[1]；delta[0] 由前一条目间接推出。
                // The last entry of the pack stores delta[1] instead; derive delta[0] from the previous one.
                var (prevLo, prevType) = DecodeCompactedBits(index, packStartRel, lobits, encodebits * (i - 1));
                if (prevType != LclusterTypeNonHead)
                {
                    prevLo = 0;
                }
                else if ((prevLo & (1u << 11)) != 0)
                {
                    prevLo = 1;
                }

                return new LclusterEntry(type, clusterOfs, delta0: prevLo + 1, delta1: lo, pblk: 0, compressedBlocks: 0);
            }

            return new LclusterEntry(type, clusterOfs, delta0: lo, delta1: 0, pblk: 0, compressedBlocks: 0);
        }

        // HEAD / PLAIN：clusterofs = lo；pblk = pack 尾部 blkaddr + 本 pack 内回看累计的物理块数。
        // HEAD / PLAIN: clusterofs = lo; pblk = trailing blkaddr of the pack + lookback block count.
        var clusterOfsH = (int)lo;
        long nblk;
        var j = i;
        if (!bigPcluster)
        {
            nblk = 1;
            while (j > 0)
            {
                j--;
                var (prevLo2, prevType2) = DecodeCompactedBits(index, packStartRel, lobits, encodebits * j);
                if (prevType2 == LclusterTypeNonHead)
                {
                    j -= (int)prevLo2;
                }

                if (j >= 0)
                {
                    nblk++;
                }
            }
        }
        else
        {
            // big-pcluster 回看：NONHEAD 带 CBLKCNT 时直接累加压缩块数，否则按 delta 回跳。
            // Big-pcluster lookback: NONHEAD with CBLKCNT accumulates compressed blocks; otherwise
            // jump back by delta.
            nblk = 0;
            while (j > 0)
            {
                j--;
                var (prevLo3, prevType3) = DecodeCompactedBits(index, packStartRel, lobits, encodebits * j);
                if (prevType3 == LclusterTypeNonHead)
                {
                    if ((prevLo3 & (1u << 11)) != 0)
                    {
                        j--;
                        nblk += prevLo3 & ~(1u << 11);
                        continue;
                    }

                    // big-pcluster 不允许普通 d0 == 1；按 d0 - 2 回跳。
                    // Big pclusters must not have plain d0 == 1; jump back by d0 - 2.
                    if (prevLo3 <= 1)
                    {
                        throw new InvalidDataException($"Corrupt big-pcluster delta for nid {nid}.");
                    }

                    j -= (int)prevLo3 - 2;
                    continue;
                }

                nblk++;
            }
        }

        var packBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(packStartRel + packSize - 4, 4));
        var pblk = packBlkaddr + nblk;
        return new LclusterEntry(type, clusterOfsH, delta0: 0, delta1: 0, pblk: pblk, compressedBlocks: 0);
    }

    /// <summary>
    /// 解码完整（8 字节）压缩索引条目，移植自内核 <c>z_erofs_load_full_lcluster</c>。
    /// 完整索引起始于 <c>Z_EROFS_FULL_INDEX_START = Z_EROFS_MAP_HEADER_END + 8</c>。
    /// <para>Decodes a full (8-byte) compressed index entry, ported from the kernel
    /// <c>z_erofs_load_full_lcluster</c>. The full index starts at
    /// <c>Z_EROFS_FULL_INDEX_START = Z_EROFS_MAP_HEADER_END + 8</c>.</para>
    /// </summary>
    private LclusterEntry LoadFullLcluster(ErofsInode inode, long headerOffset, int lclusterBits, long lcn)
    {
        var indexOffset = headerOffset + ZErofsMapHeader.Size + FullIndexStartPad + lcn * ZErofsLclusterIndex.Size;
        if (indexOffset < 0 || indexOffset > _imageSize - ZErofsLclusterIndex.Size)
        {
            throw new InvalidDataException($"Full index entry {lcn} exceeds the image bounds.");
        }

        var entry = ZErofsLclusterIndex.FromBytes(ReadBytes(indexOffset, ZErofsLclusterIndex.Size, "full index entry"));
        if (entry.IsNonHead)
        {
            // D0_CBLKCNT 判定针对 delta[0] 值（内核 z_erofs_load_full_lcluster）。
            // The D0_CBLKCNT check applies to the delta[0] value (kernel z_erofs_load_full_lcluster).
            if ((entry.Delta0 & ZErofsLclusterIndex.LiD0CblkCnt) != 0)
            {
                return new LclusterEntry(LclusterTypeNonHead, 1 << lclusterBits, delta0: 1, delta1: 0, pblk: 0, compressedBlocks: entry.Delta0 & ~ZErofsLclusterIndex.LiD0CblkCnt);
            }

            return new LclusterEntry(LclusterTypeNonHead, 1 << lclusterBits, delta0: entry.Delta0, delta1: entry.Delta1, pblk: 0, compressedBlocks: 0);
        }

        var type = entry.IsHead2 ? LclusterTypeHead2 : (entry.IsPlain ? LclusterTypePlain : LclusterTypeHead1);
        if (entry.IsHole)
        {
            return new LclusterEntry(type, entry.ClusterOfs, delta0: 0, delta1: 0, pblk: -1, compressedBlocks: 0);
        }

        return new LclusterEntry(type, entry.ClusterOfs, delta0: 0, delta1: 0, pblk: entry.Blkaddr, compressedBlocks: 0);
    }

    /// <summary>
    /// 位级解码紧凑索引条目：<c>v = le32(in + pos/8) &gt;&gt; (pos &amp; 7)</c>；
    /// 低 <paramref name="lobits"/> 位为 lo（clusterofs / delta），随后 2 位为簇类型。
    /// <para>Bit-level compact index decode: <c>v = le32(in + pos/8) &gt;&gt; (pos &amp; 7)</c>;
    /// the low <paramref name="lobits"/> bits are lo (clusterofs / delta) and the following 2 bits
    /// are the cluster type.</para>
    /// </summary>
    private static (uint Lo, byte Type) DecodeCompactedBits(byte[] index, int packStartRel, int lobits, int bitPos)
    {
        var byteOffset = packStartRel + (bitPos >> 3);
        if (byteOffset + 4 > index.Length)
        {
            throw new InvalidDataException("Compact index bits exceed the image bounds.");
        }

        var v = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(byteOffset, 4)) >> (bitPos & 7);
        var lo = v & ((1u << lobits) - 1);
        var type = (byte)((v >> lobits) & 3);
        return (lo, type);
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>
    /// 解码后的逻辑簇条目。Decoded logical-cluster entry.
    /// </summary>
    private readonly struct LclusterEntry
    {
        public byte Type { get; }
        public int ClusterOfs { get; }
        public uint Delta0 { get; }
        public uint Delta1 { get; }
        public long Pblk { get; }
        public int CompressedBlocks { get; }

        public LclusterEntry(byte type, int clusterOfs, uint delta0, uint delta1, long pblk, int compressedBlocks)
        {
            Type = type;
            ClusterOfs = clusterOfs;
            Delta0 = delta0;
            Delta1 = delta1;
            Pblk = pblk;
            CompressedBlocks = compressedBlocks;
        }
    }
}
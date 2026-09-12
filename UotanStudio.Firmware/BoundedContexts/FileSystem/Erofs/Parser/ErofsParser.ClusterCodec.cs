using System.Buffers;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed partial class ErofsParser
{
    /// <summary>
    /// Loads a logical cluster entry from compact or full indexes.
    /// <para>从紧凑或完整索引加载逻辑簇条目。</para>
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
        => LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, lcn, out ebase, out compacted4bInitial, out compacted2b, cachedIndex: null);

    /// <summary>
    /// Loads a logical cluster entry from compact or full indexes, with an optional bounded
    /// index window to avoid repeated disk I/O for the same inode.
    /// <para>从紧凑或完整索引加载逻辑簇条目，支持传入预读的紧凑索引缓存以避免同一 inode 的重复磁盘 I/O。</para>
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
        out int compacted2b,
        IndexReadWindow? cachedIndex)
    {

        ebase = headerOffset + ZErofsMapHeader.Size;
        if (inode.DataLayout == ErofsInode.DataLayoutCompactIndexes)
        {
            compacted4bInitial = (int)(((32 - ebase % 32) / 4) & 7);
            compacted2b = 0;
            if ((advise & ZErofsMapHeader.AdviseCompacted2B) != 0 && compacted4bInitial < totalIdx)
            {
                compacted2b = (totalIdx - compacted4bInitial) / 16 * 16;
            }

            return LoadCompactLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, compacted4bInitial, compacted2b, lcn, cachedIndex);
        }

        compacted4bInitial = 0;
        compacted2b = 0;
        return LoadFullLcluster(inode, headerOffset, lclusterBits, lcn, cachedIndex);
    }

    /// <summary>
    /// Decodes a single logical-cluster entry from the compact index (bit-level), ported from the kernel z_erofs_load_compact_lcluster + decode_compactedbits, including the big-pcluster lookback branch.
    /// <para>解码紧凑索引中的单个逻辑簇条目（位级），移植自内核 z_erofs_load_compact_lcluster + decode_compactedbits，含 big-pcluster 回看分支。</para>
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
        long lcn,
        IndexReadWindow? cachedIndex = null)
    {
        var bigPcluster = (advise & (ZErofsMapHeader.AdviseBigPcluster1 | ZErofsMapHeader.AdviseBigPcluster2)) != 0;
        var amortizedShift = 2;
        var pos = headerOffset + ZErofsMapHeader.Size;
        var l = lcn;
        if (l >= compacted4bInitial)
        {
            pos += compacted4bInitial * 4;
            l -= compacted4bInitial;
            if (l < compacted2b)
            {
                amortizedShift = 1;
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
        byte[] index;
        if (cachedIndex is not null)
        {
            index = cachedIndex.Get(packStartRel, packSize, out packStartRel);
        }
        else
        {
            // Random callers need only the containing 8/32-byte pack.
            index = ReadBytes(checked(ebase + packStartRel), packSize, "compact index pack");
            packStartRel = 0;
        }

        var lobits = Math.Max(lclusterBits, 12);
        var encodebits = ((packSize - 4) * 8) / vcnt;
        var (lo, type) = DecodeCompactedBits(index, packStartRel, lobits, encodebits * i);
        if (type == LclusterTypeNonHead)
        {


            var clusterOfs = 1 << lclusterBits;
            if ((lo & (1u << 11)) != 0)
            {


                return new LclusterEntry(type, clusterOfs, delta0: 1, delta1: 0, pblk: 0, compressedBlocks: (int)(lo & ~(1u << 11)));
            }

            if (i + 1 == vcnt)
            {


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
    /// Decodes a full (8-byte) compressed index entry, ported from the kernel z_erofs_load_full_lcluster.
    /// <para>解码完整（8 字节）压缩索引条目，移植自内核 z_erofs_load_full_lcluster。</para>
    /// </summary>
    private LclusterEntry LoadFullLcluster(ErofsInode inode, long headerOffset, int lclusterBits, long lcn, IndexReadWindow? cachedIndex = null)
    {
        var indexOffset = headerOffset + ZErofsMapHeader.Size + FullIndexStartPad + lcn * ZErofsLclusterIndex.Size;
        if (indexOffset < 0 || indexOffset > _imageSize - ZErofsLclusterIndex.Size)
        {
            throw new InvalidDataException($"Full index entry {lcn} exceeds the image bounds.");
        }

        var relative = 0;
        var bytes = cachedIndex is null
            ? ReadBytes(indexOffset, ZErofsLclusterIndex.Size, "full index entry")
            : cachedIndex.Get(indexOffset - headerOffset - ZErofsMapHeader.Size, ZErofsLclusterIndex.Size, out relative);
        var entry = ZErofsLclusterIndex.FromBytes(bytes.AsSpan(relative, ZErofsLclusterIndex.Size));
        if (entry.IsNonHead)
        {


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
    /// Bit-level compact index decode: v = le32(in + pos/8) &gt;&gt; (pos &amp; 7); the low lobits bits are lo (clusterofs / delta) and the following 2 bits are the cluster type.
    /// <para>位级解码紧凑索引条目：v = le32(in + pos/8) &gt;&gt; (pos &amp; 7)； 低 lobits 位为 lo（clusterofs / delta），随后 2 位为簇类型。</para>
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
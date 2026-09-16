using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed partial class ErofsBuilder
{
    private static byte[] BuildCompressionConfiguration(ErofsOptions options)
    {
        if (!options.Compress) return [];
        if (options.SourceCompressionOptions is { } codecs)
        {
            using var output = new MemoryStream();
            foreach (var codec in codecs.OrderBy(pair => pair.Key))
                output.Write(BuildSingleCompressionConfiguration(codec.Value));
            return output.ToArray();
        }
        return BuildSingleCompressionConfiguration(options);
    }

    private static byte[] BuildSingleCompressionConfiguration(ErofsOptions options)
    {

        // Linux fs/erofs/erofs_fs.h: each algorithm has its own length-prefixed record.
        var length = options.Algorithm switch
        {
            ErofsCompressionAlgorithm.Lz4 or ErofsCompressionAlgorithm.Lzma => 14,
            ErofsCompressionAlgorithm.Deflate or ErofsCompressionAlgorithm.Zstd => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(options.Algorithm))
        };
        var configuration = new byte[length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(configuration, (ushort)length);

        switch (options.Algorithm)
        {
            case ErofsCompressionAlgorithm.Lz4:
                // erofs-utils compressor_lz4.c defaults the declared distance to LZ4_DISTANCE_MAX (65535);
                // a zero ErofsOptions.Lz4MaxDistance denotes that same default, so the record carries 65535.
                // erofs-utils compressor_lz4.c 将声明距离默认到 LZ4_DISTANCE_MAX（65535）；
                // ErofsOptions.Lz4MaxDistance 为 0 即该默认值，记录应写入 65535。
                BinaryPrimitives.WriteUInt16LittleEndian(configuration.AsSpan(2),
                    options.Lz4MaxDistance == 0 ? ErofsCompression.Lz4DistanceMax : options.Lz4MaxDistance);
                BinaryPrimitives.WriteUInt16LittleEndian(configuration.AsSpan(4), checked((ushort)(options.MaxPclusterSize / options.BlockSize)));
                break;
            case ErofsCompressionAlgorithm.Lzma:
                BinaryPrimitives.WriteUInt32LittleEndian(configuration.AsSpan(2), (uint)options.DictionarySize);
                break;
            case ErofsCompressionAlgorithm.Deflate:
                configuration[2] = (byte)(options.WindowBits == 0 ? 15 : options.WindowBits);
                break;
            case ErofsCompressionAlgorithm.Zstd:
                configuration[3] = (byte)((options.WindowBits == 0 ? 20 : options.WindowBits) - 10);
                break;
        }
        return configuration;
    }

    private static PclusterPlan[]? TryPlanCompressed(BuildNode node, int blockSize, ErofsOptions options)
    {
        // LZ4's upstream destSize API safely supports byte-granular clusters. The
        // other codecs retain the established logical-cluster planner until their
        // window-specific destSize encoders are available.
        if (options.OptimizeCompression && options.Algorithm == ErofsCompressionAlgorithm.Lz4)
            return TryPlanDenseCompressed(node, blockSize, options);
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var clusterSize = node.LogicalClusterSize;
        if (options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4MaxDistance != 0
            && options.Lz4MaxDistance < clusterSize) return null;
        var totalClusters = CeilDiv(node.Size, clusterSize);
        var raw = new byte[clusterSize * 64];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        var plans = new List<PclusterPlan>();
        var pos = 0L;
        var clusterIndex = 0L;
        while (clusterIndex < totalClusters)
        {
            options.CancellationToken.ThrowIfCancellationRequested();

            var acceptedLen = 0;
            var acceptedOut = 0;
            var buffered = 0;
            var maxTake = (int)Math.Min(64, totalClusters - clusterIndex);
            if (options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4MaxDistance != 0)
                maxTake = Math.Min(maxTake, options.Lz4MaxDistance / clusterSize);

            // Compresses the leading `take` clusters of this group. The window is
            // extended incrementally: each probe only reads the newly added cluster
            // instead of re-reading the shared prefix from disk.
            bool TryTake(int take, out int candidateLen, out int outLen)
            {
                candidateLen = (int)Math.Min(node.Size - pos, (long)take * clusterSize);
                if (candidateLen > buffered)
                {
                    fs.Position = pos + buffered;
                    fs.ReadExactly(raw, buffered, candidateLen - buffered);
                    buffered = candidateLen;
                }

                outLen = ErofsCompression.AlgorithmCompress(
                    (ErofsAlgorithm)options.Algorithm,
                    raw, 0, candidateLen, compressed, 0, compressed.Length,
                    Math.Max(1, options.CompressionLevel), options.DictionarySize,
                    options.WindowBits == 0 && options.Algorithm == ErofsCompressionAlgorithm.Zstd ? 20 : options.WindowBits);
                return outLen > 0 && outLen < options.MaxPclusterSize - 8;
            }

            // The largest mergeable take is located with an exponential probe plus a
            // binary search instead of a linear sweep: one-shot compression of a
            // longer prefix practically never produces less output, so the accepted
            // take keeps the same semantics as the linear scan while the probe count
            // drops from up to 64 compressions per group to about 12. Every accepted
            // take is still verified by an actual compression.
            var lo = 0;
            var hi = maxTake + 1;
            for (var probe = 1; probe < hi;)
            {
                if (!TryTake(probe, out var probeLen, out var probeOut))
                {
                    hi = probe;
                    break;
                }

                lo = probe;
                acceptedLen = probeLen;
                acceptedOut = probeOut;
                probe = probe == 1 ? 2 : probe * 2;
            }

            while (lo + 1 < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (TryTake(mid, out var midLen, out var midOut))
                {
                    lo = mid;
                    acceptedLen = midLen;
                    acceptedOut = midOut;
                }
                else
                {
                    hi = mid;
                }
            }

            if (acceptedLen <= 0 || acceptedOut <= 0)
            {
                // A file may mix incompressible logical blocks with compressed extents.
                // Keep the plain block and continue looking for compressible data.
                if (clusterSize != blockSize) return null;
                var length = (int)Math.Min(node.Size - pos, clusterSize);
                plans.Add(new PclusterPlan { SourceOffset = pos, SourceLength = length, CompressedLength = length, Plain = true });
                pos += length;
                clusterIndex++;
                continue;
            }

            plans.Add(new PclusterPlan { SourceOffset = pos, SourceLength = acceptedLen, CompressedLength = acceptedOut });
            pos += acceptedLen;
            clusterIndex += CeilDiv(acceptedLen, clusterSize);
        }


        if (plans.Sum(p => CeilDiv(p.CompressedLength, blockSize)) >= totalClusters)
        {
            return null;
        }

        return plans.ToArray();
    }

    private static void WriteCompressedData(Stream output, BuildNode node, int blockSize, ErofsOptions options)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var raw = new byte[node.Pclusters!.Max(p => p.SourceLength)];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        foreach (var p in node.Pclusters!)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            fs.Position = p.SourceOffset;
            fs.ReadExactly(raw, 0, p.SourceLength);
            if (p.Plain)
            {
                var offset = checked((long)p.BlockAddress * blockSize);
                WriteAt(output, offset, raw, p.SourceLength);
                WriteZeroes(output, offset + p.SourceLength, blockSize - p.SourceLength);
                continue;
            }
            var outLen = ErofsCompression.AlgorithmCompress(
                (ErofsAlgorithm)options.Algorithm,
                raw, 0, p.SourceLength, compressed, 0, compressed.Length,
                Math.Max(1, options.CompressionLevel), options.DictionarySize,
                options.WindowBits == 0 && options.Algorithm == ErofsCompressionAlgorithm.Zstd ? 20 : options.WindowBits);
            if (outLen != p.CompressedLength)
            {
                throw new InvalidDataException(
                    $"Compressed length mismatch for '{node.FullPath}': {outLen} != {p.CompressedLength}.");
            }

            if (node.InlineCompressedTail && ReferenceEquals(p, node.Pclusters![^1]))
            {
                var tailStart = node.MetaOffset + GetInodeRecordSize(node, blockSize) - outLen;
                WriteAt(output, tailStart, compressed, outLen);
                continue;
            }


            var blockBytes = CeilDiv(p.CompressedLength, blockSize) * blockSize;
            var start = checked(p.BlockAddress * blockSize);
            if (outLen < blockBytes)
            {
                WriteZeroes(output, start, blockBytes - outLen);
            }

            WriteAt(output, start + blockBytes - outLen, compressed, outLen);
        }
    }

    private static void WriteCompressedIndexes(Stream output, long indexOffset, BuildNode node, int blockSize, ErofsOptions options)
    {


        var bigPcluster = node.BigPcluster;
        var advise = bigPcluster ? ZErofsMapHeader.AdviseBigPcluster1 : (ushort)0;
        if (bigPcluster && node.CompactedIndexes)
        {
            advise |= ZErofsMapHeader.AdviseBigPcluster2;
        }

        var header = new byte[ZErofsMapHeader.Size];
        if (node.InlineCompressedTail)
        {
            advise |= ZErofsMapHeader.AdviseInlinePcluster;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), checked((ushort)node.Pclusters![^1].CompressedLength));
        }
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), advise);
        header[6] = (byte)options.Algorithm;
        header[7] = (byte)(BitOperations.Log2((uint)node.LogicalClusterSize) - BitOperations.Log2((uint)blockSize));
        WriteAt(output, indexOffset, header, header.Length);

        if (node.CompactedIndexes)
        {


            WriteCompactedIndexes4B(output, indexOffset + ZErofsMapHeader.Size, node, blockSize);
            return;
        }


        var entryOffset = indexOffset + ZErofsMapHeader.Size + 8;
        var clusters = node.ClusterCount;
        var pclusters = node.Pclusters!;
        var pclusterIndex = 0;
        for (var c = 0L; c < clusters; c++)
        {
            var plan = pclusters[pclusterIndex];
            var headCluster = plan.SourceOffset / node.LogicalClusterSize;
            var coveredClusters = PlanEndCluster(node, pclusterIndex) - headCluster;
            var entry = new byte[ZErofsLclusterIndex.Size];
            if (c >= PlanEndCluster(node, pclusterIndex))
            {
                // Upstream z_erofs_fini_full_indexes: a final PLAIN index marks EOF.
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), (ushort)(node.Size % node.LogicalClusterSize));
            }
            else if (c == headCluster)
            {


                BinaryPrimitives.WriteUInt16LittleEndian(entry, plan.Plain ? ZErofsLclusterIndex.LclusterTypePlain : ZErofsLclusterIndex.LclusterTypeHead1);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), (ushort)(plan.SourceOffset % node.LogicalClusterSize));
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), plan.BlockAddress);
            }
            else
            {
                var d0 = c - headCluster;


                var d1 = (ushort)Math.Min(headCluster + coveredClusters - c, (long)(ZErofsLclusterIndex.LiD0CblkCnt - 1));
                BinaryPrimitives.WriteUInt16LittleEndian(entry, ZErofsLclusterIndex.LclusterTypeNonHead);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), 0);
                if (bigPcluster && d0 == 1)
                {


                    var compressedBlocks = CeilDiv(plan.CompressedLength, blockSize);
                    if (compressedBlocks >= ZErofsLclusterIndex.LiD0CblkCnt)
                    {
                        throw new InvalidDataException(
                            $"Pcluster block count {compressedBlocks} exceeds the CBLKCNT limit for '{node.FullPath}'.");
                    }

                    BinaryPrimitives.WriteUInt16LittleEndian(
                        entry.AsSpan(4), (ushort)((uint)compressedBlocks | ZErofsLclusterIndex.LiD0CblkCnt));
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        entry.AsSpan(4), (ushort)Math.Min(d0, (long)(ZErofsLclusterIndex.LiD0CblkCnt - 1)));
                }

                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), d1);
            }

            WriteAt(output, entryOffset, entry, entry.Length);
            entryOffset += entry.Length;

            if (c == headCluster + coveredClusters - 1 && pclusterIndex + 1 < pclusters.Length)
            {
                pclusterIndex++;
            }
        }
    }

    /// <summary>
    /// Writes compact EROFS indexes using 4-byte entries in 8-byte packs.
    /// <para>以 8 字节打包单元写入每条 4 字节的紧凑 EROFS 索引。</para>
    /// </summary>
    private static void WriteCompactedIndexes4B(Stream output, long tableOffset, BuildNode node, int blockSize)
    {
        const int destsize = 4;
        const int vcnt = 2;
        var lclusterBits = BitOperations.Log2((uint)node.LogicalClusterSize);
        var lobits = Math.Max(lclusterBits, 12);
        var encodebits = (vcnt * destsize * 8 - 32) / vcnt;
        var packSize = vcnt * destsize;
        var bigPcluster = node.BigPcluster;

        var clusters = node.ClusterCount;
        var pclusters = node.Pclusters!;
        var pack = new byte[packSize];
        var packOffset = tableOffset;
        var entryIndex = 0L;
        var pclusterIndex = 0;


        var blkaddr = (long)pclusters[0].BlockAddress - (bigPcluster ? 0 : 1);


        var dummyHead = !bigPcluster;

        while (entryIndex < clusters)
        {


            var packStartBlkaddr = blkaddr + (bigPcluster && dummyHead ? 1 : 0);
            var bits = 0;
            for (var i = 0; i < vcnt; i++)
            {
                uint offset;
                byte type;
                if (entryIndex < clusters)
                {
                    var plan = pclusters[pclusterIndex];
                    var headCluster = plan.SourceOffset / node.LogicalClusterSize;
                    var coveredClusters = PlanEndCluster(node, pclusterIndex) - headCluster;
                    if (entryIndex >= PlanEndCluster(node, pclusterIndex))
                    {
                        type = 0;
                        offset = (uint)(node.Size % node.LogicalClusterSize);
                        if (dummyHead) blkaddr++;
                        dummyHead = true;
                    }
                    else if (entryIndex == headCluster)
                    {


                        type = plan.Plain ? (byte)0 : (byte)1;
                        offset = (uint)(plan.SourceOffset % node.LogicalClusterSize);
                        if (dummyHead)
                        {
                            blkaddr++;
                            if (blkaddr != plan.BlockAddress)
                            {
                                throw new InvalidDataException(
                                    $"Compacted index blkaddr {blkaddr} != planned block {plan.BlockAddress} for '{node.FullPath}'.");
                            }
                        }

                        dummyHead = true;
                    }
                    else
                    {
                        type = 2;
                        var d0 = entryIndex - headCluster;
                        if (bigPcluster && d0 == 1)
                        {


                            var compressedBlocks = CeilDiv(plan.CompressedLength, blockSize);
                            if (compressedBlocks >= ZErofsLclusterIndex.LiD0CblkCnt)
                            {
                                throw new InvalidDataException(
                                    $"Pcluster block count {compressedBlocks} exceeds the CBLKCNT limit for '{node.FullPath}'.");
                            }

                            offset = (uint)compressedBlocks | ZErofsLclusterIndex.LiD0CblkCnt;
                            blkaddr += compressedBlocks;
                            dummyHead = false;
                            if (pclusterIndex + 1 < pclusters.Length && blkaddr != pclusters[pclusterIndex + 1].BlockAddress)
                            {
                                throw new InvalidDataException(
                                    $"Compacted index running blkaddr {blkaddr} != planned block {pclusters[pclusterIndex + 1].BlockAddress} for '{node.FullPath}'.");
                            }
                        }
                        else if (i + 1 == vcnt)
                        {


                            var d1 = coveredClusters - d0;
                            offset = (uint)Math.Min(d1, ZErofsLclusterIndex.LiD0CblkCnt - 1);
                        }
                        else
                        {
                            offset = (uint)Math.Min(d0, (long)(ZErofsLclusterIndex.LiD0CblkCnt - 1));
                        }
                    }

                    if (entryIndex == headCluster + coveredClusters - 1 && pclusterIndex + 1 < pclusters.Length)
                    {
                        pclusterIndex++;
                    }

                    entryIndex++;
                }
                else
                {
                    type = 0;
                    offset = 0;
                }


                var v = ((uint)type << lobits) | offset;
                var bytePos = bits >> 3;
                var bitShift = bits & 7;
                var chunk = v << bitShift;
                for (var b = 0; b < 4; b++)
                {
                    if (bytePos + b < packSize - 4)
                    {
                        pack[bytePos + b] |= (byte)(chunk >> (8 * b));
                    }
                }

                bits += encodebits;
            }


            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(packSize - 4, 4), (uint)packStartBlkaddr);
            WriteAt(output, packOffset, pack, pack.Length);
            packOffset += pack.Length;
            Array.Clear(pack);
        }
    }

    private static void WriteZeroes(Stream output, long offset, long count)
    {
        output.Position = offset;
        var zeroes = new byte[Math.Min(count, 1 << 16)];
        var written = 0L;
        while (written < count)
        {
            var n = (int)Math.Min(zeroes.Length, count - written);
            output.Write(zeroes, 0, n);
            written += n;
        }
    }

    /// <summary>
    /// Writes the packed inode's fragment data area: each fragment slot's source bytes are streamed to its offset inside the packed region (unwritten gaps stay zeroed by the image's preallocation).
    /// <para>写出 packed inode 的碎片数据区：把每个碎片槽位按文件内偏移写入数据区对应位置 （槽位之间未写的空白由镜像预分配/置零保证为零）。</para>
    /// </summary>
    private static void WritePackedData(Stream output, BuildNode packedNode, int blockSize)
    {
        var baseOffset = packedNode.FlatBlockAddr * blockSize;
        foreach (var slot in packedNode.PackedFragments!)
        {
            CopyFileRange(output, slot.Owner, 0, slot.Length, baseOffset + slot.Offset);
        }
    }

    /// <summary>
    /// Whether a node qualifies as a METABOX container record: a regular file (not a directory / symlink / device / synthetic node) stored as pure whole-block flat data (no inline tail), without compression / fragments / xattrs, whose size fits 32 bits (compact inode).
    /// <para>判断节点是否可作为 METABOX 容器驻留记录：普通文件（非目录/符号链接/设备/合成节点）、 存储为纯全块 flat（无内联尾部）、无压缩/碎片/xattr，且尺寸 ≤ 32 位（compact inode）。</para>
    /// </summary>
    private static bool IsMetaboxEligible(BuildNode node)
        => !node.IsDirectory
           && !node.IsSymlink
           && !node.IsDevice
           && !node.IsPacked
           && node.Size <= uint.MaxValue
           && node.Pclusters is null
           && !node.IsFragment
           && node.XattrIbody.Length == 0
           && GetInodeHeaderSize(node) == ErofsInode.CompactSize;

    /// <summary>
    /// Writes the metabox inode's container data area: each resident file's 32-byte compact inode record is streamed to its slot (the metabox inode's i_u points at this data area and its i_size is slotCount × 32).
    /// <para>写出 metabox inode 的容器数据区：把每个容器驻留文件的 32 字节 compact inode 记录写到其 槽位（metabox inode 的 i_u 指向该数据区，i_size = 槽数 × 32）。</para>
    /// </summary>
    private static void WriteMetaboxContainer(Stream output, BuildNode metaboxNode, int blockSize, ulong now, List<BuildNode> residents)
    {
        var baseOffset = checked(metaboxNode.FlatBlockAddr * blockSize);
        foreach (var resident in residents)
        {
            var inode = BuildInode(resident, blockSize, now);
            var buffer = new byte[inode.OnDiskSize];
            inode.WriteTo(buffer);
            WriteAt(output, baseOffset + resident.MetaboxSlot * ErofsSuperblock.CompactInodeSize, buffer, buffer.Length);
        }
    }

    /// <summary>
    /// Writes a fragment inode's 8-byte z_erofs_map_header: h_fragmentoff is the byte offset inside the packed inode and h_clusterbits bit 7 (ZErofsMapHeader.FragmentInodeMask) marks a whole-file fragment.
    /// <para>写出 fragment inode 的 8 字节 z_erofs_map_header：h_fragmentoff = packed 区内字节偏移、 h_clusterbits bit7（ZErofsMapHeader.FragmentInodeMask）声明整文件 fragment。</para>
    /// </summary>
    private static void WriteFragmentHeader(Stream output, BuildNode node, ErofsInode inode, ErofsOptions options)
    {
        var headerOffset = node.MetaOffset + AlignUp(inode.OnDiskSize + inode.XattrTotalSize, 8);
        var header = new byte[ZErofsMapHeader.Size];
        BinaryPrimitives.WriteUInt64LittleEndian(header, checked((ulong)node.FragmentOffset) | (1UL << 63));
        WriteAt(output, headerOffset, header, header.Length);
    }

    /// <summary>
    /// Streams a whole file through SHA-256 via IncrementalHash in chunks without loading the file wholly into memory.
    /// <para>流式计算整文件 SHA-256（IncrementalHash，分块读取，不整读大文件）。</para>
    /// </summary>
    private static byte[] HashFile(BuildNode node)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        var remaining = node.Size;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            fs.ReadExactly(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }

        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Whole-file fragment offsets occupy the low 63 bits of the map header and need no alignment.
    /// </summary>
    private static long AlignFragmentStart(long cursor)
        => cursor;

    private static void CopyFileRange(Stream output, BuildNode node, long sourceOffset, long length, long targetOffset)
    {
        if (length <= 0)
        {
            return;
        }

        using var source = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        source.Position = sourceOffset;
        var buffer = new byte[1 << 20];
        var remaining = length;
        var pos = targetOffset;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            source.ReadExactly(buffer, 0, count);
            WriteAt(output, pos, buffer, count);
            pos += count;
            remaining -= count;
        }
    }

    private static long CeilDiv(long value, long divisor) => value == 0 ? 0 : (value + divisor - 1) / divisor;

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static void ZeroImage(Stream output, long length)
    {
        output.Position = 0;
        var zeroes = new byte[1 << 16];
        while (length > 0)
        {
            var count = (int)Math.Min(zeroes.Length, length);
            output.Write(zeroes, 0, count);
            length -= count;
        }
    }

    private static void WriteAt(Stream output, long offset, byte[] data, int count)
    {
        output.Position = offset;
        output.Write(data, 0, count);
    }
}
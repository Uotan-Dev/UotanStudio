using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using UotanStudio.Firmware.FileSystem.Erofs.Models;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

public sealed partial class ErofsBuilder
{
    /// <summary>
    /// Builds the compression configuration records that follow the superblock, one per codec the build may
    /// encode with. The records exist only while the feature declaration advertises them, so a preserved
    /// rebuild of a pre-5.13 source keeps the compact slot-free layout, and an uncompressed build never
    /// writes them.
    /// <para>构造紧随超级块的压缩配置记录，本次构建每个可能使用的编解码器一条。仅当特性声明 advertise 这些记录时
    /// 才写出，因此对 pre-5.13 源镜像的保留重建维持无槽位的紧凑布局，未压缩的构建则一律不写。</para>
    /// </summary>
    /// <param name="options">Validated build options carrying the declaration.
    /// <para>携带声明的经校验构建选项。</para></param>
    /// <returns>The concatenated record bytes, or an empty array when none are emitted.
    /// <para>串联后的记录字节；不写出时为空数组。</para></returns>
    private static byte[] BuildCompressionConfiguration(ErofsOptions options)
    {
        if (!options.Compress || !DeclaredCompressionConfigs(options)) return [];
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

    /// <summary>
    /// Plans pclusters for one payload with the cluster-restarting planner. A candidate is accepted only when the
    /// emitted index can name its physical length: without a declared big-pcluster feature
    /// <c>z_erofs_get_extent_compressedlen</c> pins every pcluster to exactly one filesystem block, so a frame
    /// larger than one block would be read back truncated; with the feature the block count travels in the first
    /// non-head row, so a frame larger than one block has to be given a second logical cluster to carry that row.
    /// Non-big candidates are additionally trimmed to whole logical clusters, which is the granularity that layout
    /// has always used.
    /// The search reuses one scratch buffer for every probe, so the accepted frame is copied to a second buffer as
    /// soon as a probe is accepted: the probe that ends the search is usually a longer span whose encoding starts
    /// with the accepted frame's bytes but does not stop where they stop, and staging that prefix would credit the
    /// index with <c>acceptedLen</c> bytes while the stored frame decodes to a different span, which a conformant
    /// reader reports as <c>failed to full decompress</c>.
    /// <para>以逐簇重启的规划器为单个负载规划 pcluster。只有在已发出的索引能够表明其物理长度时才接受候选：
    /// 未声明 big pcluster 时 <c>z_erofs_get_extent_compressedlen</c> 把每个 pcluster 钉死为一个文件系统块，
    /// 超过一块的帧读回时必被截断；声明该特性时块数写在首个非头簇行里，因此超过一块的帧必须有第二个逻辑簇
    /// 来承载该行。非 big 候选还会被裁剪为整数个逻辑簇，这也是该布局一直使用的粒度。
    /// 搜索过程复用同一块暂存缓冲，因此候选一旦被接受就立刻把该帧复制到第二块缓冲：结束搜索的那次探测通常是
    /// 更长的跨度，其编码以被接受帧的字节开头却不会在同样的位置收尾，若把这段前缀写入暂存文件，索引会记给
    /// 该帧 <c>acceptedLen</c> 字节，而盘上帧却解码成另一个跨度，合规读取方会报
    /// <c>failed to full decompress</c>。</para>
    /// </summary>
    private static PclusterPlan[]? TryPlanCompressed(BuildNode node, int blockSize, ErofsOptions options,
        Core.CompressionPayloadStaging staging)
    {
        if (options.OptimizeCompression && options.Algorithm == ErofsCompressionAlgorithm.Lz4)
            return TryPlanDenseCompressed(node, blockSize, options, staging);
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var clusterSize = node.LogicalClusterSize;
        if (options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4MaxDistance != 0
            && options.Lz4MaxDistance < clusterSize) return null;
        var bigPcluster = DeclaredBigPcluster(options);
        var frameLimit = bigPcluster
            ? Math.Min(options.MaxPclusterSize, ErofsCompression.MaxPclusterSize) - 8
            : blockSize;
        var totalClusters = CeilDiv(node.Size, clusterSize);
        var raw = new byte[clusterSize * 64];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        var accepted = new byte[Math.Clamp(frameLimit, 0, compressed.Length)];
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

            bool TryTake(int take, out int candidateLen, out int outLen)
            {
                candidateLen = (int)Math.Min(node.Size - pos, (long)take * clusterSize);
                if (!bigPcluster)
                {
                    var tail = candidateLen % clusterSize;
                    if (tail != 0) candidateLen -= tail;
                }
                if (candidateLen <= 0)
                {
                    outLen = 0;
                    return false;
                }
                if (candidateLen > buffered)
                {
                    fs.Position = pos + buffered;
                    fs.ReadExactly(raw, buffered, candidateLen - buffered);
                    buffered = candidateLen;
                }

                outLen = ErofsCompression.AlgorithmCompress(
                    options, raw, 0, candidateLen, compressed, 0, compressed.Length);
                if (outLen <= 0 || outLen > frameLimit)
                {
                    return false;
                }

                if (outLen > blockSize && pos % clusterSize + candidateLen <= clusterSize)
                {
                    return false;
                }

                compressed.AsSpan(0, outLen).CopyTo(accepted);
                return true;
            }

            var lo = 0;
            var hi = maxTake + 1;
            for (var probe = 1; probe < hi;)
            {
                if (!TryTake(probe, out var probeLen, out var probeOut))
                {
                    if (probe == 1 && bigPcluster && probeOut > blockSize)
                    {
                        lo = probe;
                        probe = 2;
                        continue;
                    }

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
                if (clusterSize != blockSize) return null;
                var length = (int)Math.Min(node.Size - pos, clusterSize);
                plans.Add(new PclusterPlan { SourceOffset = pos, SourceLength = length, CompressedLength = length, Plain = true });
                pos += length;
                clusterIndex++;
                continue;
            }

            plans.Add(new PclusterPlan
            {
                SourceOffset = pos,
                SourceLength = acceptedLen,
                CompressedLength = acceptedOut,
                StagedOffset = staging.Append(accepted.AsSpan(0, acceptedOut))
            });
            pos += acceptedLen;
            clusterIndex += CeilDiv(acceptedLen, clusterSize);
        }

        if (plans.Sum(p => CeilDiv(p.CompressedLength, blockSize)) >= totalClusters)
        {
            return null;
        }

        return [.. plans];
    }

    /// <summary>
    /// Writes each planned pcluster: the encoding accepted by either planner during the planning pass is
    /// copied from the staging file, and only plain clusters re-read the source, so no accepted pcluster is
    /// compressed twice. A plan without a staged offset stays supported by re-reading and re-compressing
    /// the source, which has to reproduce the planned length exactly. Each compressed frame is stored
    /// right-aligned inside its pcluster block region with leading zero padding: the index carries only
    /// <c>di_blkaddr</c> and a block count, so a reader takes the whole padded region as the input length
    /// and removes the padding by skipping leading zero bytes (<c>z_erofs_fixup_insize</c> in erofs-utils
    /// and the same rule in <c>fs/erofs/decompressor.c</c>). Padding the tail instead would hand the
    /// decoder trailing zero bytes that it reads as further LZ4 sequences, so the frame would fail with
    /// <c>failed to full decompress</c> even though the index and the frame agree on the logical span.
    /// <para>写出每个规划的 pcluster：任一规划器在规划阶段接受的编码都从暂存文件复制，只有原样簇会
    /// 重读源文件，因此被接受的 pcluster 不会被压缩第二次。未记录暂存偏移的规划仍受支持：重读源文件
    /// 并重新压缩，其结果长度必须与规划值完全一致。每个压缩帧都以首部零填充的方式右对齐存放于其
    /// pcluster 块区间：索引只记录 <c>di_blkaddr</c> 与块数，因此读取方会把整段含填充的区域当作输入长度，
    /// 再靠前导零字节数扣除填充（erofs-utils 的 <c>z_erofs_fixup_insize</c>，与内核
    /// <c>fs/erofs/decompressor.c</c> 同规则）。若改为尾部填充，解码器会把填充零字节当作后续的 LZ4 序列读取，
    /// 即使索引与帧对逻辑长度的认定一致，也会以 <c>failed to full decompress</c> 失败。</para>
    /// </summary>
    private static void WriteCompressedData(Stream output, BuildNode node, int blockSize, ErofsOptions options,
        Core.CompressionPayloadStaging staging)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var raw = new byte[node.Pclusters!.Max(p => p.SourceLength)];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        foreach (var p in node.Pclusters!)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (p.Plain)
            {
                fs.Position = p.SourceOffset;
                fs.ReadExactly(raw, 0, p.SourceLength);
                var offset = checked((long)p.BlockAddress * blockSize);
                WriteAt(output, offset, raw, p.SourceLength);
                WriteZeroes(output, offset + p.SourceLength, blockSize - p.SourceLength);
                continue;
            }

            int outLen;
            if (p.StagedOffset >= 0)
            {
                outLen = p.CompressedLength;
                staging.Read(p.StagedOffset, compressed.AsSpan(0, outLen));
            }
            else
            {
                fs.Position = p.SourceOffset;
                fs.ReadExactly(raw, 0, p.SourceLength);
                outLen = ErofsCompression.AlgorithmCompress(
                    options, raw, 0, p.SourceLength, compressed, 0, compressed.Length);
                if (outLen != p.CompressedLength)
                {
                    throw new FileSystemCorruptedException(
                        $"Compressed length mismatch for '{node.FullPath}': {outLen} != {p.CompressedLength}.");
                }
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
                        throw new FileSystemCorruptedException(
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
                                throw new FileSystemCorruptedException(
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
                                throw new FileSystemCorruptedException(
                                    $"Pcluster block count {compressedBlocks} exceeds the CBLKCNT limit for '{node.FullPath}'.");
                            }

                            offset = (uint)compressedBlocks | ZErofsLclusterIndex.LiD0CblkCnt;
                            blkaddr += compressedBlocks;
                            dummyHead = false;
                            if (pclusterIndex + 1 < pclusters.Length && blkaddr != pclusters[pclusterIndex + 1].BlockAddress)
                            {
                                throw new FileSystemCorruptedException(
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
    private static void WritePackedData(Stream output, BuildNode packedNode, int blockSize, CancellationToken token)
    {
        var baseOffset = packedNode.FlatBlockAddr * blockSize;
        foreach (var slot in packedNode.PackedFragments!)
        {
            token.ThrowIfCancellationRequested();
            CopyFileRange(output, slot.Owner, 0, slot.Length, baseOffset + slot.Offset, token);
        }
    }

    /// <summary>
    /// Whether an uncompressed regular inode and its inline payload fit within one METABOX block.
    /// <para>仅将 inode、扩展属性和内联数据整体能放入单个 METABOX 块的普通文件作为驻留记录。</para>
    /// </summary>
    private static bool IsMetaboxEligible(BuildNode node, int blockSize)
        => !node.IsDirectory
           && !node.IsSymlink
           && !node.IsDevice
           && !node.IsPacked
           && node.Size <= uint.MaxValue
           && node.Pclusters is null
           && !node.IsFragment
           && GetInodeRecordSize(node, blockSize) <= blockSize;

    /// <summary>
    /// Writes each variable-sized METABOX inode record with its inline xattrs and tail data.
    /// <para>按分配的变长槽位写入 inode、扩展属性与内联尾部数据。</para>
    /// </summary>
    private static void WriteMetaboxContainer(Stream output, BuildNode metaboxNode, int blockSize, ulong now, List<BuildNode> residents, ErofsOptions options)
    {
        var baseOffset = checked(metaboxNode.FlatBlockAddr * blockSize);
        foreach (var resident in residents)
        {
            var inode = BuildInode(resident, blockSize, now, options);
            var buffer = new byte[inode.OnDiskSize];
            inode.WriteTo(buffer);
            var slotOffset = checked(baseOffset + resident.MetaboxSlot * ErofsSuperblock.CompactInodeSize);
            WriteAt(output, slotOffset, buffer, buffer.Length);
            if (resident.XattrIbody.Length > 0)
            {
                WriteAt(output, slotOffset + inode.OnDiskSize, resident.XattrIbody, resident.XattrIbody.Length);
            }
            if (resident.InlineData.Length > 0)
            {
                WriteAt(output, slotOffset + inode.InlineDataOffset, resident.InlineData, resident.InlineData.Length);
            }
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
    private static byte[] HashFile(BuildNode node, CancellationToken token)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        var remaining = node.Size;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
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

    private static void CopyFileRange(Stream output, BuildNode node, long sourceOffset, long length, long targetOffset, CancellationToken token)
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
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, remaining);
            source.ReadExactly(buffer, 0, count);
            WriteAt(output, pos, buffer, count);
            pos += count;
            remaining -= count;
        }
    }

    private static long CeilDiv(long value, long divisor) => value == 0 ? 0 : (value + divisor - 1) / divisor;

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static void ZeroImage(Stream output, long length, CancellationToken token)
    {
        output.Position = 0;
        var zeroes = new byte[1 << 16];
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
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
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

public sealed partial class ErofsBuilder
{
    private static PclusterPlan[]? TryPlanCompressed(BuildNode node, int blockSize, ErofsOptions options)
    {
        // 与 mkfs 同款：一个 pcluster 覆盖若干整逻辑簇（lcluster = 块大小），压缩输出 ≤ 一块，
        // 右对齐写入物理块（前导 0 由 0-padding 特性容忍）。逐簇试探累计输入直至压缩输出
        // 逼近一块；单簇都压不进一块（或毫无收益）时整文件退回 flat。
        // A pcluster covers several whole logical clusters (lcluster = block size) whose compressed
        // output fits one block, written right-aligned (leading zeros tolerated via 0-padding).
        // We greedily accumulate clusters until the output approaches one block; if a single
        // cluster cannot fit into one block the file falls back to flat.
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var clusterSize = blockSize;
        var totalClusters = CeilDiv(node.Size, clusterSize);
        var raw = new byte[clusterSize * 64];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        var plans = new List<PclusterPlan>();
        var pos = 0L;
        var clusterIndex = 0L;
        while (clusterIndex < totalClusters)
        {
            // 当前 pcluster 已接受的长度与压缩输出。
            var acceptedLen = 0;
            var acceptedOut = 0;
            for (var take = 1; clusterIndex + take <= totalClusters && take <= 64; take++)
            {
                var candidateLen = (int)Math.Min(node.Size - pos, (long)take * clusterSize);
                fs.Position = pos;
                fs.ReadExactly(raw, 0, candidateLen);
                var outLen = ErofsCompression.AlgorithmCompress(
                    (ErofsAlgorithm)options.Algorithm,
                    raw, 0, candidateLen, compressed, 0, compressed.Length,
                    Math.Max(1, options.CompressionLevel));
                if (outLen <= 0 || outLen >= blockSize - 8)
                {
                    // 输出超一块：终止扩展（保留上一个成功的窗口）。
                    // Output would exceed one block: stop growing (keep the last good window).
                    break;
                }

                acceptedLen = candidateLen;
                acceptedOut = outLen;
                if (candidateLen == node.Size - pos)
                {
                    break; // 到文件尾。End of file.
                }
            }

            if (acceptedLen <= 0 || acceptedOut >= acceptedLen && clusterIndex + 1 == totalClusters && acceptedOut == 0)
            {
                return null; // 单簇无法压缩。A single cluster is incompressible.
            }

            if (acceptedOut <= 0)
            {
                return null;
            }

            plans.Add(new PclusterPlan { SourceOffset = pos, SourceLength = acceptedLen, CompressedLength = acceptedOut });
            pos += acceptedLen;
            clusterIndex += CeilDiv(acceptedLen, clusterSize);
        }

        // 压缩后物理块数必须少于 flat。Compressed blocks must be fewer than flat storage.
        if (plans.Sum(p => CeilDiv(p.CompressedLength, blockSize)) >= totalClusters)
        {
            return null;
        }

        return plans.ToArray();
    }

    private static void WriteCompressedData(Stream output, BuildNode node, int blockSize, ErofsOptions options)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var raw = new byte[blockSize * 64];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        foreach (var p in node.Pclusters!)
        {
            fs.Position = p.SourceOffset;
            fs.ReadExactly(raw, 0, p.SourceLength);
            var outLen = ErofsCompression.AlgorithmCompress(
                (ErofsAlgorithm)options.Algorithm,
                raw, 0, p.SourceLength, compressed, 0, compressed.Length,
                Math.Max(1, options.CompressionLevel));
            if (outLen != p.CompressedLength)
            {
                throw new InvalidDataException(
                    $"Compressed length mismatch for '{node.FullPath}': {outLen} != {p.CompressedLength}.");
            }

            // 右对齐写入物理块（前导 0 由 0-padding 特性容忍，读端 fixup 跳过）。
            // Write right-aligned in the physical block; the leading zeros are tolerated by the
            // 0-padding feature and skipped by the reader's fixup.
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
        // z_erofs_map_header（8B，位于 inode 记录内联区起点）：h_advise、h_algorithmtype、
        // h_clusterbits。h_algorithmtype 低 4 位为 HEAD1 算法（与内核 Z_EROFS_COMPRESSION_*
        // 一致；默认 LZ4=0）。big-pcluster 文件置 h_advise 的 BIG_PCLUSTER_1，compact 布局
        // 再加 _2（与 erofs-utils z_erofs_write_indexes 的 advise 逻辑一致）。
        // The 8-byte z_erofs_map_header at the start of the inode's inline area: h_advise,
        // h_algorithmtype and h_clusterbits. The low nibble of h_algorithmtype is the HEAD1
        // algorithm (matching the kernel Z_EROFS_COMPRESSION_*; LZ4 = 0 by default). Big-pcluster
        // files set BIG_PCLUSTER_1 in h_advise (+ _2 for the compact layout, matching the
        // erofs-utils z_erofs_write_indexes advise logic).
        var bigPcluster = node.BigPcluster;
        var advise = bigPcluster ? ZErofsMapHeader.AdviseBigPcluster1 : (ushort)0;
        if (bigPcluster && node.CompactedIndexes)
        {
            advise |= ZErofsMapHeader.AdviseBigPcluster2;
        }

        var header = new byte[ZErofsMapHeader.Size];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), advise);
        header[6] = (byte)options.Algorithm; // h_algorithmtype = HEAD1 算法
        header[7] = 0; // h_clusterbits = 0
        WriteAt(output, indexOffset, header, header.Length);

        if (node.CompactedIndexes)
        {
            // compacted 4B amortized 位打包索引：map header 后紧跟位打包表，无 8B 保留区。
            // The compacted 4B-amortized bit-packed index follows the map header directly (no
            // 8-byte pad).
            WriteCompactedIndexes4B(output, indexOffset + ZErofsMapHeader.Size, node, blockSize);
            return;
        }

        // legacy full：map header 后 8B 保留（内核 Z_EROFS_FULL_INDEX_START 约定），之后每逻辑簇
        // 一条 8B 索引。pcluster 首簇为 HEAD（clusterofs = 0，blkaddr = 物理块号）；其余为
        // NONHEAD——big-pcluster 时首个 NONHEAD 的 delta[0] = 压缩块数 | D0_CBLKCNT（内核
        // z_erofs_get_extent_compressedlen 要求：非 CBLKCNT 的 d0=1 会判 EFSCORRUPTED），
        // delta[1] = 距下一 HEAD 的前向簇数（erofs-utils d1）；后续 NONHEAD 的 delta[0] = 距
        // HEAD 的簇数（≥ Z_EROFS_LI_D0_CBLKCNT 时按 erofs-utils 截断防 CBLKCNT 误判）。
        // Legacy full: an 8-byte pad after the map header (kernel Z_EROFS_FULL_INDEX_START), then
        // one 8-byte index per logical cluster. The first cluster of a pcluster is a HEAD
        // (clusterofs = 0, blkaddr = physical block); the covered clusters are NONHEAD entries.
        // Under big pcluster the first NONHEAD carries delta[0] = compressed block count |
        // D0_CBLKCNT (required by the kernel's z_erofs_get_extent_compressedlen: a plain d0=1 is
        // rejected as EFSCORRUPTED), and delta[1] = forward distance to the next HEAD (the
        // erofs-utils d1); later NONHEADs store the distance to the HEAD (capped below
        // Z_EROFS_LI_D0_CBLKCNT as erofs-utils does, to avoid aliasing the CBLKCNT flag).
        var entryOffset = indexOffset + ZErofsMapHeader.Size + 8;
        var clusters = node.ClusterCount;
        var pclusters = node.Pclusters!;
        var pclusterIndex = 0;
        for (var c = 0L; c < clusters; c++)
        {
            var plan = pclusters[pclusterIndex];
            var headCluster = plan.SourceOffset / blockSize;
            var coveredClusters = CeilDiv(plan.SourceLength, blockSize);
            var entry = new byte[ZErofsLclusterIndex.Size];
            if (c == headCluster)
            {
                // 该 pcluster 的首簇：HEAD（blkaddr 指向物理块，clusterofs = 0）。
                // The pcluster's first cluster: HEAD (blkaddr points at the physical block,
                // clusterofs = 0).
                BinaryPrimitives.WriteUInt16LittleEndian(entry, ZErofsLclusterIndex.LclusterTypeHead1);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), 0); // clusterofs
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), plan.BlockAddress);
            }
            else
            {
                var d0 = c - headCluster;
                // delta[1] = headCluster + coveredClusters - c，即到下一 HEAD 的前向簇数
                // （erofs-utils 循环中 d1 = N - i，数学等价）。
                // delta[1] = headCluster + coveredClusters - c, the forward cluster distance to
                // the next HEAD (equivalent to the erofs-utils loop's d1 = N - i).
                var d1 = (ushort)Math.Min(headCluster + coveredClusters - c, (long)(ZErofsLclusterIndex.LiD0CblkCnt - 1));
                BinaryPrimitives.WriteUInt16LittleEndian(entry, ZErofsLclusterIndex.LclusterTypeNonHead);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), 0); // clusterofs 未用
                if (bigPcluster && d0 == 1)
                {
                    // 首个 NONHEAD：CBLKCNT 条目（压缩块数置入 delta[0] 高位）。
                    // First NONHEAD: the CBLKCNT entry (compressed block count OR-ed into delta[0]).
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
    /// 写出 compacted 4B amortized 位打包索引表（datalayout = COMPRESSED_COMPACT，仅 4B 区、
    /// 不启用 2B 区）。严格对照 erofs-utils <c>write_compacted_indexes</c> 的位打包：
    /// 每条目 v = (clustertype &lt;&lt; lobits) | offset 按 encodebits 位连续写入，每 2 簇一个
    /// 8B pack，pack 尾部 4B 存该 pack 起点块号快照。NONHEAD 的 pack 末槽存 delta[1]（读端
    /// delta[0] = 前条 + 1 间接还原）；big-pcluster 时首个 NONHEAD 为 CBLKCNT 条目
    ///（offset = 压缩块数 | D0_CBLKCNT，运行块号前移压缩块数），pack 末槽的 CBLKCNT 条目仍存
    /// delta[0]（读端先查 CBLKCNT 标志，不受末槽 delta[1] 规则影响）。
    /// <para>Writes a compacted 4B-amortized bit-packed index table (datalayout =
    /// COMPRESSED_COMPACT; 4B-only section, no 2B section), mirroring erofs-utils
    /// <c>write_compacted_indexes</c> bit packing: each entry v = (clustertype &lt;&lt; lobits) |
    /// offset is written at encodebits bits; two clusters form one 8-byte pack whose trailing 4
    /// bytes hold the pack-start block-address snapshot. A NONHEAD in the pack's last slot stores
    /// delta[1] (readers derive delta[0] as previous-lo + 1); under big pcluster the first NONHEAD
    /// of each pcluster is a CBLKCNT entry (offset = compressed block count | D0_CBLKCNT, advancing
    /// the running block address), and a CBLKCNT entry in the last slot still stores delta[0]
    /// (readers check the CBLKCNT flag before the last-slot rule).</para>
    /// </summary>
    private static void WriteCompactedIndexes4B(Stream output, long tableOffset, BuildNode node, int blockSize)
    {
        const int destsize = 4;
        const int vcnt = 2;
        var lclusterBits = BitOperations.Log2((uint)blockSize);
        var lobits = Math.Max(lclusterBits, 12); // ilog2(Z_EROFS_LI_D0_CBLKCNT) + 1 = 11 + 1
        var encodebits = (vcnt * destsize * 8 - 32) / vcnt; // (2*4*8-32)/2 = 16
        var packSize = vcnt * destsize; // 8 字节每 pack。8 bytes per pack.
        var bigPcluster = node.BigPcluster;

        var clusters = node.ClusterCount;
        var pclusters = node.Pclusters!;
        var pack = new byte[packSize];
        var packOffset = tableOffset;
        var entryIndex = 0L;          // 逻辑簇序号。Logical cluster index.
        var pclusterIndex = 0;
        // 运行块号：big-pcluster 从首个 HEAD 真实块号起（读端回看 nblk 从 0 起算）；
        // 非 big-pcluster 先减一（读端 nblk 从 1 起算）。
        // Running block address: big-pcluster starts at the first HEAD's real block (reader's
        // lookback nblk starts at 0); non-big starts one below (reader's nblk starts at 1).
        var blkaddr = (long)pclusters[0].BlockAddress - (bigPcluster ? 0 : 1);
        // dummy_head（erofs-utils 语义）：置位表示“上一 pcluster 尚未记账其物理块数”，下一个
        // HEAD 需自增块号。非 big-pcluster 恒置位（每 pcluster 恒占 1 块，由 HEAD 自增记账）；
        // big-pcluster 由 CBLKCNT 条目记账并清除。
        // dummy_head (erofs-utils semantics): set means the previous pcluster has not yet accounted
        // for its physical blocks, so the next HEAD must bump the running block address. Non-big
        // keeps it set forever (every pcluster holds exactly one block, accounted by the HEAD bump);
        // big-pcluster clears it via the CBLKCNT entry.
        var dummyHead = !bigPcluster;

        while (entryIndex < clusters)
        {
            // 每个 pack 处理 vcnt 条（不足时余下条目以全零占位，读端不会访问超出 totalIdx 的簇）。
            // pack 尾部 4B 存“进入本 pack 时的当前块号”快照（读端经 pblk = packBlkaddr + 回看
            // 累计块数还原，与 erofs-utils 写端一致）。
            // Each pack handles vcnt entries (short packs are zero-padded; readers never access
            // clusters beyond totalIdx). The pack's trailing 4 bytes hold the block-address
            // snapshot taken when entering this pack (readers reconstruct
            // pblk = packBlkaddr + lookback count, matching the erofs-utils writer).
            var packStartBlkaddr = blkaddr;
            var bits = 0;
            for (var i = 0; i < vcnt; i++)
            {
                uint offset;
                byte type;
                if (entryIndex < clusters)
                {
                    var plan = pclusters[pclusterIndex];
                    var headCluster = plan.SourceOffset / blockSize;
                    var coveredClusters = CeilDiv(plan.SourceLength, blockSize);
                    if (entryIndex == headCluster)
                    {
                        // HEAD：clusterofs = 0；dummy_head 置位时自增运行块号并校验与规划一致。
                        // HEAD: clusterofs = 0; bump the running block address when dummy_head is
                        // set and verify against the plan.
                        type = 1; // Z_EROFS_LCLUSTER_TYPE_HEAD1
                        offset = 0;
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
                        type = 2; // Z_EROFS_LCLUSTER_TYPE_NONHEAD
                        var d0 = entryIndex - headCluster;
                        if (bigPcluster && d0 == 1)
                        {
                            // 首个 NONHEAD：CBLKCNT 条目，运行块号前移压缩块数（pcluster 的
                            // 物理块连续分配，读端回看据此还原下一 HEAD 的块号）。
                            // First NONHEAD: the CBLKCNT entry; the running block address advances
                            // by the compressed block count (pcluster blocks are allocated
                            // contiguously, so the reader's lookback reconstructs the next HEAD).
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
                            // pack 末槽存 delta[1]（erofs-utils：NONHEAD 末槽 delta[0] 不可达，
                            // 读端 delta[0] = 前条 + 1 间接还原；按 CBLKCNT-1 截断防误判）。
                            // The pack's last slot stores delta[1] (erofs-utils: delta[0] is
                            // unreachable there; readers derive delta[0] = previous-lo + 1;
                            // capped below CBLKCNT to avoid aliasing the flag).
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
                    type = 0; // 占位 PLAIN。Zero placeholder.
                    offset = 0;
                }

                // 位打包写入：v 占 encodebits 位，从 bits 偏移起写入 pack。
                // Bit-packed write: v occupies encodebits bits starting at bit offset "bits".
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

            // pack 尾部 4B 存进入时的块号快照（读端 pblk = packBlkaddr + 回看累计块数）。
            // The pack's trailing 4 bytes store the entry-time block snapshot (reader:
            // pblk = packBlkaddr + lookback count).
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
    /// 写出 packed inode 的碎片数据区：把每个碎片槽位按文件内偏移写入数据区对应位置
    /// （槽位之间未写的空白由镜像预分配/置零保证为零）。
    /// <para>Writes the packed inode's fragment data area: each fragment slot's source bytes are
    /// streamed to its offset inside the packed region (unwritten gaps stay zeroed by the image's
    /// preallocation).</para>
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
    /// 判断节点是否可作为 METABOX 容器驻留记录：普通文件（非目录/符号链接/设备/合成节点）、
    /// 存储为纯全块 flat（无内联尾部）、无压缩/碎片/xattr，且尺寸 ≤ 32 位（compact inode）。
    /// 解析侧只能自 metabox 槽位恢复这类“纯 32 字节紧凑记录”，其余节点（目录、符号链接、
    /// 设备、压缩/碎片/xattr 文件）留在经典 inode 表。
    /// <para>Whether a node qualifies as a METABOX container record: a regular file (not a directory /
    /// symlink / device / synthetic node) stored as pure whole-block flat data (no inline tail),
    /// without compression / fragments / xattrs, whose size fits 32 bits (compact inode). The read
    /// side can only recover such pure 32-byte compact records from a metabox slot; every other node
    /// (directories, symlinks, devices, compressed/fragment/xattr files) stays in the classic table.</para>
    /// </summary>
    private static bool IsMetaboxEligible(BuildNode node)
        => !node.IsDirectory
           && !node.IsSymlink
           && !node.IsDevice
           && !node.IsPacked
           && node.Size <= uint.MaxValue
           && node.Pclusters is null
           && !node.IsFragment
           && node.XattrIbody.Length == 0;

    /// <summary>
    /// 写出 metabox inode 的容器数据区：把每个容器驻留文件的 32 字节 compact inode 记录写到其
    /// 槽位（metabox inode 的 i_u 指向该数据区，i_size = 槽数 × 32）。
    /// <para>Writes the metabox inode's container data area: each resident file's 32-byte compact
    /// inode record is streamed to its slot (the metabox inode's i_u points at this data area and
    /// its i_size is slotCount × 32).</para>
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
    /// 写出 fragment inode 的 8 字节 z_erofs_map_header：h_fragmentoff = packed 区内字节偏移、
    /// h_clusterbits bit7（<see cref="ZErofsMapHeader.FragmentInodeMask"/>）声明整文件 fragment。
    /// header 起始位置与读端一致：inode + inline xattr 之后 8 字节对齐（<see
    /// cref="GetInodeRecordSize"/> 已把该填充计入记录长度）。
    /// <para>Writes a fragment inode's 8-byte z_erofs_map_header: h_fragmentoff is the byte offset
    /// inside the packed inode and h_clusterbits bit 7
    /// (<see cref="ZErofsMapHeader.FragmentInodeMask"/>) marks a whole-file fragment. The header
    /// starts where the reader expects it: 8-byte aligned after inode + inline xattrs (the pad is
    /// accounted for in <see cref="GetInodeRecordSize"/>).</para>
    /// </summary>
    private static void WriteFragmentHeader(Stream output, BuildNode node, ErofsInode inode, ErofsOptions options)
    {
        var headerOffset = node.MetaOffset + AlignUp(inode.OnDiskSize + inode.XattrTotalSize, 8);
        var header = new byte[ZErofsMapHeader.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)node.FragmentOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0); // h_advise：无额外标记
        header[6] = (byte)options.Algorithm;                            // h_algorithmtype（读端 fragment 路径不使用）
        header[7] = ZErofsMapHeader.FragmentInodeMask;                  // h_clusterbits bit7
        WriteAt(output, headerOffset, header, header.Length);
    }

    /// <summary>
    /// 流式计算整文件 SHA-256（IncrementalHash，分块读取，不整读大文件）。
    /// <para>Streams a whole file through SHA-256 via <see cref="IncrementalHash"/> in chunks
    /// without loading the file wholly into memory.</para>
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
    /// 选择下一碎片起点：避免 h_fragmentoff 的 bit7 与 read-side 的 fragment 标记掩码冲突
    /// （读端清除该位后仍应还原真实偏移）。packed 区内偏移按 256 对齐补齐越界位。
    /// <para>Picks the next fragment start so bit 7 of h_fragmentoff never collides with the
    /// reader's fragment-flag mask (the reader clears that bit, so real offsets must leave it
    /// zero); starts landing in [128..255] modulo 256 are rounded up to the next 256 boundary.</para>
    /// </summary>
    private static long AlignFragmentStart(long cursor)
        => (cursor & 0x80) == 0 ? cursor : (cursor & ~0xFFL) + 0x100L;

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
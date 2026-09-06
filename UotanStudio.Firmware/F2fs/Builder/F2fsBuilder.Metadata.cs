using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.F2fs;

public sealed partial class F2fsBuilder
{
    private static F2fsSuperblock CreateSuperblock(Layout layout, F2fsOptions options, bool hasCompressedFiles)
    {
        return new F2fsSuperblock
        {
            MagicValue = F2fsSuperblock.Magic,
            MajorVersion = 1,
            MinorVersion = 0,
            LogSectorsize = 9,
            LogSectorsPerBlock = 3,
            LogBlocksize = 12,
            LogBlocksPerSeg = 9,
            SegsPerSec = 1,
            SecsPerZone = 1,
            ChecksumOffset = 3068,
            // s_block_count = 镜像总块数（非仅主区用户块数）。
            // fsck.f2fs 校验 segment_count * blocks_per_seg == block_count 不变量。
            // s_block_count = total image block count (not just main-area user blocks).
            // fsck.f2fs enforces segment_count * blocks_per_seg == block_count invariant.
            BlockCount = (ulong)layout.ImageBlocks,
            SectionCount = layout.MainSegments,
            SegmentCount = layout.TotalSegments,
            SegmentCountCkpt = CkptSegments,
            SegmentCountSit = layout.SitSegments,
            SegmentCountNat = layout.NatSegments,
            SegmentCountSsa = layout.SsaSegments,
            SegmentCountMain = layout.MainSegments,
            Segment0Blkaddr = Segment0Blkaddr,
            CpBlkaddr = Segment0Blkaddr,
            SitBlkaddr = layout.SitBlkaddr,
            NatBlkaddr = layout.NatBlkaddr,
            SsaBlkaddr = layout.SsaBlkaddr,
            MainBlkaddr = layout.MainBlkaddr,
            RootIno = RootIno,
            NodeIno = NodeIno,
            MetaIno = MetaIno,
            Uuid = (options.Uuid ?? Guid.NewGuid()).ToByteArray(),
            VolumeName = string.IsNullOrEmpty(options.VolumeName) ? "f2fs" : options.VolumeName,
            CpPayload = 0,
            Version = new byte[256],
            // 仅声明镜像真实使用的特性：压缩文件存在时声明 F2FS_FEATURE_COMPRESSION（0x2000）。
            // inline data / inline dentry 在现代内核为核心特性，无需特性位。
            // Only declare features actually used: F2FS_FEATURE_COMPRESSION (0x2000) when compressed
            // files exist. Inline data / inline dentry are core features on modern kernels.
            Feature = hasCompressedFiles ? FeatureCompression : 0,
        };
    }

    private static void WriteSuperblock(Stream output, long baseOffset, F2fsSuperblock superblock, int blockSize)
    {
        var buffer = new byte[blockSize];
        superblock.WriteTo(buffer.AsSpan(F2fsSuperblock.SuperblockOffset, F2fsSuperblock.SuperblockSize));
        var crc = Crc32(F2fsSuperblock.Magic, buffer.AsSpan(F2fsSuperblock.SuperblockOffset, F2fsSuperblock.SuperblockSize - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(F2fsSuperblock.SuperblockOffset + 3068, 4), crc);
        WriteAt(output, baseOffset, buffer.AsSpan(0, blockSize));
        WriteAt(output, baseOffset + blockSize, buffer.AsSpan(0, blockSize));
    }

    /// <summary>
    /// 写出两个 checkpoint pack（mkfs.f2fs 布局）：pack 布局为
    /// [cp][compact data 摘要：n_nats(507B)+n_sits(507B)+data 条目][hot/warm/cold node 摘要]
    /// [cp 副本]，共 6 块（node 摘要按 sum_blk_addr(NR_CURSEG_NODE_TYPE, t) = start + total − 4 + t
    /// 定位，cp 副本在 total − 1）。
    /// <para>Writes both checkpoint packs (the mkfs.f2fs layout): each pack is
    /// [cp][compact data summary: n_nats(507B)+n_sits(507B)+data entries][hot/warm/cold node
    /// summaries][cp copy], 6 blocks total (node summaries at sum_blk_addr(NR_CURSEG_NODE_TYPE, t)
    /// = start + total − 4 + t, the cp copy at total − 1).</para>
    /// </summary>
    private static void WriteCheckpoints(Stream output, long baseOffset, Layout layout, CursegPlan plan, uint validNodeCount, uint validInodeCount)
    {
        var usedMainBlocks = layout.NodeBlocks + layout.DataBlocks;
        // overprov 段数：对齐 mkfs.f2fs（overprovision 默认 5%）：(main − rsvd) * 5% + rsvd。
        // Overprovision segments: mirrors mkfs.f2fs (default 5%): (main − rsvd) * 5% + rsvd.
        var overprovSegments = (layout.MainSegments - RsvdSegments) * 5 / 100 + RsvdSegments;

        var sitBitmapBytes = (uint)((layout.SitSegments / 2) * BlocksPerSeg / 8);
        var natBitmapBytes = (uint)((layout.NatSegments / 2) * BlocksPerSeg / 8);
        var versionBitmap = new byte[sitBitmapBytes + natBitmapBytes];

        F2fsCheckpoint CreateCheckpoint(ulong version)
            => new F2fsCheckpoint
            {
                CheckpointVer = version,
                // user_block_count = (main − overprov) × 每段块数（mkfs 语义）。
                // user_block_count = (main − overprov) × blocks-per-segment (mkfs semantics).
                UserBlockCount = ((ulong)layout.MainSegments - overprovSegments) * BlocksPerSeg,
                ValidBlockCount = (ulong)usedMainBlocks,
                RsvdSegmentCount = RsvdSegments,
                OverprovSegmentCount = overprovSegments,
                // 空闲段数与 SIT 严格一致（空 curseg 段按"有效"计入）。
                // Free segment count exactly matches the SIT (empty curseg segments count as valid).
                FreeSegmentCount = plan.FreeSegmentCount,
                // curseg 段号/blkoff：node 区与 data 区前 3 段；不足时为指派的空闲段。
                // Curseg segment numbers / blkoffs: the first 3 segments of each region, or the
                // assigned free segments when a region is smaller.
                CurNodeSegno = [plan.NodeSegno[0], plan.NodeSegno[1], plan.NodeSegno[2], uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue],
                CurNodeBlkoff = [plan.NodeBlkoff[0], plan.NodeBlkoff[1], plan.NodeBlkoff[2], 0, 0, 0, 0, 0],
                CurDataSegno = [plan.DataSegno[0], plan.DataSegno[1], plan.DataSegno[2], uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue],
                CurDataBlkoff = [plan.DataBlkoff[0], plan.DataBlkoff[1], plan.DataBlkoff[2], 0, 0, 0, 0, 0],
                // mkfs.f2fs 的干净布局：UMOUNT + COMPACT_SUM（compact data 摘要）。
                // The mkfs.f2fs clean layout: UMOUNT + COMPACT_SUM (compact data summaries).
                CkptFlags = F2fsCheckpoint.FlagUnmount | F2fsCheckpoint.FlagCompactSum,
                CpPackTotalBlockCount = CpPackTotalBlocks,
                CpPackStartSum = 1,
                ValidNodeCount = validNodeCount,
                ValidInodeCount = validInodeCount,
                NextFreeNid = layout.MaxNid + 1,
                SitVerBitmapBytesize = sitBitmapBytes,
                NatVerBitmapBytesize = natBitmapBytes,
                ChecksumOffset = F2fsCheckpoint.DefaultChecksumOffset,
                ElapsedTime = 0,
            };

        // 写 checkpoint pack 1（cp_blkaddr，cp_ver=1，与 mkfs 一致两 pack 同版本）与
        // pack 2（+512 块，同一 checkpoint 内容）。
        // Write checkpoint pack 1 (cp_blkaddr, cp_ver=1; mkfs uses the same version for both packs)
        // and pack 2 (+512 blocks, identical checkpoint content).
        var pack1Offset = checked(baseOffset + Segment0Blkaddr * F2fsNode.NodeSize);
        var pack2Offset = pack1Offset + (long)BlocksPerSeg * F2fsNode.NodeSize;
        WriteCheckpointPack(output, pack1Offset, CreateCheckpoint(1), versionBitmap, plan);
        WriteCheckpointPack(output, pack2Offset, CreateCheckpoint(1), versionBitmap, plan);
    }

    private static void WriteCheckpointPack(Stream output, long packOffset, F2fsCheckpoint checkpoint, byte[] versionBitmap, CursegPlan plan)
    {
        var block = new byte[F2fsNode.NodeSize];
        checkpoint.WriteTo(block, versionBitmap);
        var crc = Crc32(F2fsSuperblock.Magic, block.AsSpan(0, F2fsCheckpoint.DefaultChecksumOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(F2fsCheckpoint.DefaultChecksumOffset, 4), crc);

        // block 0 与 block 5（cp_pack_total_block_count − 1）：checkpoint 双副本，
        // fsck 的 validate_checkpoint 要求两副本 CRC 与版本一致。
        // Blocks 0 and 5 (cp_pack_total_block_count − 1): the two cp copies; fsck's
        // validate_checkpoint requires matching CRC and version across both.
        WriteAt(output, packOffset, block);
        WriteAt(output, packOffset + (CpPackTotalBlocks - 1) * F2fsNode.NodeSize, block);

        // block 1：compact data 摘要（read_compacted_summaries 布局）：
        //   [0, 507)      n_nats + nat journal（空：n_nats=0）
        //   [507, 1014)   n_sits + sit journal（空：n_sits=0）
        //   [1014, 4091)  hot/warm/cold data curseg 摘要条目（7 字节/条，Σ blkoff ≤ 439）
        //   footer.entry_type @4091 = SUM_TYPE_DATA
        // Block 1: compact data summary (read_compacted_summaries layout): empty nat/sit journals,
        // then the hot/warm/cold data curseg entries, footer = SUM_TYPE_DATA.
        var compact = new byte[F2fsNode.NodeSize];
        var offset = 2 * SumJournalSize;
        for (var t = 0; t < 3; t++)
        {
            var count = Math.Min(plan.DataBlkoff[t], CompactDataEntryCap - (offset - 2 * SumJournalSize) / SummarySize);
            for (var i = 0; i < count; i++)
            {
                var (nid, ofs) = plan.DataEntries[t][i];
                BinaryPrimitives.WriteUInt32LittleEndian(compact.AsSpan(offset, 4), nid);
                compact[offset + 4] = 0; // version
                BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(offset + 5, 2), ofs);
                offset += SummarySize;
            }
        }

        compact[F2fsNode.NodeSize - SumFooterSize] = 0; // SUM_TYPE_DATA
        WriteAt(output, packOffset + F2fsNode.NodeSize, compact);

        // blocks 2..4：hot/warm/cold node 摘要块（struct f2fs_summary_block：entries[512]@0，
        // 7 字节/条，条目 nid 为节点自身 NID；journal@3584 零；footer = SUM_TYPE_NODE）。
        // Blocks 2..4: hot/warm/cold node summary blocks (struct f2fs_summary_block: entries@0,
        // each entry's nid is the node block's own NID; journal zero; footer = SUM_TYPE_NODE).
        for (var t = 0; t < 3; t++)
        {
            var summary = new byte[F2fsNode.NodeSize];
            FillSummaryEntries(summary, plan.NodeEntries[t], plan.NodeBlkoff[t]);
            summary[F2fsNode.NodeSize - SumFooterSize] = 1; // SUM_TYPE_NODE
            WriteAt(output, packOffset + (2 + t) * F2fsNode.NodeSize, summary);
        }
    }

    /// <summary>
    /// 把 curseg 摘要条目写入 summary_block 的 entries 区（偏移 0 起，7 字节/条，
    /// struct f2fs_summary：nid(4) + version(1) + ofs_in_node(2)，小端）。
    /// <para>Writes curseg summary entries into the entries area of a summary_block (from offset 0,
    /// 7 bytes each; struct f2fs_summary: nid(4) + version(1) + ofs_in_node(2), little-endian).</para>
    /// </summary>
    private static void FillSummaryEntries(byte[] summaryBlock, List<(uint Nid, ushort Ofs)> entries, int count)
    {
        for (var i = 0; i < count && i < EntriesInSum; i++)
        {
            var (nid, ofs) = entries[i];
            var offset = i * SummarySize;
            BinaryPrimitives.WriteUInt32LittleEndian(summaryBlock.AsSpan(offset, 4), nid);
            summaryBlock[offset + 4] = 0; // version
            BinaryPrimitives.WriteUInt16LittleEndian(summaryBlock.AsSpan(offset + 5, 2), ofs);
        }
    }

    /// <summary>
    /// 写出 SIT 区：每主区段的 f2fs_sit_entry（74 字节：vblocks(2) + valid_map(64) + mtime(8)）。
    /// vblocks[15:10] 为分配类型（GET_SIT_TYPE）、[9:0] 为有效块数；双副本寻址对齐内核
    /// get_current_sit_page：主副本 sit_blkaddr + 块号，第二副本 +sit_blocks（sit_segs/2 × 512）。
    /// <para>Writes the SIT area: one f2fs_sit_entry (74 bytes: vblocks(2) + valid_map(64) +
    /// mtime(8)) per main-area segment. vblocks[15:10] holds the allocation type (GET_SIT_TYPE) and
    /// [9:0] the valid block count; the dual-copy addressing follows the kernel's
    /// get_current_sit_page: primary at sit_blkaddr + block index, second copy +sit_blocks
    /// (sit_segs/2 × 512).</para>
    /// </summary>
    private static void WriteSit(Stream output, long baseOffset, Layout layout, CursegPlan plan, int blockSize)
    {
        var sitBlocks = (long)(layout.SitSegments / 2) * BlocksPerSeg;
        var block = new byte[blockSize];
        var currentBlockIndex = -1L;
        foreach (var (segno, usage) in plan.Segments.OrderBy(static kvp => kvp.Key))
        {
            var (count, map, type) = usage;
            var blockIndex = segno / F2fsNode.SitEntryPerBlock;
            if (blockIndex != currentBlockIndex)
            {
                if (currentBlockIndex >= 0)
                {
                    FlushSitBlock(output, baseOffset, layout, sitBlocks, currentBlockIndex, block);
                }

                block.AsSpan().Clear();
                currentBlockIndex = blockIndex;
            }

            // vblocks = (type << 10) | count（SIT_VBLOCKS_SHIFT = 10）。
            // vblocks = (type << 10) | count (SIT_VBLOCKS_SHIFT = 10).
            var vblocks = (ushort)((type << 10) | count);
            var entryIndex = segno % F2fsNode.SitEntryPerBlock;
            var entryOffset = checked((int)entryIndex * 74);
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(entryOffset, 2), vblocks);
            map.CopyTo(block, entryOffset + 2);
            // mtime @ entryOffset+66（__le64）保持 0：构建时镜像无段年龄信息。
            // mtime @ entryOffset+66 (__le64) stays 0: no segment age for a freshly built image.
        }

        if (currentBlockIndex >= 0)
        {
            FlushSitBlock(output, baseOffset, layout, sitBlocks, currentBlockIndex, block);
        }
    }

    /// <summary>把累积好的 SIT 块写入主副本与第二副本（内核 get_current_sit_page 寻址）。
    /// Flushes an accumulated SIT block to its primary and second copies (kernel
    /// get_current_sit_page addressing).</summary>
    private static void FlushSitBlock(Stream output, long baseOffset, Layout layout, long sitBlocks, long blockIndex, byte[] block)
    {
        // 上游寻址：主副本 sit_blkaddr + 块号，第二副本 +sit_blocks（sit_segs/2 × 512）。
        // Upstream addressing: primary at sit_blkaddr + block index, second copy +sit_blocks
        // (sit_segs/2 × 512).
        var primary = (long)layout.SitBlkaddr + blockIndex;
        WriteAt(output, baseOffset + primary * F2fsNode.NodeSize, block);
        WriteAt(output, baseOffset + (primary + sitBlocks) * F2fsNode.NodeSize, block);
    }

    private static void WriteNat(Stream output, long baseOffset, Layout layout, Dictionary<uint, (uint Ino, uint Block)> nat)
    {
        // NAT 寻址对齐内核 current_nat_addr：NAT 块按每 512 块分组，组 g 的主副本位于
        // nat_blkaddr + g*1024 + 块组内序号，nat_bitmap 位清零时读主副本，第二副本 +512 块。
        // NAT addressing follows the kernel's current_nat_addr: blocks are grouped per 512; group
        // g's primary copy sits at nat_blkaddr + g*1024 + index, and with a zeroed nat_bitmap the
        // primary copy is read while the second copy lives 512 blocks further.
        var grouped = new Dictionary<uint, Dictionary<uint, (uint Ino, uint Block)>>();
        foreach (var (nid, entry) in nat)
        {
            var blockIndex = nid / F2fsNode.NatEntryPerBlock;
            if (!grouped.TryGetValue(blockIndex, out var entries))
            {
                entries = new Dictionary<uint, (uint Ino, uint Block)>();
                grouped[blockIndex] = entries;
            }

            entries[nid % F2fsNode.NatEntryPerBlock] = entry;
        }

        foreach (var (blockIndex, entries) in grouped)
        {
            var natBlock = new byte[F2fsNode.NodeSize];
            foreach (var (entryIndex, entry) in entries)
            {
                var offset = checked((int)entryIndex * F2fsNode.NatEntrySize);
                natBlock[offset] = 0; // version
                BinaryPrimitives.WriteUInt32LittleEndian(natBlock.AsSpan(offset + F2fsNode.NatEntryInoOffset, 4), entry.Ino);
                BinaryPrimitives.WriteUInt32LittleEndian(natBlock.AsSpan(offset + F2fsNode.NatEntryBlockOffset, 4), entry.Block);
            }

            // NAT 块恰好容纳 455 个 9 字节条目（字节 0..4094），尾部 4 字节不写校验和（与内核一致：
            // 读侧只校验 superblock/checkpoint 的 CRC）。
            // A NAT block holds exactly 455 9-byte entries (bytes 0..4094); no trailing checksum is
            // written (the kernel's read side only verifies superblock/checkpoint CRCs).
            var segOff = blockIndex / BlocksPerSeg;
            var inSeg = blockIndex % BlocksPerSeg;
            var primary = (long)layout.NatBlkaddr + segOff * 2 * BlocksPerSeg + inSeg;
            WriteAt(output, baseOffset + primary * F2fsNode.NodeSize, natBlock);
            WriteAt(output, baseOffset + (primary + BlocksPerSeg) * F2fsNode.NodeSize, natBlock);
        }
    }

    private static void WriteMainArea(Stream output, long baseOffset, List<BuildNode> ordered, int blockSize)
    {
        foreach (var node in ordered)
        {
            WriteInodeNode(output, baseOffset, node, blockSize);
            if (!node.IsDirectory && !node.InlineData)
            {
                foreach (var dnode in node.Dnodes)
                {
                    WriteDnode(output, baseOffset, node, dnode, blockSize);
                }

                WriteFileDataBlocks(output, baseOffset, node, blockSize);
            }
            else if (node.IsDirectory)
            {
                for (var i = 0; i < node.DirBlocks.Count; i++)
                {
                    WriteAt(output, baseOffset + node.DirBlockAddrs[i] * blockSize, node.DirBlocks[i]);
                }
            }
        }
    }

    /// <summary>
    /// 写出 SSA（Segment Summary Area）：每个有有效块的主区段一块（ssa_blkaddr + segno），
    /// summary_block 布局为 entries[512]@0（7 字节/条）+ journal@3584（零）+ summary_footer@4091
    /// （entry_type：node 段=1、data 段=0）。curseg 段的摘要由 cp pack 承载，此处同时写出磁盘
    /// SSA 供非 curseg 段（溢出段）查询与工具检查。
    /// <para>Writes the SSA (Segment Summary Area): one block per main-area segment with valid
    /// blocks (at ssa_blkaddr + segno), in the summary_block layout entries[512]@0 (7 bytes each) +
    /// journal@3584 (zero) + summary_footer@4091 (entry_type: 1 for node segments, 0 for data).
    /// Curseg segments carry their summaries in the cp pack; the on-disk SSA is still written for
    /// overflow (non-curseg) segment lookups and tooling.</para>
    /// </summary>
    private static void WriteSsa(Stream output, long baseOffset, Layout layout, CursegPlan plan, int blockSize)
    {
        // 主区段号为主区相对（segment 0 = 第一个主区段），与内核/工具约定一致。
        // Segment numbers are main-area-relative (segment 0 = the first main-area segment),
        // matching the kernel/tooling convention.
        var mainBase = 0u;
        foreach (var (segno, entries) in plan.SegmentEntries)
        {
            var block = new byte[blockSize];
            foreach (var (ofsInSeg, (nid, ofsInNode)) in entries)
            {
                var offset = ofsInSeg * SummarySize;
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset, 4), nid);
                block[offset + 4] = 0; // version
                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(offset + 5, 2), ofsInNode);
            }

            block[F2fsNode.NodeSize - SumFooterSize] = IsNodeSegment(segno, mainBase, layout, plan)
                ? (byte)1 // SUM_TYPE_NODE
                : (byte)0; // SUM_TYPE_DATA
            var ssaBlockAddr = (long)layout.SsaBlkaddr + segno;
            WriteAt(output, baseOffset + ssaBlockAddr * blockSize, block);
        }
    }

    /// <summary>段是否为 node 段（node 区段或被指派为 node curseg 的空闲段）。
    /// Whether a segment is a node segment (node-region segments or free segments assigned to a
    /// node curseg).</summary>
    private static bool IsNodeSegment(uint segno, uint mainBase, Layout layout, CursegPlan plan)
        => segno - mainBase < layout.NodeSegments
            || plan.NodeSegno.Contains(segno);

    private static void WriteInodeNode(Stream output, long baseOffset, BuildNode node, int blockSize)
    {
        var block = new byte[blockSize];
        var inline = node.IsDirectory ? 0 : (node.InlineData ? (F2fsInode.InlineDataFlag | F2fsInode.DataExistFlag) : 0);
        var inode = new F2fsInode
        {
            Mode = node.Mode != 0 ? node.Mode
                : node.IsDirectory ? ModeDirectory
                : node.IsSymlink ? ModeSymbolicLink
                : ModeRegularFile,
            Inline = (byte)inline,
            Uid = node.Uid,
            Gid = node.Gid,
            Links = node.IsDirectory ? (uint)(2 + node.Children.Count(static c => c.IsDirectory)) : 1u,
            Size = node.IsDirectory ? (ulong)node.Size : (ulong)(node.IsSymlink ? Encoding.UTF8.GetByteCount(node.SymlinkTarget ?? string.Empty) : node.Size),
            // i_blocks 以 4 KB 块为单位且含节点块自身（内核 i_blocks 语义；mkfs 根为 2 =
            // 1 inode + 1 dentry 块）。inline 文件无额外数据块。
            // i_blocks counts 4 KB blocks including the node block itself (kernel i_blocks
            // semantics; mkfs root is 2 = 1 inode + 1 dentry block). Inline files add none.
            Blocks = node.IsDirectory
                ? 1UL + (ulong)node.DirBlocks.Count
                : node.InlineData
                    ? 1UL
                    : 1UL + (ulong)(node.AllocatedDataBlockCount + node.Dnodes.Count),
            XattrNid = 0,
            // 压缩文件：置 F2FS_COMPR 标志（读侧按 i_flags bit 27 路由），并写内核算法 ID 与
            // 簇大小对数（i_advise 低 4 位 = 算法，高 4 位 = log_cluster_size）。
            // Compressed file: set the COMPR flag (the read side routes on i_flags bit 27) and write
            // the kernel algorithm id and the log cluster size (i_advise low nibble = algorithm,
            // high nibble = log_cluster_size).
            Flags = node.IsCompressedFile ? F2fsInode.ComprFlag : 0,
            CompressAlgorithm = node.IsCompressedFile
                ? KernelCompressionAlgorithmId(node.CompressedAlgorithm)
                : (byte)0,
            LogClusterSize = node.IsCompressedFile
                ? LogClusterSizeFromBytes(F2fsCompression.ClusterBytes)
                : (byte)0,
            Pino = node.Parent?.Ino ?? 0,
            // 目录 i_current_depth = 已用哈希桶层数（内核 dir.c 语义）。
            // Directory i_current_depth = the number of hash-bucket levels in use (kernel dir.c).
            CurrentDepth = node.IsDirectory ? node.DirDepth : 0u,
            Namelen = (uint)Encoding.UTF8.GetByteCount(node.Name),
            Name = Encoding.UTF8.GetBytes(node.Name),
            Extent = node.Extent,
            Addr = BuildInodeAddrs(node),
            Nid = BuildInodeNids(node),
            Mtime = node.Mtime > 0 ? (uint)node.Mtime : 0,
            MtimeNsec = 0,
            Ctime = node.Ctime > 0 ? (uint)node.Ctime : 0,
            CtimeNsec = 0,
            ExtraIsize = 0,
            InlineXattrSize = 0,
        };
        inode.WriteTo(block);
        if (node.InlineData)
        {
            var inlineOffset = inode.InlineDataOffset;
            node.InlineBytes.AsSpan(0, Math.Min(node.InlineBytes.Length, blockSize - inlineOffset))
                .CopyTo(block.AsSpan(inlineOffset));
        }

        F2fsNodeFooter.WriteTo(block, node.Ino, node.Ino, 0, 1, node.InodeBlockAddr + 1);
        WriteAt(output, baseOffset + node.InodeBlockAddr * blockSize, block);
    }

    private static void WriteDnode(Stream output, long baseOffset, BuildNode file, DnodePlan dnode, int blockSize)
    {
        var block = new byte[blockSize];
        var flag = checked((uint)(dnode.LogicalStart << F2fsNodeFooter.OffsetBitShift));
        if (dnode.IsIndirect || dnode.IsDoubleIndirect)
        {
            for (var i = 0; i < dnode.IndirectChildNids.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(i * 4, 4), dnode.IndirectChildNids[i]);
            }
        }
        else
        {
            for (var i = 0; i < F2fsNode.AddrsPerBlock; i++)
            {
                var logical = dnode.LogicalStart + i;
                var address = logical < file.DataBlockAddrs.Count ? file.DataBlockAddrs[(int)logical] : 0u;
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(i * 4, 4), address);
            }
        }

        F2fsNodeFooter.WriteTo(block, dnode.Nid, file.Ino, flag, 1, dnode.BlockAddr + 1);
        WriteAt(output, baseOffset + dnode.BlockAddr * blockSize, block);
    }

    private static void WriteFileDataBlocks(Stream output, long baseOffset, BuildNode node, int blockSize)
    {
        if (node.InlineData || node.DataBlockCount == 0)
        {
            return;
        }

        if (node.IsCompressedFile)
        {
            WriteCompressedFileDataBlocks(output, baseOffset, node, blockSize);
            return;
        }

        using var source = node.IsSymlink
            ? (Stream)new MemoryStream(Encoding.UTF8.GetBytes(node.SymlinkTarget ?? string.Empty), writable: false)
            : File.OpenRead(node.FullPath);
        var buffer = new byte[1 << 16];
        for (var logical = 0; logical < node.DataBlockCount; logical++)
        {
            var address = node.DataBlockAddrs[logical];
            var remaining = blockSize;
            while (remaining > 0)
            {
                var read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    // 文件末尾：剩余块尾补零（镜像已预分配为零）。EOF: the tail of the last
                    // block is zero-filled (the image is pre-allocated as zeros).
                    break;
                }

                if (address != 0)
                {
                    var blockOffset = checked(address * blockSize + (blockSize - remaining));
                    WriteAt(output, baseOffset + blockOffset, buffer.AsSpan(0, read));
                }

                remaining -= read;
            }
        }
    }

    /// <summary>
    /// 压缩文件的第二遍数据写出：按 64 KB 簇流式重读源文件。规划为压缩的簇重新压缩后把
    /// 含头载荷写入该簇起始逻辑槽位后的物理块（与读侧“从簇首块读簇头、再按 logical+1.. 续读”
    /// 的布局一致）；规划为普通块的簇（完整但不可压缩 / 末尾部分簇）逐逻辑块写出原始数据。
    /// <para>Second-pass data write for a compressed file: re-streams the source in 64 KB clusters.
    /// Clusters planned as compressed are re-compressed and their payload (with header) is written
    /// to the physical blocks behind the cluster's leading logical slots (matching the read side,
    /// which reads the cluster header from the first block and continues at logical+1..); clusters
    /// planned as raw (full but incompressible / partial trailing) are written block by block.</para>
    /// </summary>
    private static void WriteCompressedFileDataBlocks(Stream output, long baseOffset, BuildNode node, int blockSize)
    {
        var clusterBytes = F2fsCompression.ClusterBytes;
        var clusterPages = F2fsCompression.ClusterPages;
        var algorithm = node.CompressedAlgorithm;
        var level = node.CompressionLevel;
        var clusterBuffer = new byte[clusterBytes];

        using var source = File.OpenRead(node.FullPath);
        for (var c = 0; c < node.CompressedClusterSizes!.Length; c++)
        {
            var logical = c * clusterPages;
            var clusterBlocks = Math.Min(clusterPages, node.DataBlockCount - logical);
            var toRead = (int)Math.Min(clusterBytes, Math.Max(0, node.Size - (long)logical * blockSize));
            ReadExactly(source, clusterBuffer, toRead);

            var plannedPayloadLength = node.CompressedClusterSizes[c];
            if (plannedPayloadLength >= 0)
            {
                // 压缩簇：重压并与规划比对（同一文件两遍读应确定性一致），随后写含头载荷。
                // Compressed cluster: re-compress and cross-check the planned size (reading the same
                // file twice must be deterministic), then write the payload with its header.
                var payload = F2fsCompression.CompressCluster(
                    clusterBuffer.AsSpan(0, clusterBytes),
                    algorithm,
                    level);
                if (payload is null || payload.Length != plannedPayloadLength)
                {
                    throw new InvalidOperationException(
                        $"F2FS source file '{node.FullPath}' changed between the layout and write passes "
                        + $"(cluster {c}).  F2FS 源文件在两遍构建之间发生了变化。");
                }

                var payloadBlocks = (payload.Length + blockSize - 1) / blockSize;
                for (var b = 0; b < payloadBlocks; b++)
                {
                    var address = node.DataBlockAddrs[logical + b];
                    if (address == 0)
                    {
                        throw new InvalidOperationException(
                            $"F2FS compressed cluster {c} of '{node.FullPath}' has a missing block address.");
                    }

                    var length = Math.Min(blockSize, payload.Length - b * blockSize);
                    WriteAt(output, baseOffset + address * blockSize, payload.AsSpan(b * blockSize, length));
                }
            }
            else
            {
                // 普通块簇：逐逻辑块写出原始数据（块尾留零，镜像已预分配为零）。
                // Raw cluster: write the source bytes block by block (block tails stay zero from the
                // pre-allocated image).
                for (var b = 0; b < clusterBlocks; b++)
                {
                    var address = node.DataBlockAddrs[logical + b];
                    var sourceOffset = b * blockSize;
                    var length = Math.Min(blockSize, Math.Max(0, toRead - sourceOffset));
                    if (length > 0 && address != 0)
                    {
                        WriteAt(output, baseOffset + address * blockSize, clusterBuffer.AsSpan(sourceOffset, length));
                    }
                }
            }
        }
    }

    private static uint[] BuildInodeAddrs(BuildNode node)
    {
        var addrs = new uint[F2fsNode.DefAddrsPerInode];
        if (node.IsDirectory)
        {
            for (var i = 0; i < node.DirBlockAddrs.Count; i++)
            {
                addrs[i] = node.DirBlockAddrs[i];
            }
        }
        else if (!node.InlineData)
        {
            for (var i = 0; i < node.DataBlockAddrs.Count && i < addrs.Length; i++)
            {
                addrs[i] = node.DataBlockAddrs[i];
            }
        }

        return addrs;
    }

    private static uint[] BuildInodeNids(BuildNode node)
    {
        var nids = new uint[F2fsNode.DefNidsPerInode];
        if (node.IsDirectory || node.InlineData)
        {
            return nids;
        }

        // 前两个非间接 dnode（direct 节点）占据 i_nid[0]/i_nid[1]。
        // The first two non-indirect dnodes (direct nodes) occupy i_nid[0]/i_nid[1].
        var index = 0;
        foreach (var dnode in node.Dnodes)
        {
            if (!dnode.IsIndirect && !dnode.IsDoubleIndirect && index < 2)
            {
                nids[index] = dnode.Nid;
            }

            index++;
        }

        // i_nid[2] = 文件自身的 single indirect 节点；i_nid[3] = double indirect 节点。
        // 必须用显式引用而非遍历顺序：double indirect 子树内的 indirect child 也带
        // IsIndirect 标记且排在 single-indirect 节点之后，按顺序赋值会错误覆盖 i_nid[2]。
        // i_nid[2] = the file's own single-indirect node; i_nid[3] = the double-indirect node.
        // Explicit references are required instead of iteration order: indirect children inside
        // the double-indirect subtree also carry IsIndirect and are appended after the real
        // single-indirect node, so positional assignment would wrongly overwrite i_nid[2].
        if (node.IndirectNode is not null)
        {
            nids[2] = node.IndirectNode.Nid;
        }

        if (node.DoubleIndirectNode is not null)
        {
            nids[3] = node.DoubleIndirectNode.Nid;
        }

        return nids;
    }

    private static bool IsSafeName(string name)
        => !name.Contains('/') && !name.Contains('\\') && !name.Contains('\0') && name != "." && name != "..";

    private static byte[] ReadFileBytes(string fullPath, long size)
    {
        using var stream = File.OpenRead(fullPath);
        var result = new byte[size];
        var read = 0;
        while (read < size)
        {
            var n = stream.Read(result, read, (int)Math.Min(size - read, int.MaxValue));
            if (n <= 0)
            {
                throw new EndOfStreamException($"Unexpected end of file '{fullPath}'.");
            }

            read += n;
        }

        return result;
    }

    private static void WriteAt(Stream output, long offset, ReadOnlySpan<byte> data)
    {
        output.Position = offset;
        output.Write(data);
    }
}
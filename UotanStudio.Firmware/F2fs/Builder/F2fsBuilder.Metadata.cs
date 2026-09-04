using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.F2fs;

public sealed partial class F2fsBuilder
{
    private static F2fsSuperblock CreateSuperblock(Layout layout, F2fsOptions options)
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
            Feature = 0,
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

    private static void WriteCheckpoints(Stream output, long baseOffset, Layout layout, byte[] natBitmap, byte[] sitBitmap, uint validNodeCount, uint validInodeCount, long usedMainBlocks)
    {
        var versionBitmap = new byte[checked(natBitmap.Length + sitBitmap.Length)];

        F2fsCheckpoint CreateCheckpoint(ulong version)
            => new F2fsCheckpoint
            {
                CheckpointVer = version,
                UserBlockCount = (ulong)layout.MainSegments * BlocksPerSeg,
                ValidBlockCount = (ulong)usedMainBlocks,
                CurNodeSegno = [0, 0, 0, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue],
                CurDataSegno = [0, 0, 0, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue],
                CkptFlags = F2fsCheckpoint.FlagUnmount | F2fsCheckpoint.FlagCompactSum,
                CpPackTotalBlockCount = 6,
                CpPackStartSum = 1,
                ValidNodeCount = validNodeCount,
                ValidInodeCount = validInodeCount,
                NextFreeNid = layout.MaxNid + 1,
                SitVerBitmapBytesize = layout.SitSegments * 64,
                NatVerBitmapBytesize = layout.NatSegments * 64,
                ChecksumOffset = F2fsCheckpoint.DefaultChecksumOffset,
                ElapsedTime = 0,
            };

        // 写 checkpoint pack 1（cp_blkaddr，cp_ver 奇数）与 pack 2（+512 块，cp_ver=0）。
        // Write checkpoint pack 1 (cp_blkaddr, odd cp_ver) and pack 2 (+512 blocks, cp_ver=0).
        var pack1Block = checked(baseOffset + Segment0Blkaddr * F2fsNode.NodeSize);
        var pack2Block = pack1Block + (long)BlocksPerSeg * F2fsNode.NodeSize;
        WriteCheckpointPack(output, pack1Block, CreateCheckpoint(1), versionBitmap);
        WriteCheckpointPack(output, pack2Block, CreateCheckpoint(0), versionBitmap);
    }

    private static void WriteCheckpointPack(Stream output, long packOffset, F2fsCheckpoint checkpoint, byte[] versionBitmap)
    {
        var block = new byte[F2fsNode.NodeSize];
        checkpoint.WriteTo(block, versionBitmap);
        var crc = Crc32(F2fsSuperblock.Magic, block.AsSpan(0, F2fsCheckpoint.DefaultChecksumOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(F2fsCheckpoint.DefaultChecksumOffset, 4), crc);

        // block 0 / block 5：checkpoint 双副本。Blocks 0/5: the two checkpoint copies.
        WriteAt(output, packOffset, block);
        WriteAt(output, packOffset + 5 * F2fsNode.NodeSize, block);

        // block 1：紧凑 data 摘要（footer SUM_TYPE_DATA）。Block 1: compact data summary.
        var summary = new byte[F2fsNode.NodeSize];
        summary[F2fsNode.NodeSize - 5] = 0; // SUM_TYPE_DATA
        WriteAt(output, packOffset + F2fsNode.NodeSize, summary);

        // blocks 2-4：node 摘要（footer SUM_TYPE_NODE）。Blocks 2-4: node summaries.
        for (var i = 0; i < 3; i++)
        {
            summary[F2fsNode.NodeSize - 5] = 1; // SUM_TYPE_NODE
            WriteAt(output, packOffset + (2 + i) * F2fsNode.NodeSize, summary);
        }
    }

    private static void WriteSit(Stream output, long baseOffset, Layout layout, long usedMainBlocks, int blockSize)
    {
        // 计算每个段的使用情况（仅主区内实际分配的块）。Compute usage per segment (only blocks
        // actually allocated in the main area).
        var usedBlocks = new Dictionary<uint, (uint Count, byte[] Map)>();
        var usedSegnos = new HashSet<uint>();
        for (var block = 0L; block < usedMainBlocks; block++)
        {
            var segno = (uint)(block / BlocksPerSeg);
            var offsetInSeg = (int)(block % BlocksPerSeg);
            if (!usedBlocks.TryGetValue(segno, out var entry))
            {
                entry = (0, new byte[F2fsNode.SitValidMapSize]);
                usedBlocks[segno] = entry;
            }

            entry.Map[offsetInSeg >> 3] |= (byte)(1 << (7 - (offsetInSeg & 7)));
            usedBlocks[segno] = (entry.Count + 1, entry.Map);
            usedSegnos.Add(segno);
        }

        var sitBlock = new byte[blockSize];
        foreach (var segno in usedSegnos)
        {
            var entry = usedBlocks[segno];
            var blockIndex = segno / F2fsNode.SitEntryPerBlock;
            var entryIndex = segno % F2fsNode.SitEntryPerBlock;
            var baseSitBlock = (layout.SitBlkaddr + blockIndex * 2L) * blockSize;
            sitBlock.AsSpan().Clear();
            var entryOffset = checked((int)entryIndex * 74);
            BinaryPrimitives.WriteUInt16LittleEndian(sitBlock.AsSpan(entryOffset, 2), (ushort)entry.Count);
            entry.Map.CopyTo(sitBlock, entryOffset + 2);
            WriteAt(output, baseOffset + baseSitBlock, sitBlock);
            WriteAt(output, baseOffset + baseSitBlock + blockSize, sitBlock);
        }
    }

    private static void WriteNat(Stream output, long baseOffset, Layout layout, Dictionary<uint, (uint Ino, uint Block)> nat)
    {
        // NAT 块按 2 块间隔存放（内核 current_nat_addr 读取 nat_blkaddr + 2*block_off）。
        // NAT blocks are stored 2 apart (the kernel reads nat_blkaddr + 2*block_off).
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

            // NAT 块恰好容纳 455 个 9 字节条目（字节 0..4094），仅剩 1 字节，无处再存放尾部
            // 4 字节 CRC。若把 CRC 写到 4088..4091，会覆盖最后一条目（entryIndex=454）的
            // ino/block_addr 字段，损坏该 NID 的 NAT 映射——该 bug 仅在 NID ≡ 454 (mod 455)
            // 真正被分配时暴露（本仓库大文件测试的 C_447 即 NID 454）。与内核一致：满条目
            // NAT 块不写块级校验和（读侧也只校验 superblock/checkpoint 的 CRC）。
            // A NAT block holds exactly 455 9-byte entries (bytes 0..4094) plus a single spare
            // byte, leaving no room for a trailing 4-byte CRC. Writing one at 4088..4091 would
            // clobber the last entry's (entryIndex=454) ino/block_addr fields and corrupt that
            // NID's NAT mapping — latent until NID ≡ 454 (mod 455) is actually allocated (here,
            // the C_447 direct node of the large-file test). Consistent with the kernel: full NAT
            // blocks carry no per-block checksum (the read side only verifies superblock and
            // checkpoint CRCs).

            var baseNatBlock = (layout.NatBlkaddr + blockIndex * 2L) * F2fsNode.NodeSize;
            WriteAt(output, baseOffset + baseNatBlock, natBlock);
            WriteAt(output, baseOffset + baseNatBlock + F2fsNode.NodeSize, natBlock);
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
            Blocks = node.IsDirectory
                ? (ulong)node.DirBlocks.Count * (ulong)(blockSize / 512)
                : node.InlineData
                    ? 0
                    : (ulong)(node.AllocatedDataBlockCount + node.Dnodes.Count) * (ulong)(blockSize / 512),
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
            CurrentDepth = node.IsDirectory ? 1u : 0u,
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

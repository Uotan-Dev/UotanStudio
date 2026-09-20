using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;

public sealed partial class F2fsBuilder
{
    private static F2fsSuperblock CreateSuperblock(Layout layout, F2fsOptions options, bool hasCompressedFiles, bool hasXattrs)
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
            VolumeName = string.IsNullOrEmpty(options.VolumeName) && UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding.FileSystemEditPlan.Current is null ? "f2fs" : options.VolumeName,
            CpPayload = 0,
            Version = new byte[256],


            Feature = (hasCompressedFiles ? FeatureCompression | 0x0008u : 0) | (hasXattrs ? 0x48u : 0),
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
    /// Writes both six-block checkpoint packs with compact data and node summaries.
    /// <para>写入两个六块布局的检查点包，包含紧凑数据摘要和节点摘要。</para>
    /// </summary>
    private static void WriteCheckpoints(Stream output, long baseOffset, Layout layout, CursegPlan plan, uint validNodeCount, uint validInodeCount)
    {
        var usedMainBlocks = layout.NodeBlocks + layout.DataBlocks + layout.ReservedCompressionBlocks;


        var overprovSegments = (layout.MainSegments - RsvdSegments) * 5 / 100 + RsvdSegments;

        var sitBitmapBytes = (uint)((layout.SitSegments / 2) * BlocksPerSeg / 8);
        var natBitmapBytes = (uint)((layout.NatSegments / 2) * BlocksPerSeg / 8);
        var versionBitmap = new byte[sitBitmapBytes + natBitmapBytes];

        F2fsCheckpoint CreateCheckpoint(ulong version)
            => new F2fsCheckpoint
            {
                CheckpointVer = version,


                UserBlockCount = ((ulong)layout.MainSegments - overprovSegments) * BlocksPerSeg,
                ValidBlockCount = (ulong)usedMainBlocks,
                RsvdSegmentCount = RsvdSegments,
                OverprovSegmentCount = overprovSegments,


                FreeSegmentCount = plan.FreeSegmentCount,


                CurNodeSegno = [plan.NodeSegno[0], plan.NodeSegno[1], plan.NodeSegno[2], uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue],
                CurNodeBlkoff = [plan.NodeBlkoff[0], plan.NodeBlkoff[1], plan.NodeBlkoff[2], 0, 0, 0, 0, 0],
                CurDataSegno = [plan.DataSegno[0], plan.DataSegno[1], plan.DataSegno[2], uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue],
                CurDataBlkoff = [plan.DataBlkoff[0], plan.DataBlkoff[1], plan.DataBlkoff[2], 0, 0, 0, 0, 0],


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


        WriteAt(output, packOffset, block);
        WriteAt(output, packOffset + (CpPackTotalBlocks - 1) * F2fsNode.NodeSize, block);


        var compact = new byte[F2fsNode.NodeSize];
        var offset = 2 * SumJournalSize;
        for (var t = 0; t < 3; t++)
        {
            var count = Math.Min(plan.DataBlkoff[t], CompactDataEntryCap - (offset - 2 * SumJournalSize) / SummarySize);
            for (var i = 0; i < count; i++)
            {
                var (nid, ofs) = plan.DataEntries[t][i];
                BinaryPrimitives.WriteUInt32LittleEndian(compact.AsSpan(offset, 4), nid);
                compact[offset + 4] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(offset + 5, 2), ofs);
                offset += SummarySize;
            }
        }

        compact[F2fsNode.NodeSize - SumFooterSize] = 0;
        WriteAt(output, packOffset + F2fsNode.NodeSize, compact);


        for (var t = 0; t < 3; t++)
        {
            var summary = new byte[F2fsNode.NodeSize];
            FillSummaryEntries(summary, plan.NodeEntries[t], plan.NodeBlkoff[t]);
            summary[F2fsNode.NodeSize - SumFooterSize] = 1;
            WriteAt(output, packOffset + (2 + t) * F2fsNode.NodeSize, summary);
        }
    }

    /// <summary>
    /// Writes curseg summary entries into the entries area of a summary_block (from offset 0, 7 bytes each; struct f2fs_summary: nid(4) + version(1) + ofs_in_node(2), little-endian).
    /// <para>把 curseg 摘要条目写入 summary_block 的 entries 区（偏移 0 起，7 字节/条， struct f2fs_summary：nid(4) + version(1) + ofs_in_node(2)，小端）。</para>
    /// </summary>
    private static void FillSummaryEntries(byte[] summaryBlock, List<(uint Nid, ushort Ofs)> entries, int count)
    {
        for (var i = 0; i < count && i < EntriesInSum; i++)
        {
            var (nid, ofs) = entries[i];
            var offset = i * SummarySize;
            BinaryPrimitives.WriteUInt32LittleEndian(summaryBlock.AsSpan(offset, 4), nid);
            summaryBlock[offset + 4] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(summaryBlock.AsSpan(offset + 5, 2), ofs);
        }
    }

    /// <summary>
    /// Writes both SIT copies with each segment's allocation type and valid-block map.
    /// <para>写入 SIT 双副本，记录各段的分配类型和有效块位图。</para>
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


            var vblocks = (ushort)((type << 10) | count);
            var entryIndex = segno % F2fsNode.SitEntryPerBlock;
            var entryOffset = checked((int)entryIndex * 74);
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(entryOffset, 2), vblocks);
            map.CopyTo(block, entryOffset + 2);


        }

        if (currentBlockIndex >= 0)
        {
            FlushSitBlock(output, baseOffset, layout, sitBlocks, currentBlockIndex, block);
        }
    }

    /// <summary>
    /// Flushes an accumulated SIT block to its primary and second copies (kernel get_current_sit_page addressing).
    /// <para>把累积好的 SIT 块写入主副本与第二副本（内核 get_current_sit_page 寻址）。</para>
    /// </summary>
    private static void FlushSitBlock(Stream output, long baseOffset, Layout layout, long sitBlocks, long blockIndex, byte[] block)
    {


        var primary = (long)layout.SitBlkaddr + blockIndex;
        WriteAt(output, baseOffset + primary * F2fsNode.NodeSize, block);
        WriteAt(output, baseOffset + (primary + sitBlocks) * F2fsNode.NodeSize, block);
    }

    private static void WriteNat(Stream output, long baseOffset, Layout layout, Dictionary<uint, (uint Ino, uint Block)> nat)
    {


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
                natBlock[offset] = 0;
                BinaryPrimitives.WriteUInt32LittleEndian(natBlock.AsSpan(offset + F2fsNode.NatEntryInoOffset, 4), entry.Ino);
                BinaryPrimitives.WriteUInt32LittleEndian(natBlock.AsSpan(offset + F2fsNode.NatEntryBlockOffset, 4), entry.Block);
            }


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
            if (node.XattrNid != 0)
            {
                var block = new byte[blockSize];
                node.XattrBytes.AsSpan(512).CopyTo(block);
                F2fsNodeFooter.WriteTo(block, node.XattrNid, node.Ino, 0xFFFF_FFF8, 1, node.XattrBlock + 1);
                WriteAt(output, baseOffset + node.XattrBlock * blockSize, block);
            }
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
    /// Writes SSA summary blocks for segments containing valid blocks.
    /// <para>为包含有效块的段写入 SSA 摘要块。</para>
    /// </summary>
    private static void WriteSsa(Stream output, long baseOffset, Layout layout, CursegPlan plan, int blockSize)
    {


        var mainBase = 0u;
        foreach (var (segno, entries) in plan.SegmentEntries)
        {
            var block = new byte[blockSize];
            foreach (var (ofsInSeg, (nid, ofsInNode)) in entries)
            {
                var offset = ofsInSeg * SummarySize;
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset, 4), nid);
                block[offset + 4] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(offset + 5, 2), ofsInNode);
            }

            block[F2fsNode.NodeSize - SumFooterSize] = IsNodeSegment(segno, mainBase, layout, plan)
                ? (byte)1
                : (byte)0;
            var ssaBlockAddr = (long)layout.SsaBlkaddr + segno;
            WriteAt(output, baseOffset + ssaBlockAddr * blockSize, block);
        }
    }

    /// <summary>
    /// Whether a segment is a node segment (node-region segments or free segments assigned to a node curseg).
    /// <para>段是否为 node 段（node 区段或被指派为 node curseg 的空闲段）。</para>
    /// </summary>
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
            Inline = (byte)(inline | (node.ExtraWords > 0 ? F2fsInode.ExtraAttrFlag : 0) | (node.XattrWords > 0 ? F2fsInode.InlineXattrFlag : 0)),
            Uid = node.Uid,
            Gid = node.Gid,
            Links = node.IsDirectory ? (uint)(2 + node.Children.Count(static c => c.IsDirectory)) : node.LinkCount,
            Size = node.IsDirectory ? (ulong)node.Size : (ulong)(node.IsSymlink ? Encoding.UTF8.GetByteCount(node.SymlinkTarget ?? string.Empty) : node.Size),


            Blocks = (node.XattrNid != 0 ? 1UL : 0UL) + (node.IsDirectory
                ? 1UL + (ulong)node.DirBlocks.Count
                : node.InlineData
                    ? 1UL
                    : 1UL + (ulong)((node.IsCompressedFile ? node.DataBlockCount : node.AllocatedDataBlockCount) + node.Dnodes.Count)),
            XattrNid = node.XattrNid,


            Flags = (node.IsCompressedFile ? F2fsInode.ComprFlag : 0) | node.SemanticFlags,
            CompressAlgorithm = node.IsCompressedFile
                ? KernelCompressionAlgorithmId(node.CompressedAlgorithm)
                : (byte)0,
            LogClusterSize = node.IsCompressedFile
                ? LogClusterSizeFromBytes(F2fsCompression.ClusterBytes)
                : (byte)0,
            Pino = node.Parent?.Ino ?? 0,


            CurrentDepth = node.IsDirectory ? node.DirDepth : 0u,
            Namelen = (uint)Encoding.UTF8.GetByteCount(node.Name),
            Name = Encoding.UTF8.GetBytes(node.Name),
            Extent = node.Extent,
            Addr = BuildInodeAddrs(node),
            Nid = BuildInodeNids(node),
            Mtime = node.Mtime > 0 ? (ulong)node.Mtime : 0,
            MtimeNsec = node.MtimeNsec,
            Ctime = node.Ctime > 0 ? (ulong)node.Ctime : 0,
            CtimeNsec = node.CtimeNsec,
            ExtraIsize = (ushort)(node.ExtraWords * 4),
            CompressedBlocks = node.IsCompressedFile ? (ulong)(node.DataBlockCount - node.AllocatedDataBlockCount) : 0,
            InlineXattrSize = (ushort)node.XattrWords,
        };
        inode.WriteTo(block);
        if (node.InlineData)
        {
            var inlineOffset = inode.InlineDataOffset;
            node.InlineBytes.AsSpan(0, Math.Min(node.InlineBytes.Length, blockSize - inlineOffset))
                .CopyTo(block.AsSpan(inlineOffset));
        }

        if (node.XattrBytes.Length > 0)
            node.XattrBytes.AsSpan(0, Math.Min(512, node.XattrBytes.Length)).CopyTo(block.AsSpan(F2fsNode.AddrArrayOffset + (F2fsNode.DefAddrsPerInode - node.XattrWords) * 4));
        F2fsNodeFooter.WriteTo(block, node.Ino, node.Ino, 0, 1, node.InodeBlockAddr + 1);
        WriteAt(output, baseOffset + node.InodeBlockAddr * blockSize, block);
    }

    private static void WriteDnode(Stream output, long baseOffset, BuildNode file, DnodePlan dnode, int blockSize)
    {
        var block = new byte[blockSize];
        var flag = checked(dnode.NodeOffset << F2fsNodeFooter.OffsetBitShift);
        if (dnode.IsIndirect || dnode.IsDoubleIndirect)
        {
            for (var i = 0; i < dnode.IndirectChildNids.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(i * 4, 4), dnode.IndirectChildNids[i]);
            }
        }
        else
        {
            for (var i = 0; i < file.DataAddressesPerNode; i++)
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
    /// Second-pass data write for a compressed file: re-streams the source in 64 KB clusters.
    /// <para>压缩文件的第二遍数据写出：按 64 KB 簇流式重读源文件。</para>
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
                    var address = node.DataBlockAddrs[logical + 1 + b];
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
        if (node.IsSpecial)
        {
            if ((node.Mode & 0xF000) is 0x2000 or 0x6000) addrs[node.ExtraWords + 1] = node.DeviceId;
        }
        else if (node.IsDirectory)
        {
            for (var i = 0; i < node.DirBlockAddrs.Count; i++)
            {
                addrs[i + node.ExtraWords] = node.DirBlockAddrs[i];
            }
        }
        else if (!node.InlineData)
        {
            for (var i = 0; i < node.DataBlockAddrs.Count && i < node.InodeAddressCount; i++)
            {
                addrs[i + node.ExtraWords] = node.DataBlockAddrs[i];
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


        for (var i = 0; i < nids.Length; i++) nids[i] = node.RootDnodes[i]?.Nid ?? 0;

        return nids;
    }

    private static bool IsSafeName(string name)
        => !name.Contains('/') && !name.Contains('\0') && name != "." && name != "..";

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
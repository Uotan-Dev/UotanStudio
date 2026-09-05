using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Ext4.Core;

namespace UotanStudio.Firmware.Ext4;

public sealed partial class Ext4Builder
{
    /// <summary>
    /// 逐块读取文件，返回所有全零块的索引集合；若无空洞则返回 null。
    /// <para>Reads the file block-by-block and returns the set of indices whose blocks are
    /// entirely zero (holes); returns <c>null</c> if no holes are found.</para>
    /// </summary>
    private static HashSet<int>? DetectHoles(string fullPath, long size, int blockSize)
    {
        if (size == 0)
        {
            return null;
        }

        var holes = new HashSet<int>();
        var buffer = new byte[blockSize];
        using var fs = File.OpenRead(fullPath);
        long remaining = size;
        var index = 0;
        while (remaining > 0)
        {
            var want = (int)Math.Min(blockSize, remaining);
            var read = 0;
            while (read < want)
            {
                var n = fs.Read(buffer, read, want - read);
                if (n <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of file '{fullPath}'.");
                }

                read += n;
            }

            var allZero = true;
            for (var i = 0; i < want; i++)
            {
                if (buffer[i] != 0)
                {
                    allZero = false;
                    break;
                }
            }

            if (allZero)
            {
                holes.Add(index);
            }

            remaining -= want;
            index++;
        }

        return holes.Count > 0 ? holes : null;
    }

    /// <summary>
    /// 将空洞集合转换为连续的非空洞块运行列表。
    /// <para>Converts a set of hole block indices into a list of contiguous non-hole
    /// runs (each expressed as a logical-start block and a block count).</para>
    /// </summary>
    private static List<(long LogicalStart, long Count)> BuildRuns(long totalBlocks, HashSet<int>? holes)
    {
        var runs = new List<(long LogicalStart, long Count)>();
        if (totalBlocks == 0)
        {
            return runs;
        }

        if (holes is null || holes.Count == 0)
        {
            runs.Add((0, totalBlocks));
            return runs;
        }

        long? runStart = null;
        for (var i = 0L; i < totalBlocks; i++)
        {
            var isHole = holes.Contains((int)i);
            if (!isHole)
            {
                runStart ??= i;
            }
            else if (runStart is not null)
            {
                runs.Add((runStart.Value, i - runStart.Value));
                runStart = null;
            }
        }

        if (runStart is not null)
        {
            runs.Add((runStart.Value, totalBlocks - runStart.Value));
        }

        return runs;
    }

    /// <summary>
    /// 将 extent 树写入 inode i_block 区域（60 字节），支持深度 0/1/2，返回需要额外写出的元数据块列表。
    /// <para>Writes an extent tree into the inode i_block area (60 bytes).  Supports tree
    /// depth 0, 1 and 2.  Returns a list of metadata blocks (leaf / index blocks) that
    /// the caller must flush to disk at the indicated addresses.</para>
    /// </summary>
    private static List<(long BlockAddress, byte[] Data)> WriteExtentTree(
        Span<byte> dest, List<(long LogicalStart, long Count)> runs, long dataStart,
        long[] leafBlocks, long[] indexBlocks, int blockSize)
    {
        var epb = EntriesPerBlock(blockSize);
        var extents = new List<(uint Logical, ulong Start, ushort Len)>();
        var cursor = dataStart;
        foreach (var run in runs)
        {
            var remaining = run.Count;
            var logical = run.LogicalStart;
            while (remaining > 0)
            {
                var len = (ushort)Math.Min(MaxExtentLength, remaining);
                extents.Add(((uint)logical, (ulong)cursor, len));
                cursor += len;
                logical += len;
                remaining -= len;
            }
        }

        var blocksToWrite = new List<(long BlockAddress, byte[] Data)>();

        if (extents.Count <= 4)
        {
            // depth 0：全部 extent 直接存于 inode i_block，容量恒为 4。
            // depth 0: all extents live in the inode i_block, whose capacity is always 4.
            WriteExtentHeader(dest, (ushort)extents.Count, 0, 4);
            var offset = 12;
            foreach (var extent in extents)
            {
                WriteExtentEntry(dest, offset, extent);
                offset += 12;
            }

            return blocksToWrite;
        }

        // 按 epb 分组成叶块。
        var leafGroups = new List<(uint Logical, ulong Start, ushort Len)[]>();
        for (var i = 0; i < extents.Count; i += epb)
        {
            leafGroups.Add(extents.Skip(i).Take(epb).ToArray());
        }

        if (leafBlocks.Length < leafGroups.Count)
        {
            throw new InvalidOperationException("Extent index tree under-allocated leaf blocks.");
        }

        // 写叶块。
        for (var g = 0; g < leafGroups.Count; g++)
        {
            var leaf = new byte[blockSize];
            var group = leafGroups[g];
            WriteExtentHeader(leaf.AsSpan(), (ushort)group.Length, 0, epb);
            for (var i = 0; i < group.Length; i++)
            {
                WriteExtentEntry(leaf, 12 + i * 12, group[i]);
            }

            blocksToWrite.Add((leafBlocks[g], leaf));
        }

        // 构建索引项列表：每项指向一个叶块。
        var indexEntries = new List<(uint LogicalStart, long BlockAddress)>();
        for (var g = 0; g < leafGroups.Count; g++)
        {
            indexEntries.Add((leafGroups[g][0].Logical, leafBlocks[g]));
        }

        if (indexEntries.Count <= 4)
        {
            // depth 1：索引项直接存于 inode i_block。
            WriteExtentHeader(dest, (ushort)indexEntries.Count, 1, 4);
            for (var i = 0; i < indexEntries.Count; i++)
            {
                WriteExtentIndex(dest, 12 + i * 12, indexEntries[i].LogicalStart, indexEntries[i].BlockAddress);
            }

            return blocksToWrite;
        }

        // depth 2：索引项过多，需要二级索引块。
        // 按 epb 分组成二级索引块。
        var indexGroups = new List<(uint LogicalStart, long BlockAddress)[]>();
        for (var i = 0; i < indexEntries.Count; i += epb)
        {
            indexGroups.Add(indexEntries.Skip(i).Take(epb).ToArray());
        }

        if (indexBlocks.Length < indexGroups.Count)
        {
            throw new InvalidOperationException("Extent index tree under-allocated index blocks.");
        }

        // 写二级索引块。
        for (var g = 0; g < indexGroups.Count; g++)
        {
            var block = new byte[blockSize];
            var group = indexGroups[g];
            WriteExtentHeader(block.AsSpan(), (ushort)group.Length, 1, epb);
            for (var i = 0; i < group.Length; i++)
            {
                WriteExtentIndex(block, 12 + i * 12, group[i].LogicalStart, group[i].BlockAddress);
            }

            blocksToWrite.Add((indexBlocks[g], block));
        }

        // inode i_block 存 depth=2 的根索引项，指向二级索引块。
        if (indexGroups.Count > 4)
        {
            throw new InvalidOperationException(
                $"Extent tree depth 2 requires ≤4 root index entries, got {indexGroups.Count}.");
        }

        WriteExtentHeader(dest, (ushort)indexGroups.Count, 2, 4);
        for (var i = 0; i < indexGroups.Count; i++)
        {
            WriteExtentIndex(dest, 12 + i * 12, indexGroups[i][0].LogicalStart, indexBlocks[i]);
        }

        return blocksToWrite;
    }

    private static void WriteExtentHeader(Span<byte> dest, ushort entries, ushort depth, int maxEntries)
    {
        WriteU16(dest, 0, ExtentMagic);
        WriteU16(dest, 2, entries);
        WriteU16(dest, 4, (ushort)maxEntries);
        WriteU16(dest, 6, depth);
        WriteU32(dest, 8, 0); // generation
    }

    private static void WriteExtentEntry(Span<byte> dest, int offset, (uint Logical, ulong Start, ushort Len) extent)
    {
        WriteU32(dest, offset, extent.Logical);
        WriteU16(dest, offset + 4, extent.Len);
        WriteU16(dest, offset + 6, (ushort)(extent.Start >> 32));
        WriteU32(dest, offset + 8, (uint)(extent.Start & 0xFFFFFFFF));
    }

    /// <summary>
    /// 写入一个 extent 索引项（ei_block + ei_leaf_lo + ei_leaf_hi + ei_unused）。
    /// <para>Writes an extent index entry (ei_block + ei_leaf_lo + ei_leaf_hi + ei_unused).</para>
    /// </summary>
    private static void WriteExtentIndex(Span<byte> dest, int offset, uint logicalBlock, long leafBlock)
    {
        WriteU32(dest, offset, logicalBlock);                              // ei_block
        WriteU32(dest, offset + 4, (uint)(leafBlock & 0xFFFFFFFF));       // ei_leaf_lo
        WriteU16(dest, offset + 8, (ushort)((ulong)leafBlock >> 32));     // ei_leaf_hi
        WriteU16(dest, offset + 10, 0);                                   // ei_unused
    }

    /// <summary>
    /// 将文件数据块写入输出流：目录块按序写出，符号链接写入目标路径字节，常规文件按逻辑块跳过空洞后从磁盘读取写出。
    /// <para>Writes file data blocks to the output stream: directory blocks are written
    /// sequentially, symlinks store the encoded target path, and regular files are read
    /// from disk block-by-block (skipping sparse holes) and written to their allocated
    /// on-disk locations.</para>
    /// </summary>
    private static void WriteNodeData(Stream output, long baseOffset, Node node, int blockSize)
    {
        if ((node.IsSymlink && node.Size < 60) || node.IsInlineData)
        {
            return;
        }

        if (node.IsDirectory)
        {
            for (var i = 0; i < node.DirectoryBlocks.Count; i++)
            {
                var offset = checked((node.DataStartBlock + i) * blockSize);
                WriteAt(output, baseOffset, offset, node.DirectoryBlocks[i], blockSize);
            }

            return;
        }

        if (node.IsSymlink)
        {
            var bytes = Encoding.UTF8.GetBytes(node.SymlinkTarget ?? string.Empty);
            WriteBytesToBlocks(output, baseOffset, node, bytes, blockSize);
            return;
        }

        var buffer = new byte[blockSize];
        using (var fs = File.OpenRead(node.FullPath))
        {
            var assigned = node.DataStartBlock;
            var totalBlocks = (node.Size + blockSize - 1) / blockSize;
            for (var logical = 0L; logical < totalBlocks; logical++)
            {
                if (node.HoleBlocks?.Contains((int)logical) == true)
                {
                    continue;
                }

                // 稀疏文件跳过洞块后顺序流会错位，须按逻辑块偏移重新定位。
                // Skipping hole blocks desyncs the sequential stream; seek to the logical offset.
                fs.Position = logical * blockSize;

                buffer.AsSpan().Clear();
                var want = (int)Math.Min(blockSize, node.Size - logical * blockSize);
                var read = 0;
                while (read < want)
                {
                    var n = fs.Read(buffer, read, want - read);
                    if (n <= 0)
                    {
                        throw new EndOfStreamException($"Unexpected end of file '{node.FullPath}'.");
                    }

                    read += n;
                }

                var offset = checked(assigned * blockSize);
                WriteAt(output, baseOffset, offset, buffer, want);
                assigned++;
            }
        }
    }

    private static void WriteBytesToBlocks(Stream output, long baseOffset, Node node, byte[] bytes, int blockSize)
    {
        var buffer = new byte[blockSize];
        var assigned = node.DataStartBlock;
        var total = (bytes.Length + blockSize - 1) / blockSize;
        for (var i = 0; i < total; i++)
        {
            var want = Math.Min(blockSize, bytes.Length - i * blockSize);
            buffer.AsSpan().Clear();
            bytes.AsSpan(i * blockSize, want).CopyTo(buffer);
            var offset = checked(assigned * blockSize);
            WriteAt(output, baseOffset, offset, buffer, want);
            assigned++;
        }
    }

    private static int EntriesPerBlock(int blockSize) => (blockSize - 12) / 12;
}
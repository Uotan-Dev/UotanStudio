using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Core;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

public sealed partial class Ext4Builder
{
    /// <summary>
    /// Reads the file block-by-block and returns the set of indices whose blocks are entirely zero (holes); returns null if no holes are found.
    /// <para>逐块读取文件，返回所有全零块的索引集合；若无空洞则返回 null。</para>
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
    /// Converts a set of hole block indices into a list of contiguous non-hole runs (each expressed as a logical-start block and a block count).
    /// <para>将空洞集合转换为连续的非空洞块运行列表。</para>
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
    /// Writes an extent tree into the inode i_block area (60 bytes).
    /// <para>将 extent 树写入 inode i_block 区域（60 字节），支持深度 0/1/2，返回需要额外写出的元数据块列表。</para>
    /// </summary>
    private static List<(long BlockAddress, byte[] Data)> WriteExtentTree(
        Span<byte> dest, List<(uint Logical, ulong Start, ushort Len)> extents,
        long[] leafBlocks, long[] indexBlocks, int blockSize)
    {
        var epb = EntriesPerBlock(blockSize);
        var blocksToWrite = new List<(long BlockAddress, byte[] Data)>();

        if (extents.Count <= 4)
        {


            WriteExtentHeader(dest, (ushort)extents.Count, 0, 4);
            var offset = 12;
            foreach (var extent in extents)
            {
                WriteExtentEntry(dest, offset, extent);
                offset += 12;
            }

            return blocksToWrite;
        }


        // 索引切片替代 Skip(i).Take(epb)，避免分块循环的 O(n^2) LINQ 拷贝。
        // Index slicing instead of Skip(i).Take(epb), avoiding O(n^2) LINQ copies in the loop.
        var leafGroups = new List<(uint Logical, ulong Start, ushort Len)[]>();
        for (var i = 0; i < extents.Count; i += epb)
        {
            var count = Math.Min(epb, extents.Count - i);
            leafGroups.Add(extents.GetRange(i, count).ToArray());
        }

        if (leafBlocks.Length < leafGroups.Count)
        {
            throw new InvalidOperationException("Extent index tree under-allocated leaf blocks.");
        }


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


        var indexEntries = new List<(uint LogicalStart, long BlockAddress)>();
        for (var g = 0; g < leafGroups.Count; g++)
        {
            indexEntries.Add((leafGroups[g][0].Logical, leafBlocks[g]));
        }

        if (indexEntries.Count <= 4)
        {

            WriteExtentHeader(dest, (ushort)indexEntries.Count, 1, 4);
            for (var i = 0; i < indexEntries.Count; i++)
            {
                WriteExtentIndex(dest, 12 + i * 12, indexEntries[i].LogicalStart, indexEntries[i].BlockAddress);
            }

            return blocksToWrite;
        }


        // 索引切片替代 Skip(i).Take(epb)，避免分块循环的 O(n^2) LINQ 拷贝。
        // Index slicing instead of Skip(i).Take(epb), avoiding O(n^2) LINQ copies in the loop.
        var indexGroups = new List<(uint LogicalStart, long BlockAddress)[]>();
        for (var i = 0; i < indexEntries.Count; i += epb)
        {
            var count = Math.Min(epb, indexEntries.Count - i);
            indexGroups.Add(indexEntries.GetRange(i, count).ToArray());
        }

        if (indexBlocks.Length < indexGroups.Count)
        {
            throw new InvalidOperationException("Extent index tree under-allocated index blocks.");
        }


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
        WriteU32(dest, 8, 0);
    }

    private static void WriteExtentEntry(Span<byte> dest, int offset, (uint Logical, ulong Start, ushort Len) extent)
    {
        WriteU32(dest, offset, extent.Logical);
        WriteU16(dest, offset + 4, extent.Len);
        WriteU16(dest, offset + 6, (ushort)(extent.Start >> 32));
        WriteU32(dest, offset + 8, (uint)(extent.Start & 0xFFFFFFFF));
    }

    /// <summary>
    /// Writes an extent index entry (ei_block + ei_leaf_lo + ei_leaf_hi + ei_unused).
    /// <para>写入一个 extent 索引项（ei_block + ei_leaf_lo + ei_leaf_hi + ei_unused）。</para>
    /// </summary>
    private static void WriteExtentIndex(Span<byte> dest, int offset, uint logicalBlock, long leafBlock)
    {
        WriteU32(dest, offset, logicalBlock);
        WriteU32(dest, offset + 4, (uint)(leafBlock & 0xFFFFFFFF));
        WriteU16(dest, offset + 8, (ushort)((ulong)leafBlock >> 32));
        WriteU16(dest, offset + 10, 0);
    }

    /// <summary>
    /// Writes file data blocks to the output stream: directory blocks are written sequentially, symlinks store the encoded target path, and regular files are read from disk block-by-block (skipping sparse holes) and written to their allocated on-disk locations.
    /// <para>将文件数据块写入输出流：目录块按序写出，符号链接写入目标路径字节，常规文件按逻辑块跳过空洞后从磁盘读取写出。</para>
    /// </summary>
    private static void WriteNodeData(Stream output, long baseOffset, Node node, int blockSize)
    {
        if ((node.IsSymlink && node.Size < 60) || node.IsInlineData) return;

        using var input = !node.IsDirectory && !node.IsSymlink ? File.OpenRead(node.FullPath) : null;
        var target = node.IsSymlink ? Encoding.UTF8.GetBytes(node.SymlinkTarget ?? string.Empty) : null;
        var buffer = new byte[blockSize];
        foreach (var extent in node.Extents)
        {
            for (var i = 0; i < extent.Len; i++)
            {
                var logical = (long)extent.Logical + i;
                var offset = checked(((long)extent.Start + i) * blockSize);
                if (node.IsDirectory)
                {
                    WriteAt(output, baseOffset, offset, node.DirectoryBlocks[checked((int)logical)], blockSize);
                    continue;
                }

                var sourceOffset = checked(logical * blockSize);
                var count = checked((int)Math.Min(blockSize, node.Size - sourceOffset));
                if (target is not null)
                    target.AsSpan(checked((int)sourceOffset), count).CopyTo(buffer);
                else
                {
                    input!.Position = sourceOffset;
                    input.ReadExactly(buffer.AsSpan(0, count));
                }
                WriteAt(output, baseOffset, offset, buffer, count);
            }
        }
    }

    private static int EntriesPerBlock(int blockSize) => (blockSize - 12) / 12;
}

using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Ext4.Core;

namespace UotanStudio.Firmware.Ext4;

/// <summary>
/// Fully managed ext4 image builder that packs a source directory into a valid ext4
/// filesystem image (4096-byte blocks, 256-byte inodes) with extents, block/inode
/// bitmaps and fast symlinks.
/// <para>全托管 ext4 镜像构建器：将源目录打包为有效的 ext4 文件系统镜像
/// （4096 字节块、256 字节 inode），包含 extent、块/inode 位图与快速符号链接。</para>
/// </summary>
public sealed class Ext4Builder
{
    private const ushort ModeRegularFile = 0x81A4; // S_IFREG | 0644
    private const ushort ModeDirectory = 0x41ED;   // S_IFDIR | 0755
    private const ushort ModeSymbolicLink = 0xA1FF; // S_IFLNK | 0777
    private const uint FlagExtents = 0x0008_0000;
    private const ushort ExtentMagic = 0xF30A;
    private const uint MaxExtentLength = 32768;
    private const uint FixedTimestamp = 1_700_000_000;
    private const uint RootInodeNumber = 2;
    private const uint FirstUsableInode = 11;

    private static readonly byte[] FixedUuid = [0x53, 0xAA, 0x0F, 0x4D, 0x2C, 0x7A, 0x11, 0xEF, 0x8A, 0xB5, 0x00, 0x0C, 0x29, 0xC0, 0xA1, 0xB2];

    private sealed class Node
    {
        public required string Name { get; init; }
        public required string RelativePath { get; init; }
        public required string FullPath { get; init; }
        public required bool IsDirectory { get; init; }
        public required bool IsSymlink { get; init; }
        public string? SymlinkTarget { get; set; }
        public Node? Parent { get; init; }
        public List<Node> Children { get; } = [];
        public long Size { get; set; }
        public uint Inode { get; set; }
        public long DataBlockCount { get; set; }
        public long DataStartBlock { get; set; }
        public long[] LeafBlocks { get; set; } = [];
        public HashSet<int>? HoleBlocks { get; set; }
        public List<(long LogicalStart, long Count)> Runs { get; } = [];
        public List<byte[]> DirectoryBlocks { get; set; } = [];
    }

    /// <summary>
    /// Builds an ext4 image from <paramref name="sourceDirectory"/> and returns it as a byte array.
    /// <para>从 <paramref name="sourceDirectory"/> 构建 ext4 镜像并以字节数组返回。</para>
    /// </summary>
    public static byte[] Build(string sourceDirectory, Ext4Options? options = null)
    {
        options ??= new Ext4Options();
        var blockSize = options.BlockSize;
        var inodeSize = options.InodeSize;
        if (blockSize < 4096 || !IsPowerOfTwo(blockSize))
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Ext4 block size must be at least 4096 and a power of two (got {blockSize}).");
        }

        if (inodeSize < 128 || !IsPowerOfTwo(inodeSize))
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Ext4 inode size must be at least 128 and a power of two (got {inodeSize}).");
        }

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Source directory not found: '{sourceDirectory}'.");
        }

        var root = BuildTree(Path.GetFullPath(sourceDirectory));
        var nodes = new List<Node>();
        var maxInode = AssignInodes(root, nodes);

        PlanDirectoryBlocks(nodes, blockSize);
        PlanDataBlocks(nodes, blockSize, options.EnableSparse);
        PlanLeafBlocks(nodes);

        var inodesPerGroup = RoundUpPowerOfTwo(Math.Max(maxInode, 16));
        var inodesPerInodeBlock = blockSize / inodeSize;
        var inodeTableBlocks = (inodesPerGroup + inodesPerInodeBlock - 1) / inodesPerInodeBlock;
        var dataStart = 3L + inodeTableBlocks;
        var totalDataBlocks = nodes.Sum(static n => n.DataBlockCount) + nodes.Sum(static n => (long)n.LeafBlocks.Length);
        var usedBlocks = dataStart + totalDataBlocks;
        var blocksPerGroup = RoundUpPowerOfTwo(Math.Max(usedBlocks, 8));
        var totalBlocks = blocksPerGroup;

        AllocateDataBlocks(nodes, dataStart);

        var image = new byte[checked(totalBlocks * blockSize)];
        WriteSuperblock(image.AsSpan(), blockSize, inodeSize, (uint)inodesPerGroup, totalBlocks, usedBlocks, nodes.Count);
        WriteGroupDescriptor(image.AsSpan(2048, 32), (uint)inodesPerGroup, totalBlocks, usedBlocks, nodes.Count, nodes.Count(static n => n.IsDirectory));
        WriteBlockBitmap(image, usedBlocks, blockSize);
        WriteInodeBitmap(image, nodes, blockSize);

        foreach (var node in nodes)
        {
            WriteInode(image, node, blockSize, inodeSize);
        }

        foreach (var node in nodes)
        {
            WriteNodeData(image, node, blockSize);
        }

        return image;
    }

    /// <summary>
    /// Builds an ext4 image from <paramref name="sourceDirectory"/> and writes it to <paramref name="outputPath"/>.
    /// <para>从 <paramref name="sourceDirectory"/> 构建 ext4 镜像并写入 <paramref name="outputPath"/>。</para>
    /// </summary>
    public static void Build(string sourceDirectory, string outputPath, Ext4Options? options = null)
    {
        var bytes = Build(sourceDirectory, options);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllBytes(outputPath, bytes);
    }

    private static Node BuildTree(string sourceRoot)
    {
        var root = new Node
        {
            Name = string.Empty,
            RelativePath = string.Empty,
            FullPath = sourceRoot,
            IsDirectory = true,
            IsSymlink = false,
        };
        Populate(root);
        return root;
    }

    private static void Populate(Node node)
    {
        var children = Directory.EnumerateFileSystemEntries(node.FullPath)
            .OrderBy(static p => Path.GetFileName(p), StringComparer.Ordinal);
        foreach (var childPath in children)
        {
            var name = Path.GetFileName(childPath);
            var attributes = File.GetAttributes(childPath);
            var isSymlink = (attributes & FileAttributes.ReparsePoint) != 0;
            var isDirectory = (attributes & FileAttributes.Directory) != 0 && !isSymlink;
            var child = new Node
            {
                Name = name,
                RelativePath = node.RelativePath.Length == 0 ? name : node.RelativePath + "/" + name,
                FullPath = childPath,
                IsDirectory = isDirectory,
                IsSymlink = isSymlink,
                Parent = node,
            };
            if (isSymlink)
            {
                child.SymlinkTarget = new FileInfo(childPath).LinkTarget ?? string.Empty;
                child.Size = Encoding.UTF8.GetByteCount(child.SymlinkTarget);
            }
            else if (isDirectory)
            {
                child.Size = 0;
            }
            else
            {
                child.Size = new FileInfo(childPath).Length;
            }

            node.Children.Add(child);
            if (isDirectory)
            {
                Populate(child);
            }
        }
    }

    private static uint AssignInodes(Node root, List<Node> nodes)
    {
        root.Inode = RootInodeNumber;
        nodes.Add(root);
        var nextInode = FirstUsableInode;
        var maxInode = RootInodeNumber;
        for (var i = 0; i < nodes.Count; i++)
        {
            foreach (var child in nodes[i].Children)
            {
                child.Inode = nextInode;
                maxInode = nextInode;
                nextInode++;
                nodes.Add(child);
            }
        }

        return maxInode;
    }

    private static void PlanDirectoryBlocks(List<Node> nodes, int blockSize)
    {
        foreach (var node in nodes)
        {
            if (!node.IsDirectory)
            {
                continue;
            }

            node.DirectoryBlocks = BuildDirectoryBlocks(node, blockSize);
            node.Size = (long)node.DirectoryBlocks.Count * blockSize;
            node.DataBlockCount = node.DirectoryBlocks.Count;
            node.Runs.Add((0, node.DataBlockCount));
        }
    }

    private static void PlanDataBlocks(List<Node> nodes, int blockSize, bool enableSparse)
    {
        foreach (var node in nodes)
        {
            if (node.IsDirectory || (node.IsSymlink && node.Size < 60))
            {
                continue;
            }

            var totalBlocks = (node.Size + blockSize - 1) / blockSize;
            if (enableSparse && !node.IsSymlink)
            {
                node.HoleBlocks = DetectHoles(node.FullPath, node.Size, blockSize);
            }

            node.DataBlockCount = totalBlocks - (node.HoleBlocks?.Count ?? 0);
            node.Runs.AddRange(BuildRuns(totalBlocks, node.HoleBlocks));
        }
    }

    private static void PlanLeafBlocks(List<Node> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Runs.Count == 0)
            {
                continue;
            }

            var extentCount = node.Runs.Sum(static run => (run.Count + MaxExtentLength - 1) / MaxExtentLength);
            if (extentCount > 4)
            {
                node.LeafBlocks = new long[(extentCount + 3) / 4];
            }
        }
    }

    private static void AllocateDataBlocks(List<Node> nodes, long dataStart)
    {
        var cursor = dataStart;
        foreach (var node in nodes)
        {
            if (node.DataBlockCount > 0)
            {
                node.DataStartBlock = cursor;
                cursor += node.DataBlockCount;
            }

            for (var i = 0; i < node.LeafBlocks.Length; i++)
            {
                node.LeafBlocks[i] = cursor;
                cursor++;
            }
        }
    }

    private static List<byte[]> BuildDirectoryBlocks(Node dir, int blockSize)
    {
        var entries = new List<(string Name, byte Type, uint Inode)>
        {
            (".", Ext4DirectoryEntry.TypeDirectory, dir.Inode),
            ("..", Ext4DirectoryEntry.TypeDirectory, dir.Parent?.Inode ?? dir.Inode),
        };
        foreach (var child in dir.Children)
        {
            entries.Add((child.Name, FileTypeOf(child), child.Inode));
        }

        var blocks = new List<byte[]>();
        var block = new byte[blockSize];
        var offset = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var (name, type, inode) = entries[i];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var naturalLength = (8 + nameBytes.Length + 3) & ~3;
            if (offset + naturalLength > blockSize)
            {
                // Flush the current block with an empty tail entry.
                // 以空尾部目录项收尾当前块。
                WriteU32(block, offset, 0);
                WriteU16(block, offset + 4, (ushort)(blockSize - offset));
                block[offset + 6] = 0;
                block[offset + 7] = 0;
                blocks.Add(block);
                block = new byte[blockSize];
                offset = 0;
            }

            var recordLength = i == entries.Count - 1 ? blockSize - offset : naturalLength;
            WriteU32(block, offset, inode);
            WriteU16(block, offset + 4, (ushort)recordLength);
            block[offset + 6] = (byte)nameBytes.Length;
            block[offset + 7] = type;
            nameBytes.CopyTo(block.AsSpan(offset + 8));
            offset += recordLength;
            if (offset == blockSize)
            {
                blocks.Add(block);
                block = new byte[blockSize];
                offset = 0;
            }
        }

        if (offset > 0)
        {
            blocks.Add(block);
        }

        return blocks;
    }

    private static byte FileTypeOf(Node node)
        => node.IsDirectory ? Ext4DirectoryEntry.TypeDirectory : node.IsSymlink ? Ext4DirectoryEntry.TypeSymlink : Ext4DirectoryEntry.TypeRegular;

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
                    break;
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

    private static void WriteSuperblock(
        Span<byte> image, int blockSize, int inodeSize, uint inodesPerGroup, long totalBlocks, long usedBlocks, int usedInodes)
    {
        var span = image.Slice(1024, 1024);
        span.Clear();
        WriteU32(span, 0x00, inodesPerGroup);
        WriteU32(span, 0x04, (uint)totalBlocks);
        WriteU32(span, 0x08, 0);
        WriteU32(span, 0x0C, (uint)(totalBlocks - usedBlocks));
        WriteU32(span, 0x10, inodesPerGroup - (uint)usedInodes);
        WriteU32(span, 0x14, 0);
        WriteU32(span, 0x18, (uint)(Log2(blockSize) - 10));
        WriteU32(span, 0x1C, (uint)(Log2(blockSize) - 10));
        WriteU32(span, 0x20, (uint)totalBlocks);
        WriteU32(span, 0x24, (uint)totalBlocks);
        WriteU32(span, 0x28, inodesPerGroup);
        WriteU32(span, 0x2C, FixedTimestamp);
        WriteU32(span, 0x30, FixedTimestamp);
        WriteU16(span, 0x34, 0);
        WriteU16(span, 0x36, 0xFFFF);
        WriteU16(span, 0x38, 0xEF53);
        WriteU16(span, 0x3A, 1);
        WriteU16(span, 0x3C, 1);
        WriteU16(span, 0x3E, 0);
        WriteU32(span, 0x40, FixedTimestamp);
        WriteU32(span, 0x44, 0);
        WriteU32(span, 0x48, 0);
        WriteU32(span, 0x4C, 1);
        WriteU16(span, 0x50, 0);
        WriteU16(span, 0x52, 0);
        WriteU32(span, 0x54, FirstUsableInode);
        WriteU16(span, 0x58, (ushort)inodeSize);
        WriteU16(span, 0x5A, 0);
        WriteU32(span, 0x5C, 0);
        WriteU32(span, 0x60, 0x0001); // EXT4_FEATURE_INCOMPAT_EXTENTS
        WriteU32(span, 0x64, 0);
        FixedUuid.CopyTo(span[0x68..0x78]);
        Encoding.ASCII.GetBytes("UOTANSTUDIO", span[0x78..0x88]);
        WriteU32(span, 0xE8, 0);
        WriteU16(span, 0xFC, 0);
        WriteU16(span, 0xFE, 32); // s_desc_size
        WriteU32(span, 0x100, 0);
        WriteU32(span, 0x104, 0);
        WriteU32(span, 0x150, 0); // s_blocks_count_hi
        WriteU32(span, 0x154, 0); // s_r_blocks_count_hi
        WriteU32(span, 0x158, 0); // s_free_blocks_count_hi
    }

    private static void WriteGroupDescriptor(
        Span<byte> desc, uint inodesPerGroup, long totalBlocks, long usedBlocks, int usedInodes, int usedDirectories)
    {
        desc.Clear();
        WriteU32(desc, 0, 1); // block bitmap
        WriteU32(desc, 4, 2); // inode bitmap
        WriteU32(desc, 8, 3); // inode table
        WriteU16(desc, 12, (ushort)(totalBlocks - usedBlocks));
        WriteU16(desc, 14, (ushort)(inodesPerGroup - (uint)usedInodes));
        WriteU16(desc, 16, (ushort)usedDirectories);
        WriteU16(desc, 18, 0);
        WriteU32(desc, 20, 0);
        WriteU16(desc, 24, 0);
        WriteU16(desc, 26, 0);
        WriteU16(desc, 28, 0);
        WriteU16(desc, 30, 0);
    }

    private static void WriteBlockBitmap(byte[] image, long usedBlocks, int blockSize)
    {
        var bitmap = new byte[blockSize];
        for (var i = 0L; i < usedBlocks; i++)
        {
            bitmap[(int)(i >> 3)] |= (byte)(1 << (int)(i & 7));
        }

        Buffer.BlockCopy(bitmap, 0, image, blockSize, blockSize);
    }

    private static void WriteInodeBitmap(byte[] image, List<Node> nodes, int blockSize)
    {
        var bitmap = new byte[blockSize];
        foreach (var node in nodes)
        {
            var bit = (int)(node.Inode - 1);
            bitmap[bit >> 3] |= (byte)(1 << (bit & 7));
        }

        Buffer.BlockCopy(bitmap, 0, image, blockSize * 2, blockSize);
    }

    private static void WriteInode(byte[] image, Node node, int blockSize, int inodeSize)
    {
        var inodeOffset = checked((int)((long)3 * blockSize + ((long)node.Inode - 1) * inodeSize));
        var inode = new byte[inodeSize];
        var isFastSymlink = node.IsSymlink && node.Size < 60;

        var mode = node.IsDirectory ? ModeDirectory : node.IsSymlink ? ModeSymbolicLink : ModeRegularFile;
        var leaves = new List<byte[]>();
        var flags = 0u;
        if (isFastSymlink)
        {
            var target = Encoding.UTF8.GetBytes(node.SymlinkTarget ?? string.Empty);
            target.AsSpan().CopyTo(inode.AsSpan(40, Math.Min(target.Length, 60)));
        }
        else
        {
            flags = FlagExtents;
            leaves = WriteExtentTree(inode.AsSpan(40, 60), node.Runs, node.DataStartBlock, node.LeafBlocks, blockSize);
        }

        WriteU16(inode, 0, mode);
        WriteU16(inode, 2, 0);
        WriteU32(inode, 4, (uint)(node.Size & 0xFFFFFFFF));
        WriteU32(inode, 8, FixedTimestamp);
        WriteU32(inode, 12, FixedTimestamp);
        WriteU32(inode, 16, FixedTimestamp);
        WriteU32(inode, 20, 0);
        WriteU16(inode, 24, 0);
        WriteU16(inode, 26, node.IsDirectory ? (ushort)(2 + node.Children.Count(static c => c.IsDirectory)) : (ushort)1);
        var allocatedBlocks = isFastSymlink ? 0 : node.DataBlockCount + node.LeafBlocks.Length;
        WriteU32(inode, 28, (uint)(allocatedBlocks * (blockSize / 512)));
        WriteU32(inode, 32, flags);
        if (inodeSize >= 256)
        {
            WriteU32(inode, 108, (uint)(node.Size >> 32));
        }

        Buffer.BlockCopy(inode, 0, image, inodeOffset, inodeSize);
        for (var i = 0; i < leaves.Count; i++)
        {
            var leafOffset = checked((int)(node.LeafBlocks[i] * blockSize));
            Buffer.BlockCopy(leaves[i], 0, image, leafOffset, blockSize);
        }
    }

    private static List<byte[]> WriteExtentTree(Span<byte> dest, List<(long LogicalStart, long Count)> runs, long dataStart, long[] leafBlocks, int blockSize)
    {
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

        var leaves = new List<byte[]>();
        if (extents.Count <= 4)
        {
            WriteExtentHeader(dest, (ushort)extents.Count, 0);
            var offset = 12;
            foreach (var extent in extents)
            {
                WriteExtentEntry(dest, offset, extent);
                offset += 12;
            }
        }
        else
        {
            var groupCount = (extents.Count + 3) / 4;
            if (leafBlocks.Length < groupCount)
            {
                throw new InvalidOperationException("Extent index tree under-allocated leaf blocks.");
            }

            WriteExtentHeader(dest, (ushort)groupCount, 1);
            var offset = 12;
            for (var g = 0; g < groupCount; g++)
            {
                var first = extents[g * 4];
                WriteU32(dest, offset, first.Logical);
                WriteU32(dest, offset + 4, (uint)(leafBlocks[g] & 0xFFFFFFFF));
                WriteU16(dest, offset + 8, (ushort)(leafBlocks[g] >> 32));
                WriteU16(dest, offset + 10, 0);
                offset += 12;
            }

            for (var g = 0; g < groupCount; g++)
            {
                var leaf = new byte[blockSize];
                var group = extents.Skip(g * 4).Take(4).ToArray();
                WriteExtentHeader(leaf.AsSpan(0, 12), (ushort)group.Length, 0);
                var leafOffset = 12;
                foreach (var extent in group)
                {
                    WriteExtentEntry(leaf, leafOffset, extent);
                    leafOffset += 12;
                }

                leaves.Add(leaf);
            }
        }

        return leaves;
    }

    private static void WriteExtentHeader(Span<byte> dest, ushort entries, ushort depth)
    {
        WriteU16(dest, 0, ExtentMagic);
        WriteU16(dest, 2, entries);
        WriteU16(dest, 4, 4); // max entries
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

    private static void WriteNodeData(byte[] image, Node node, int blockSize)
    {
        if (node.IsSymlink && node.Size < 60)
        {
            return;
        }

        if (node.IsDirectory)
        {
            for (var i = 0; i < node.DirectoryBlocks.Count; i++)
            {
                var offset = checked((int)((node.DataStartBlock + i) * blockSize));
                Buffer.BlockCopy(node.DirectoryBlocks[i], 0, image, offset, blockSize);
            }

            return;
        }

        if (node.IsSymlink)
        {
            var bytes = Encoding.UTF8.GetBytes(node.SymlinkTarget ?? string.Empty);
            WriteBytesToBlocks(image, node, bytes, blockSize);
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
                        break;
                    }

                    read += n;
                }

                var offset = checked((int)(assigned * blockSize));
                Buffer.BlockCopy(buffer, 0, image, offset, want);
                assigned++;
            }
        }
    }

    private static void WriteBytesToBlocks(byte[] image, Node node, byte[] bytes, int blockSize)
    {
        var buffer = new byte[blockSize];
        var assigned = node.DataStartBlock;
        var total = (bytes.Length + blockSize - 1) / blockSize;
        for (var i = 0; i < total; i++)
        {
            var want = Math.Min(blockSize, bytes.Length - i * blockSize);
            buffer.AsSpan().Clear();
            bytes.AsSpan(i * blockSize, want).CopyTo(buffer);
            var offset = checked((int)(assigned * blockSize));
            Buffer.BlockCopy(buffer, 0, image, offset, want);
            assigned++;
        }
    }

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;

    private static int Log2(int value)
    {
        var result = 0;
        while ((1 << result) < value)
        {
            result++;
        }

        return result;
    }

    private static long RoundUpPowerOfTwo(long value)
    {
        var result = 8L;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    private static void WriteU16(Span<byte> span, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], value);

    private static void WriteU32(Span<byte> span, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], value);

    private static void WriteU16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteU32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
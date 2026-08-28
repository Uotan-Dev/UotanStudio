using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// 从目录打包 erofs 镜像的简化构建器（与 ErofsParser 严格对齐同一布局）。
/// <para>A simplified erofs image builder that packs a directory (strictly aligned with the
/// ErofsParser layout).</para>
/// </summary>
public sealed class ErofsBuilder
{
    private const uint MetaBlkAddr = 1;

    /// <summary>
    /// 将目录打包为 erofs 镜像字节。
    /// <para>Packs a directory into erofs image bytes.</para>
    /// </summary>
    public byte[] Build(string directoryPath, ErofsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Input path is not a directory: '{directoryPath}'.");
        }

        var root = CollectNodes(directoryPath);
        return BuildImage(root, options ?? new ErofsOptions());
    }

    /// <summary>
    /// 将目录打包为 erofs 镜像并写入输出流。
    /// <para>Packs a directory into an erofs image and writes it to the output stream.</para>
    /// </summary>
    public void Build(string directoryPath, Stream output, ErofsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var image = Build(directoryPath, options);
        output.Write(image);
    }

    private static BuildNode CollectNodes(string directoryPath)
    {
        var rootFull = Path.GetFullPath(directoryPath);
        return CollectNode(rootFull, isRoot: true);
    }

    private static BuildNode CollectNode(string fullPath, bool isRoot)
    {
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // 符号链接不纳入镜像。Symbolic links are skipped.
            throw new InvalidOperationException($"Symbolic links are not supported: '{fullPath}'.");
        }

        var name = isRoot ? string.Empty : Path.GetFileName(fullPath);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            var node = new BuildNode { Name = name, IsDirectory = true, FullPath = fullPath };
            foreach (var childPath in Directory.EnumerateFileSystemEntries(fullPath))
            {
                node.Children.Add(CollectNode(childPath, isRoot: false));
            }

            return node;
        }

        var info = new FileInfo(fullPath);
        return new BuildNode { Name = name, IsDirectory = false, FullPath = fullPath, Size = info.Length };
    }

    private byte[] BuildImage(BuildNode root, ErofsOptions options)
    {
        options.Validate();
        var blockSize = options.BlockSize;
        var clusterSize = blockSize;

        var ordered = new List<BuildNode>();
        CollectPreOrder(root, ordered);

        // 目录块数量（决定目录 inode 的 i_size）。Directory block counts (drive directory i_size).
        foreach (var node in ordered)
        {
            if (!node.IsDirectory)
            {
                continue;
            }

            node.DirBlockCount = CountDirBlocks(node.Children, blockSize);
            node.Size = (long)node.DirBlockCount * blockSize;
        }

        // 分配 nid（元数据区连续字节偏移）。Assign nids (contiguous byte offsets in the metadata area).
        var metaOff = 0L;
        var ino = 1U;
        foreach (var node in ordered)
        {
            var inode = CreateInode(node, options, ino++);
            node.Inode = inode;
            node.IndexSize = inode.IsCompressed
                ? ErofsCompression.PclusterCount(inode.Size, clusterSize) * ErofsCompression.PclusterIndexSize
                : 0;

            var total = inode.OnDiskSize + node.IndexSize;
            var blockOff = metaOff & (blockSize - 1);
            if (blockOff != 0 && blockOff + total > blockSize)
            {
                metaOff = AlignUp(metaOff, blockSize);
            }

            node.Nid = (ulong)metaOff;
            metaOff += total;
        }

        // 目录块（元数据区，块对齐）。Directory blocks (metadata area, block aligned).
        foreach (var node in ordered)
        {
            if (!node.IsDirectory)
            {
                continue;
            }

            var dirStart = AlignUp(metaOff, blockSize);
            node.DataBlkAddr = (uint)(MetaBlkAddr + dirStart / blockSize);
            node.DirBlocks = BuildDirBlocks(node.Children, blockSize);
            node.Inode.U = node.DataBlkAddr;
            metaOff = dirStart + (long)node.DirBlocks.Length * blockSize;
        }

        // 数据区布局。Data area layout.
        var dataStartBlk = MetaBlkAddr + (uint)(AlignUp(metaOff, blockSize) / blockSize);
        var dataBlk = (long)dataStartBlk;
        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {
                continue;
            }

            if (node.Inode.IsCompressed)
            {
                var pclusterCount = ErofsCompression.PclusterCount((ulong)node.Size, clusterSize);
                var pclusters = new List<PclusterPlan>(pclusterCount);
                for (var i = 0; i < pclusterCount; i++)
                {
                    pclusters.Add(new PclusterPlan { BlockAddress = (uint)dataBlk });
                    dataBlk++;
                }

                node.Pclusters = pclusters;
                node.Inode.U = (uint)pclusterCount;
            }
            else
            {
                node.DataBlkAddr = (uint)dataBlk;
                node.Inode.U = node.DataBlkAddr;
                dataBlk += CeilDiv(node.Size, blockSize);
            }
        }

        var totalBlocks = dataBlk;
        if (totalBlocks > uint.MaxValue)
        {
            throw new InvalidOperationException($"Image block count {totalBlocks} exceeds the 32-bit superblock field.");
        }

        if (root.Nid > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Root nid {root.Nid} exceeds the 16-bit superblock field.");
        }

        var image = new byte[totalBlocks * blockSize];

        // 超级块。Superblock.
        var superblock = new ErofsSuperblock
        {
            MagicValue = ErofsSuperblock.Magic,
            BlockSizeBits = (byte)BitOperations.Log2((uint)blockSize),
            RootNid = (ushort)root.Nid,
            Inos = (ulong)ordered.Count,
            BuildTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Blocks = (uint)totalBlocks,
            MetaBlkAddr = MetaBlkAddr,
            AvailableComprAlgs = options.Compress ? (ushort)1 : (ushort)0,
            Uuid = Guid.NewGuid().ToByteArray(),
            VolumeName = options.VolumeName,
        };
        superblock.WriteTo(image.AsSpan(ErofsSuperblock.SuperblockOffset, ErofsSuperblock.SuperblockSize));

        // 数据区（先写数据，随后元数据中的压缩索引表才能引用块号）。
        // Data area first so the compressed index table can reference the block numbers.
        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {
                continue;
            }

            if (node.Inode.IsCompressed)
            {
                WriteCompressedData(image, node, blockSize, options);
            }
            else
            {
                WriteFlatData(image, node, blockSize);
            }
        }

        // 元数据区：inode + 压缩索引表 + 目录块。Metadata area: inodes + compressed index tables + directory blocks.
        foreach (var node in ordered)
        {
            var inodeOffset = (int)(MetaBlkAddr * (ulong)blockSize + node.Nid);
            node.Inode.WriteTo(image.AsSpan(inodeOffset, node.Inode.OnDiskSize));
            if (node.Inode.IsCompressed)
            {
                WritePclusterIndexes(image, inodeOffset + node.Inode.OnDiskSize, node);
            }

            if (node.IsDirectory)
            {
                var dirOffset = (int)node.DataBlkAddr * blockSize;
                foreach (var block in node.DirBlocks)
                {
                    block.CopyTo(image, dirOffset);
                    dirOffset += blockSize;
                }
            }
        }

        return image;
    }

    private static void CollectPreOrder(BuildNode node, List<BuildNode> ordered)
    {
        ordered.Add(node);
        foreach (var child in node.Children)
        {
            CollectPreOrder(child, ordered);
        }
    }

    private static ErofsInode CreateInode(BuildNode node, ErofsOptions options, uint ino)
    {
        var inode = new ErofsInode { Ino = ino };
        if (node.IsDirectory)
        {
            inode.Mode = (ushort)(ErofsInode.ModeDirectory | 0x1ED); // S_IFDIR | 0755
            inode.Advise = ErofsInode.DataLayoutFlat;
            inode.Size = (ulong)node.Size;
            inode.Nlink = (ulong)(2 + node.Children.Count(child => child.IsDirectory));
        }
        else
        {
            inode.Mode = (ushort)(ErofsInode.ModeRegularFile | 0x1A4); // S_IFREG | 0644
            inode.Size = (ulong)node.Size;
            inode.Nlink = 1;
            inode.Advise = options.Compress ? ErofsInode.DataLayoutCompressed : ErofsInode.DataLayoutFlat;
        }

        if (node.Size > uint.MaxValue || inode.Nlink > ushort.MaxValue)
        {
            inode.Advise |= ErofsInode.FlagExtended;
        }

        return inode;
    }

    private static int CountDirBlocks(List<BuildNode> children, int blockSize)
    {
        if (children.Count == 0)
        {
            return 1;
        }

        var blocks = 1;
        var usedDirent = 0;
        var usedNames = 0;
        foreach (var child in children)
        {
            var nameBytes = Encoding.UTF8.GetByteCount(child.Name);
            var need = ErofsDirectoryEntry.DirentSize + nameBytes;
            if (usedDirent + usedNames + need > blockSize)
            {
                blocks++;
                usedDirent = 0;
                usedNames = 0;
            }

            usedDirent += ErofsDirectoryEntry.DirentSize;
            usedNames += nameBytes;
        }

        return blocks;
    }

    private static byte[][] BuildDirBlocks(List<BuildNode> children, int blockSize)
    {
        if (children.Count == 0)
        {
            return [new byte[blockSize]];
        }

        var sorted = children.OrderBy(child => child.Name, StringComparer.Ordinal).ToList();
        var blocks = new List<byte[]>();
        var current = new byte[blockSize];
        var direntPos = 0;
        var namePos = blockSize;
        foreach (var child in sorted)
        {
            var nameBytes = Encoding.UTF8.GetBytes(child.Name);
            if (namePos - nameBytes.Length < direntPos + ErofsDirectoryEntry.DirentSize)
            {
                blocks.Add(current);
                current = new byte[blockSize];
                direntPos = 0;
                namePos = blockSize;
                if (namePos - nameBytes.Length < direntPos + ErofsDirectoryEntry.DirentSize)
                {
                    throw new InvalidOperationException($"Directory entry name too long: '{child.Name}'.");
                }
            }

            namePos -= nameBytes.Length;
            nameBytes.CopyTo(current, namePos);
            BinaryPrimitives.WriteUInt64LittleEndian(current.AsSpan(direntPos), child.Nid);
            BinaryPrimitives.WriteUInt16LittleEndian(current.AsSpan(direntPos + 8), (ushort)namePos);
            current[direntPos + 10] = child.IsDirectory
                ? ErofsInode.FileTypeDirectory
                : ErofsInode.FileTypeRegularFile;
            current[direntPos + 11] = 0;
            direntPos += ErofsDirectoryEntry.DirentSize;
        }

        blocks.Add(current);
        return blocks.ToArray();
    }

    private static void WritePclusterIndexes(byte[] image, int indexOffset, BuildNode node)
    {
        var pclusters = node.Pclusters!;
        for (var i = 0; i < pclusters.Count; i++)
        {
            var plan = pclusters[i];
            new PclusterIndex(plan.BlockAddress, plan.CompressedLength, plan.Flags)
                .WriteTo(image.AsSpan(indexOffset + i * ErofsCompression.PclusterIndexSize, ErofsCompression.PclusterIndexSize));
        }
    }

    private static void WriteCompressedData(byte[] image, BuildNode node, int blockSize, ErofsOptions options)
    {
        var clusterSize = blockSize;
        var pclusters = node.Pclusters!;
        using var stream = File.OpenRead(node.FullPath);
        var buffer = new byte[clusterSize];
        var compressed = new byte[ErofsCompression.MaximumOutputSize(clusterSize)];
        var remaining = node.Size;
        for (var i = 0; i < pclusters.Count; i++)
        {
            var plan = pclusters[i];
            var count = (int)Math.Min(clusterSize, remaining);
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of file '{node.FullPath}'.");
                }

                read += n;
            }

            var blockOffset = (int)plan.BlockAddress * blockSize;
            var compressedLength = ErofsCompression.Compress(buffer, 0, count, compressed, 0, compressed.Length, options.CompressionLevel);
            if (compressedLength <= 0 || compressedLength >= count)
            {
                // 数据不可压缩：按原始方式存储。Incompressible data: stored plain.
                buffer.AsSpan(0, count).CopyTo(image.AsSpan(blockOffset, count));
                plan.CompressedLength = (ushort)count;
                plan.Flags = ErofsCompression.FlagPlain;
            }
            else
            {
                compressed.AsSpan(0, compressedLength).CopyTo(image.AsSpan(blockOffset, compressedLength));
                plan.CompressedLength = (ushort)compressedLength;
                plan.Flags = 0;
            }

            remaining -= count;
        }
    }

    private static void WriteFlatData(byte[] image, BuildNode node, int blockSize)
    {
        if (node.Size == 0)
        {
            return;
        }

        using var stream = File.OpenRead(node.FullPath);
        var blockOffset = (long)node.DataBlkAddr * blockSize;
        var remaining = node.Size;
        var buffer = new byte[1 << 16];
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new EndOfStreamException($"Unexpected end of file '{node.FullPath}'.");
            }

            buffer.AsSpan(0, read).CopyTo(image.AsSpan((int)blockOffset, read));
            blockOffset += read;
            remaining -= read;
        }
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~(long)(alignment - 1);

    private static long CeilDiv(long value, int divisor) => (value + divisor - 1) / divisor;

    /// <summary>
    /// 构建树节点（打包中间模型）。
    /// <para>Build-tree node (intermediate packing model).</para>
    /// </summary>
    private sealed class BuildNode
    {
        public string Name = string.Empty;
        public bool IsDirectory;
        public string FullPath = string.Empty;
        public long Size;
        public List<BuildNode> Children = [];
        public ulong Nid;
        public ErofsInode Inode = null!;
        public int IndexSize;
        public int DirBlockCount;
        public byte[][] DirBlocks = [];
        public uint DataBlkAddr;
        public List<PclusterPlan>? Pclusters;
    }

    /// <summary>
    /// 压缩 pcluster 布局计划（数据块号 + 存储长度 + 标志）。
    /// <para>Compressed pcluster layout plan (data block number + stored length + flags).</para>
    /// </summary>
    private sealed class PclusterPlan
    {
        public uint BlockAddress;
        public ushort CompressedLength;
        public byte Flags;
    }
}
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// 从目录打包 erofs 镜像的简化构建器（与 ErofsParser 严格对齐同一布局）。
/// <para>A simplified erofs image builder that packs a directory (strictly aligned with the
/// ErofsParser layout).</para>
/// <para>Layout is planned in a first pass and the image is streamed out in a second pass:
/// the output stream is <see cref="Stream.SetLength(long)"/>-preallocated and each region
/// (superblock, data area, inodes, compressed index tables and directory blocks) is seeked
/// to and written individually, so no full in-memory image array is ever built.
/// 布局在第一遍扫描中规划，第二遍将镜像流式写出：先对输出流
/// <see cref="Stream.SetLength(long)"/> 预分配，再逐个定位写入超级块、数据区、
/// inode、压缩索引表与目录块，全程不构造完整镜像数组。</para>
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
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"uotan-erofs-{Guid.NewGuid():N}.tmp");
        try
        {
            Build(directoryPath, temporaryPath, options);
            using var image = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (image.Length > Array.MaxLength)
            {
                throw new InvalidOperationException($"Image size {image.Length} exceeds the maximum byte-array length.");
            }

            var result = new byte[(int)image.Length];
            image.ReadExactly(result);
            return result;
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* Cleanup must not replace the primary failure. */ }
        }
    }

    /// <summary>
    /// 将目录原子打包到指定路径。构建使用目标同目录的临时文件，成功刷新后再覆盖目标。
    /// <para>Atomically packs a directory to a path by building and flushing a temporary file
    /// in the destination directory before replacing the destination.</para>
    /// </summary>
    public void Build(string directoryPath, string outputPath, ErofsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        var temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        // Collect before creating a destination parent that may be inside the source tree.
        var effective = ValidateAndCollect(directoryPath, out var root, options, fullOutputPath, temporaryPath);
        Directory.CreateDirectory(outputDirectory);

        try
        {
            using (var temporary = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                       FileOptions.SequentialScan))
            {
                BuildImage(root, effective, temporary, 0, freshDestination: true);
                temporary.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* Cleanup must not replace the primary failure. */ }
        }
    }

    /// <summary>
    /// 将目录打包为 erofs 镜像并从输出流当前 Position 写入。可寻址流保留此前前缀；
    /// 仅可写的不可寻址流通过临时文件顺序复制。
    /// <para>Packs a directory starting at the output stream's current position. Seekable streams
    /// retain their prefix; writable non-seekable streams are supported through file staging.</para>
    /// </summary>
    public void Build(string directoryPath, Stream output, ErofsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        var excludedPaths = output is FileStream fileStream
            ? new[] { Path.GetFullPath(fileStream.Name) }
            : [];
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"uotan-erofs-{Guid.NewGuid():N}.tmp");
        var stagingExclusions = excludedPaths.Append(Path.GetFullPath(temporaryPath)).ToArray();
        var effective = ValidateAndCollect(directoryPath, out var root, options, stagingExclusions);
        if (output.CanSeek && SupportsResizeWithoutMutation(output))
        {
            var baseOffset = output.Position;
            BuildImage(root, effective, output, baseOffset, freshDestination: false);
            return;
        }

        try
        {
            using (var staging = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                       FileOptions.SequentialScan))
            {
                BuildImage(root, effective, staging, 0, freshDestination: true);
                staging.Position = 0;
                staging.CopyTo(output);
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* Cleanup must not replace the primary failure. */ }
        }
    }

    private static bool SupportsResizeWithoutMutation(Stream output)
    {
        var position = output.Position;
        try
        {
            output.SetLength(output.Length);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        finally
        {
            output.Position = position;
        }
    }

    private static ErofsOptions ValidateAndCollect(
        string directoryPath,
        out BuildNode root,
        ErofsOptions? options,
        params string[] excludedPaths)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Input path is not a directory: '{directoryPath}'.");
        }

        var effective = options ?? new ErofsOptions();
        effective.Validate();
        var exclusions = excludedPaths.Length == 0
            ? null
            : new HashSet<string>(
                excludedPaths.Select(Path.GetFullPath),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        root = CollectNodes(directoryPath, exclusions);
        return effective;
    }

    private static BuildNode CollectNodes(string directoryPath, HashSet<string>? excludedPaths)
    {
        var rootFull = Path.GetFullPath(directoryPath);
        return CollectNode(rootFull, isRoot: true, excludedPaths);
    }

    private static BuildNode CollectNode(string fullPath, bool isRoot, HashSet<string>? excludedPaths)
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
                var childFullPath = Path.GetFullPath(childPath);
                if (excludedPaths?.Contains(childFullPath) == true)
                {
                    continue;
                }

                node.Children.Add(CollectNode(childFullPath, isRoot: false, excludedPaths));
            }

            return node;
        }

        var info = new FileInfo(fullPath);
        return new BuildNode { Name = name, IsDirectory = false, FullPath = fullPath, Size = info.Length };
    }

    private static void BuildImage(BuildNode root, ErofsOptions options, Stream destination, long baseOffset, bool freshDestination)
    {
        using var output = new OffsetStream(destination, baseOffset);
        var blockSize = options.BlockSize;
        var clusterSize = blockSize;

        // ---- First pass: plan the layout (no image allocation). ----
        // 第一遍：规划布局（不分配镜像）。
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

        // ---- Second pass: stream the image out. ----
        // 第二遍：流式写出镜像。
        var finalSize = checked(totalBlocks * blockSize);
        var oldLength = output.Length;
        output.SetLength(finalSize);
        if (!freshDestination)
        {
            // Newly extended storage is already zero; clear only bytes that overlap old content.
            ZeroImage(output, Math.Min(oldLength, finalSize));
        }

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
        var superblockBuffer = new byte[ErofsSuperblock.SuperblockSize];
        superblock.WriteTo(superblockBuffer);
        WriteAt(output, ErofsSuperblock.SuperblockOffset, superblockBuffer, superblockBuffer.Length);

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
                WriteCompressedData(output, node, blockSize, options);
            }
            else
            {
                WriteFlatData(output, node, blockSize);
            }
        }

        // 元数据区：inode + 压缩索引表 + 目录块。Metadata area: inodes + compressed index tables + directory blocks.
        foreach (var node in ordered)
        {
            var inodeOffset = checked((long)MetaBlkAddr * blockSize + (long)node.Nid);
            var inodeBuffer = new byte[node.Inode.OnDiskSize];
            node.Inode.WriteTo(inodeBuffer);
            WriteAt(output, inodeOffset, inodeBuffer, inodeBuffer.Length);
            if (node.Inode.IsCompressed)
            {
                WritePclusterIndexes(output, checked(inodeOffset + node.Inode.OnDiskSize), node);
            }

            if (node.IsDirectory)
            {
                var dirOffset = checked((long)node.DataBlkAddr * blockSize);
                foreach (var block in node.DirBlocks)
                {
                    WriteAt(output, dirOffset, block, blockSize);
                    dirOffset += blockSize;
                }
            }
        }

        output.Position = finalSize;
    }

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

    private static void WritePclusterIndexes(Stream output, long indexOffset, BuildNode node)
    {
        const int EntriesPerBatch = 1024;
        var pclusters = node.Pclusters!;
        var batch = new byte[EntriesPerBatch * ErofsCompression.PclusterIndexSize];
        output.Position = indexOffset;
        for (var first = 0; first < pclusters.Count; first += EntriesPerBatch)
        {
            var count = Math.Min(EntriesPerBatch, pclusters.Count - first);
            for (var i = 0; i < count; i++)
            {
                var plan = pclusters[first + i];
                var entry = batch.AsSpan(i * ErofsCompression.PclusterIndexSize, ErofsCompression.PclusterIndexSize);
                new PclusterIndex(plan.BlockAddress, plan.CompressedLength, plan.Flags).WriteTo(entry);
            }
            output.Write(batch, 0, count * ErofsCompression.PclusterIndexSize);
        }
    }

    private static void WriteCompressedData(Stream output, BuildNode node, int blockSize, ErofsOptions options)
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

            var blockOffset = checked((long)plan.BlockAddress * blockSize);
            var compressedLength = ErofsCompression.Compress(buffer, 0, count, compressed, 0, compressed.Length, options.CompressionLevel);
            if (compressedLength <= 0 || compressedLength >= count)
            {
                // 数据不可压缩：按原始方式存储。Incompressible data: stored plain.
                WriteAt(output, blockOffset, buffer, count);
                plan.CompressedLength = (ushort)count;
                plan.Flags = ErofsCompression.FlagPlain;
            }
            else
            {
                WriteAt(output, blockOffset, compressed, compressedLength);
                plan.CompressedLength = (ushort)compressedLength;
                plan.Flags = 0;
            }

            remaining -= count;
        }
    }

    private static void WriteFlatData(Stream output, BuildNode node, int blockSize)
    {
        if (node.Size == 0)
        {
            return;
        }

        using var stream = File.OpenRead(node.FullPath);
        var blockOffset = checked((long)node.DataBlkAddr * blockSize);
        var remaining = node.Size;
        var buffer = new byte[1 << 16];
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new EndOfStreamException($"Unexpected end of file '{node.FullPath}'.");
            }

            output.Position = blockOffset;
            output.Write(buffer, 0, read);
            blockOffset += read;
            remaining -= read;
        }
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~(long)(alignment - 1);

    private static long CeilDiv(long value, int divisor) => (value + divisor - 1) / divisor;

    private sealed class OffsetStream(Stream inner, long baseOffset) : Stream
    {
        private long _position;

        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => Math.Max(0, inner.Length - baseOffset);

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
                inner.Position = checked(baseOffset + value);
            }
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return target;
        }

        public override void SetLength(long value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            var requiredEnd = checked(baseOffset + value);
            if (requiredEnd > inner.Length)
            {
                inner.SetLength(requiredEnd);
            }
            if (_position > value)
            {
                Position = value;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Position = checked(baseOffset + _position);
            inner.Write(buffer, offset, count);
            _position += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Position = checked(baseOffset + _position);
            inner.Write(buffer);
            _position += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            // The caller owns the destination stream.
            base.Dispose(disposing);
        }
    }

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
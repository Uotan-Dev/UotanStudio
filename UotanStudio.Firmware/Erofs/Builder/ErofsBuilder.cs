using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// 从目录打包 erofs 镜像的构建器，磁盘布局对齐 erofs-utils mkfs.erofs 与内核 erofs_fs.h：
/// 超级块位于偏移 1024（128 字节槽位，结构体 112 字节）；inode 表紧随其后，每个 inode 记录
/// 占 OnDiskSize(32/64B) + inline xattr + 内联数据，记录按 32 字节对齐推进，
/// nid = 距 inode 表起点的 32 字节槽数。目录为“dirent 数组 + 紧随名字区”的紧凑布局
/// （de[0].nameoff = 名字区起点，名字按 UTF-8 字节序递增，与 fsck 校验一致）；目录数据
/// 单块内联（tail-packing），多块写入数据区。文件支持 flat 与 LZ4 压缩（legacy full
/// 8B 索引，lcluster = 块大小）。
/// <para>A directory-to-image EROFS builder whose on-disk layout follows erofs-utils mkfs.erofs and
/// the kernel erofs_fs.h: the superblock lives at offset 1024 (128-byte slot, 112-byte struct);
/// the inode table follows it, each inode record occupying OnDiskSize (32/64B) + inline xattrs +
/// inline data, advanced on 32-byte boundaries, with nid = the 32-byte slot count from the inode
/// table start. Directories use the compact "dirent array + contiguous name area" layout
/// (de[0].nameoff anchors the name area; names are strictly increasing UTF-8 byte sequences, as
/// fsck checks); single-block directory payloads are inlined (tail-packing), larger ones go to
/// the data area. Files support flat and LZ4 compression (legacy full 8-byte indexes,
/// lcluster = block size).</para>
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
    /// <summary>inode 表起点：超级块 128 字节槽位之后。Inode table start: right after the 128-byte superblock slot.</summary>
    private const long InodeTableStart = ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize;

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

    private static void CollectPreOrder(BuildNode node, List<BuildNode> ordered)
    {
        ordered.Add(node);
        foreach (var child in node.Children)
        {
            child.Parent = node;
            CollectPreOrder(child, ordered);
        }
    }

    /// <summary>
    /// 把 <see cref="ErofsOptions.FileXattrs"/> 中按相对路径列出的 xattr 编码为 inline ibody
    /// 挂到对应节点。未配置注入或路径不匹配时节点保持无 xattr。
    /// <para>Encodes the per-path xattrs from <see cref="ErofsOptions.FileXattrs"/> into inline
    /// ibodies and attaches them to the matching nodes. Nodes stay xattr-free when no injection is
    /// configured or the path does not match.</para>
    /// </summary>
    private static void BindXattrs(List<BuildNode> ordered, ErofsOptions options)
    {
        if (options.FileXattrs is not { Count: > 0 })
        {
            return;
        }

        foreach (var node in ordered)
        {
            if (node.IsDirectory || node.Parent is null)
            {
                // 目录与根节点不承载文件 xattr 注入（目录 xattr 不在本写端支持范围）。
                // Directories and the root node do not carry file-xattr injection.
                continue;
            }

            if (!options.FileXattrs.TryGetValue(BuildImagePath(node), out var entries) ||
                entries is not { Count: > 0 })
            {
                continue;
            }

            node.XattrIbody = ErofsXattr.EncodeInline(entries);
        }
    }

    /// <summary>节点的镜像内相对路径（'/' 分隔；根为 ""）。Image-relative path of a node ('/'-separated; root is "").</summary>
    private static string BuildImagePath(BuildNode node)
    {
        var parts = new List<string>();
        for (var current = node; current.Parent is not null; current = current.Parent)
        {
            parts.Add(current.Name);
        }

        parts.Reverse();
        return string.Join('/', parts);
    }

    /// <summary>单节点构建状态。Per-node build state.</summary>
    private sealed class BuildNode
    {
        public string Name = string.Empty;
        public bool IsDirectory;
        public string FullPath = string.Empty;
        public long Size;
        public List<BuildNode> Children = [];
        public BuildNode? Parent;
        public ulong Nid;
        public long MetaOffset;
        public byte[] InlineData = [];   // 内联数据：目录单块负载 / 压缩 map header+索引表
        public long DirBlocks;           // 目录数据块数（≤1 内联，>1 数据区）
        public long DirBlockAddr = -1;
        public long FlatBlockAddr = -1;  // flat 文件数据区起始块
        public long FlatBlocks;
        public PclusterPlan[]? Pclusters;
        public uint CompressedBlocks;
        public uint ClusterCount;
        public bool CompactedIndexes;   // 压缩索引布局：false = legacy full 8B；true = compacted 4B amortized
        public byte[] XattrIbody = [];  // 编码后的 inline xattr ibody（空 = 无 xattr）。Encoded inline xattr ibody (empty = none).
    }

    /// <summary>压缩 pcluster 规划。Per-pcluster compression plan.</summary>
    private sealed class PclusterPlan
    {
        public long SourceOffset;
        public int SourceLength;
        public int CompressedLength;
        public uint BlockAddress;
    }

    private static void BuildImage(BuildNode root, ErofsOptions options, Stream destination, long baseOffset, bool freshDestination)
    {
        using var output = new OffsetStream(destination, baseOffset);
        var blockSize = options.BlockSize;

        // ---- Pass 1: collect in pre-order and plan per-node storage. ----
        // 第一遍：先序收集并规划各节点存储方式（决策与 nid 无关）。
        var ordered = new List<BuildNode>();
        CollectPreOrder(root, ordered);
        BindXattrs(ordered, options);
        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {
                // 目录：整负载 ≤ 一块 → 内联（与 odm/mkfs 小目录一致，root 目录即内联）；
                // 否则多块目录数据写入数据区，Pass 4 按块独立序列化（名字不跨块）。
                node.DirBlocks = CeilDiv(EstimateDirPayloadLength(node), blockSize);
            }
            else if (node.Size > 0)
            {
                // 文件存储（与 mkfs 一致）：
                //  - 压缩（legacy full / compacted）仅在开启 Compress 且确有收益时采用；
                //  - 未压缩：主体整块写入数据区（FLAT_PLAIN），余数尾部内联在 inode 后
                //    （FLAT_INLINE，tail-packing）；不足一块的整个文件全部内联。
                // File storage (matching mkfs): compressed layouts only when requested and the
                // output is smaller; otherwise whole blocks go to the data area (FLAT_PLAIN) and
                // the remainder is inlined after the inode (FLAT_INLINE / tail-packing). A file
                // smaller than one block is fully inlined.
                if (options.Compress)
                {
                    node.Pclusters = TryPlanCompressed(node, blockSize, options);
                    if (node.Pclusters is not null)
                    {
                        node.CompressedBlocks = (uint)node.Pclusters.Sum(p => (long)CeilDiv(p.CompressedLength, blockSize));
                        node.ClusterCount = (uint)CeilDiv(node.Size, blockSize);
                        node.CompactedIndexes = options.UseCompactedIndexes;
                        continue;
                    }
                }

                var wholeBlocks = node.Size / blockSize;
                var remainder = node.Size % blockSize;
                node.FlatBlocks = wholeBlocks;
                if (remainder > 0)
                {
                    using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fs.Position = wholeBlocks * blockSize;
                    var tail = new byte[remainder];
                    fs.ReadExactly(tail);
                    node.InlineData = tail;
                }
            }
        }

        // ---- Pass 2: assign nids / metadata offsets. ----
        // 第二遍：分配 nid 与元数据偏移。每个 inode 记录 = OnDiskSize + 内联数据
        //（目录单块负载 / 压缩 map header + 索引表），记录不得跨元数据块（fsck 校验
        // "inline data cross block boundary"），跨块时整体对齐到下一块起点。
        var metaOff = InodeTableStart;
        foreach (var node in ordered)
        {
            var recordSize = GetInodeRecordSize(node, blockSize);
            if (metaOff % blockSize + recordSize > blockSize)
            {
                metaOff = AlignUp(metaOff, blockSize);
            }

            node.MetaOffset = metaOff;
            node.Nid = (ulong)(metaOff / 32);
            metaOff += AlignUp(recordSize, 32);
        }

        // ---- Pass 3: allocate data-area blocks. ----
        // 第三遍：分配数据区起始块。
        var dataBlock = AlignUp(metaOff, blockSize) / blockSize;
        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {
                if (node.DirBlocks > 1)
                {
                    node.DirBlockAddr = dataBlock;
                    dataBlock += node.DirBlocks;
                }
            }
            else if (node.Pclusters is not null)
            {
                foreach (var p in node.Pclusters)
                {
                    p.BlockAddress = (uint)dataBlock;
                    dataBlock += CeilDiv(p.CompressedLength, blockSize);
                }
            }
            else if (node.FlatBlocks > 0)
            {
                node.FlatBlockAddr = dataBlock;
                dataBlock += node.FlatBlocks;
            }
        }
        var totalBlocks = dataBlock;
        if (totalBlocks > uint.MaxValue)
        {
            throw new InvalidOperationException($"Image block count {totalBlocks} exceeds the 32-bit superblock field.");
        }

        if (root.Nid > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Root nid {root.Nid} exceeds the 16-bit superblock field.");
        }

        // ---- Pass 4: stream the image out. ----
        // 第四遍：流式写出（此时 nid 已知，目录 dirent 可引用子节点 nid）。
        var finalSize = checked(totalBlocks * blockSize);
        var oldLength = output.Length;
        output.SetLength(finalSize);
        if (!freshDestination)
        {
            // Newly extended storage is already zero; clear only bytes that overlap old content.
            ZeroImage(output, Math.Min(oldLength, finalSize));
        }

        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var superblock = new ErofsSuperblock
        {
            MagicValue = ErofsSuperblock.Magic,
            BlockSizeBits = (byte)BitOperations.Log2((uint)blockSize),
            RootNid = (ushort)root.Nid,
            Inos = (ulong)ordered.Count,
            BuildTime = now,
            Blocks = (uint)totalBlocks,
            MetaBlkAddr = 0,
            AvailableComprAlgs = options.Compress ? (ushort)(1 << (int)options.Algorithm) : (ushort)0,
            FeatureIncompat = options.Compress ? ErofsSuperblock.IncompatLz4_0Padding : 0,
            Uuid = Guid.NewGuid().ToByteArray(),
            VolumeName = options.VolumeName,
        };
        var superblockBuffer = new byte[ErofsSuperblock.SuperblockSize];
        superblock.WriteTo(superblockBuffer);
        WriteAt(output, ErofsSuperblock.SuperblockOffset, superblockBuffer, superblockBuffer.Length);

        // 数据区（先写数据，随后元数据中的压缩索引表才能引用块号）。
        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {
                if (node.DirBlockAddr >= 0)
                {
                    WriteDirBlocks(output, node, blockSize);
                }
            }
            else if (node.Pclusters is not null)
            {
                WriteCompressedData(output, node, blockSize, options);
            }
            else if (node.FlatBlockAddr >= 0 && node.FlatBlocks > 0)
            {
                CopyFileRange(output, node, 0, node.FlatBlocks * blockSize, node.FlatBlockAddr * blockSize);
            }
        }

        // 元数据区：inode 记录 + 内联数据 + 压缩索引表。
        foreach (var node in ordered)
        {
            var inode = BuildInode(node, blockSize, now);
            var inodeBuffer = new byte[inode.OnDiskSize];
            inode.WriteTo(inodeBuffer);
            WriteAt(output, node.MetaOffset, inodeBuffer, inodeBuffer.Length);
            // inline xattr ibody 紧随 inode（大小 = XattrTotalSize）。
            // The inline xattr ibody follows the inode (size = XattrTotalSize).
            if (node.XattrIbody.Length > 0)
            {
                WriteAt(output, node.MetaOffset + inode.OnDiskSize, node.XattrIbody, node.XattrIbody.Length);
            }

            var cursor = node.MetaOffset + inode.OnDiskSize + inode.XattrTotalSize;

            if (node.IsDirectory)
            {
                // 目录单块内联数据紧随 inode；多块目录已在数据区写出。
                if (node.DirBlocks <= 1)
                {
                    WriteAt(output, cursor, BuildDirPayload(node), (int)EstimateDirPayloadLength(node));
                }
            }
            else if (node.Pclusters is not null)
            {
                WriteCompressedIndexes(output, cursor, node, blockSize, options);
            }
            else if (node.InlineData.Length > 0)
            {
                // 文件尾部（tail-packing）内联数据紧随 inode。
                // The file remainder (tail-packing) is inlined right after the inode.
                WriteAt(output, cursor, node.InlineData, node.InlineData.Length);
            }
        }

        output.Position = finalSize;
    }

    /// <summary>估算目录负载长度（dirent 区 + 名字区；含 "." 与 ".." 两个合成条目）。
    /// Estimates the directory payload length (dirent area + name area, incl. "." and "..").</summary>
    private static long EstimateDirPayloadLength(BuildNode node)
        => 2 * ErofsDirectoryEntry.DirentSize + 1 + 2
           + node.Children.Sum(child => (long)ErofsDirectoryEntry.DirentSize + Encoding.UTF8.GetByteCount(child.Name));

    /// <summary>
    /// 构造目录条目：`. ` 与 `..` 在前（mkfs 默认不省略点条目，fsck 强制检查二者存在），
    /// 随后子条目按 UTF-8 字节序排序（与 mkfs mergesort / fsck 递增校验一致）。
    /// <para>Builds the directory entries: "." and ".." first (mkfs keeps dot entries by default
    /// and fsck enforces their presence), then children ordered by their UTF-8 bytes (matches the
    /// mergesort in mkfs and fsck's strictly-increasing check).</para>
    /// </summary>
    private static List<DirEntry> BuildDirEntries(BuildNode node)
    {
        var dotNid = node.Nid;
        var dotDotNid = node.Parent?.Nid ?? node.Nid; // root 的 .. 指向自身。Root's .. points at itself.
        var entries = new List<DirEntry>(node.Children.Count + 2)
        {
            new DirEntry(new BuildNode { Nid = dotNid, IsDirectory = true, Name = "." }, "."u8.ToArray()),
            new DirEntry(new BuildNode { Nid = dotDotNid, IsDirectory = true, Name = ".." }, ".."u8.ToArray()),
        };
        entries.AddRange(node.Children
            .Select(child => new DirEntry(child, Encoding.UTF8.GetBytes(child.Name)))
            .OrderBy(e => e.NameBytes, ByteArrayComparer.Instance));
        return entries;
    }

    /// <summary>
    /// 目录单块负载：dirent 数组在前、名字区紧随（de[i].nameoff = 名字区起点 + 之前名字长
    /// 之和）。与内核 fill_dentries / mkfs fill_dirblock 同构。
    /// <para>Single-block directory payload: the dirent array first, then the contiguous name area
    /// (de[i].nameoff = name-area start + the sum of preceding name lengths), isomorphic to the
    /// kernel fill_dentries / mkfs fill_dirblock.</para>
    /// </summary>
    private static byte[] BuildDirPayload(BuildNode node)
    {
        var sorted = BuildDirEntries(node);
        var totalNames = sorted.Sum(e => e.NameBytes.Length);
        var payload = new byte[sorted.Count * ErofsDirectoryEntry.DirentSize + totalNames];
        var nameOff = sorted.Count * ErofsDirectoryEntry.DirentSize;
        for (var i = 0; i < sorted.Count; i++)
        {
            var (child, nameBytes) = (sorted[i].Child, sorted[i].NameBytes);
            var slot = payload.AsSpan(i * ErofsDirectoryEntry.DirentSize, ErofsDirectoryEntry.DirentSize);
            BinaryPrimitives.WriteUInt64LittleEndian(slot, child.Nid);
            BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(8), (ushort)nameOff);
            slot[10] = child.IsDirectory ? ErofsInode.FileTypeDirectory : ErofsInode.FileTypeRegularFile;
            slot[11] = 0;
            nameBytes.CopyTo(payload.AsSpan(nameOff, nameBytes.Length));
            nameOff += nameBytes.Length;
        }

        return payload;
    }

    private readonly record struct DirEntry(BuildNode Child, byte[] NameBytes);

    /// <summary>按 UTF-8 字节序比较名字（与内核 memcmp / fsck 校验一致）。Compares names by UTF-8 bytes.</summary>
    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var n = Math.Min(x.Length, y.Length);
            for (var i = 0; i < n; i++)
            {
                var d = x[i].CompareTo(y[i]);
                if (d != 0) return d;
            }

            return x.Length.CompareTo(y.Length);
        }
    }

    /// <summary>
    /// 多块目录：把排序后的条目按块切分，每块是独立的“dirent 数组 + 名字区”
    /// （名字不跨块；dirent 数组位于块首，名字区紧随其后；块尾空余置零）。
    /// <para>Multi-block directory: sorted entries are split into independent blocks, each holding a
    /// "dirent array + name area" (no name crosses a block; the dirent array is at the block head,
    /// followed by the name area; the block tail is zeroed).</para>
    /// </summary>
    private static void WriteDirBlocks(Stream output, BuildNode node, int blockSize)
    {
        var sorted = BuildDirEntries(node);
        var index = 0;
        for (var block = 0L; block < node.DirBlocks; block++)
        {
            var blockData = new byte[blockSize];
            var used = 0;
            var entriesInBlock = 0;
            while (index < sorted.Count)
            {
                var entry = sorted[index];
                var need = ErofsDirectoryEntry.DirentSize + entry.NameBytes.Length;
                if (entriesInBlock > 0 && used + need > blockSize)
                {
                    break; // 放不下该条目：封块。Cannot fit: seal the block.
                }

                // 名字区起点由 de[0].nameoff 锚定，且名字在块内连续存放；
                // 这里按“dirent 数组区 + 名字区”两段式排布。
                if (entriesInBlock == 0 && used + need > blockSize)
                {
                    break;
                }

                entriesInBlock++;
                var slot = blockData.AsSpan(used, ErofsDirectoryEntry.DirentSize);
                BinaryPrimitives.WriteUInt64LittleEndian(slot, entry.Child.Nid);
                BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(8), 0); // nameoff 稍后回填
                slot[10] = entry.Child.IsDirectory ? ErofsInode.FileTypeDirectory : ErofsInode.FileTypeRegularFile;
                slot[11] = 0;
                used += ErofsDirectoryEntry.DirentSize;
                entry.NameBytes.CopyTo(blockData.AsSpan(used, entry.NameBytes.Length));
                used += entry.NameBytes.Length;
                index++;
            }

            // 名字区起点 = dirent 区大小；回填所有 nameoff。
            if (entriesInBlock == 0)
            {
                break;
            }

            var nameOff = entriesInBlock * ErofsDirectoryEntry.DirentSize;
            var pos = 0;
            for (var i = 0; i < entriesInBlock; i++)
            {
                var slot = blockData.AsSpan(pos, ErofsDirectoryEntry.DirentSize);
                BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(8), (ushort)nameOff);
                pos += ErofsDirectoryEntry.DirentSize;
                nameOff += Encoding.UTF8.GetByteCount(sorted[index - entriesInBlock + i].Child.Name);
            }

            WriteAt(output, (node.DirBlockAddr + block) * blockSize, blockData, blockData.Length);
        }
    }

    private static long GetInodeRecordSize(BuildNode node, int blockSize)
    {
        var header = GetInodeHeaderSize(node) + node.XattrIbody.Length;
        if (node.IsDirectory)
        {
            return node.DirBlocks <= 1 ? header + EstimateDirPayloadLength(node) : header;
        }

        if (node.Pclusters is not null)
        {
            if (node.CompactedIndexes)
            {
                // compacted 4B amortized 记录 = inode + 8B map header + 位打包索引表
                //（每 2 簇一个 8B pack：2×encodebits 位 + pack 尾 4B blkaddr）。
                // Compacted 4B-amortized record = inode + 8B map header + the bit-packed index
                // table (one 8-byte pack per two clusters: 2×encodebits bits + a 4-byte trailing
                // pack-start block address).
                return header + ZErofsMapHeader.Size + 8L * CeilDiv(node.ClusterCount, 2L);
            }

            // 压缩记录 = inode + map header(8B) + 保留(8B) + nclusters × 8B 索引。
            // Compressed record = inode + 8B map header + 8B pad + nclusters × 8B index.
            return header + ZErofsMapHeader.Size + 8L + node.ClusterCount * ZErofsLclusterIndex.Size;
        }

        // 未压缩文件：整块部分在数据区，尾部内联在 inode 之后。
        // Uncompressed file: whole blocks live in the data area, the remainder follows the inode.
        return header + node.InlineData.Length;
    }

    private static long GetInodeHeaderSize(BuildNode node)
    {
        // compact inode(32B) 是 mkfs 默认（odm 文件/目录均为 compact）；仅当 size 超 32 位、
        // 目录 nlink 超 16 位时使用 extended(64B)。
        // The compact 32-byte inode is the mkfs default (odm files/dirs are compact); the extended
        // 64-byte form is only needed for sizes beyond 32 bits or 16-bit directory nlinks.
        if (node.IsDirectory)
        {
            var subdirs = (ulong)node.Children.Count(child => child.IsDirectory);
            return node.Size > uint.MaxValue || 2 + subdirs > ushort.MaxValue
                ? ErofsInode.ExtendedSize
                : ErofsInode.CompactSize;
        }

        return node.Size > uint.MaxValue ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
    }

    private static ErofsInode BuildInode(BuildNode node, int blockSize, ulong now)
    {
        var inode = new ErofsInode();
        if (node.Size > uint.MaxValue)
        {
            inode.Advise |= ErofsInode.FlagExtended;
        }

        inode.CTime = now;
        inode.CTimeNsec = 0;
        inode.Uid = 0;
        inode.Gid = 0;
        // inline xattr ibody 紧随 inode：XattrICount 与 ibody 长度满足
        // XattrTotalSize = 12 + 4 * (XattrICount - 1)（读端按该公式还原 ibody 大小）。
        // The inline xattr ibody follows the inode: XattrICount satisfies
        // XattrTotalSize = 12 + 4 * (XattrICount - 1) so the reader recovers the ibody size.
        if (node.XattrIbody.Length > 0)
        {
            inode.XattrICount = ErofsXattr.GetICountForIbodyLength(node.XattrIbody.Length);
        }
        if (node.IsDirectory)
        {
            inode.Mode = (ushort)(ErofsInode.ModeDirectory | 0x1ED); // S_IFDIR | 0755
            inode.Nlink = (ulong)(2 + node.Children.Count(child => child.IsDirectory));
            inode.Size = node.DirBlocks <= 1
                ? (ulong)EstimateDirPayloadLength(node)
                : (ulong)(node.DirBlocks * blockSize);
            if (node.DirBlocks <= 1)
            {
                // 目录数据内联：i_u = 空洞哨兵（EROFS_NULL_ADDR）。
                inode.Advise |= ErofsInode.DataLayoutInline;
                inode.U = uint.MaxValue;
            }
            else
            {
                inode.Advise |= ErofsInode.DataLayoutFlat;
                inode.U = (uint)node.DirBlockAddr;
            }
        }
        else
        {
            inode.Mode = (ushort)(ErofsInode.ModeRegularFile | 0x1A4); // S_IFREG | 0644
            inode.Nlink = 1;
            inode.Size = (ulong)node.Size;
            if (node.Pclusters is not null)
            {
                // 压缩：i_u 存压缩后物理块数。索引布局按 <see cref="ErofsOptions.UseCompactedIndexes"/>
                // 选择：legacy full 8B 索引（EROFS_INODE_COMPRESSED_FULL，默认）或 compacted
                // 4B amortized 位打包索引（EROFS_INODE_COMPRESSED_COMPACT）。
                // Compressed: i_u holds the compressed physical block count. The index layout is
                // selected via ErofsOptions.UseCompactedIndexes: legacy full 8-byte indexes
                // (COMPRESSED_FULL, default) or compacted 4-byte amortized bit-packed indexes
                // (COMPRESSED_COMPACT).
                inode.Advise |= node.CompactedIndexes
                    ? ErofsInode.DataLayoutCompactIndexes
                    : ErofsInode.DataLayoutCompressed;
                inode.U = node.CompressedBlocks;
            }
            else if (node.FlatBlocks > 0)
            {
                // 主体整块 flat：有尾部内联时为 FLAT_INLINE（tail-packing），否则纯 FLAT。
                // Whole blocks are flat; with an inline remainder this is FLAT_INLINE
                // (tail-packing), otherwise plain FLAT.
                inode.Advise |= node.InlineData.Length > 0 ? ErofsInode.DataLayoutInline : ErofsInode.DataLayoutFlat;
                inode.U = (uint)node.FlatBlockAddr;
            }
            else if (node.InlineData.Length > 0)
            {
                // 整个文件不足一块：全部内联，i_u = 空洞哨兵（EROFS_NULL_ADDR）。
                // The whole file is smaller than a block: fully inlined, i_u = hole sentinel.
                inode.Advise |= ErofsInode.DataLayoutInline;
                inode.U = uint.MaxValue;
            }
            else
            {
                // 空文件。Empty file.
                inode.Advise |= ErofsInode.DataLayoutFlat;
                inode.U = 0;
            }
        }

        return inode;
    }

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
        if (plans.Sum(p => (long)CeilDiv(p.CompressedLength, blockSize)) >= totalClusters)
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
        // 一致；默认 LZ4=0）。
        // The 8-byte z_erofs_map_header at the start of the inode's inline area: h_advise,
        // h_algorithmtype and h_clusterbits. The low nibble of h_algorithmtype is the HEAD1
        // algorithm (matching the kernel Z_EROFS_COMPRESSION_*; LZ4 = 0 by default).
        var header = new byte[ZErofsMapHeader.Size];
        header[4] = 0; // h_advise low
        header[5] = 0;
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
        // 一条 8B 索引：pcluster 首簇为 HEAD（clusterofs = 0，blkaddr = 物理块号），该 pcluster
        // 覆盖的其余逻辑簇为 NONHEAD（delta[0] = 距 HEAD 的簇数）。
        // Legacy full: an 8-byte pad after the map header (kernel Z_EROFS_FULL_INDEX_START), then
        // one 8-byte index per logical cluster: the first cluster of a pcluster is a HEAD
        // (clusterofs = 0, blkaddr = physical block); the covered clusters are NONHEAD entries
        // (delta[0] = distance from the HEAD cluster).
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
                BinaryPrimitives.WriteUInt16LittleEndian(entry, ZErofsLclusterIndex.LclusterTypeHead1);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), 0); // clusterofs
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), plan.BlockAddress);
            }
            else
            {
                // 同 pcluster 的其余簇：NONHEAD，di_u.delta[0]（offset 4）= 距 HEAD 的簇数。
                // Remaining clusters of the pcluster are NONHEAD entries whose di_u.delta[0]
                // (offset 4) holds the distance to the HEAD cluster.
                BinaryPrimitives.WriteUInt16LittleEndian(entry, ZErofsLclusterIndex.LclusterTypeNonHead);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), 0); // clusterofs 未用
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), (ushort)(c - headCluster));
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), 0); // delta[1] 未用
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
    /// 8B pack，pack 尾部 4B 存该 pack 起点块号（blkaddr 随 HEAD 递增，非 big-pcluster 从
    /// 首块号 - 1 起并 dummy_head 置位）。表起点由调用方传入（map header 之后）。
    /// <para>Writes a compacted 4B-amortized bit-packed index table (datalayout =
    /// COMPRESSED_COMPACT; 4B-only section, no 2B section), mirroring erofs-utils
    /// <c>write_compacted_indexes</c> bit packing: each entry v = (clustertype &lt;&lt; lobits) |
    /// offset is written at encodebits bits; two clusters form one 8-byte pack whose trailing 4
    /// bytes hold the pack-start block address (blkaddr increments per HEAD; without
    /// big-pcluster it starts at first-block-minus-one with dummy_head set). The table starts at
    /// the caller-provided offset (right after the map header).</para>
    /// </summary>
    private static void WriteCompactedIndexes4B(Stream output, long tableOffset, BuildNode node, int blockSize)
    {
        const int destsize = 4;
        const int vcnt = 2;
        var lclusterBits = BitOperations.Log2((uint)blockSize);
        var lobits = Math.Max(lclusterBits, 12); // ilog2(Z_EROFS_LI_D0_CBLKCNT) + 1 = 11 + 1
        var encodebits = (vcnt * destsize * 8 - 32) / vcnt; // (2*4*8-32)/2 = 16
        var packSize = vcnt * destsize; // 8 字节每 pack。8 bytes per pack.

        var clusters = node.ClusterCount;
        var pclusters = node.Pclusters!;
        var pack = new byte[packSize];
        var packOffset = tableOffset;
        var entryIndex = 0L;          // 逻辑簇序号。Logical cluster index.
        var pclusterIndex = 0;
        long blkaddr = (long)pclusters[0].BlockAddress - 1; // 非 big-pcluster 先减一。Non-big: minus one first.

        while (entryIndex < clusters)
        {
            // 每个 pack 处理 vcnt 条（不足时余下条目以全零占位，读端不会访问超出 totalIdx 的簇）。
            // Each pack handles vcnt entries (short packs are zero-padded; readers never access
            // clusters beyond totalIdx).
            // pack 尾部 4B 存“进入本 pack 时的当前块号”快照（即 pack 内首个 HEAD 的物理块号
            // 减一；读端经 pblk = packBlkaddr + 回看累计块数还原，与 erofs-utils 写端一致：
            // 非 big-pcluster 场景 mkfs 从首块号 - 1 起步、每遇 HEAD 递增，pack 尾写入进入值）。
            // The pack's trailing 4 bytes hold the block-address snapshot taken when entering this
            // pack (the first HEAD's physical block minus one; readers reconstruct
            // pblk = packBlkaddr + lookback count, matching the erofs-utils writer: without
            // big-pcluster mkfs starts at first-block-minus-one, increments per HEAD, and stores
            // the entry value in the pack tail).
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
                        // HEAD：clusterofs = 0，物理块号 = 递增后的 blkaddr（校验与规划一致）。
                        // HEAD: clusterofs = 0; the physical block is the incremented blkaddr
                        // (verified against the plan).
                        type = 1; // Z_EROFS_LCLUSTER_TYPE_HEAD1
                        offset = 0;
                        blkaddr++;
                        if (blkaddr != plan.BlockAddress)
                        {
                            throw new InvalidDataException(
                                $"Compacted index blkaddr {blkaddr} != planned block {plan.BlockAddress} for '{node.FullPath}'.");
                        }
                    }
                    else
                    {
                        // NONHEAD：offset = 距 HEAD 的簇数（pack 末条存 delta[1] 的规则只影响
                        // 2B 区与部分解码，纯 4B 区按 delta[0] 存即可；读端按前条回推 delta[0]）。
                        // NONHEAD: offset = cluster distance to the HEAD. The "last slot stores
                        // delta[1]" rule concerns the 2B section; in the 4B section we store
                        // delta[0] and the reader derives delta[0] from the previous entry anyway.
                        type = 2; // Z_EROFS_LCLUSTER_TYPE_NONHEAD
                        offset = (uint)(entryIndex - headCluster);
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

    /// <summary>
    /// 在底层可寻址流上叠加基偏移的写包装（使镜像可写到前缀数据之后）。
    /// <para>A write wrapper adding a base offset over an underlying seekable stream (lets an image
    /// be written after a prefix).</para>
    /// </summary>
    private sealed class OffsetStream(Stream inner, long baseOffset) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length - baseOffset;

        public override long Position
        {
            get => inner.Position - baseOffset;
            set => inner.Position = checked(value + baseOffset);
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
            => inner.Seek(offset + (origin == SeekOrigin.Begin ? baseOffset : 0), origin) - baseOffset;

        public override void SetLength(long value) => inner.SetLength(checked(value + baseOffset));

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

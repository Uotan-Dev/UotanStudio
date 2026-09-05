using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.F2fs;

/// <summary>
/// 全托管 f2fs 镜像构建器：将源目录打包为最小可用 f2fs 文件系统镜像（superblock、checkpoint、
/// NAT、SIT、根目录、文件数据与目录项位图）。两遍模型：第一遍扫描文件树并计算布局，
/// 第二遍对输出流 <see cref="Stream.SetLength(long)"/> 预分配后定位写入，不构造完整镜像数组。
/// <para>A fully managed F2FS image builder that packs a source directory into a minimal usable
/// F2FS image (superblock, checkpoint, NAT, SIT, root directory, file data and dentry bitmaps).
/// Two-pass model: the file tree and layout are planned in the first pass; the second pass
/// pre-allocates the output stream with <see cref="Stream.SetLength(long)"/> and writes each region
/// at its offset, never building a full in-memory image array.</para>
/// </summary>
public sealed partial class F2fsBuilder
{
    private const uint RootIno = 3;
    private const uint NodeIno = 1;
    private const uint MetaIno = 2;
    private const ushort ModeRegularFile = 0x81A4;   // S_IFREG | 0644
    private const ushort ModeDirectory = 0x41ED;     // S_IFDIR | 0755
    private const ushort ModeSymbolicLink = 0xA1FF;  // S_IFLNK | 0777
    private const uint Segment0Blkaddr = 512;
    private const uint CkptSegments = 2;
    private const int BlocksPerSeg = 512;
    private const uint OverprovSlackSegments = 8;
    private const uint CrcPolynomial = 0xEDB88320;
    private const int MaxNameBytes = 255;

    /// <summary>
    /// 从 <paramref name="sourceDirectory"/> 构建 f2fs 镜像并以字节数组返回。
    /// <para>Builds an F2FS image from <paramref name="sourceDirectory"/> and returns it as a byte
    /// array.</para>
    /// </summary>
    public static byte[] Build(string sourceDirectory, F2fsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"uotan-f2fs-{Guid.NewGuid():N}.tmp");
        try
        {
            Build(sourceDirectory, temporaryPath, options);
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
    /// 从 <paramref name="sourceDirectory"/> 构建 f2fs 镜像并原子写入 <paramref name="outputPath"/>
    /// （先写同目录临时文件，刷新成功后再覆盖目标）。
    /// <para>Builds an F2FS image from <paramref name="sourceDirectory"/> and atomically writes it to
    /// <paramref name="outputPath"/> (a temporary file in the same directory is flushed first, then
    /// moved over the destination).</para>
    /// </summary>
    public static void Build(string sourceDirectory, string outputPath, F2fsOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentNullException.ThrowIfNull(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        var temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(outputDirectory);
        var root = CollectNodes(Path.GetFullPath(sourceDirectory), [fullOutputPath, temporaryPath]);

        try
        {
            using (var temporary = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                       FileOptions.SequentialScan))
            {
                WriteImageCore(root, temporary, 0, freshDestination: true, options);
                temporary.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* Cleanup must not replace the build failure. */ }
        }
    }

    /// <summary>
    /// 从 <paramref name="sourceDirectory"/> 构建 f2fs 镜像并从输出流当前 Position 写入。
    /// 可寻址流保留此前缀；仅可写不可寻址流通过有界临时文件顺序复制。
    /// <para>Builds an F2FS image from <paramref name="sourceDirectory"/> starting at the output
    /// stream's current position. Seekable streams retain their prefix; writable non-seekable
    /// streams are staged through a bounded temporary file.</para>
    /// </summary>
    public static void Build(string sourceDirectory, Stream output, F2fsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        var excludedPaths = output is FileStream fileStream
            ? new[] { Path.GetFullPath(fileStream.Name) }
            : [];
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"uotan-f2fs-{Guid.NewGuid():N}.tmp");
        var stagingExclusions = excludedPaths.Append(Path.GetFullPath(temporaryPath)).ToArray();
        var root = CollectNodes(Path.GetFullPath(sourceDirectory), stagingExclusions);
        if (output.CanSeek && SupportsResizeWithoutMutation(output))
        {
            WriteImageCore(root, output, output.Position, freshDestination: false, options);
            return;
        }

        try
        {
            using (var staging = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                       FileOptions.SequentialScan))
            {
                WriteImageCore(root, staging, 0, freshDestination: true, options);
                staging.Position = 0;
                staging.CopyTo(output);
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* Cleanup must not replace the build failure. */ }
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

    private static void WriteImageCore(BuildNode root, Stream output, long baseOffset, bool freshDestination, F2fsOptions? options)
    {
        ArgumentNullException.ThrowIfNull(output);
        var effective = options ?? new F2fsOptions();
        effective.Validate();
        var blockSize = effective.BlockSize;

        // ---- 第一遍：扫描并规划布局（不分配镜像）。First pass: scan and plan (no allocation). ----
        var ordered = CollectPreOrder(root!);
        AssignInodeNumbers(ordered);
        foreach (var node in ordered)
        {
            if (!node.IsDirectory)
            {
                PlanFileData(node, effective);
            }
            else
            {
                PlanDirectoryBlocks(node);
            }
        }

        var mainBlocks = CountMainBlocks(ordered);
        var layout = ComputeLayout(ordered, mainBlocks, effective);
        var usedMainBlocks = AllocateBlocks(ordered, layout);

        // ---- 第二遍：流式写出镜像。Second pass: stream the image out. ----
        var imageBytes = checked(layout.ImageBlocks * blockSize);
        var requiredEnd = checked(baseOffset + imageBytes);
        var oldLength = output.Length;
        if (freshDestination || requiredEnd > oldLength)
        {
            output.SetLength(requiredEnd);
        }

        var nat = new Dictionary<uint, (uint Ino, uint Block)>();
        CollectNatEntries(ordered, nat);
        var natBitmap = new byte[checked((int)layout.NatSegments * 64)];
        var sitBitmap = new byte[checked((int)layout.SitSegments * 64)];

        var superblock = CreateSuperblock(layout, effective);
        WriteSuperblock(output, baseOffset, superblock, blockSize);
        var inodeCount = (uint)ordered.Count;
        var nodeBlockCount = inodeCount;
        foreach (var node in ordered)
        {
            nodeBlockCount += (uint)node.Dnodes.Count;
        }

        WriteCheckpoints(output, baseOffset, layout, natBitmap, sitBitmap, nodeBlockCount, inodeCount, usedMainBlocks);
        WriteSit(output, baseOffset, layout, usedMainBlocks, blockSize);
        WriteNat(output, baseOffset, layout, nat);
        WriteMainArea(output, baseOffset, ordered, blockSize);
        WriteSsa(output, baseOffset, layout, ordered, blockSize);
    }

    private static BuildNode CollectNodes(string fullSourcePath, IReadOnlyCollection<string> excludedPaths)
    {
        if (!Directory.Exists(fullSourcePath))
        {
            throw new DirectoryNotFoundException($"Source directory not found: '{fullSourcePath}'.");
        }

        var exclusions = excludedPaths.Count == 0
            ? null
            : new HashSet<string>(
                excludedPaths.Select(Path.GetFullPath),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return CollectNode(fullSourcePath, isRoot: true, exclusions);
    }

    private static BuildNode CollectNode(string fullPath, bool isRoot, HashSet<string>? excludedPaths)
    {
        var attributes = File.GetAttributes(fullPath);
        var name = isRoot ? string.Empty : Path.GetFileName(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            var linkTarget = new FileInfo(fullPath).LinkTarget;
            var symlinkNode = new BuildNode
            {
                Name = name,
                FullPath = fullPath,
                IsSymlink = true,
                SymlinkTarget = linkTarget ?? string.Empty,
                Size = Encoding.UTF8.GetByteCount(linkTarget ?? string.Empty),
            };

            SetFilePermissions(symlinkNode, fullPath, isDir: false, isSymlink: true);
            return symlinkNode;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            var node = new BuildNode { Name = name, IsDirectory = true, FullPath = fullPath };
            SetFilePermissions(node, fullPath, isDir: true, isSymlink: false);
            foreach (var childPath in Directory.EnumerateFileSystemEntries(fullPath))
            {
                var childFullPath = Path.GetFullPath(childPath);
                if (excludedPaths?.Contains(childFullPath) == true)
                {
                    continue;
                }

                var child = CollectNode(childFullPath, isRoot: false, excludedPaths);
                child.Parent = node;
                node.Children.Add(child);
            }

            return node;
        }

        var info = new FileInfo(fullPath);
        var fileNode = new BuildNode { Name = name, FullPath = fullPath, Size = info.Length };
        SetFilePermissions(fileNode, fullPath, isDir: false, isSymlink: false);
        return fileNode;
    }

    private static void SetFilePermissions(BuildNode node, string fullPath, bool isDir, bool isSymlink)
    {
        if (isSymlink)
        {
            node.Mode = ModeSymbolicLink;
            node.Uid = 0;
            node.Gid = 0;
        }
        else if (isDir)
        {
            node.Mode = ModeDirectory;
            node.Uid = 0;
            node.Gid = 0;
        }
        else
        {
            node.Mode = ModeRegularFile;
            node.Uid = 0;
            node.Gid = 0;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                var unixMode = File.GetUnixFileMode(fullPath);
                if (isSymlink)
                {
                    node.Mode = (ushort)unixMode;
                }
                else
                {
                    // Preserve permission bits from source, but ensure correct file type
                    var permBits = (ushort)((int)unixMode & 0x0FFF);
                    if (isDir)
                    {
                        node.Mode = (ushort)(0x4000 | permBits);
                    }
                    else
                    {
                        node.Mode = (ushort)(0x8000 | permBits);
                    }
                }
            }
            catch
            {
                // Fall back to defaults above
            }
        }

        try
        {
            node.Mtime = (long)(File.GetLastWriteTimeUtc(fullPath) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            node.Ctime = (long)(File.GetCreationTimeUtc(fullPath) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
        catch
        {
            node.Mtime = 0;
            node.Ctime = 0;
        }
    }

    private static List<BuildNode> CollectPreOrder(BuildNode root)
    {
        var result = new List<BuildNode>();
        result.Add(root);
        foreach (var child in root.Children)
        {
            CollectPreOrderCore(child, result);
        }

        return result;
    }

    private static void CollectPreOrderCore(BuildNode node, List<BuildNode> result)
    {
        result.Add(node);
        foreach (var child in node.Children)
        {
            CollectPreOrderCore(child, result);
        }
    }

    private static void AssignInodeNumbers(List<BuildNode> ordered)
    {
        var nextIno = RootIno;
        foreach (var node in ordered)
        {
            node.Ino = nextIno++;
            if (nextIno == 0)
            {
                throw new InvalidOperationException("F2FS inode number space exhausted.");
            }
        }
    }

    private static void PlanFileData(BuildNode node, F2fsOptions options)
    {
        var content = node.IsSymlink ? Encoding.UTF8.GetBytes(node.SymlinkTarget ?? string.Empty) : null;
        var size = node.Size;
        var maxInlineData = (F2fsNode.DefAddrsPerInode - F2fsNode.DefInlineReservedSize) * 4;
        if (size == 0)
        {
            return;
        }

        if (size <= maxInlineData)
        {
            node.InlineData = true;
            node.InlineBytes = node.IsSymlink ? content! : ReadFileBytes(node.FullPath, size);
            return;
        }

        // 压缩分支：尺寸 > 1 个簇（即至少跨越 2 个簇）的常规大文件才尝试逐簇压缩。
        // 只有至少一个簇确实被压缩存储，该文件才被标记为压缩文件（读回侧按 inode 标志路由）。
        // Compression branch: only regular files larger than one cluster (spanning at least two
        // clusters) are compressed cluster by cluster. A file is flagged as compressed only when at
        // least one cluster is genuinely stored compressed (the read side routes on the inode flag).
        if (options.EnableCompression && !node.IsSymlink && size > F2fsCompression.ClusterBytes)
        {
            // LZO(0)/LZ4(1)/ZSTD(2)/LZO-RLE(3) 均可写；LZO-RLE 以普通 LZO1X 位流落盘。
            // LZO(0)/LZ4(1)/ZSTD(2)/LZO-RLE(3) are all writable; LZO-RLE is stored as a plain
            // LZO1X bitstream.
            if (TryPlanCompressedFileData(node, options))
            {
                PlanDnodes(node, node.DataBlockCount);
                return;
            }
        }

        var dataBlocks = (int)((size + options.BlockSize - 1) / options.BlockSize);
        node.DataBlockCount = dataBlocks;
        node.DataBlockAddrs = new List<uint>(new uint[dataBlocks]);

        // 稀疏检测：全零块记录为空洞。Sparse detection: all-zero blocks become holes.
        var holes = options.EnableSparse ? DetectHoles(node, dataBlocks, options.BlockSize) : null;
        var allocated = 0;
        for (var i = 0; i < dataBlocks; i++)
        {
            if (holes?[i] == true)
            {
                node.DataBlockAddrs[i] = 0;
            }
            else
            {
                node.DataBlockAddrs[i] = uint.MaxValue; // placeholder, replaced during allocation
                allocated++;
            }
        }

        node.AllocatedDataBlockCount = allocated;

        // 规划 dnode。Plan dnodes.
        PlanDnodes(node, dataBlocks);
    }

    /// <summary>
    /// 压缩布局规划（第一遍）：按 64 KB 簇流式读取源文件，对完整簇尝试压缩。
    /// 值得压缩（占用物理块数 &lt; 簇内块数）的簇记录压缩后载荷长度并把其余逻辑槽位留空
    /// （地址 0）；不值得压缩的完整簇与末尾部分簇按普通块规划。任一簇压缩成功才返回 true。
    /// <para>Compressed layout planning (first pass): streams the source in 64 KB clusters and
    /// compresses full clusters. A cluster that is worth compressing (fewer physical blocks than
    /// the cluster slot count) records its payload length and leaves the remaining cluster slots
    /// empty (address 0); full clusters that are not worth compressing and the partial trailing
    /// cluster are planned as ordinary blocks. Returns true only if at least one cluster is stored
    /// compressed.</para>
    /// </summary>
    private static bool TryPlanCompressedFileData(BuildNode node, F2fsOptions options)
    {
        var blockSize = options.BlockSize;
        var clusterBytes = F2fsCompression.ClusterBytes;
        var clusterPages = F2fsCompression.ClusterPages;
        var dataBlocks = (int)((node.Size + blockSize - 1) / blockSize);
        var clusterCount = (dataBlocks + clusterPages - 1) / clusterPages;

        node.DataBlockCount = dataBlocks;
        node.DataBlockAddrs = new List<uint>(new uint[dataBlocks]);
        node.AllocatedDataBlockCount = 0;
        node.IsCompressedFile = false;
        node.CompressedClusterSizes = null;

        var compressedSizes = new int[clusterCount];
        compressedSizes.AsSpan().Fill(-1);
        var allocated = 0;
        var anyCompressed = false;
        var clusterBuffer = new byte[clusterBytes];

        using var source = File.OpenRead(node.FullPath);
        for (var c = 0; c < clusterCount; c++)
        {
            var logical = c * clusterPages;
            var clusterBlocks = Math.Min(clusterPages, dataBlocks - logical);
            var toRead = (int)Math.Min(clusterBytes, Math.Max(0, node.Size - (long)logical * blockSize));
            ReadExactly(source, clusterBuffer, toRead);

            if (toRead == clusterBytes)
            {
                // 完整簇：尝试压缩；压缩后块数须严格小于簇内块数才值得存储。
                // Full cluster: try compression; store compressed only when it uses fewer physical
                // blocks than the cluster's slot count.
                var payload = F2fsCompression.CompressCluster(
                    clusterBuffer.AsSpan(0, clusterBytes),
                    options.CompressionAlgorithm,
                    options.CompressionLevel);
                var payloadBlocks = payload is null ? 0 : (payload.Length + blockSize - 1) / blockSize;
                if (payload is not null && payloadBlocks < clusterPages)
                {
                    compressedSizes[c] = payload.Length;
                    anyCompressed = true;
                    for (var i = 0; i < payloadBlocks; i++)
                    {
                        node.DataBlockAddrs[logical + i] = uint.MaxValue; // placeholder
                    }

                    allocated += payloadBlocks;
                    continue;
                }

                // 不值得压缩：整簇按普通块写出。Not worth compressing: store the whole cluster raw.
                for (var i = 0; i < clusterPages; i++)
                {
                    node.DataBlockAddrs[logical + i] = uint.MaxValue; // placeholder
                }

                allocated += clusterPages;
            }
            else
            {
                // 末尾部分簇（不足一个完整簇）：按普通块规划（读侧按未压缩簇处理）。
                // Partial trailing cluster (less than one full cluster): planned as ordinary blocks
                // (the read side treats it as an uncompressed cluster).
                for (var i = 0; i < clusterBlocks; i++)
                {
                    node.DataBlockAddrs[logical + i] = uint.MaxValue; // placeholder
                }

                allocated += clusterBlocks;
            }
        }

        if (!anyCompressed)
        {
            // 全部簇都不值得压缩：清空规划状态，由调用方走普通未压缩路径。
            // No cluster is worth compressing: reset the plan so the caller uses the ordinary path.
            node.DataBlockCount = 0;
            node.DataBlockAddrs = [];
            return false;
        }

        node.AllocatedDataBlockCount = allocated;
        node.CompressedClusterSizes = compressedSizes;
        node.IsCompressedFile = true;
        node.CompressedAlgorithm = options.CompressionAlgorithm;
        node.CompressionLevel = options.CompressionLevel;
        return true;
    }

    /// <summary>
    /// 把压缩算法的公共枚举映射为内核 i_advise 低 4 位的算法 ID
    /// （kernel：0=LZO，1=LZ4，2=ZSTD，3=LZORLE）。
    /// <para>Maps the public <see cref="CompressionAlgorithm"/> enum to the kernel algorithm id
    /// stored in the low 4 bits of i_advise (kernel: 0=LZO, 1=LZ4, 2=ZSTD, 3=LZORLE).</para>
    /// </summary>
    private static byte KernelCompressionAlgorithmId(CompressionAlgorithm algorithm) => algorithm switch
    {
        CompressionAlgorithm.Lz4 => 1,
        CompressionAlgorithm.Zstd => 2,
        CompressionAlgorithm.Lzo => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>
    /// 压缩簇大小对数：簇大小 = 1 &lt;&lt; (log_cluster_size + 12) = 65536。
    /// <para>Log cluster size: cluster bytes = 1 &lt;&lt; (log_cluster_size + 12) = 65536.</para>
    /// </summary>
    private static byte LogClusterSizeFromBytes(int clusterBytes)
    {
        if (clusterBytes <= 0 || (clusterBytes & (clusterBytes - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clusterBytes), "Compression cluster size must be a positive power of two.");
        }

        var log = 0;
        while ((1 << log) < clusterBytes)
        {
            log++;
        }

        return (byte)(log - 12);
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int length)
    {
        if (length == 0)
        {
            return;
        }

        var offset = 0;
        while (offset < length)
        {
            var read = stream.Read(buffer, offset, length - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    "F2FS source file changed between the layout and write passes (short read).  F2FS 源文件在两遍构建之间被截短。");
            }

            offset += read;
        }
    }

    private static bool[]? DetectHoles(BuildNode node, int dataBlocks, int blockSize)
    {
        if (node.IsSymlink)
        {
            return null;
        }

        var holes = new bool[dataBlocks];
        using var stream = File.OpenRead(node.FullPath);
        var buffer = new byte[blockSize];
        for (var i = 0; i < dataBlocks; i++)
        {
            var read = 0;
            while (read < blockSize)
            {
                var n = stream.Read(buffer, read, blockSize - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            var allZero = true;
            for (var j = 0; j < read; j++)
            {
                if (buffer[j] != 0)
                {
                    allZero = false;
                    break;
                }
            }

            holes[i] = allZero;
        }

        return holes;
    }

    private static void PlanDnodes(BuildNode node, int dataBlocks)
    {
        var direct1 = dataBlocks > F2fsNode.DefAddrsPerInode;
        var direct2 = dataBlocks > F2fsNode.DefAddrsPerInode + F2fsNode.AddrsPerBlock;
        var indirect1 = dataBlocks > F2fsNode.DefAddrsPerInode + 2 * F2fsNode.AddrsPerBlock;
        var perIndirectCapacity = (long)F2fsNode.AddrsPerBlock * F2fsNode.AddrsPerBlock;
        var remainingForIndirect = indirect1
            ? dataBlocks - (F2fsNode.DefAddrsPerInode + 2 * F2fsNode.AddrsPerBlock)
            : 0;
        var indirectUsed = Math.Min(remainingForIndirect, (int)perIndirectCapacity);
        var remainingForDoubleIndirect = remainingForIndirect > perIndirectCapacity
            ? remainingForIndirect - perIndirectCapacity
            : 0;
        var perDoubleIndirectCapacity = perIndirectCapacity * F2fsNode.AddrsPerBlock;
        if (remainingForDoubleIndirect > perDoubleIndirectCapacity)
        {
            throw new InvalidOperationException(
                $"File '{node.FullPath}' exceeds the maximum f2fs data size.");
        }

        var indirectChildCount = indirectUsed > 0 ? (indirectUsed + F2fsNode.AddrsPerBlock - 1) / F2fsNode.AddrsPerBlock : 0;
        if (direct1)
        {
            node.Dnodes.Add(new DnodePlan { LogicalStart = F2fsNode.DefAddrsPerInode });
        }

        if (direct2)
        {
            node.Dnodes.Add(new DnodePlan { LogicalStart = F2fsNode.DefAddrsPerInode + F2fsNode.AddrsPerBlock });
        }

        if (indirectUsed > 0)
        {
            var indirect = new DnodePlan { IsIndirect = true, LogicalStart = F2fsNode.DefAddrsPerInode + 2 * F2fsNode.AddrsPerBlock };
            for (var i = 0; i < indirectChildCount; i++)
            {
                var child = new DnodePlan { LogicalStart = indirect.LogicalStart + (long)i * F2fsNode.AddrsPerBlock };
                node.IndirectChildren.Add(child);
                node.Dnodes.Add(child);
            }

            node.Dnodes.Add(indirect);
            node.IndirectNode = indirect;
        }

        // Double indirect: i_nid[3] → indirect nodes → direct nodes → data blocks.
        if (remainingForDoubleIndirect > 0)
        {
            var doubleIndirectLogicalStart = F2fsNode.DefAddrsPerInode + 2 * F2fsNode.AddrsPerBlock + perIndirectCapacity;
            var diIndirectCount = (int)((remainingForDoubleIndirect + perIndirectCapacity - 1) / perIndirectCapacity);

            for (var i = 0; i < diIndirectCount; i++)
            {
                var indirectStart = doubleIndirectLogicalStart + i * perIndirectCapacity;
                var remainingInIndirect = Math.Min(remainingForDoubleIndirect - i * perIndirectCapacity, perIndirectCapacity);
                var directCount = (int)((remainingInIndirect + F2fsNode.AddrsPerBlock - 1) / F2fsNode.AddrsPerBlock);

                for (var j = 0; j < directCount; j++)
                {
                    var directChild = new DnodePlan { LogicalStart = indirectStart + (long)j * F2fsNode.AddrsPerBlock };
                    node.DoubleIndirectDirectChildren.Add(directChild);
                    node.Dnodes.Add(directChild);
                }

                var indirectChild = new DnodePlan { IsIndirect = true, LogicalStart = indirectStart };
                node.DoubleIndirectIndirectChildren.Add(indirectChild);
                node.Dnodes.Add(indirectChild);
            }

            var doubleIndirect = new DnodePlan { IsDoubleIndirect = true, LogicalStart = doubleIndirectLogicalStart };
            node.Dnodes.Add(doubleIndirect);
            node.DoubleIndirectNode = doubleIndirect;
        }
    }

    private static void PlanDirectoryBlocks(BuildNode node)
    {
        if (node.Name.Length > MaxNameBytes)
        {
            throw new InvalidOperationException($"File name too long (>{MaxNameBytes} bytes): '{node.Name}'.");
        }

        var entries = new List<F2fsDirectoryEntry>
        {
            new() { HashCode = 0, Ino = node.Ino, NameLen = 1, FileType = F2fsDirectoryEntry.TypeDirectory, Name = "." },
            new() { HashCode = 0, Ino = node.Parent?.Ino ?? node.Ino, NameLen = 2, FileType = F2fsDirectoryEntry.TypeDirectory, Name = ".." },
        };
        foreach (var child in node.Children.OrderBy(static c => c.Name, StringComparer.Ordinal))
        {
            if (child.Name.Length == 0 || child.Name.Length > MaxNameBytes || !IsSafeName(child.Name))
            {
                throw new InvalidOperationException($"Invalid f2fs file name: '{child.Name}'.");
            }

            var type = child.IsDirectory ? F2fsDirectoryEntry.TypeDirectory
                : child.IsSymlink ? F2fsDirectoryEntry.TypeSymlink
                : F2fsDirectoryEntry.TypeRegularFile;
            entries.Add(new F2fsDirectoryEntry
            {
                HashCode = ComputeDentryHash(child.Name),
                Ino = child.Ino,
                NameLen = (ushort)Encoding.UTF8.GetByteCount(child.Name),
                FileType = type,
                Name = child.Name,
            });
        }

        // 逐块打包（每块最多 214 槽）。Pack entries into blocks (max 214 slots per block).
        var blocks = new List<byte[]>();
        var current = new List<F2fsDirectoryEntry>();
        var used = 0;
        foreach (var entry in entries)
        {
            if (used + entry.SlotCount > F2fsDirectoryEntry.SlotsPerBlock)
            {
                blocks.Add(BuildBlock(current));
                current = new List<F2fsDirectoryEntry>();
                used = 0;
            }

            current.Add(entry);
            used += entry.SlotCount;
        }

        if (current.Count > 0)
        {
            blocks.Add(BuildBlock(current));
        }

        if (blocks.Count > F2fsNode.DefAddrsPerInode)
        {
            throw new InvalidOperationException(
                $"Directory '{node.Name}' exceeds the supported size ({blocks.Count} dentry blocks, max {F2fsNode.DefAddrsPerInode}).");
        }

        // 多 block 目录：通过 direct node 指针链接多个 dentry block。
        // Multi-block directory: link multiple dentry blocks via direct node pointers.
        node.DirBlocks = blocks;
        node.Size = (long)blocks.Count * F2fsNode.NodeSize;

        // Inline dentry：当单块目录且数据量适合 inline 区域时，使用 inline dentry。
        // Inline dentry: when a single-block directory whose data fits the inline area, use inline dentry.
        if (blocks.Count == 1 && blocks[0].Length <= F2fsNode.NodeSize - 202)
        {
            node.InlineDentry = true;
            node.Inline = F2fsInode.InlineDentryFlag | F2fsInode.DataExistFlag;
        }
    }

    private static byte[] BuildBlock(List<F2fsDirectoryEntry> entries)
    {
        var block = new byte[F2fsNode.NodeSize];
        F2fsDirectoryEntry.BuildBlock(block, entries, F2fsNode.NodeSize);
        var crc = Crc32(0, block.AsSpan(0, F2fsNode.NodeSize - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(F2fsNode.NodeSize - 4, 4), crc);
        return block;
    }

    private static long CountMainBlocks(List<BuildNode> ordered)
    {
        long count = 0;
        foreach (var node in ordered)
        {
            count += 1; // inode node
            if (!node.IsDirectory && !node.InlineData)
            {
                count += node.Dnodes.Count;
                count += node.AllocatedDataBlockCount;
            }
            else if (node.IsDirectory)
            {
                count += node.DirBlocks.Count;
            }
        }

        return count;
    }

    private static Layout ComputeLayout(List<BuildNode> ordered, long mainBlocks, F2fsOptions options)
    {
        uint maxNid = 0;
        var nextDnodeNid = RootIno + (uint)ordered.Count;
        foreach (var node in ordered)
        {
            maxNid = Math.Max(maxNid, node.Ino);
            foreach (var dnode in node.Dnodes)
            {
                dnode.Nid = nextDnodeNid++;
                maxNid = Math.Max(maxNid, dnode.Nid);
            }

            if (node.IndirectNode is not null)
            {
                for (var i = 0; i < node.IndirectChildren.Count; i++)
                {
                    node.IndirectNode.IndirectChildNids.Add(node.IndirectChildren[i].Nid);
                }
            }

            if (node.DoubleIndirectNode is not null)
            {
                for (var i = 0; i < node.DoubleIndirectIndirectChildren.Count; i++)
                {
                    node.DoubleIndirectNode.IndirectChildNids.Add(node.DoubleIndirectIndirectChildren[i].Nid);
                }

                // Each double-indirect indirect child needs the NIDs of its direct children.
                // The direct children for indirect child i are in DoubleIndirectDirectChildren,
                // assigned in order: childCount[0], childCount[1], ...
                var diDirectIdx = 0;
                for (var i = 0; i < node.DoubleIndirectIndirectChildren.Count; i++)
                {
                    var indirectChild = node.DoubleIndirectIndirectChildren[i];
                    // Count how many direct children this indirect node covers
                    var startLogical = indirectChild.LogicalStart;
                    var endLogical = Math.Min(
                        node.DoubleIndirectNode.LogicalStart + (long)(i + 1) * F2fsNode.AddrsPerBlock * F2fsNode.AddrsPerBlock,
                        startLogical + (long)F2fsNode.AddrsPerBlock * F2fsNode.AddrsPerBlock);
                    var directCount = (int)Math.Min(
                        (endLogical - startLogical + F2fsNode.AddrsPerBlock - 1) / F2fsNode.AddrsPerBlock,
                        F2fsNode.AddrsPerBlock);
                    for (var j = 0; j < directCount && diDirectIdx < node.DoubleIndirectDirectChildren.Count; j++)
                    {
                        indirectChild.IndirectChildNids.Add(node.DoubleIndirectDirectChildren[diDirectIdx].Nid);
                        diDirectIdx++;
                    }
                }
            }
        }

        var layout = new Layout { MaxNid = maxNid };
        var mainSegments = (uint)((mainBlocks + BlocksPerSeg - 1) / BlocksPerSeg) + OverprovSlackSegments;
        if (mainSegments == 0)
        {
            mainSegments = 1;
        }

        layout.MainSegments = mainSegments;
        var blocksForSit = (mainSegments + F2fsNode.SitEntryPerBlock - 1) / F2fsNode.SitEntryPerBlock;
        layout.SitSegments = 2 * ((blocksForSit + BlocksPerSeg - 1) / BlocksPerSeg);
        if (layout.SitSegments == 0)
        {
            layout.SitSegments = 2;
        }

        var blocksForNat = (maxNid + F2fsNode.NatEntryPerBlock - 1) / F2fsNode.NatEntryPerBlock;
        layout.NatSegments = 2 * ((blocksForNat + BlocksPerSeg - 1) / BlocksPerSeg);
        if (layout.NatSegments < 2)
        {
            layout.NatSegments = 2;
        }

        // SSA 区每主区段需要一个 SSA 块（内核按 ssa_blkaddr + segno 寻址），512 块为一段。
        // SSA area needs one block per main segment (kernel uses ssa_blkaddr + segno),
        // 512 blocks per segment.
        layout.SsaSegments = Math.Max(1u, (mainSegments + BlocksPerSeg - 1) / BlocksPerSeg);
        layout.SitBlkaddr = Segment0Blkaddr + CkptSegments * BlocksPerSeg;
        layout.NatBlkaddr = layout.SitBlkaddr + layout.SitSegments * BlocksPerSeg;
        layout.SsaBlkaddr = layout.NatBlkaddr + layout.NatSegments * BlocksPerSeg;
        layout.MainBlkaddr = layout.SsaBlkaddr + layout.SsaSegments * BlocksPerSeg;
        layout.TotalSegments = CkptSegments + layout.SitSegments + layout.NatSegments + layout.SsaSegments + layout.MainSegments;

        if (options.SegmentCount > layout.TotalSegments)
        {
            var fixedSegments = CkptSegments + layout.SitSegments + layout.NatSegments + layout.SsaSegments;
            layout.MainSegments = (uint)(options.SegmentCount - fixedSegments);
            layout.SsaSegments = Math.Max(1u, (layout.MainSegments + BlocksPerSeg - 1) / BlocksPerSeg);
            layout.TotalSegments = fixedSegments + layout.MainSegments;
            layout.MainBlkaddr = layout.SsaBlkaddr + layout.SsaSegments * BlocksPerSeg;
            if ((long)layout.MainSegments * BlocksPerSeg < mainBlocks)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"The requested segment count ({options.SegmentCount}) is too small for the source data.");
            }
        }

        layout.ImageBlocks = Segment0Blkaddr + (long)layout.TotalSegments * BlocksPerSeg;
        return layout;
    }

    private static long AllocateBlocks(List<BuildNode> ordered, Layout layout)
    {
        var next = (long)layout.MainBlkaddr;
        foreach (var node in ordered)
        {
            node.InodeBlockAddr = checked((uint)next++);
            if (!node.IsDirectory && !node.InlineData)
            {
                foreach (var dnode in node.Dnodes)
                {
                    dnode.BlockAddr = checked((uint)next++);
                }

                for (var i = 0; i < node.DataBlockAddrs.Count; i++)
                {
                    if (node.DataBlockAddrs[i] == uint.MaxValue)
                    {
                        node.DataBlockAddrs[i] = checked((uint)next++);
                    }
                }

                // 为连续（无洞）且仅用 i_addr 直接指针的小文件写入 extent cache；大文件走 dnode
                // 寻址路径以保持两者均可验证。Write the extent cache only for small contiguous
                // (hole-free) files addressed via i_addr; large files go through dnodes so both
                // mapping paths remain exercised.
                if (node.Dnodes.Count == 0
                    && node.AllocatedDataBlockCount == node.DataBlockCount
                    && node.DataBlockCount > 0)
                {
                    node.Extent = new F2fsExtent { Fofs = 0, Blk = node.DataBlockAddrs[0], Len = (uint)node.DataBlockCount };
                }
            }
            else if (node.IsDirectory)
            {
                foreach (var _ in node.DirBlocks)
                {
                    node.DirBlockAddrs.Add(checked((uint)next++));
                }
            }
        }

        return next - layout.MainBlkaddr;
    }

    private static void CollectNatEntries(List<BuildNode> ordered, Dictionary<uint, (uint Ino, uint Block)> nat)
    {
        // 内核保留 NID：node_ino(1) 和 meta_ino(2) 需要有效的 NAT 条目。
        // mkfs.f2fs 为它们设置 block_addr=1，ino=各自的 NID。
        // Kernel reserved NIDs: node_ino(1) and meta_ino(2) need valid NAT entries.
        // mkfs.f2fs sets block_addr=1 for both, ino=their respective NID.
        nat[NodeIno] = (NodeIno, 1);
        nat[MetaIno] = (MetaIno, 1);

        foreach (var node in ordered)
        {
            nat[node.Ino] = (node.Ino, node.InodeBlockAddr);
            if (!node.IsDirectory && !node.InlineData)
            {
                foreach (var dnode in node.Dnodes)
                {
                    nat[dnode.Nid] = (node.Ino, dnode.BlockAddr);
                }
            }
        }
    }


    /// <summary>
    /// 构建树节点（打包中间模型）。
    /// <para>Build-tree node (intermediate packing model).</para>
    /// </summary>
    private sealed class BuildNode
    {
        public string Name = string.Empty;
        public string FullPath = string.Empty;
        public bool IsDirectory;
        public bool IsSymlink;
        public string? SymlinkTarget;
        public BuildNode? Parent;
        public List<BuildNode> Children = [];
        public long Size;
        public uint Ino;
        public bool InlineData;
        public byte[] InlineBytes = [];
        public int DataBlockCount;
        public int AllocatedDataBlockCount;
        public List<uint> DataBlockAddrs = [];
        public bool IsCompressedFile;
        public int[]? CompressedClusterSizes;
        public CompressionAlgorithm CompressedAlgorithm;
        public int CompressionLevel;
        public List<DnodePlan> Dnodes = [];
        public List<DnodePlan> IndirectChildren = [];
        public DnodePlan? IndirectNode;
        public List<DnodePlan> DoubleIndirectIndirectChildren = [];
        public List<DnodePlan> DoubleIndirectDirectChildren = [];
        public DnodePlan? DoubleIndirectNode;
        public List<byte[]> DirBlocks = [];
        public List<uint> DirBlockAddrs = [];
        public bool InlineDentry;
        public byte Inline;
        public uint InodeBlockAddr;
        public F2fsExtent Extent = new F2fsExtent();
        public ushort Mode;
        public uint Uid;
        public uint Gid;
        public long Mtime;
        public long Ctime;
    }

    /// <summary>
    /// dnode 布局计划（direct 节点或 indirect 节点）。
    /// <para>Dnode layout plan (a direct node or an indirect node).</para>
    /// </summary>
    private sealed class DnodePlan
    {
        public uint Nid;
        public uint BlockAddr;
        public bool IsIndirect;
        public bool IsDoubleIndirect;
        public long LogicalStart;
        public List<uint> IndirectChildNids = [];
    }

    /// <summary>
    /// 镜像布局（段数与各区起始块号）。
    /// <para>Image layout (segment counts and area start blocks).</para>
    /// </summary>
    private sealed class Layout
    {
        public uint MaxNid;
        public uint SitSegments;
        public uint NatSegments;
        public uint SsaSegments;
        public uint MainSegments;
        public uint TotalSegments;
        public uint SitBlkaddr;
        public uint NatBlkaddr;
        public uint SsaBlkaddr;
        public uint MainBlkaddr;
        public long ImageBlocks;
    }
}
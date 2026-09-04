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
public sealed class F2fsBuilder
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
            if (options.CompressionAlgorithm == CompressionAlgorithm.Lzo)
            {
                // LZO 压缩不可用：回退未压缩路径（禁止产出 LZO 镜像）。
                // LZO compression is unavailable: fall back to the uncompressed path (never emit an
                // LZO image).
                System.Diagnostics.Trace.TraceWarning(
                    $"F2FS LZO compression is unsupported; '{node.FullPath}' is stored uncompressed.  LZO 压缩不受支持，该文件已回退未压缩。");
            }
            else if (TryPlanCompressedFileData(node, options))
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

        var indirectChildCount = indirectUsed > 0 ? (int)((indirectUsed + F2fsNode.AddrsPerBlock - 1) / F2fsNode.AddrsPerBlock) : 0;
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
                var indirectStart = doubleIndirectLogicalStart + (long)i * perIndirectCapacity;
                var remainingInIndirect = Math.Min(remainingForDoubleIndirect - (long)i * perIndirectCapacity, perIndirectCapacity);
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
        layout.SitSegments = 2 * (uint)((blocksForSit + BlocksPerSeg - 1) / BlocksPerSeg);
        if (layout.SitSegments == 0)
        {
            layout.SitSegments = 2;
        }

        var blocksForNat = (maxNid + (uint)F2fsNode.NatEntryPerBlock - 1) / (uint)F2fsNode.NatEntryPerBlock;
        layout.NatSegments = 2 * (uint)((blocksForNat + (uint)BlocksPerSeg - 1) / (uint)BlocksPerSeg);
        if (layout.NatSegments < 2)
        {
            layout.NatSegments = 2;
        }

        layout.SsaSegments = Math.Max(1u, (mainSegments + BlocksPerSeg - 1) / (uint)BlocksPerSeg);
        layout.SitBlkaddr = Segment0Blkaddr + CkptSegments * BlocksPerSeg;
        layout.NatBlkaddr = layout.SitBlkaddr + layout.SitSegments * BlocksPerSeg;
        layout.SsaBlkaddr = layout.NatBlkaddr + layout.NatSegments * BlocksPerSeg;
        layout.MainBlkaddr = layout.SsaBlkaddr + layout.SsaSegments * BlocksPerSeg;
        layout.TotalSegments = CkptSegments + layout.SitSegments + layout.NatSegments + layout.SsaSegments + layout.MainSegments;

        if (options.SegmentCount > layout.TotalSegments)
        {
            var fixedSegments = CkptSegments + layout.SitSegments + layout.NatSegments + layout.SsaSegments;
            layout.MainSegments = (uint)(options.SegmentCount - fixedSegments);
            layout.SsaSegments = Math.Max(1u, (layout.MainSegments + BlocksPerSeg - 1) / (uint)BlocksPerSeg);
            layout.TotalSegments = fixedSegments + layout.MainSegments;
            layout.MainBlkaddr = layout.SsaBlkaddr + layout.SsaSegments * BlocksPerSeg;
            if ((long)layout.MainSegments * BlocksPerSeg < mainBlocks)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"The requested segment count ({options.SegmentCount}) is too small for the source data.");
            }
        }

        layout.ImageBlocks = (long)Segment0Blkaddr + (long)layout.TotalSegments * BlocksPerSeg;
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
        var pack1Block = checked((long)baseOffset + Segment0Blkaddr * F2fsNode.NodeSize);
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
            var baseSitBlock = ((long)layout.SitBlkaddr + blockIndex * 2L) * blockSize;
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

            var baseNatBlock = ((long)layout.NatBlkaddr + blockIndex * 2L) * F2fsNode.NodeSize;
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
                    WriteAt(output, baseOffset + (long)node.DirBlockAddrs[i] * blockSize, node.DirBlocks[i]);
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
        WriteAt(output, baseOffset + (long)node.InodeBlockAddr * blockSize, block);
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
        WriteAt(output, baseOffset + (long)dnode.BlockAddr * blockSize, block);
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
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    // 文件末尾：剩余块尾补零（镜像已预分配为零）。EOF: the tail of the last
                    // block is zero-filled (the image is pre-allocated as zeros).
                    break;
                }

                if (address != 0)
                {
                    var blockOffset = checked((long)address * blockSize + (blockSize - remaining));
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
                    WriteAt(output, baseOffset + (long)address * blockSize, payload.AsSpan(b * blockSize, length));
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
                        WriteAt(output, baseOffset + (long)address * blockSize, clusterBuffer.AsSpan(sourceOffset, length));
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

    /// <summary>
    /// 计算 f2fs 目录条目哈希（TEA 变换，与 mkfs.f2fs / 内核 f2fs_dentry_hash 一致；"." 与 ".." 为 0）。
    /// <para>Computes the F2FS dentry hash (TEA transform, matching mkfs.f2fs / kernel
    /// f2fs_dentry_hash; "." and ".." hash to 0).</para>
    /// </summary>
    internal static uint ComputeDentryHash(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length <= 2 && nameBytes.Length > 0 && nameBytes[0] == (byte)'.'
            && (nameBytes.Length == 1 || nameBytes[1] == (byte)'.'))
        {
            return 0;
        }

        var buf = new uint[4] { 0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u };
        var offset = 0;
        var length = nameBytes.Length;
        while (true)
        {
            var input = Str2HashBuf(nameBytes.AsSpan(offset, Math.Min(length, 16)), Math.Min(length, 16));
            TeaTransform(buf, input);
            offset += 16;
            if (length <= 16)
            {
                break;
            }

            length -= 16;
        }

        return buf[0];
    }

    private static uint[] Str2HashBuf(ReadOnlySpan<byte> message, int length)
    {
        var result = new uint[4];
        var pad = (uint)(length | (length << 8));
        pad |= pad << 16;
        var val = pad;
        var len = Math.Min(length, 16);
        var bufIndex = 0;
        var num = 4;
        for (var i = 0; i < len; i++)
        {
            if (i % 4 == 0)
            {
                val = pad;
            }

            val = message[i] + (val << 8);
            if (i % 4 == 3)
            {
                result[bufIndex++] = val;
                val = pad;
                num--;
            }
        }

        if (--num >= 0)
        {
            result[bufIndex++] = val;
        }

        while (--num >= 0)
        {
            result[bufIndex++] = pad;
        }

        return result;
    }

    private static void TeaTransform(uint[] buf, uint[] input)
    {
        const uint delta = 0x9E3779B9u;
        var sum = 0u;
        var b0 = buf[0];
        var b1 = buf[1];
        var a = input[0];
        var b = input[1];
        var c = input[2];
        var d = input[3];
        var n = 16;
        do
        {
            sum += delta;
            b0 += ((b1 << 4) + a) ^ (b1 + sum) ^ ((b1 >> 5) + b);
            b1 += ((b0 << 4) + c) ^ (b0 + sum) ^ ((b0 >> 5) + d);
        }
        while (--n > 0);

        buf[0] += b0;
        buf[1] += b1;
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? CrcPolynomial : 0);
            }
        }

        return crc;
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
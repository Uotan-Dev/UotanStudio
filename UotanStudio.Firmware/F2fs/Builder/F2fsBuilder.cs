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

    // ---- 上游常量（内核 fs/f2fs/f2fs.h 与 f2fs-tools）。Upstream constants. ----

    /// <summary>GC 保留段数（内核默认 6，计入 fsck sanity_check_ckpt 的 fsmeta）。
    /// Reserved GC segments (kernel default 6; counted into fsck's fsmeta).</summary>
    private const uint RsvdSegments = 6;

    /// <summary>cp pack 块数：1 cp + 1 compact data 摘要 + 3 node summary + 1 cp 副本
    /// （mkfs.f2fs 布局：set_cp(cp_pack_total_block_count, 6 + cp_payload)，cp_payload=0）。
    /// Cp pack blocks: 1 cp + 1 compact data summary + 3 node summaries + 1 cp copy (the
    /// mkfs.f2fs layout: cp_pack_total_block_count = 6 + cp_payload with cp_payload = 0).</summary>
    private const int CpPackTotalBlocks = 6;

    /// <summary>compact data 摘要单块容量（条）：journal 区（2×507 字节）之后、footer 之前可容纳
    /// 的 7 字节条目数 = (4096 − 5 − 1014) / 7 = 439。data curseg 摘要总数不得超过该值。
    /// Compact data summary capacity of one block: (4096 − 5 − 2×507) / 7 = 439 entries.</summary>
    private const int CompactDataEntryCap = (F2fsNode.NodeSize - SumFooterSize - 2 * SumJournalSize) / SummarySize;

    /// <summary>sum journal 大小（SUM_JOURNAL_SIZE = 4096 − 5 − 512×7 = 507）。
    /// SUM_JOURNAL_SIZE from the kernel f2fs_fs.h.</summary>
    private const int SumJournalSize = F2fsNode.NodeSize - SumFooterSize - EntriesInSum * SummarySize;

    /// <summary>段的 SIT 分配类型（内核 enum CURSEG_TYPE：0=HOT_DATA … 5=COLD_NODE）。
    /// SIT allocation types (kernel enum CURSEG_TYPE: 0=HOT_DATA … 5=COLD_NODE).</summary>
    private const byte SegTypeHotData = 0;
    private const byte SegTypeWarmData = 1;
    private const byte SegTypeColdData = 2;
    private const byte SegTypeHotNode = 3;
    private const byte SegTypeWarmNode = 4;
    private const byte SegTypeColdNode = 5;

    /// <summary>结构 summary_block 布局常量（内核 f2fs_fs.h）：ENTRIES_IN_SUM=512、
    /// SUMMARY_SIZE=7、SUM_FOOTER_SIZE=5、SUM_JOURNAL_SIZE=507。
    /// Summary-block layout constants from the kernel f2fs_fs.h.</summary>
    private const int EntriesInSum = 512;
    private const int SummarySize = 7;
    private const int SumFooterSize = 5;

    /// <summary>F2FS_FEATURE_COMPRESSION（内核 f2fs.h）：声明压缩支持。
    /// F2FS_FEATURE_COMPRESSION (kernel f2fs.h): declares compression support.</summary>
    private const uint FeatureCompression = 0x0000_2000;

    /// <summary>目录哈希树最大层数（内核 MAX_DIR_HASH_DEPTH）。
    /// Maximum directory hash depth (kernel MAX_DIR_HASH_DEPTH).</summary>
    private const uint MaxDirHashDepth = 63;

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

        var layout = ComputeLayout(ordered, effective);
        var plan = AllocateBlocks(ordered, layout);

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

        var superblock = CreateSuperblock(layout, effective, hasCompressedFiles: ordered.Exists(static n => n.IsCompressedFile));
        WriteSuperblock(output, baseOffset, superblock, blockSize);
        var inodeCount = (uint)ordered.Count;
        var validNodeCount = (uint)layout.NodeBlocks;
        WriteCheckpoints(output, baseOffset, layout, plan, validNodeCount, inodeCount);
        WriteSit(output, baseOffset, layout, plan, blockSize);
        WriteNat(output, baseOffset, layout, nat);
        WriteMainArea(output, baseOffset, ordered, blockSize);
        WriteSsa(output, baseOffset, layout, plan, blockSize);
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

        // 内核式哈希桶目录布局（fs/f2fs/dir.c）：每个名字必须落在其哈希桶对应的 dentry 块，
        // 否则 fsck 的 f2fs_check_dirent_position 会拒绝。层级布局：
        //   dir_buckets(level) = level < MAX_DIR_HASH_DEPTH/2 ? 2^level : 2^30
        //   bucket_blocks(level) = level < MAX_DIR_HASH_DEPTH/2 ? 2 : 4
        //   dir_block_index(level, bucket) = Σ_{i<level} dir_buckets(i)×bucket_blocks(i)
        //                                    + bucket×bucket_blocks(level)
        // 插入按内核 f2fs_add_link 语义：先在当前层该哈希桶的各块找空位，找不到则加深一层。
        // Kernel-style hash-bucketed directory layout (fs/f2fs/dir.c): every name must live in the
        // dentry block selected by its hash bucket, or fsck's f2fs_check_dirent_position rejects it.
        // Insertion follows f2fs_add_link: try the current level's bucket blocks, deepen when full.
        var blocks = new List<List<F2fsDirectoryEntry>>
        {
            new()
            {
                new() { HashCode = 0, Ino = node.Ino, NameLen = 1, FileType = F2fsDirectoryEntry.TypeDirectory, Name = "." },
                new() { HashCode = 0, Ino = node.Parent?.Ino ?? node.Ino, NameLen = 2, FileType = F2fsDirectoryEntry.TypeDirectory, Name = ".." },
            },
        };

        uint currentDepth = 0;
        foreach (var child in node.Children.OrderBy(static c => c.Name, StringComparer.Ordinal))
        {
            if (child.Name.Length == 0 || child.Name.Length > MaxNameBytes || !IsSafeName(child.Name))
            {
                throw new InvalidOperationException($"Invalid f2fs file name: '{child.Name}'.");
            }

            var type = child.IsDirectory ? F2fsDirectoryEntry.TypeDirectory
                : child.IsSymlink ? F2fsDirectoryEntry.TypeSymlink
                : F2fsDirectoryEntry.TypeRegularFile;
            var entry = new F2fsDirectoryEntry
            {
                HashCode = ComputeDentryHash(child.Name),
                Ino = child.Ino,
                NameLen = (ushort)Encoding.UTF8.GetByteCount(child.Name),
                FileType = type,
                Name = child.Name,
            };
            var slots = entry.SlotCount;

            var level = currentDepth;
            while (true)
            {
                var bucketBlocksCount = BucketBlocks(level);
                var baseIndex = DirBlockIndex(level, entry.HashCode % DirBuckets(level));
                var placed = false;
                for (var b = 0; b < bucketBlocksCount; b++)
                {
                    var index = (int)(baseIndex + b);
                    while (blocks.Count <= index)
                    {
                        blocks.Add(new List<F2fsDirectoryEntry>());
                    }

                    if (blocks[index].Sum(static e => e.SlotCount) + slots <= F2fsDirectoryEntry.SlotsPerBlock)
                    {
                        blocks[index].Add(entry);
                        placed = true;
                        break;
                    }
                }

                if (placed)
                {
                    break;
                }

                level++;
                currentDepth = level;
            }
        }

        if (blocks.Count > F2fsNode.DefAddrsPerInode)
        {
            throw new InvalidOperationException(
                $"Directory '{node.Name}' exceeds the supported size ({blocks.Count} dentry blocks, max {F2fsNode.DefAddrsPerInode}).");
        }

        node.DirBlocks = blocks.Select(static b => BuildDirBlock(b)).ToList();
        node.Size = (long)node.DirBlocks.Count * F2fsNode.NodeSize;
        // i_current_depth = 已用层数（内核语义：下一层号；单块目录为 1）。
        // i_current_depth = the number of levels in use (kernel semantics; 1 for single-block dirs).
        node.DirDepth = currentDepth + 1;
    }

    /// <summary>dir_buckets(level, dir_level=0)（内核 fs/f2fs/f2fs.h）。dir_buckets(level, dir_level=0) from the kernel f2fs.h.</summary>
    private static uint DirBuckets(uint level)
        => level < MaxDirHashDepth / 2 ? 1u << (int)level : 1u << (int)(MaxDirHashDepth / 2 - 1);

    /// <summary>bucket_blocks(level)（内核 fs/f2fs/f2fs.h）。bucket_blocks(level) from the kernel f2fs.h.</summary>
    private static uint BucketBlocks(uint level)
        => level < MaxDirHashDepth / 2 ? 2u : 4u;

    /// <summary>dir_block_index(level, dir_level=0, idx)（内核 fs/f2fs/f2fs.h）。
    /// dir_block_index(level, dir_level=0, idx) from the kernel f2fs.h.</summary>
    private static uint DirBlockIndex(uint level, uint bucket)
    {
        uint index = 0;
        for (var i = 0u; i < level; i++)
        {
            index += DirBuckets(i) * BucketBlocks(i);
        }

        return index + bucket * BucketBlocks(level);
    }

    private static byte[] BuildDirBlock(List<F2fsDirectoryEntry> entries)
    {
        // f2fs_dentry_block 恰好占满 4096 字节（bitmap + reserved + dentry[214] +
        // filename[214][8]），无块级校验和——内核与 fsck 均不校验，且末尾 4 字节属于
        // 最后一个文件名槽位，写入 CRC 会破坏跨槽文件名（内核定义，无尾部 CRC）。
        // struct f2fs_dentry_block fills the whole 4096-byte block (bitmap + reserved +
        // dentry[214] + filename[214][8]) and carries no block checksum — the kernel and fsck
        // never verify one, and the last 4 bytes belong to the final filename slot.
        var block = new byte[F2fsNode.NodeSize];
        F2fsDirectoryEntry.BuildBlock(block, entries, F2fsNode.NodeSize);
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

    /// <summary>
    /// 计算镜像布局：节点块（inode + dnode）与数据块（文件数据 + 目录项块）分属不同段区，
    /// 对齐 f2fs 的 node/data curseg 划分（fsck 的 fsck_chk_curseg_info 要求每个 curseg 段的
    /// SIT 类型与 curseg 类型一致，节点块与数据块不得混入同一段）。
    /// <para>Computes the image layout: node blocks (inodes + dnodes) and data blocks (file data +
    /// directory dentry blocks) live in separate segment regions, matching the f2fs node/data curseg
    /// split (fsck's fsck_chk_curseg_info requires each curseg segment's SIT type to match its curseg
    /// type, so node and data blocks must never share a segment).</para>
    /// </summary>
    private static Layout ComputeLayout(List<BuildNode> ordered, F2fsOptions options)
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

        long nodeBlocks = 0;
        long dataBlocks = 0;
        foreach (var node in ordered)
        {
            nodeBlocks++; // inode 块。The inode block itself.
            if (node.IsDirectory)
            {
                dataBlocks += node.DirBlocks.Count;
            }
            else if (!node.InlineData)
            {
                nodeBlocks += node.Dnodes.Count;
                dataBlocks += node.AllocatedDataBlockCount;
            }
        }

        var nodeSegments = (uint)((nodeBlocks + BlocksPerSeg - 1) / BlocksPerSeg);
        // data 区 = 3 个 curseg 段 + 溢出段；curseg 区总填充量受 compact data 摘要单块容量
        // （CompactDataEntryCap 条）限制，超出的数据块进入溢出段。
        // The data region = 3 curseg segments + overflow segments; the curseg zone's total fill is
        // bounded by the compact data summary's single-block capacity, extra blocks overflow.
        var dataSegments = 3u + (uint)((Math.Max(0, dataBlocks - CompactDataEntryCap) + BlocksPerSeg - 1) / BlocksPerSeg);
        var mainSegments = nodeSegments + dataSegments + OverprovSlackSegments;
        if (mainSegments == 0)
        {
            mainSegments = 1;
        }

        var layout = new Layout { MaxNid = maxNid, NodeBlocks = nodeBlocks, DataBlocks = dataBlocks };

        // 迭代求解：SIT/NAT/SSA 段数随主区段数变化，且须满足 f2fs-tools sanity_check_ckpt 的
        // 不变量 ckpt + sit + nat + rsvd + ssa < segment_count（rsvd 取 GC 保留段数 6）。
        // Iterate: SIT/NAT/SSA segment counts follow the main area size, and the f2fs-tools
        // sanity_check_ckpt invariant ckpt + sit + nat + rsvd + ssa < segment_count must hold
        // (rsvd = 6 reserved GC segments, matching the kernel default).
        uint sitSegments;
        uint natSegments;
        uint ssaSegments;
        uint fixedSegments;
        uint totalSegments;
        while (true)
        {
            var blocksForSit = (mainSegments + F2fsNode.SitEntryPerBlock - 1) / F2fsNode.SitEntryPerBlock;
            sitSegments = Math.Max(2, 2 * ((blocksForSit + BlocksPerSeg - 1) / BlocksPerSeg));
            // NAT 块数按 NID 空间上限（maxNid+1）计算，避免 NID 恰好落在块边界时越界。
            // NAT blocks sized for the full NID space (maxNid + 1) so a NID exactly on a block
            // boundary never overruns.
            var blocksForNat = (maxNid + 1 + F2fsNode.NatEntryPerBlock - 1) / F2fsNode.NatEntryPerBlock;
            natSegments = Math.Max(2, 2 * ((blocksForNat + BlocksPerSeg - 1) / BlocksPerSeg));
            ssaSegments = Math.Max(1u, (mainSegments + BlocksPerSeg - 1) / BlocksPerSeg);
            fixedSegments = CkptSegments + sitSegments + natSegments + ssaSegments;
            totalSegments = fixedSegments + mainSegments;
            if (fixedSegments + RsvdSegments < totalSegments)
            {
                break;
            }

            mainSegments++;
        }

        if (options.SegmentCount > totalSegments)
        {
            var newMainSegments = (uint)(options.SegmentCount - fixedSegments);
            if ((long)newMainSegments * BlocksPerSeg < nodeBlocks + dataBlocks)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"The requested segment count ({options.SegmentCount}) is too small for the source data.");
            }

            mainSegments = newMainSegments;
            ssaSegments = Math.Max(1u, (mainSegments + BlocksPerSeg - 1) / BlocksPerSeg);
            fixedSegments = CkptSegments + sitSegments + natSegments + ssaSegments;
            totalSegments = fixedSegments + mainSegments;
            if (fixedSegments + RsvdSegments >= totalSegments)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"The requested segment count ({options.SegmentCount}) leaves no room for the reserved segments.");
            }
        }

        layout.SitSegments = sitSegments;
        layout.NatSegments = natSegments;
        layout.SsaSegments = ssaSegments;
        layout.MainSegments = mainSegments;
        layout.TotalSegments = totalSegments;
        layout.NodeSegments = nodeSegments;
        layout.DataSegments = dataSegments;
        layout.SitBlkaddr = Segment0Blkaddr + CkptSegments * BlocksPerSeg;
        layout.NatBlkaddr = layout.SitBlkaddr + sitSegments * BlocksPerSeg;
        layout.SsaBlkaddr = layout.NatBlkaddr + natSegments * BlocksPerSeg;
        layout.MainBlkaddr = layout.SsaBlkaddr + ssaSegments * BlocksPerSeg;
        layout.NodeBlkaddr = layout.MainBlkaddr;
        layout.DataBlkaddr = layout.NodeBlkaddr + nodeSegments * BlocksPerSeg;
        layout.ImageBlocks = Segment0Blkaddr + (long)totalSegments * BlocksPerSeg;
        return layout;
    }

    /// <summary>
    /// 分配主区块并构建 curseg 记账：node 区（inode + dnode）与 data 区（文件数据 + 目录项块）
    /// 分别从各自区起点顺序分配；随后确定 6 个 curseg 段号（区段不足 3 个时取后续空闲段）、
    /// 段内使用位图、SIT 类型与 SSA/cp-pack 摘要条目。
    /// <para>Allocates main-area blocks and builds the curseg bookkeeping: node blocks (inodes +
    /// dnodes) and data blocks (file data + directory dentry blocks) are assigned sequentially from
    /// their own region starts; the 6 curseg segment numbers are then determined (falling back to
    /// free segments when a region has fewer than 3), along with per-segment valid bitmaps, SIT
    /// types and the SSA / cp-pack summary entries.</para>
    /// </summary>
    private static CursegPlan AllocateBlocks(List<BuildNode> ordered, Layout layout)
    {
        var plan = new CursegPlan();
        // 主区段号：f2fs 全程使用主区相对段号（segment 0 = 第一个主区段；内核 START_BLOCK/
        // GET_SUM_BLKADDR/SIT_BLOCK_OFFSET 均以主区相对段号为索引，checkpoint 的
        // cur_*_segno 亦为主区相对，参照 mkfs.f2fs 生成的镜像）。
        // Segment numbers are main-area-relative throughout f2fs (segment 0 = the first main-area
        // segment; the kernel's START_BLOCK/GET_SUM_BLKADDR/SIT_BLOCK_OFFSET and the checkpoint's
        // cur_*_segno are all main-relative, as produced by mkfs.f2fs).
        var mainBase = 0u;
        var freeSegmentCursor = mainBase + layout.NodeSegments + layout.DataSegments;
        var maxFreeSegment = mainBase + layout.MainSegments;

        uint FreeSegment()
        {
            if (freeSegmentCursor >= maxFreeSegment)
            {
                throw new InvalidOperationException("Not enough free segments for curseg bookkeeping.");
            }

            return freeSegmentCursor++;
        }

        // curseg 段号：node/data 区的前 3 个段；区段不足时取空闲段。
        // Curseg segment numbers: the first 3 segments of the node/data regions; free segments
        // fill in when a region has fewer than 3.
        for (var t = 0; t < 3; t++)
        {
            plan.NodeSegno[t] = t < layout.NodeSegments ? (uint)(mainBase + t) : FreeSegment();
            plan.DataSegno[t] = t < layout.DataSegments ? (uint)(mainBase + layout.NodeSegments + t) : FreeSegment();
        }

        long nodeCursor = layout.NodeBlkaddr;
        long dataCursor = layout.DataBlkaddr;

        /// <summary>分配一个 data 区块：curseg 区达到 compact 容量后跳到溢出段。</summary>
        uint AllocateDataBlock(ref long cursor, Layout l)
        {
            var assigned = cursor - l.DataBlkaddr;
            if (assigned / BlocksPerSeg < 3 && assigned >= CompactDataEntryCap)
            {
                cursor = l.DataBlkaddr + 3L * BlocksPerSeg;
            }

            return checked((uint)cursor++);
        }

        void AssignBlock(long address, uint nid, ushort ofsInNode)
        {
            var segno = mainBase + (uint)((address - layout.MainBlkaddr) / BlocksPerSeg);
            var ofsInSeg = (int)((address - layout.MainBlkaddr) % BlocksPerSeg);
            if (!plan.SegmentEntries.TryGetValue(segno, out var entries))
            {
                entries = new Dictionary<int, (uint Nid, ushort Ofs)>();
                plan.SegmentEntries[segno] = entries;
            }

            entries[ofsInSeg] = (nid, ofsInNode);
        }

        // node 块：inode 与 dnode 顺序占用 node 区（文件内连续，inode.next_blkaddr 语义成立）。
        // Node blocks: inodes and dnodes consume the node region sequentially (each file's node
        // blocks stay adjacent, keeping the inode.next_blkaddr chain meaningful).
        foreach (var node in ordered)
        {
            node.InodeBlockAddr = checked((uint)nodeCursor);
            AssignBlock(nodeCursor++, node.Ino, 0);
            if (!node.IsDirectory && !node.InlineData)
            {
                foreach (var dnode in node.Dnodes)
                {
                    dnode.BlockAddr = checked((uint)nodeCursor);
                    AssignBlock(nodeCursor++, dnode.Nid, 0);
                }
            }
        }

        // data 块：文件数据与目录项块占用 data 区；curseg 区（前 3 段）总填充量达到
        // compact 摘要单块容量后跳到溢出段（第 4 段起），保证 Σ data curseg blkoff ≤ 439。
        // Data blocks: file data and directory dentry blocks consume the data region; once the
        // curseg zone (first 3 segments) reaches the compact summary's single-block capacity the
        // cursor jumps to overflow segment 4, keeping Σ data-curseg blkoff ≤ 439.
        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {
                for (var i = 0; i < node.DirBlocks.Count; i++)
                {
                    var address = AllocateDataBlock(ref dataCursor, layout);
                    node.DirBlockAddrs.Add(address);
                    AssignBlock(address, node.Ino, (ushort)i);
                }
            }
            else if (!node.InlineData)
            {
                for (var i = 0; i < node.DataBlockAddrs.Count; i++)
                {
                    if (node.DataBlockAddrs[i] != uint.MaxValue)
                    {
                        continue; // 空洞/压缩簇空槽保持 0。Holes and spare compressed slots stay 0.
                    }

                    node.DataBlockAddrs[i] = AllocateDataBlock(ref dataCursor, layout);
                    AssignBlock(node.DataBlockAddrs[i], ResolveOwnerNid(node, i, out var ofsInNode), (ushort)ofsInNode);
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
        }

        // curseg 段写满时须迁移到空闲段（fsck 的 check_curseg_offset 要求 next_blkoff < 512）。
        // A full curseg segment must be replaced by a free one (fsck's check_curseg_offset
        // requires next_blkoff < blocks-per-segment).
        for (var t = 0; t < 3; t++)
        {
            if (plan.SegmentEntries.TryGetValue(plan.NodeSegno[t], out var nodeEntries) && nodeEntries.Count >= BlocksPerSeg)
            {
                plan.NodeSegno[t] = FreeSegment();
            }

            if (plan.SegmentEntries.TryGetValue(plan.DataSegno[t], out var dataEntries) && dataEntries.Count >= BlocksPerSeg)
            {
                plan.DataSegno[t] = FreeSegment();
            }
        }

        // 每段使用表：有效块数 + 位图 + SIT 类型（vblocks[15:10]，GET_SIT_TYPE 语义）。
        // Per-segment usage: valid block count + bitmap + SIT type (vblocks[15:10], GET_SIT_TYPE).
        var cursegSegnos = new HashSet<uint>(plan.NodeSegno.Concat(plan.DataSegno));
        for (var s = 0; s < layout.MainSegments; s++)
        {
            var segno = mainBase + (uint)s;
            var count = 0;
            var map = new byte[F2fsNode.SitValidMapSize];
            if (plan.SegmentEntries.TryGetValue(segno, out var entries))
            {
                count = entries.Count;
                foreach (var ofs in entries.Keys)
                {
                    // SIT 有效位图使用内核 f2fs_test_bit 的 MSB-first 位序
                    // （与 dentry 位图的 test_bit_le LSB-first 不同，f2fs 两种位序并存）。
                    // The SIT valid map uses the kernel f2fs_test_bit MSB-first bit order
                    // (unlike the dentry bitmap's LSB-first test_bit_le — f2fs mixes both).
                    map[ofs >> 3] |= (byte)(1 << (7 - (ofs & 7)));
                }
            }

            plan.Segments[segno] = (count, map, SegmentTypeOf(segno, mainBase, layout, plan));
        }

        // curseg 记账：blkoff = 段内已用块数；摘要条目按块偏移顺序排列。
        // Curseg bookkeeping: blkoff = used block count in the segment; summary entries in
        // block-offset order.
        for (var t = 0; t < 3; t++)
        {
            plan.NodeBlkoff[t] = BlkoffOf(plan, plan.NodeSegno[t]);
            plan.DataBlkoff[t] = BlkoffOf(plan, plan.DataSegno[t]);
            if (plan.SegmentEntries.TryGetValue(plan.NodeSegno[t], out var nodeEntries))
            {
                for (var i = 0; i < plan.NodeBlkoff[t]; i++)
                {
                    plan.NodeEntries[t].Add(nodeEntries[i]);
                }
            }

            if (plan.SegmentEntries.TryGetValue(plan.DataSegno[t], out var dataEntries))
            {
                for (var i = 0; i < plan.DataBlkoff[t]; i++)
                {
                    plan.DataEntries[t].Add(dataEntries[i]);
                }
            }
        }

        // 空闲段数：主区段数 −（非空段数 + 空 curseg 段数）；空 curseg 段按 fsck
        // build_sit_area_bitmap 的语义计入"有效"。
        // Free segments: main segments minus (non-empty segments + empty curseg segments); empty
        // curseg segments count as valid per fsck's build_sit_area_bitmap.
        var validSegments = plan.Segments.Count(kvp => kvp.Value.Count > 0 || cursegSegnos.Contains(kvp.Key));
        plan.FreeSegmentCount = (uint)(plan.Segments.Count - validSegments);
        return plan;
    }

    /// <summary>curseg 段内下一空闲块偏移。Next free block offset inside a curseg segment.</summary>
    private static ushort BlkoffOf(CursegPlan plan, uint segno)
        => (ushort)(plan.SegmentEntries.TryGetValue(segno, out var entries) ? entries.Count : 0);

    /// <summary>
    /// 数据块归属解析：直接区（i_addr）归 inode 自身，dnode 区归对应 direct 节点；
    /// ofs_in_node 为该节点地址数组内的逻辑下标（fsck sanity_check_nid 依据）。
    /// <para>Resolves a data block's owner: the inode itself for the direct (i_addr) range, or the
    /// covering direct node beyond it; ofs_in_node is the logical index inside that node's address
    /// array (as validated by fsck's sanity_check_nid).</para>
    /// </summary>
    private static uint ResolveOwnerNid(BuildNode node, int logical, out int ofsInNode)
    {
        if (logical < F2fsNode.DefAddrsPerInode)
        {
            ofsInNode = logical;
            return node.Ino;
        }

        foreach (var dnode in node.Dnodes)
        {
            if (!dnode.IsIndirect && !dnode.IsDoubleIndirect
                && dnode.LogicalStart <= logical && logical < dnode.LogicalStart + F2fsNode.AddrsPerBlock)
            {
                ofsInNode = (int)(logical - dnode.LogicalStart);
                return dnode.Nid;
            }
        }

        ofsInNode = 0;
        return node.Ino;
    }

    /// <summary>
    /// 段的 SIT 分配类型：node 区前 3 段为 hot/warm/cold node（3/4/5），data 区前 3 段为
    /// hot/warm/cold data（0/1/2），溢出段延续 hot 类型，被指派为空 curseg 的空闲段取其 curseg 类型。
    /// <para>SIT allocation type of a segment: the first 3 node-region segments are hot/warm/cold
    /// node (3/4/5), the first 3 data-region segments hot/warm/cold data (0/1/2); overflow segments
    /// keep the hot type, and free segments assigned to an empty curseg take that curseg's type.</para>
    /// </summary>
    private static byte SegmentTypeOf(uint segno, uint mainBase, Layout layout, CursegPlan plan)
    {
        var rel = segno - mainBase;
        if (rel < layout.NodeSegments)
        {
            return rel switch
            {
                0 => SegTypeHotNode,
                1 => SegTypeWarmNode,
                2 => SegTypeColdNode,
                _ => SegTypeHotNode,
            };
        }

        if (rel < layout.NodeSegments + layout.DataSegments)
        {
            var dataRel = (int)(rel - layout.NodeSegments);
            return dataRel switch
            {
                0 => SegTypeHotData,
                1 => SegTypeWarmData,
                2 => SegTypeColdData,
                _ => SegTypeHotData,
            };
        }

        // 空闲段：若被指派为 curseg 段则取其类型，否则为未分配（hot data=0）。
        // Free segments: a curseg-assigned one takes its curseg type, others stay unassigned (0).
        for (var t = 0; t < 3; t++)
        {
            if (segno == plan.NodeSegno[t])
            {
                return (byte)(SegTypeHotNode + t);
            }

            if (segno == plan.DataSegno[t])
            {
                return (byte)t;
            }
        }

        return SegTypeHotData;
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
        public uint DirDepth;
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
        public long NodeBlocks;
        public long DataBlocks;
        public uint NodeSegments;
        public uint DataSegments;
        public uint SitSegments;
        public uint NatSegments;
        public uint SsaSegments;
        public uint MainSegments;
        public uint TotalSegments;
        public uint SitBlkaddr;
        public uint NatBlkaddr;
        public uint SsaBlkaddr;
        public uint MainBlkaddr;
        public long NodeBlkaddr;
        public long DataBlkaddr;
        public long ImageBlocks;
    }

    /// <summary>
    /// curseg 记账：6 个 curseg 段号与 blkoff、每段 SSA 摘要条目、SIT 使用表。
    /// <para>Curseg bookkeeping: the 6 curseg segment numbers with blkoffs, per-segment SSA
    /// summary entries and the SIT usage table.</para>
    /// </summary>
    private sealed class CursegPlan
    {
        public uint[] NodeSegno = new uint[3];
        public ushort[] NodeBlkoff = new ushort[3];
        public uint[] DataSegno = new uint[3];
        public ushort[] DataBlkoff = new ushort[3];
        public List<(uint Nid, ushort Ofs)>[] NodeEntries = [new(), new(), new()];
        public List<(uint Nid, ushort Ofs)>[] DataEntries = [new(), new(), new()];

        /// <summary>segno →（段内块偏移 →（nid, ofs_in_node））。segno → (block offset in segment → (nid, ofs_in_node)).</summary>
        public Dictionary<uint, Dictionary<int, (uint Nid, ushort Ofs)>> SegmentEntries = new();

        /// <summary>segno →（有效块数, 有效位图, SIT 类型）。segno → (valid count, valid bitmap, SIT type).</summary>
        public Dictionary<uint, (int Count, byte[] Map, byte Type)> Segments = new();

        /// <summary>与 SIT 一致的空闲段数。Free segment count consistent with the SIT.</summary>
        public uint FreeSegmentCount;
    }
}
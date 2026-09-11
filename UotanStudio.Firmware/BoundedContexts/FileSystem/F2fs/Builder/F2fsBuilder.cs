using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

public sealed partial class F2fsBuilder
{
    private const uint RootIno = 3;
    private const uint NodeIno = 1;
    private const uint MetaIno = 2;
    private const ushort ModeRegularFile = 0x81A4;
    private const ushort ModeDirectory = 0x41ED;
    private const ushort ModeSymbolicLink = 0xA1FF;
    private const uint Segment0Blkaddr = 512;
    private const uint CkptSegments = 2;
    private const int BlocksPerSeg = 512;
    private const uint OverprovSlackSegments = 8;
    private const uint CrcPolynomial = 0xEDB88320;
    private const int MaxNameBytes = 255;


    private const uint RsvdSegments = 6;

    private const int CpPackTotalBlocks = 6;

    private const int CompactDataEntryCap = (F2fsNode.NodeSize - SumFooterSize - 2 * SumJournalSize) / SummarySize;

    private const int SumJournalSize = F2fsNode.NodeSize - SumFooterSize - EntriesInSum * SummarySize;

    private const byte SegTypeHotData = 0;
    private const byte SegTypeWarmData = 1;
    private const byte SegTypeColdData = 2;
    private const byte SegTypeHotNode = 3;
    private const byte SegTypeWarmNode = 4;
    private const byte SegTypeColdNode = 5;

    private const int EntriesInSum = 512;
    private const int SummarySize = 7;
    private const int SumFooterSize = 5;

    private const uint FeatureCompression = 0x0000_2000;

    private const uint MaxDirHashDepth = 63;

    /// <summary>
    /// Builds an F2FS image from sourceDirectory and returns it as a byte array.
    /// <para>从 sourceDirectory 构建 f2fs 镜像并以字节数组返回。</para>
    /// </summary>
    // 结果整镜像驻留内存（峰值约为镜像大小的 2 倍托管内存）；大型输出请改用流式重载。
    // The result is resident in memory (peak managed memory about 2x the image);
    // use the streaming overloads for large outputs.
    [Obsolete("Loads the entire image into memory. Use Build(sourceDirectory, outputPath) or Build(sourceDirectory, Stream) instead. 整镜像驻留内存；请使用文件路径或流式重载。")]
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
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    /// <summary>
    /// Builds an F2FS image from sourceDirectory and atomically writes it to outputPath (a temporary file in the same directory is flushed first, then moved over the destination).
    /// <para>从 sourceDirectory 构建 f2fs 镜像并原子写入 outputPath （先写同目录临时文件，刷新成功后再覆盖目标）。</para>
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
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    /// <summary>
    /// Builds an F2FS image from sourceDirectory starting at the output stream's current position.
    /// <para>从 sourceDirectory 构建 f2fs 镜像并从输出流当前 Position 写入。</para>
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
            try { File.Delete(temporaryPath); } catch { }
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

    /// <summary>
    /// Applies fs_config (uid/gid/mode) overrides from the Android-style metadata file onto the
    /// collected tree. Permission bits from fs_config (mode &amp; 0xFFF, no type bits) replace the
    /// node's permission bits while the file-type bits (directory/symlink/regular) are preserved.
    /// F2FS SELinux xattr writing is not yet implemented; file_contexts is intentionally not consumed.
    /// <para>将 Android 风格 fs_config（uid/gid/mode）覆盖应用到收集完成的目录树。
    /// fs_config 的权限位（mode &amp; 0xFFF，不含类型位）替换节点权限位，同时保留文件类型位
    /// （目录/符号链接/普通文件）。F2FS SELinux xattr 写入尚未实现，暂不消费 file_contexts。</para>
    /// </summary>
    private static void ApplyFsConfigOverrides(List<BuildNode> ordered, F2fsOptions options)
    {
        if (options.FsConfigPath is null) return;
        var fsConfig = FileSystemMetadataOverrides.LoadFsConfig(options.FsConfigPath);
        foreach (var node in ordered)
        {
            var relative = node.Parent is null ? string.Empty : BuildRelativePath(node);
            if (!FileSystemMetadataOverrides.TryGetFsConfig(fsConfig, relative, out var entry)) continue;
            node.Uid = entry.Uid;
            node.Gid = entry.Gid;
            // fs_config stores permission bits only (mode & 0xFFF); keep the node's type bits.
            node.Mode = (ushort)((node.Mode & 0xF000) | (entry.Mode & 0xFFF));
        }
    }

    /// <summary>
    /// Image-relative path of a node ('/'-separated; root is "").
    /// <para>节点的镜像内相对路径（'/' 分隔；根为 ""）。</para>
    /// </summary>
    private static string BuildRelativePath(BuildNode node)
    {
        var parts = new List<string>();
        for (var current = node; current.Parent is not null; current = current.Parent)
        {
            parts.Add(current.Name);
        }

        parts.Reverse();
        return string.Join('/', parts);
    }

    private static void WriteImageCore(BuildNode root, Stream output, long baseOffset, bool freshDestination, F2fsOptions? options)
    {
        ArgumentNullException.ThrowIfNull(output);
        var effective = options ?? new F2fsOptions();
        effective.Validate();
        var blockSize = effective.BlockSize;


        var ordered = CollectPreOrder(root!);
        AssignInodeNumbers(ordered);
        ApplyFsConfigOverrides(ordered, effective);
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


        var imageBytes = checked(layout.ImageBlocks * blockSize);
        var requiredEnd = checked(baseOffset + imageBytes);
        var oldLength = output.Length;
        if (freshDestination || requiredEnd > oldLength)
        {
            output.SetLength(requiredEnd);
        }

        var nat = new Dictionary<uint, (uint Ino, uint Block)>();
        CollectNatEntries(ordered, nat);

        layout.ReservedCompressionBlocks = ordered.Where(static n => n.IsCompressedFile).Sum(static n => (long)n.DataBlockCount - n.AllocatedDataBlockCount);
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
                if (SharedKernel.RepackMetadata.IsMetadataFile(childPath)) continue;
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

            }
        }

        try
        {
            // F2FS stores inode timestamps as a 64-bit Unix-epoch second plus a
            // 32-bit nanosecond field.  Compute both from UTC ticks using integer
            // arithmetic to preserve sub-second precision and avoid the lossy
            // (long)TotalSeconds cast.  One tick = 100 ns = 1e-7 s, so
            // nanoseconds = (ticks % 10_000_000) * 100.
            const long TicksPerSecond = TimeSpan.TicksPerSecond; // 10,000,000
            const long NanosPerTick = 100;                       // 100 ns per tick

            var writeUtc = File.GetLastWriteTimeUtc(fullPath);
            var createUtc = File.GetCreationTimeUtc(fullPath);

            var writeEpochTicks = writeUtc.Ticks - DateTime.UnixEpoch.Ticks;
            node.Mtime = Math.Max(0, writeEpochTicks / TicksPerSecond);
            node.MtimeNsec = (uint)(Math.Max(0, writeEpochTicks % TicksPerSecond) * NanosPerTick);

            var createEpochTicks = createUtc.Ticks - DateTime.UnixEpoch.Ticks;
            node.Ctime = Math.Max(0, createEpochTicks / TicksPerSecond);
            node.CtimeNsec = (uint)(Math.Max(0, createEpochTicks % TicksPerSecond) * NanosPerTick);
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


        if (options.EnableCompression && !node.IsSymlink && size > F2fsCompression.ClusterBytes)
        {


            if (TryPlanCompressedFileData(node, options))
            {
                PlanDnodes(node, node.DataBlockCount);
                return;
            }
        }

        var dataBlocks = (int)((size + options.BlockSize - 1) / options.BlockSize);
        node.DataBlockCount = dataBlocks;
        node.DataBlockAddrs = new List<uint>(new uint[dataBlocks]);


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
                node.DataBlockAddrs[i] = uint.MaxValue;
                allocated++;
            }
        }

        node.AllocatedDataBlockCount = allocated;


        PlanDnodes(node, dataBlocks);
    }

    /// <summary>
    /// Compressed layout planning (first pass): streams the source in 64 KB clusters and compresses full clusters.
    /// <para>压缩布局规划（第一遍）：按 64 KB 簇流式读取源文件，对完整簇尝试压缩。</para>
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


                var payload = F2fsCompression.CompressCluster(
                    clusterBuffer.AsSpan(0, clusterBytes),
                    options.CompressionAlgorithm,
                    options.CompressionLevel);
                var payloadBlocks = payload is null ? 0 : (payload.Length + blockSize - 1) / blockSize;
                if (payload is not null && payloadBlocks < clusterPages)
                {
                    compressedSizes[c] = payload.Length;
                    anyCompressed = true;
                    node.DataBlockAddrs[logical] = uint.MaxValue - 1;
                    for (var i = 0; i < payloadBlocks; i++)
                    {
                        node.DataBlockAddrs[logical + 1 + i] = uint.MaxValue;
                    }
                    for (var i = payloadBlocks + 1; i < clusterPages; i++) node.DataBlockAddrs[logical + i] = uint.MaxValue - 2;

                    allocated += payloadBlocks;
                    continue;
                }


                for (var i = 0; i < clusterPages; i++)
                {
                    node.DataBlockAddrs[logical + i] = uint.MaxValue;
                }

                allocated += clusterPages;
            }
            else
            {


                for (var i = 0; i < clusterBlocks; i++)
                {
                    node.DataBlockAddrs[logical + i] = uint.MaxValue;
                }

                allocated += clusterBlocks;
            }
        }

        if (!anyCompressed)
        {


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
    /// Maps the compression algorithm to the inode extra-attribute identifier.
    /// <para>把压缩算法映射为 inode 扩展属性中的内核算法标识。</para>
    /// </summary>
    private static byte KernelCompressionAlgorithmId(CompressionAlgorithm algorithm) => algorithm switch
    {
        CompressionAlgorithm.Lz4 => 1,
        CompressionAlgorithm.Zstd => 2,
        CompressionAlgorithm.Lzo => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>
    /// Log cluster size: cluster bytes = 1 &lt;&lt; (log_cluster_size + 12) = 65536.
    /// <para>压缩簇大小对数：簇大小 = 1 &lt;&lt; (log_cluster_size + 12) = 65536。</para>
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
        var start = (long)node.InodeAddressCount;
        var directCapacity = node.DataAddressesPerNode;
        var indirectCapacity = (long)F2fsNode.AddrsPerBlock * directCapacity;
        for (var slot = 0; slot < 2; slot++, start += directCapacity)
        {
            if (dataBlocks > start) node.RootDnodes[slot] = AddNode(start, 0, (uint)slot + 1);
        }
        for (var slot = 2; slot < 4; slot++, start += indirectCapacity)
        {
            if (dataBlocks > start) node.RootDnodes[slot] = AddNode(start, 1, (uint)(3 + (slot - 2) * (F2fsNode.AddrsPerBlock + 1)));
        }
        if (dataBlocks > start)
        {
            if (dataBlocks - start > indirectCapacity * F2fsNode.AddrsPerBlock)
                throw new InvalidOperationException($"File '{node.FullPath}' exceeds the maximum f2fs data size.");
            node.RootDnodes[4] = AddNode(start, 2, (uint)(5 + 2 * F2fsNode.AddrsPerBlock));
        }

        DnodePlan AddNode(long logicalStart, int depth, uint nodeOffset)
        {
            var plan = new DnodePlan { LogicalStart = logicalStart, IsIndirect = depth == 1, IsDoubleIndirect = depth == 2, NodeOffset = nodeOffset };
            node.Dnodes.Add(plan);
            if (depth > 0)
            {
                var childCapacity = depth == 1 ? directCapacity : indirectCapacity;
                var childCount = (int)Math.Min(F2fsNode.AddrsPerBlock, (dataBlocks - logicalStart + childCapacity - 1) / childCapacity);
                for (var i = 0; i < childCount; i++)
                    plan.Children.Add(AddNode(logicalStart + i * childCapacity, depth - 1, checked(nodeOffset + 1 + (uint)i * (depth == 1 ? 1u : F2fsNode.AddrsPerBlock + 1u))));
            }
            return plan;
        }
    }

    private static void PlanDirectoryBlocks(BuildNode node)
    {
        if (node.Name.Length > MaxNameBytes)
        {
            throw new InvalidOperationException($"File name too long (>{MaxNameBytes} bytes): '{node.Name}'.");
        }


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


        node.DirDepth = currentDepth + 1;
    }

    /// <summary>
    /// dir_buckets(level, dir_level=0) from the kernel f2fs.h.
    /// <para>dir_buckets(level, dir_level=0)（内核 fs/f2fs/f2fs.h）。</para>
    /// </summary>
    private static uint DirBuckets(uint level)
        => level < MaxDirHashDepth / 2 ? 1u << (int)level : 1u << (int)(MaxDirHashDepth / 2 - 1);

    /// <summary>
    /// bucket_blocks(level) from the kernel f2fs.h.
    /// <para>bucket_blocks(level)（内核 fs/f2fs/f2fs.h）。</para>
    /// </summary>
    private static uint BucketBlocks(uint level)
        => level < MaxDirHashDepth / 2 ? 2u : 4u;

    /// <summary>
    /// dir_block_index(level, dir_level=0, idx) from the kernel f2fs.h.
    /// <para>dir_block_index(level, dir_level=0, idx)（内核 fs/f2fs/f2fs.h）。</para>
    /// </summary>
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


        var block = new byte[F2fsNode.NodeSize];
        F2fsDirectoryEntry.BuildBlock(block, entries, F2fsNode.NodeSize);
        return block;
    }

    private static long CountMainBlocks(List<BuildNode> ordered)
    {
        long count = 0;
        foreach (var node in ordered)
        {
            count += 1;
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
    /// Computes the image layout with separate node and data segment regions.
    /// <para>计算镜像布局，为节点和数据分配独立段区。</para>
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

            foreach (var dnode in node.Dnodes)
                foreach (var child in dnode.Children) dnode.IndirectChildNids.Add(child.Nid);
        }

        long nodeBlocks = 0;
        long dataBlocks = 0;
        foreach (var node in ordered)
        {
            nodeBlocks++;
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


        var dataSegments = 3u + (uint)((Math.Max(0, dataBlocks - CompactDataEntryCap) + BlocksPerSeg - 1) / BlocksPerSeg);
        var mainSegments = nodeSegments + dataSegments + OverprovSlackSegments;
        if (mainSegments == 0)
        {
            mainSegments = 1;
        }

        var layout = new Layout { MaxNid = maxNid, NodeBlocks = nodeBlocks, DataBlocks = dataBlocks };


        uint sitSegments;
        uint natSegments;
        uint ssaSegments;
        uint fixedSegments;
        uint totalSegments;
        while (true)
        {
            var blocksForSit = (mainSegments + F2fsNode.SitEntryPerBlock - 1) / F2fsNode.SitEntryPerBlock;
            sitSegments = Math.Max(2, 2 * ((blocksForSit + BlocksPerSeg - 1) / BlocksPerSeg));


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
    /// Allocates main-area blocks and records current segments, valid blocks, and summaries.
    /// <para>分配主区块并记录当前段、有效块和摘要信息。</para>
    /// </summary>
    private static CursegPlan AllocateBlocks(List<BuildNode> ordered, Layout layout)
    {
        var plan = new CursegPlan();


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


        for (var t = 0; t < 3; t++)
        {
            plan.NodeSegno[t] = t < layout.NodeSegments ? (uint)(mainBase + t) : FreeSegment();
            plan.DataSegno[t] = t < layout.DataSegments ? (uint)(mainBase + layout.NodeSegments + t) : FreeSegment();
        }

        long nodeCursor = layout.NodeBlkaddr;
        long dataCursor = layout.DataBlkaddr;

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
                    if (node.DataBlockAddrs[i] == uint.MaxValue - 2)
                    {
                        node.DataBlockAddrs[i] = uint.MaxValue;
                        continue;
                    }
                    if (node.DataBlockAddrs[i] != uint.MaxValue)
                    {
                        continue;
                    }

                    node.DataBlockAddrs[i] = AllocateDataBlock(ref dataCursor, layout);
                    AssignBlock(node.DataBlockAddrs[i], ResolveOwnerNid(node, i, out var ofsInNode), (ushort)ofsInNode);
                }


                if (node.Dnodes.Count == 0
                    && node.AllocatedDataBlockCount == node.DataBlockCount
                    && node.DataBlockCount > 0)
                {
                    node.Extent = new F2fsExtent { Fofs = 0, Blk = node.DataBlockAddrs[0], Len = (uint)node.DataBlockCount };
                }
            }
        }


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


                    map[ofs >> 3] |= (byte)(1 << (7 - (ofs & 7)));
                }
            }

            plan.Segments[segno] = (count, map, SegmentTypeOf(segno, mainBase, layout, plan));
        }


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


        var validSegments = plan.Segments.Count(kvp => kvp.Value.Count > 0 || cursegSegnos.Contains(kvp.Key));
        plan.FreeSegmentCount = (uint)(plan.Segments.Count - validSegments);
        return plan;
    }

    /// <summary>
    /// Next free block offset inside a curseg segment.
    /// <para>curseg 段内下一空闲块偏移。</para>
    /// </summary>
    private static ushort BlkoffOf(CursegPlan plan, uint segno)
        => (ushort)(plan.SegmentEntries.TryGetValue(segno, out var entries) ? entries.Count : 0);

    /// <summary>
    /// Resolves a data block's owner: the inode itself for the direct (i_addr) range, or the covering direct node beyond it; ofs_in_node is the logical index inside that node's address array (as validated by fsck's sanity_check_nid).
    /// <para>数据块归属解析：直接区（i_addr）归 inode 自身，dnode 区归对应 direct 节点； ofs_in_node 为该节点地址数组内的逻辑下标（fsck sanity_check_nid 依据）。</para>
    /// </summary>
    private static uint ResolveOwnerNid(BuildNode node, int logical, out int ofsInNode)
    {
        if (logical < node.InodeAddressCount)
        {
            ofsInNode = logical;
            return node.Ino;
        }

        foreach (var dnode in node.Dnodes)
        {
            if (!dnode.IsIndirect && !dnode.IsDoubleIndirect
                && dnode.LogicalStart <= logical && logical < dnode.LogicalStart + node.DataAddressesPerNode)
            {
                ofsInNode = (int)(logical - dnode.LogicalStart);
                return dnode.Nid;
            }
        }

        ofsInNode = 0;
        return node.Ino;
    }

    /// <summary>
    /// Determines the SIT allocation type from the segment's region and current-segment assignment.
    /// <para>根据段所属区域与当前段分配确定 SIT 分配类型。</para>
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
        public DnodePlan?[] RootDnodes = new DnodePlan?[5];
        public int InodeAddressCount => IsCompressedFile ? (F2fsNode.DefAddrsPerInode - 9) & ~(F2fsCompression.ClusterPages - 1) : F2fsNode.DefAddrsPerInode;
        public int DataAddressesPerNode => IsCompressedFile ? F2fsNode.AddrsPerBlock & ~(F2fsCompression.ClusterPages - 1) : F2fsNode.AddrsPerBlock;
        public List<byte[]> DirBlocks = [];
        public List<uint> DirBlockAddrs = [];
        public uint DirDepth;
        public uint InodeBlockAddr;
        public F2fsExtent Extent = new F2fsExtent();
        public ushort Mode;
        public uint Uid;
        public uint Gid;
        public long Mtime;
        public long Ctime;
        public uint MtimeNsec;
        public uint CtimeNsec;
    }

    private sealed class DnodePlan
    {
        public uint Nid;
        public uint BlockAddr;
        public bool IsIndirect;
        public bool IsDoubleIndirect;
        public long LogicalStart;
        public uint NodeOffset;
        public List<DnodePlan> Children = [];
        public List<uint> IndirectChildNids = [];
    }

    private sealed class Layout
    {
        public long ReservedCompressionBlocks;
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

    private sealed class CursegPlan
    {
        public uint[] NodeSegno = new uint[3];
        public ushort[] NodeBlkoff = new ushort[3];
        public uint[] DataSegno = new uint[3];
        public ushort[] DataBlkoff = new ushort[3];
        public List<(uint Nid, ushort Ofs)>[] NodeEntries = [new(), new(), new()];
        public List<(uint Nid, ushort Ofs)>[] DataEntries = [new(), new(), new()];

        public Dictionary<uint, Dictionary<int, (uint Nid, ushort Ofs)>> SegmentEntries = new();

        public Dictionary<uint, (int Count, byte[] Map, byte Type)> Segments = new();

        public uint FreeSegmentCount;
    }
}

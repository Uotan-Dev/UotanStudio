using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Checksums;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Ext4.Models;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.Ext4.Builder;

/// <summary>
/// Builds raw ext4 filesystem images from a host directory. The layout (block groups, bitmaps, group
/// descriptors, extent trees, journal and resize inode) is planned entirely from the scanned tree, and
/// every geometry decision is bounded: a capacity that cannot hold metadata plus content fails instead
/// of silently truncating.
/// <para>从主机目录构建 ext4 原始文件系统镜像。布局（块组、位图、块组描述符、extent 树、日志与
/// resize inode）完全基于扫描得到的目录树规划；所有几何决策均有边界：无法容纳元数据与内容的容量
/// 会直接失败，而不是静默截断。</para>
/// </summary>
public sealed partial class Ext4Builder
{
    private const ushort ModeRegularFile = 0x81A4;
    private const ushort ModeDirectory = 0x41ED;
    private const ushort ModeSymbolicLink = 0xA1FF;
    private const ushort ModeJournalFile = 0x8180;
    private const uint FlagExtents = 0x0008_0000;
    private const uint FlagInlineData = 0x1000_0000;

    private const uint JournalInodeNumber = 8;

    private const int DefaultJournalBlocks = 1024;

    private const uint FeatureCompatHasJournal = 0x0004;
    private const uint FeatureRoCompatSharedBlocks = 0x4000;
    private const uint FeatureIncompatEaInode = 0x0400;
    private const uint FeatureRoCompatProject = 0x2000;

    /// <summary>
    /// EXT4 compatible feature bit <c>EXT2_FEATURE_COMPAT_EXT_ATTR</c>: extended attributes are stored in inode bodies.
    /// <para>EXT4 兼容特性位 <c>EXT2_FEATURE_COMPAT_EXT_ATTR</c>：扩展属性存储于 inode 体内。</para>
    /// </summary>
    private const uint FeatureCompatExtAttr = 0x0008;

    /// <summary>
    /// EXT4 compatible feature bit <c>EXT2_FEATURE_COMPAT_RESIZE_INODE</c>: the resize inode and group descriptor backups are present.
    /// <para>EXT4 兼容特性位 <c>EXT2_FEATURE_COMPAT_RESIZE_INODE</c>：存在 resize inode 与组描述符备份。</para>
    /// </summary>
    private const uint FeatureCompatResizeInodeBit = 0x0010;

    /// <summary>
    /// EXT4 compatible feature bit <c>EXT2_FEATURE_COMPAT_DIR_INDEX</c>: directories may use hashed b-tree (HTree) indexes.
    /// <para>EXT4 兼容特性位 <c>EXT2_FEATURE_COMPAT_DIR_INDEX</c>：目录可使用哈希 B 树（HTree）索引。</para>
    /// </summary>
    private const uint FeatureCompatDirIndex = 0x0020;

    /// <summary>
    /// EXT4 incompatible feature bit <c>EXT2_FEATURE_INCOMPAT_FILETYPE</c>: directory entries carry an explicit file type byte.
    /// <para>EXT4 不兼容特性位 <c>EXT2_FEATURE_INCOMPAT_FILETYPE</c>：目录项携带显式文件类型字节。</para>
    /// </summary>
    private const uint FeatureIncompatFileType = 0x0002;

    /// <summary>
    /// EXT4 incompatible feature bit <c>EXT3_FEATURE_INCOMPAT_EXTENTS</c>: inodes store extents instead of indirect blocks.
    /// <para>EXT4 不兼容特性位 <c>EXT3_FEATURE_INCOMPAT_EXTENTS</c>：inode 使用 extent 树代替间接块。</para>
    /// </summary>
    private const uint FeatureIncompatExtents = 0x0040;

    /// <summary>
    /// EXT4 incompatible feature bit <c>EXT4_FEATURE_INCOMPAT_64BIT</c>: group descriptor block addresses are 64 bits wide.
    /// <para>EXT4 不兼容特性位 <c>EXT4_FEATURE_INCOMPAT_64BIT</c>：组描述符块地址为 64 位宽。</para>
    /// </summary>
    private const uint FeatureIncompat64Bit = 0x0080;

    /// <summary>
    /// EXT4 incompatible feature bit <c>EXT4_FEATURE_INCOMPAT_FLEX_BG</c>: data, inode and bitmap blocks of adjacent groups may share one flexible block group.
    /// <para>EXT4 不兼容特性位 <c>EXT4_FEATURE_INCOMPAT_FLEX_BG</c>：相邻组的元数据块可归属同一个弹性块组。</para>
    /// </summary>
    private const uint FeatureIncompatFlexBg = 0x0200;

    /// <summary>
    /// EXT4 incompatible feature bit <c>EXT4_FEATURE_INCOMPAT_CASEFOLD</c>: directory entries use filename case-insensitive hashing.
    /// <para>EXT4 不兼容特性位 <c>EXT4_FEATURE_INCOMPAT_CASEFOLD</c>：目录项使用文件名大小写不敏感哈希。</para>
    /// </summary>
    private const uint FeatureIncompatCasefold = 0x20000;

    /// <summary>
    /// EXT4 read-only compatible feature bit <c>EXT2_FEATURE_RO_COMPAT_SPARSE_SUPER</c>: superblock and group descriptors are backed up only in select block groups.
    /// <para>EXT4 只读兼容特性位 <c>EXT2_FEATURE_RO_COMPAT_SPARSE_SUPER</c>：超级块与组描述符仅在部分块组备份。</para>
    /// </summary>
    private const uint FeatureRoCompatSparseSuper = 0x0001;

    /// <summary>
    /// EXT4 read-only compatible feature bit <c>EXT2_FEATURE_RO_COMPAT_LARGE_FILE</c>: files larger than 2 GiB are supported.
    /// <para>EXT4 只读兼容特性位 <c>EXT2_FEATURE_RO_COMPAT_LARGE_FILE</c>：支持超过 2 GiB 的文件。</para>
    /// </summary>
    private const uint FeatureRoCompatLargeFile = 0x0002;

    /// <summary>
    /// EXT4 read-only compatible feature bit <c>EXT4_FEATURE_RO_COMPAT_HUGE_FILE</c>: inode block counts are stored in 48-bit sector units.
    /// <para>EXT4 只读兼容特性位 <c>EXT4_FEATURE_RO_COMPAT_HUGE_FILE</c>：inode 块计数以 48 位扇区单位存储。</para>
    /// </summary>
    private const uint FeatureRoCompatHugeFile = 0x0008;

    /// <summary>
    /// EXT4 read-only compatible feature bit <c>EXT4_FEATURE_RO_COMPAT_DIR_NLINK</c>: directory i_links_count is fixed at 1 instead of the entry count.
    /// <para>EXT4 只读兼容特性位 <c>EXT4_FEATURE_RO_COMPAT_DIR_NLINK</c>：目录的 i_links_count 固定为 1 而非子目录数。</para>
    /// </summary>
    private const uint FeatureRoCompatDirNlink = 0x0020;

    /// <summary>
    /// EXT4 read-only compatible feature bit <c>EXT4_FEATURE_RO_COMPAT_EXTRA_ISIZE</c>: inodes carry extra on-disk fields beyond the 128-byte base.
    /// <para>EXT4 只读兼容特性位 <c>EXT4_FEATURE_RO_COMPAT_EXTRA_ISIZE</c>：inode 在 128 字节基础布局之外携带额外磁盘字段。</para>
    /// </summary>
    private const uint FeatureRoCompatExtraIsize = 0x0040;

    /// <summary>
    /// EXT4 read-only compatible feature bit <c>EXT4_FEATURE_RO_COMPAT_METADATA_CSUM</c>: metadata blocks carry CRC32c checksums (implies gdt_csum and unshare_tables).
    /// <para>EXT4 只读兼容特性位 <c>EXT4_FEATURE_RO_COMPAT_METADATA_CSUM</c>：元数据块携带 CRC32c 校验和（隐含 gdt_csum 与 unshare_tables）。</para>
    /// </summary>
    private const uint FeatureRoCompatMetadataChecksum = 0x0400;

    /// <summary>
    /// EXT4 read-only compatible feature bit <c>EXT4_FEATURE_RO_COMPAT_READONLY</c>: the filesystem must be mounted read-only.
    /// <para>EXT4 只读兼容特性位 <c>EXT4_FEATURE_RO_COMPAT_READONLY</c>：文件系统必须以只读方式挂载。</para>
    /// </summary>
    private const uint FeatureRoCompatReadOnly = 0x1000;

    private const int SbFeatureIncompat = 0x60;
    private const int SbFeatureRoCompat = 0x64;

    /// <summary>
    /// Maximum filesystem block count representable by the 32-bit on-disk superblock block counters; it is also the growth target used to size the reserved group descriptor table once the image approaches that limit.
    /// <para>磁盘超级块 32 位块计数字段可表示的最大文件系统块数；镜像接近该上限时，也作为保留组描述符表的扩容目标。</para>
    /// </summary>
    private const long Max32BitBlockCount = 0xFFFF_FFFFL;

    /// <summary>
    /// Filesystem block counts below this threshold size the reserved group descriptor table for growth to the current count times 1024 (which still fits the 32-bit block counter) instead of the absolute 32-bit maximum.
    /// <para>文件系统块数低于该阈值时，保留组描述符表按“当前块数 × 1024”（仍落在 32 位块计数之内）预留扩容空间，而不是按绝对 32 位上限预留。</para>
    /// </summary>
    private const long GrowthExpansionThresholdBlocks = (1L << 32) / 1024;

    private const ushort Ext4ExtraIsize = 32;

    private const ushort Ext4InodeChecksumHiExtraEnd = 4;
    private const ushort ExtentMagic = 0xF30A;
    private const uint MaxExtentLength = 32768;
    private const uint FixedTimestamp = 1_700_000_000;
    private const uint RootInodeNumber = 2;
    private const uint FirstUsableInode = 11;

    private const int DirEntryTailSize = 12;

    private const int BlocksPerGroupMultiplier = 8;

    private static readonly byte[] FixedUuid = [0x53, 0xAA, 0x0F, 0x4D, 0x2C, 0x7A, 0x11, 0xEF, 0x8A, 0xB5, 0x00, 0x0C, 0x29, 0xC0, 0xA1, 0xB2];

    private sealed class GroupInfo
    {
        public int GroupIndex { get; init; }
        public long BlockBitmapBlock { get; set; }
        public long InodeBitmapBlock { get; set; }
        public long InodeTableBlock { get; set; }
        public long FirstDataBlock { get; set; }
        public long BlocksInGroup { get; set; }
        public int InodesInGroup { get; set; }
        public int UsedInodes { get; set; }
        public int UsedDirectories { get; set; }
        public long UsedBlocks { get; set; }
        public List<Node> Nodes { get; } = [];

        public List<long> ExtraUsedBlocks { get; } = [];
    }

    private sealed class Node
    {
        public required string Name { get; set; }
        public required string RelativePath { get; set; }
        public required string FullPath { get; set; }
        public required bool IsDirectory { get; init; }
        public required bool IsSymlink { get; set; }
        public string? SymlinkTarget { get; set; }
        public Node? Parent { get; init; }
        public List<Node> Children { get; } = [];
        public long Size { get; set; }
        public ulong OriginalInode;
        public ushort LinkCount = 1;
        public uint SemanticFlags;
        public uint DeviceId;
        public bool IsSpecial => (Mode & InodeTypeBits.FormatMask) is InodeTypeBits.Fifo or InodeTypeBits.CharDevice or InodeTypeBits.BlockDevice or InodeTypeBits.Socket;
        public uint Inode { get; set; }
        public ushort Mode { get; set; }
        public uint Uid { get; set; }
        public uint Gid { get; set; }
        public uint Mtime { get; set; }
        public uint Ctime { get; set; }
        public long DataBlockCount { get; set; }
        public List<(uint Logical, ulong Start, ushort Len)> Extents { get; } = [];
        public long[] LeafBlocks { get; set; } = [];
        public long[] IndexBlocks { get; set; } = [];
        public int ExtentTreeDepth { get; set; }
        public bool IsInlineData { get; set; }
        public bool IsIndexed { get; set; }
        public HashSet<int>? HoleBlocks { get; set; }
        public List<(long LogicalStart, long Count)> Runs { get; } = [];
        public List<byte[]> DirectoryBlocks { get; set; } = [];
        public Dictionary<string, byte[]>? Xattrs { get; set; }
        public FileSystemInodeMetadata? SourceMetadata { get; set; }
        public byte[]? ExternalXattrs { get; set; }
        public long XattrBlock { get; set; }
        public byte[]? AttributeValue { get; set; }
        public long AttributeValueBlocks { get; set; }
        public List<long>? SharedBlockIds { get; set; }
    }

    private readonly record struct BlockAllocationResult(bool Success, long JournalStart, bool SharedBlocks);

    /// <summary>
    /// Carries the planned filesystem tree through the BuildCore planning stage: the root tree, its flattened node list, the highest assigned inode number, and the shared-block plan.
    /// <para>在 BuildCore 规划阶段承载规划得到的文件系统树：根树、扁平化节点列表、已分配的最大 inode 号，以及相同块共享计划。</para>
    /// </summary>
    private sealed class TreePlan
    {
        public required Node Root { get; init; }
        public required List<Node> Nodes { get; init; }
        public required uint MaxInode { get; init; }
        public required SharedBlockPlan? SharedPlan { get; init; }
    }

    /// <summary>
    /// Carries the block-group geometry computed from the planned tree: per-group inode and block sizing, group descriptor table sizing, journal block count, and the derived block totals consumed by the group construction and write stages.
    /// <para>承载由规划树计算得到的块组几何：每组 inode 与块规模、组描述符表大小、日志块数，以及组构建与写出阶段使用的块总量。</para>
    /// </summary>
    private sealed class GroupGeometry
    {
        public int InodesPerGroup { get; init; }
        public int InodeTableBlocks { get; init; }
        public int BlocksPerGroup { get; init; }
        public int GroupCount { get; init; }
        public long GdtBlocks { get; init; }
        public int ReservedGdtBlocks { get; init; }
        public int JournalBlocks { get; init; }
        public bool IncludeJournal { get; init; }
        public long TotalBlocks { get; set; }
        public long UsedBlocks { get; set; }
        public long? RequestedBlocks { get; init; }
    }

    /// <summary>
    /// Carries the reserved group descriptor table size and the metadata reservation totals recomputed from a final group count.
    /// <para>承载按最终组数重算的保留组描述符表大小与元数据预留总量。</para>
    /// </summary>
    /// <param name="ReservedGdtBlocks">Reserved group descriptor table block count. <para>保留组描述符表块数。</para></param>
    /// <param name="BackupGroupCount">Number of backup superblock groups. <para>备份超级块所在组的数量。</para></param>
    /// <param name="Group0Metadata">Metadata block demand of group zero. <para>组 0 的元数据块需求。</para></param>
    /// <param name="TotalBlocksNeeded">Total block demand of metadata and content. <para>元数据与内容的总块需求。</para></param>
    private readonly record struct GroupReservation(int ReservedGdtBlocks, int BackupGroupCount, long Group0Metadata, long TotalBlocksNeeded);

    /// <summary>
    /// Builds an ext4 image from sourceDirectory and returns it as a byte array.
    /// <para>从 sourceDirectory 构建 ext4 镜像并以字节数组返回。</para>
    /// </summary>
    /// <param name="sourceDirectory">Host directory whose full tree is packed into the image; it must exist. <para>被完整打包进镜像的主机目录；必须存在。</para></param>
    /// <param name="options">Layout and capacity options; <see langword="null"/> uses <see cref="Ext4Options"/> defaults. Invalid combinations are rejected during validation. <para>布局与容量选项；<see langword="null"/> 时使用 <see cref="Ext4Options"/> 默认值。非法组合会在校验阶段被拒绝。</para></param>
    /// <returns>The complete filesystem image as a single byte array. <para>以单一字节数组返回的完整文件系统镜像。</para></returns>
    [Obsolete("Builds the entire image in memory. Use Build(sourceDirectory, outputPath) or Build(sourceDirectory, Stream) instead.")]
    public static byte[] Build(string sourceDirectory, Ext4Options? options = null)
    {
        var effective = Validate(sourceDirectory, options);
        using var image = new MemoryStream();
        BuildCore(sourceDirectory, image, effective, 0, freshDestination: true, excludedFiles: []);
        if (image.TryGetBuffer(out var buffer) && buffer.Offset == 0 && buffer.Count == buffer.Array!.Length)
        {
            return buffer.Array;
        }

        return [.. buffer];
    }

    /// <summary>
    /// Builds an ext4 image from sourceDirectory and writes it to outputPath.
    /// <para>从 sourceDirectory 构建 ext4 镜像并写入 outputPath。</para>
    /// </summary>
    /// <param name="sourceDirectory">Host directory tree packed into the image; it must exist. <para>被打包的主机目录树；必须存在。</para></param>
    /// <param name="outputPath">Destination image path; written through a temporary file and published by an atomic move that overwrites an existing target. The destination and its staging file are skipped when they lie inside the source tree. <para>目标镜像路径；先写临时文件，再以覆盖式原子移动发布。当目标及其暂存文件位于源目录树内时会被跳过。</para></param>
    /// <param name="options">Layout and capacity options; <see langword="null"/> uses <see cref="Ext4Options"/> defaults. Invalid combinations are rejected during validation. <para>布局与容量选项；<see langword="null"/> 时使用 <see cref="Ext4Options"/> 默认值。非法组合会在校验阶段被拒绝。</para></param>
    /// <param name="progress"><para>可选写作进度 <c>(已写字节, 源目录估算总字节)</c>；<c>null</c> 表示不报告。</para>Optional write progress <c>(bytes written, estimated source bytes)</c>; <c>null</c> disables reporting.</param>
    public static void Build(string sourceDirectory, string outputPath, Ext4Options? options = null, IProgress<(long Processed, long Total)>? progress = null)
    {
        var effective = Validate(sourceDirectory, options);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        var plannedRoot = BuildTree(Path.GetFullPath(sourceDirectory), [fullOutputPath, temporaryPath]);
        Directory.CreateDirectory(directory);

        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.None))
            {
                var counter = progress is null ? null
                    : new ExtractionByteCounter(EstimateSourceBytes(sourceDirectory), progress);
                Stream sink = new CountingWriteStream(output, long.MaxValue, leaveOpen: true,
                    cancellationToken: effective.CancellationToken);
                if (counter is not null) sink = counter.Wrap(sink);
                BuildCore(sourceDirectory, sink, effective, 0, freshDestination: true, excludedFiles: [fullOutputPath, temporaryPath], plannedRoot);
                output.Flush(flushToDisk: true);
                counter?.Complete();
                sink.Dispose();
            }

            AtomicPublish.Publish(temporaryPath, fullOutputPath);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static long EstimateSourceBytes(string sourceDirectory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
        }
        return total == 0 ? 1 : total;
    }

    /// <summary>
    /// Builds an ext4 image from sourceDirectory and streams it to output.
    /// <para>从 sourceDirectory 构建 ext4 镜像并流式写入 output。</para>
    /// </summary>
    /// <param name="sourceDirectory">Host directory tree packed into the image; it must exist. <para>被打包的主机目录树；必须存在。</para></param>
    /// <param name="output">Writable stream receiving the image at its current position; ownership stays with the caller. A non-writable stream throws <see cref="ArgumentException"/>. <para>在当前位置接收镜像的可写流；所有权仍归调用方。流不可写时抛出 <see cref="ArgumentException"/>。</para></param>
    /// <param name="options">Layout and capacity options; <see langword="null"/> uses <see cref="Ext4Options"/> defaults. Minimize capacity or duplicate-block sharing is first staged in a temporary file, then copied exactly into the stream. <para>布局与容量选项；<see langword="null"/> 时使用 <see cref="Ext4Options"/> 默认值。容量最小化或相同块共享会先写入临时文件，再精确复制到流中。</para></param>
    public static void Build(string sourceDirectory, Stream output, Ext4Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        var effective = Validate(sourceDirectory, options);
        string[] excludedFiles = output is FileStream fileStream ? [Path.GetFullPath(fileStream.Name)] : [];
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"uotan-ext4-{Guid.NewGuid():N}.tmp");

        var stagingExclusions = excludedFiles.Append(Path.GetFullPath(temporaryPath)).ToArray();
        var plannedRoot = BuildTree(Path.GetFullPath(sourceDirectory), stagingExclusions);
        var canResize = output.CanSeek && StreamResize.SupportsResizeWithoutMutation(output);
        var requiresStaging = effective.CapacityMode == Ext4CapacityMode.Minimize
            || effective.BlockSharingMode == Ext4BlockSharingMode.ShareDuplicateData;
        if (canResize && !requiresStaging)
        {
            BuildCore(sourceDirectory, output, effective, output.Position, freshDestination: false, excludedFiles: stagingExclusions, plannedRoot);
            return;
        }

        try
        {
            using (var staging = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                       FileOptions.SequentialScan))
            {
                BuildCore(sourceDirectory, staging, effective, 0, freshDestination: true, excludedFiles: stagingExclusions, plannedRoot);
                staging.Position = 0;
                StreamCopy.CopyExactly(staging, output, staging.Length, effective.CancellationToken);
                if (canResize)
                {
                    output.SetLength(output.Position);
                }
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static Ext4Options Validate(string sourceDirectory, Ext4Options? options)
    {
        FileSystemPreservation.RejectLossyDirectoryRebuild(sourceDirectory);
        options ??= new Ext4Options();
        if (!Enum.IsDefined(options.CapacityMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Unsupported EXT4 capacity mode '{options.CapacityMode}'.");
        }
        if (!Enum.IsDefined(options.BlockSharingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Unsupported EXT4 block sharing mode '{options.BlockSharingMode}'.");
        }
        if (options.CasefoldEncoding is not null and not 1
            || options.CasefoldEncoding is null && options.CasefoldEncodingFlags != 0)
        {
            throw new NotSupportedException("EXT4 casefold requires a supported UTF-8 encoding ID.");
        }
        if (FileSystemEditPlan.Current is { } plan
            && plan.Entries.Values.Any(entry => (entry.Flags & 0x40000000) != 0)
            && options.CasefoldEncoding is null)
        {
            throw new FileSystemCorruptedException("EXT4 casefold directory requires its source encoding.");
        }
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

        if (options.TargetSizeBytes is { } targetSize
            && (targetSize <= 0 || targetSize % blockSize != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "EXT4 target size must be positive and block aligned.");
        }

        if (options.CapacityMode == Ext4CapacityMode.Minimize && options.TargetSizeBytes is not null)
        {
            throw new InvalidOperationException("EXT4 minimum capacity and an explicit target size are mutually exclusive.");
        }

        if (options.BlockSharingMode == Ext4BlockSharingMode.ShareDuplicateData && !options.ReadOnly)
        {
            throw new InvalidOperationException("EXT4 duplicate block sharing requires an explicitly read-only filesystem.");
        }

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Source directory not found: '{sourceDirectory}'.");
        }

        return options;
    }

    /// <summary>
    /// Applies fs_config (uid/gid/mode) and file_contexts (security.selinux xattr) overrides
    /// from the Android-style metadata files onto the built tree.
    /// <para>将 Android 风格元数据文件中的 fs_config（uid/gid/mode）与 file_contexts
    /// （security.selinux xattr）覆盖应用到已构建的目录树。</para>
    /// </summary>
    private static void ApplyMetadataOverrides(Node root, List<Node> nodes, Ext4Options options)
    {
        if (options.FsConfig is null && options.FileContexts is null
            && options.FsConfigPath is null && options.FileContextsPath is null) return;
        var fsConfig = options.FsConfig ??= FileSystemMetadataOverrides.LoadFsConfig(options.FsConfigPath);
        var contexts = options.FileContexts ??= FileSystemMetadataOverrides.LoadFileContexts(options.FileContextsPath);
        foreach (var node in nodes)
        {
            var relative = node == root ? string.Empty : node.RelativePath;
            if (fsConfig.Count > 0 && FileSystemMetadataOverrides.TryGetFsConfig(fsConfig, relative, out var entry))
            {
                node.Uid = entry.Uid;
                node.Gid = entry.Gid;
                var type = (node.Mode & InodeTypeBits.FormatMask) is var sourceType && sourceType != 0
                    ? sourceType : node.IsDirectory ? InodeTypeBits.Directory : node.IsSymlink ? InodeTypeBits.SymbolicLink : InodeTypeBits.RegularFile;
                node.Mode = (ushort)(type | (entry.Mode & 0x0FFF));
            }

            if (contexts.Count > 0 && FileSystemMetadataOverrides.TryGetLabel(contexts, relative, out var label))
            {
                node.Xattrs ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
                node.Xattrs["security.selinux"] = FileSystemMetadataOverrides.ToSelinuxXattrValue(label);
            }
        }
    }

    private static void BuildCore(string sourceDirectory, Stream output, Ext4Options options, long baseOffset, bool freshDestination, IReadOnlyCollection<string> excludedFiles, Node? plannedRoot = null, int minimumGroupCount = 1)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        var blockSize = options.BlockSize;
        var inodeSize = options.InodeSize;

        var uuid = options.Uuid is { Length: 16 } ? options.Uuid : FixedUuid;
        var enableMetadataCsum = options.EnableMetadataCsum;

        var tree = PlanTree(sourceDirectory, options, blockSize, inodeSize, uuid, enableMetadataCsum, excludedFiles, plannedRoot);
        using var sharedPlan = tree.SharedPlan;
        var geometry = ComputeGroupGeometry(tree.Nodes, options, tree.MaxInode, tree.SharedPlan, blockSize, inodeSize, minimumGroupCount);
        var (groups, backupGroups, resizeDindirBlock) = CreateGroups(geometry);

        var allocation = options.BlockSharingMode == Ext4BlockSharingMode.ShareDuplicateData
            ? AllocateSharedDataBlocksMultiGroup(tree.Nodes, groups, geometry.BlocksPerGroup, blockSize, geometry.JournalBlocks,
                sharedPlan!, options.CancellationToken)
            : AllocateDataBlocksMultiGroup(tree.Nodes, groups, geometry.BlocksPerGroup, blockSize, geometry.JournalBlocks, out var independentJournalStart)
                ? new BlockAllocationResult(true, independentJournalStart, false)
                : new BlockAllocationResult(false, independentJournalStart, false);
        var journalStart = allocation.JournalStart;
        if (!allocation.Success)
        {
            if (geometry.RequestedBlocks is not null)
            {
                throw new InvalidOperationException(
                    $"Requested EXT4 capacity {options.TargetSizeBytes} bytes is too small after extent allocation.");
            }
            BuildCore(sourceDirectory, output, options, baseOffset, freshDestination, excludedFiles, tree.Root, checked(geometry.GroupCount + 1));
            return;
        }
        geometry.UsedBlocks = groups.Sum(group => group.FirstDataBlock - (long)group.GroupIndex * geometry.BlocksPerGroup + group.UsedBlocks);
        if (options.CapacityMode == Ext4CapacityMode.Minimize)
        {
            geometry.TotalBlocks = groups.Max(static group => group.FirstDataBlock + group.UsedBlocks);
            var lastGroup = groups[^1];
            var lastGroupStart = (long)lastGroup.GroupIndex * geometry.BlocksPerGroup;
            lastGroup.BlocksInGroup = geometry.TotalBlocks - lastGroupStart;
            if (lastGroup.BlocksInGroup <= 0)
            {
                throw new InvalidOperationException("EXT4 minimum-capacity planning produced an empty final block group.");
            }
        }
        if (geometry.IncludeJournal)
        {

            groups[0].UsedInodes++;
        }

        WriteImage(output, baseOffset, options, freshDestination, tree.Nodes, groups, backupGroups, geometry, uuid, journalStart,
            allocation.SharedBlocks, sharedPlan, resizeDindirBlock);
    }

    /// <summary>
    /// Plans the filesystem tree: builds or reuses the root tree, assigns inodes, applies metadata overrides, plans xattrs and data blocks, creates the shared-block plan, and finalizes extent tree block counts.
    /// <para>规划文件系统树：构建或复用根树、分配 inode、应用元数据覆盖、规划 xattr 与数据块、创建相同块共享计划，并确定 extent 树块数。</para>
    /// </summary>
    /// <param name="sourceDirectory">Host directory packed into the image when no preplanned root exists. <para>不存在预规划根树时被打包的主机目录。</para></param>
    /// <param name="options">Build options supplying metadata overrides, sparse detection, and block sharing mode. <para>提供元数据覆盖、稀疏检测与相同块共享模式的构建选项。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes. <para>文件系统块大小（字节）。</para></param>
    /// <param name="inodeSize">Inode size in bytes. <para>inode 大小（字节）。</para></param>
    /// <param name="uuid">Filesystem UUID used as the planning hash seed. <para>作为规划哈希种子使用的文件系统 UUID。</para></param>
    /// <param name="enableMetadataCsum">Whether directory block metadata checksums are enabled. <para>是否启用目录块元数据校验和。</para></param>
    /// <param name="excludedFiles">Full paths excluded from the scanned tree. <para>扫描树时排除的完整路径。</para></param>
    /// <param name="plannedRoot">Previously built tree reused instead of rescanning sourceDirectory. <para>复用的既有树；提供时不再重新扫描 sourceDirectory。</para></param>
    /// <returns>The planned tree with its node list, highest inode number, and shared-block plan. <para>包含节点列表、最大 inode 号与相同块共享计划的规划结果。</para></returns>
    private static TreePlan PlanTree(string sourceDirectory, Ext4Options options, int blockSize, int inodeSize, byte[] uuid, bool enableMetadataCsum, IReadOnlyCollection<string> excludedFiles, Node? plannedRoot)
    {
        var root = plannedRoot ?? BuildTree(Path.GetFullPath(sourceDirectory), excludedFiles);
        var nodes = new List<Node>();
        var maxInode = AssignInodes(root, nodes);
        ApplyMetadataOverrides(root, nodes, options);
        maxInode = PlanXattrs(nodes, inodeSize, blockSize, maxInode, uuid);
        foreach (var node in nodes)
        {
            node.Runs.Clear();
            node.Extents.Clear();
        }

        PlanDirectoryBlocks(nodes, blockSize, uuid, enableMetadataCsum);
        PlanDataBlocks(nodes, blockSize, options.EnableSparse);
        var sharedPlan = options.BlockSharingMode == Ext4BlockSharingMode.ShareDuplicateData
            ? SharedBlockPlan.Create(nodes, blockSize, options)
            : null;
        if (sharedPlan is not null)
        {
            options.SharedBlockPlanCompleted?.Invoke();
        }
        PlanLeafBlocks(nodes, blockSize);

        return new TreePlan
        {
            Root = root,
            Nodes = nodes,
            MaxInode = maxInode,
            SharedPlan = sharedPlan,
        };
    }

    /// <summary>
    /// Computes the block group geometry: per-group inode and block sizing, the group count with its growth recomputation, the group descriptor table size with its reserved growth space, and the total block demand.
    /// <para>计算块组几何：每组 inode 与块规模、组数及其增长重算、含保留扩容空间的组描述符表大小，以及总块需求。</para>
    /// </summary>
    /// <param name="nodes">Planned nodes whose block demand drives the geometry. <para>其块需求驱动几何计算的已规划节点。</para></param>
    /// <param name="options">Build options supplying the journal preference, target size, and capacity mode. <para>提供日志偏好、目标容量与容量模式的构建选项。</para></param>
    /// <param name="maxInode">Highest assigned inode number. <para>已分配的最大 inode 号。</para></param>
    /// <param name="sharedPlan">Active shared-block plan, or <see langword="null"/> when duplicate sharing is disabled. <para>生效的相同块共享计划；未启用共享时为 <see langword="null"/>。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes. <para>文件系统块大小（字节）。</para></param>
    /// <param name="inodeSize">Inode size in bytes. <para>inode 大小（字节）。</para></param>
    /// <param name="minimumGroupCount">Lower bound on the group count carried through recursive retries. <para>递归重试传递的组数下限。</para></param>
    /// <returns>The computed group geometry. <para>计算得到的块组几何。</para></returns>
    private static GroupGeometry ComputeGroupGeometry(List<Node> nodes, Ext4Options options, uint maxInode, SharedBlockPlan? sharedPlan, int blockSize, int inodeSize, int minimumGroupCount)
    {
        var inodesPerInodeBlock = blockSize / inodeSize;

        var inodesPerGroup = (int)RoundUpPowerOfTwo(Math.Max(maxInode, 16));
        var inodeTableBlocks = (inodesPerGroup + inodesPerInodeBlock - 1) / inodesPerInodeBlock;

        var metaPerGroup = 2L + inodeTableBlocks;

        var totalDataBlocks = (sharedPlan is null
                ? nodes.Sum(static n => n.DataBlockCount)
                : nodes.Where(static n => !IsBlockSharingCandidate(n)).Sum(static n => n.DataBlockCount)
                    + sharedPlan.UniqueBlockCount)
            + nodes.Sum(static n => (long)n.LeafBlocks.Length)
            + nodes.Sum(static n => (long)n.IndexBlocks.Length)
            + nodes.Count(static n => n.ExternalXattrs is not null);

        var maxBlockGroupBlocks = BlocksPerGroupMultiplier * blockSize;
        var journalBlocks = options.IncludeJournal ? DefaultJournalBlocks : 0;
        var includeJournal = journalBlocks > 0;

        var group0Overhead = 32L + inodeTableBlocks;
        var minimumGroupBlocks = Math.Max(8L, group0Overhead + totalDataBlocks + journalBlocks);
        var blocksPerGroup = (int)Math.Min(maxBlockGroupBlocks,
            ((minimumGroupBlocks + 7) / 8) * 8);

        var requestedBlocks = options.TargetSizeBytes is { } targetSizeBytes
            ? targetSizeBytes / blockSize
            : (long?)null;
        if (requestedBlocks is { } fixedBlocks)
        {
            blocksPerGroup = checked((int)Math.Min(maxBlockGroupBlocks, ((fixedBlocks + 7) / 8) * 8));
        }

        var gdtBlocks = 1L;
        var group0Extra = 1L + gdtBlocks;
        var estimatedTotal = group0Extra + metaPerGroup + totalDataBlocks + journalBlocks;
        var requiredInodeGroups = (int)Math.Ceiling((double)maxInode / inodesPerGroup);
        var groupCount = requestedBlocks is { } targetBlocks
            ? checked((int)((targetBlocks + blocksPerGroup - 1) / blocksPerGroup))
            : Math.Max(minimumGroupCount, (int)((estimatedTotal + blocksPerGroup - 1) / blocksPerGroup));
        if (requestedBlocks is not null && requiredInodeGroups > groupCount)
        {
            throw new InvalidOperationException("Requested EXT4 capacity cannot hold the inode table.");
        }
        groupCount = Math.Max(groupCount, requiredInodeGroups);

        gdtBlocks = Math.Max(1, (long)Math.Ceiling(groupCount * 64.0 / blockSize));

        var descsPerGdtBlock = blockSize / 64;
        var reservation = ComputeGroupReservation(groupCount, blocksPerGroup, gdtBlocks, metaPerGroup, totalDataBlocks, journalBlocks, descsPerGdtBlock, blockSize, requestedBlocks);
        var reservedGdtBlocks = reservation.ReservedGdtBlocks;
        var backupGroupCount = reservation.BackupGroupCount;
        var group0Metadata = reservation.Group0Metadata;
        var totalBlocksNeeded = reservation.TotalBlocksNeeded;

        if (totalBlocksNeeded > (long)groupCount * blocksPerGroup)
        {
            if (requestedBlocks is not null)
            {
                throw new InvalidOperationException(
                    $"Requested EXT4 capacity {options.TargetSizeBytes} bytes is too small for filesystem metadata and content.");
            }
            groupCount = (int)((totalBlocksNeeded + blocksPerGroup - 1) / blocksPerGroup);
            gdtBlocks = Math.Max(1, (long)Math.Ceiling(groupCount * 64.0 / blockSize));
            reservation = ComputeGroupReservation(groupCount, blocksPerGroup, gdtBlocks, metaPerGroup, totalDataBlocks, journalBlocks, descsPerGdtBlock, blockSize, requestedBlocks);
            reservedGdtBlocks = reservation.ReservedGdtBlocks;
            backupGroupCount = reservation.BackupGroupCount;
            group0Metadata = reservation.Group0Metadata;
            totalBlocksNeeded = reservation.TotalBlocksNeeded;
        }

        var totalBlocks = requestedBlocks ?? (long)groupCount * blocksPerGroup;
        if (totalBlocksNeeded > totalBlocks)
        {
            throw new InvalidOperationException(
                $"Requested EXT4 capacity {options.TargetSizeBytes} bytes is too small for filesystem metadata and content.");
        }
        var usedBlocks = totalBlocksNeeded;

        return new GroupGeometry
        {
            InodesPerGroup = inodesPerGroup,
            InodeTableBlocks = inodeTableBlocks,
            BlocksPerGroup = blocksPerGroup,
            GroupCount = groupCount,
            GdtBlocks = gdtBlocks,
            ReservedGdtBlocks = reservedGdtBlocks,
            JournalBlocks = journalBlocks,
            IncludeJournal = includeJournal,
            TotalBlocks = totalBlocks,
            UsedBlocks = usedBlocks,
            RequestedBlocks = requestedBlocks,
        };
    }

    /// <summary>
    /// Computes the reserved group descriptor table size and the metadata reservation totals for a final group count; the initial geometry pass and the growth recomputation share this formula.
    /// <para>按最终组数计算保留组描述符表大小与元数据预留总量；初始几何计算与增长重算两个分支共用该公式。</para>
    /// </summary>
    /// <param name="groupCount">Final block group count. <para>最终块组数量。</para></param>
    /// <param name="blocksPerGroup">Blocks per group. <para>每块组块数。</para></param>
    /// <param name="gdtBlocks">Primary group descriptor table block count. <para>主组描述符表块数。</para></param>
    /// <param name="metaPerGroup">Per-group metadata block demand outside group zero. <para>除组 0 外每组的元数据块需求。</para></param>
    /// <param name="totalDataBlocks">Total planned data block demand. <para>规划的数据块总需求。</para></param>
    /// <param name="journalBlocks">Journal block count. <para>日志块数。</para></param>
    /// <param name="descsPerGdtBlock">Group descriptors per descriptor table block. <para>每个描述符表块容纳的组描述符数量。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes. <para>文件系统块大小（字节）。</para></param>
    /// <param name="requestedBlocks">Explicitly requested capacity in blocks, or <see langword="null"/> when the capacity is derived. <para>显式请求的容量（块数）；容量为派生值时为 <see langword="null"/>。</para></param>
    /// <returns>The reserved GDT size and the reservation totals derived from the final group count. <para>由最终组数推导的保留 GDT 大小与预留总量。</para></returns>
    private static GroupReservation ComputeGroupReservation(int groupCount, int blocksPerGroup, long gdtBlocks, long metaPerGroup, long totalDataBlocks, int journalBlocks, int descsPerGdtBlock, int blockSize, long? requestedBlocks)
    {
        var fsBlocks = requestedBlocks ?? (long)groupCount * blocksPerGroup;
        var maxGrowthBlocks = fsBlocks < GrowthExpansionThresholdBlocks ? fsBlocks * 1024 : Max32BitBlockCount;
        var reservedGroups = (maxGrowthBlocks + blocksPerGroup - 1) / blocksPerGroup;
        var reservedGdtBlocks = (int)Math.Min(blockSize / 4, (reservedGroups + descsPerGdtBlock - 1) / descsPerGdtBlock);

        var backupGroupCount = BackupSuperblockGroups(groupCount).Count();
        var group0Metadata = 1L + gdtBlocks + reservedGdtBlocks + metaPerGroup;
        var totalBlocksNeeded = group0Metadata + totalDataBlocks + journalBlocks
            + (groupCount - 1) * metaPerGroup + backupGroupCount * (1 + gdtBlocks + reservedGdtBlocks);

        return new GroupReservation(reservedGdtBlocks, backupGroupCount, group0Metadata, totalBlocksNeeded);
    }

    /// <summary>
    /// Builds the block group list with per-group metadata block placement for group zero, backup groups, and ordinary groups, and reserves the resize inode double-indirect block in group zero.
    /// <para>构建块组列表（含组 0、备份组与普通组三种元数据布局分支），并在组 0 中为 resize inode 的二级间接块预留位置。</para>
    /// </summary>
    /// <param name="geometry">Computed group geometry driving the layout. <para>驱动布局的块组几何。</para></param>
    /// <returns>The group list, the backup superblock group set, and the reserved double-indirect block number (-1 when no reserved GDT exists). <para>块组列表、备份超级块组集合，以及预留的二级间接块号（无保留 GDT 时为 -1）。</para></returns>
    private static (List<GroupInfo> Groups, HashSet<int> BackupGroups, long ResizeDindirBlock) CreateGroups(GroupGeometry geometry)
    {
        var blocksPerGroup = geometry.BlocksPerGroup;
        var groupCount = geometry.GroupCount;
        var gdtBlocks = geometry.GdtBlocks;
        var reservedGdtBlocks = geometry.ReservedGdtBlocks;
        var inodeTableBlocks = geometry.InodeTableBlocks;
        var inodesPerGroup = geometry.InodesPerGroup;
        var totalBlocks = geometry.TotalBlocks;

        var groups = new List<GroupInfo>();
        var backupGroups = new HashSet<int>(BackupSuperblockGroups(groupCount));

        for (var g = 0; g < groupCount; g++)
        {
            var group = new GroupInfo { GroupIndex = g };
            var groupStart = (long)g * blocksPerGroup;

            if (g == 0)
            {

                group.BlockBitmapBlock = groupStart + 1 + gdtBlocks + reservedGdtBlocks;
                group.InodeBitmapBlock = group.BlockBitmapBlock + 1;
                group.InodeTableBlock = group.InodeBitmapBlock + 1;
                group.FirstDataBlock = group.InodeTableBlock + inodeTableBlocks;
            }
            else if (backupGroups.Contains(g))
            {

                group.BlockBitmapBlock = groupStart + 1 + gdtBlocks + reservedGdtBlocks;
                group.InodeBitmapBlock = group.BlockBitmapBlock + 1;
                group.InodeTableBlock = group.InodeBitmapBlock + 1;
                group.FirstDataBlock = group.InodeTableBlock + inodeTableBlocks;
            }
            else
            {

                group.BlockBitmapBlock = groupStart;
                group.InodeBitmapBlock = groupStart + 1;
                group.InodeTableBlock = groupStart + 2;
                group.FirstDataBlock = groupStart + 2 + inodeTableBlocks;
            }

            group.BlocksInGroup = Math.Min(blocksPerGroup, totalBlocks - groupStart);
            group.InodesInGroup = inodesPerGroup;
            if (group.FirstDataBlock >= groupStart + blocksPerGroup)
                throw new InvalidOperationException("EXT4 group metadata leaves no room for file data.");
            groups.Add(group);
        }

        long resizeDindirBlock = -1;
        if (reservedGdtBlocks > 0)
        {
            resizeDindirBlock = groups[0].FirstDataBlock;
            groups[0].ExtraUsedBlocks.Add(resizeDindirBlock);
            groups[0].FirstDataBlock++;

            geometry.UsedBlocks++;
        }

        return (groups, backupGroups, resizeDindirBlock);
    }

    /// <summary>
    /// Writes the fully planned image: sizes and clears the output region, writes group metadata, then writes inodes and data, and finally positions the stream at the image end.
    /// <para>写出完整规划的镜像：调整并清零输出区域，写出组元数据，再写出 inode 与数据，最后把流定位到镜像末尾。</para>
    /// </summary>
    /// <param name="output">Writable stream receiving the image. <para>接收镜像的可写流。</para></param>
    /// <param name="baseOffset">Stream offset the image starts at. <para>镜像起始的流偏移。</para></param>
    /// <param name="options">Build options supplying sizes, checksum, and superblock fields. <para>提供大小、校验和与超级块字段的构建选项。</para></param>
    /// <param name="freshDestination">Whether the destination region may be resized without preserving prior content. <para>目标区域是否可以不保留既有内容地调整长度。</para></param>
    /// <param name="nodes">Planned nodes written as inodes and data. <para>作为 inode 与数据写出的已规划节点。</para></param>
    /// <param name="groups">Constructed block groups. <para>已构建的块组。</para></param>
    /// <param name="backupGroups">Backup superblock group set. <para>备份超级块组集合。</para></param>
    /// <param name="geometry">Computed group geometry. <para>计算得到的块组几何。</para></param>
    /// <param name="uuid">Filesystem UUID. <para>文件系统 UUID。</para></param>
    /// <param name="journalStart">First block of the journal area. <para>日志区域起始块。</para></param>
    /// <param name="sharedBlocks">Whether the read-only shared-blocks feature is active. <para>只读相同块共享特性是否生效。</para></param>
    /// <param name="sharedPlan">Active shared-block plan, or <see langword="null"/> when duplicate sharing is disabled. <para>生效的相同块共享计划；未启用共享时为 <see langword="null"/>。</para></param>
    /// <param name="resizeDindirBlock">Reserved resize inode double-indirect block number. <para>预留的 resize inode 二级间接块号。</para></param>
    private static void WriteImage(Stream output, long baseOffset, Ext4Options options, bool freshDestination, List<Node> nodes, List<GroupInfo> groups, HashSet<int> backupGroups, GroupGeometry geometry, byte[] uuid, long journalStart, bool sharedBlocks, SharedBlockPlan? sharedPlan, long resizeDindirBlock)
    {
        var blockSize = options.BlockSize;
        var totalBlocks = geometry.TotalBlocks;

        var finalSize = checked(totalBlocks * blockSize);
        var requiredEnd = checked(baseOffset + finalSize);
        var oldLength = output.Length;
        if (freshDestination || requiredEnd > oldLength
            || options.CapacityMode == Ext4CapacityMode.Minimize && requiredEnd < oldLength)
        {
            output.SetLength(requiredEnd);
        }
        if (!freshDestination)
        {
            var overlapLength = Math.Max(0, Math.Min(oldLength, requiredEnd) - baseOffset);
            ZeroRegion(output, baseOffset, overlapLength);
        }

        WriteGroupMetadata(output, baseOffset, options, nodes, groups, backupGroups, geometry, uuid, journalStart,
            sharedBlocks, resizeDindirBlock);
        WriteInodesAndData(output, baseOffset, options, nodes, groups, geometry, uuid, journalStart, sharedPlan);

        output.Position = checked(baseOffset + finalSize);
    }

    /// <summary>
    /// Writes the per-group bitmaps and descriptors, the superblock with its feature patches and checksum, the backup superblock copies, the group descriptor table with its backups, and the resize inode metadata.
    /// <para>写出各组位图与组描述符、带特性位修补和校验和的超级块、超级块备份副本、组描述符表及其备份，以及 resize inode 元数据。</para>
    /// </summary>
    /// <param name="output">Writable stream receiving the image. <para>接收镜像的可写流。</para></param>
    /// <param name="baseOffset">Stream offset the image starts at. <para>镜像起始的流偏移。</para></param>
    /// <param name="options">Build options supplying sizes, checksum, and superblock fields. <para>提供大小、校验和与超级块字段的构建选项。</para></param>
    /// <param name="nodes">Planned nodes whose xattr and project usage patch superblock features. <para>其 xattr 与 project 用量会修补超级块特性位的已规划节点。</para></param>
    /// <param name="groups">Constructed block groups. <para>已构建的块组。</para></param>
    /// <param name="backupGroups">Backup superblock group set. <para>备份超级块组集合。</para></param>
    /// <param name="geometry">Computed group geometry. <para>计算得到的块组几何。</para></param>
    /// <param name="uuid">Filesystem UUID. <para>文件系统 UUID。</para></param>
    /// <param name="journalStart">First block of the journal area. <para>日志区域起始块。</para></param>
    /// <param name="sharedBlocks">Whether the read-only shared-blocks feature is active. <para>只读相同块共享特性是否生效。</para></param>
    /// <param name="resizeDindirBlock">Reserved resize inode double-indirect block number. <para>预留的 resize inode 二级间接块号。</para></param>
    private static void WriteGroupMetadata(Stream output, long baseOffset, Ext4Options options, List<Node> nodes, List<GroupInfo> groups, HashSet<int> backupGroups, GroupGeometry geometry, byte[] uuid, long journalStart, bool sharedBlocks, long resizeDindirBlock)
    {
        var blockSize = options.BlockSize;
        var inodeSize = options.InodeSize;
        var enableMetadataCsum = options.EnableMetadataCsum;
        var blocksPerGroup = geometry.BlocksPerGroup;
        var groupCount = geometry.GroupCount;
        var gdtBlocks = geometry.GdtBlocks;
        var reservedGdtBlocks = geometry.ReservedGdtBlocks;
        var inodeTableBlocks = geometry.InodeTableBlocks;
        var inodesPerGroup = geometry.InodesPerGroup;
        var journalBlocks = geometry.JournalBlocks;
        var includeJournal = geometry.IncludeJournal;
        var totalBlocks = geometry.TotalBlocks;
        var usedBlocks = geometry.UsedBlocks;

        var bitmaps = new List<(byte[] Block, byte[] Inode)>(groupCount);
        var gdtBuffer = new byte[checked((int)(gdtBlocks * blockSize))];
        var totalFreeInodes = 0L;
        for (var g = 0; g < groupCount; g++)
        {
            var group = groups[g];
            var blockBitmap = new byte[blockSize];
            WriteBlockBitmapForGroup(blockBitmap, group, blocksPerGroup, groupCount, gdtBlocks, reservedGdtBlocks, inodeTableBlocks, backupGroups);
            var inodeBitmap = new byte[blockSize];
            WriteInodeBitmapForGroup(inodeBitmap, group, blockSize, inodeSize, includeJournal ? JournalInodeNumber : 0u);
            bitmaps.Add((blockBitmap, inodeBitmap));
            totalFreeInodes += CountFreeInodes(inodeBitmap, group.InodesInGroup);

            var desc = gdtBuffer.AsSpan(g * 64, 64);
            WriteGroupDescriptor(desc, group, g, uuid, blockBitmap, inodeBitmap, enableMetadataCsum, gdtBlocks, reservedGdtBlocks, inodeTableBlocks, blocksPerGroup, backupGroups);
        }

        var totalInodes = (long)inodesPerGroup * groupCount;
        var superblock = new byte[1024];

        Span<byte> journalIBlock = stackalloc byte[60];
        long journalSize = 0;
        if (includeJournal)
        {
            journalSize = (long)journalBlocks * blockSize;
            WriteExtentHeader(journalIBlock, 1, 0, 4);
            WriteExtentEntry(journalIBlock, 12, (0u, (ulong)journalStart, (ushort)journalBlocks));
        }

        WriteSuperblock(superblock, blockSize, inodeSize, (uint)inodesPerGroup, totalBlocks, usedBlocks,
            (int)Math.Max(0, totalInodes - totalFreeInodes), uuid, blocksPerGroup, options.EnableMetadataCsum,
            includeJournal ? JournalInodeNumber : 0u, includeJournal, reservedGdtBlocks, journalIBlock, journalSize, options.VolumeName,
            options.ReadOnly, sharedBlocks, options.CasefoldEncoding, options.CasefoldEncodingFlags);
        if (nodes.Any(static node => node.AttributeValue is not null))
        {
            WriteU32(superblock, SbFeatureIncompat, BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(SbFeatureIncompat)) | FeatureIncompatEaInode);
        }
        if (nodes.Any(static node => node.SourceMetadata?.ProjectId is > 0))
        {
            WriteU32(superblock, SbFeatureRoCompat, BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(SbFeatureRoCompat)) | FeatureRoCompatProject);
        }
        WriteU32(superblock, SbChecksum, ComputeCrc32(superblock.AsSpan(0, SbChecksum), Crc32c.InitialSeed));
        WriteAt(output, baseOffset, 1024, superblock, superblock.Length);

        foreach (var bg in BackupSuperblockGroups(groupCount))
        {
            WriteU16(superblock, SbBlockGroupNr, checked((ushort)bg));
            WriteU32(superblock, SbChecksum, ComputeCrc32(superblock.AsSpan(0, SbChecksum), Crc32c.InitialSeed));
            WriteAt(output, baseOffset, (long)bg * blocksPerGroup * blockSize, superblock, superblock.Length);
        }

        WriteAt(output, baseOffset, blockSize, gdtBuffer, gdtBuffer.Length);

        foreach (var bg in BackupSuperblockGroups(groupCount))
        {
            WriteAt(output, baseOffset, bg * blocksPerGroup * blockSize + blockSize, gdtBuffer, gdtBuffer.Length);
        }

        if (reservedGdtBlocks > 0)
        {
            WriteResizeInodeMetadata(output, baseOffset, groups, blocksPerGroup, groupCount,
                gdtBlocks, reservedGdtBlocks, blockSize, inodeSize, inodesPerGroup, uuid,
                enableMetadataCsum, resizeDindirBlock);
        }

        for (var g = 0; g < groupCount; g++)
        {
            var group = groups[g];
            var (blockBitmap, inodeBitmap) = bitmaps[g];
            WriteAt(output, baseOffset, group.BlockBitmapBlock * blockSize, blockBitmap, blockBitmap.Length);
            WriteAt(output, baseOffset, group.InodeBitmapBlock * blockSize, inodeBitmap, inodeBitmap.Length);
        }
    }

    /// <summary>
    /// Writes the inode table entries, the journal area and its inode, the shared unique blocks, and the node data blocks.
    /// <para>写出 inode 表项、日志区域及其 inode、共享的唯一块，以及各节点的数据块。</para>
    /// </summary>
    /// <param name="output">Writable stream receiving the image. <para>接收镜像的可写流。</para></param>
    /// <param name="baseOffset">Stream offset the image starts at. <para>镜像起始的流偏移。</para></param>
    /// <param name="options">Build options supplying sizes and checksum settings. <para>提供大小与校验和设置的构建选项。</para></param>
    /// <param name="nodes">Planned nodes written as inodes and data. <para>作为 inode 与数据写出的已规划节点。</para></param>
    /// <param name="groups">Constructed block groups. <para>已构建的块组。</para></param>
    /// <param name="geometry">Computed group geometry. <para>计算得到的块组几何。</para></param>
    /// <param name="uuid">Filesystem UUID. <para>文件系统 UUID。</para></param>
    /// <param name="journalStart">First block of the journal area. <para>日志区域起始块。</para></param>
    /// <param name="sharedPlan">Active shared-block plan, or <see langword="null"/> when duplicate sharing is disabled. <para>生效的相同块共享计划；未启用共享时为 <see langword="null"/>。</para></param>
    private static void WriteInodesAndData(Stream output, long baseOffset, Ext4Options options, List<Node> nodes, List<GroupInfo> groups, GroupGeometry geometry, byte[] uuid, long journalStart, SharedBlockPlan? sharedPlan)
    {
        var blockSize = options.BlockSize;
        var inodeSize = options.InodeSize;
        var inodesPerGroup = geometry.InodesPerGroup;
        var journalBlocks = geometry.JournalBlocks;
        var includeJournal = geometry.IncludeJournal;

        foreach (var node in nodes)
        {
            WriteInode(output, baseOffset, node, blockSize, inodeSize, groups, inodesPerGroup, uuid, options.EnableMetadataCsum);
        }

        if (includeJournal)
        {
            WriteJournalArea(output, baseOffset, journalStart, journalBlocks, blockSize, uuid);
            WriteJournalInode(output, baseOffset, groups, inodesPerGroup, blockSize, inodeSize, uuid,
                journalStart, journalBlocks, options.EnableMetadataCsum);
        }

        if (sharedPlan is not null)
        {
            sharedPlan.ValidateSources(nodes);
            sharedPlan.WriteUniqueBlocks(output, baseOffset);
        }

        foreach (var node in nodes)
        {
            if (sharedPlan is null || !IsBlockSharingCandidate(node))
            {
                WriteNodeData(output, baseOffset, node, blockSize);
            }
        }
    }

    /// <summary>
    /// Enumerates backup superblock groups: group 1 and powers of 3, 5, or 7 below the group count.
    /// <para>枚举备份超级块所在组：组 1 及小于总组数的 3、5、7 的幂次组。</para>
    /// </summary>
    private static IEnumerable<int> BackupSuperblockGroups(int groupCount)
    {

        if (groupCount <= 1) yield break;
        yield return 1;
        for (var p = 3; p < groupCount; p *= 3) yield return p;
        for (var p = 5; p < groupCount; p *= 5) yield return p;
        for (var p = 7; p < groupCount; p *= 7) yield return p;
    }

    /// <summary>
    /// Reserves the journal, then allocates extents and tree blocks without crossing group metadata.
    /// <para>预留日志后分配 extent 和索引块，避开各块组的元数据。</para>
    /// </summary>
    private static bool AllocateDataBlocksMultiGroup(List<Node> nodes, List<GroupInfo> groups, int blocksPerGroup, int blockSize, int journalBlocks, out long journalStart)
    {
        journalStart = -1;
        var groupIndex = 0;
        var cursor = groups[0].FirstDataBlock;
        if (journalBlocks > 0)
        {
            if (journalBlocks > groups[0].BlocksInGroup - cursor)
                throw new InvalidOperationException("EXT4 group zero cannot hold its metadata and internal journal.");
            journalStart = cursor;
            cursor += journalBlocks;
            groups[0].UsedBlocks += journalBlocks;
        }

        bool EnsureSpace()
        {
            while (cursor >= (long)groupIndex * blocksPerGroup + groups[groupIndex].BlocksInGroup)
            {
                if (++groupIndex >= groups.Count) return false;
                cursor = groups[groupIndex].FirstDataBlock;
            }
            return true;
        }

        foreach (var node in nodes)
        {
            node.Extents.Clear();
            if (node.ExternalXattrs is not null)
            {
                if (!EnsureSpace())
                {
                    return false;
                }
                node.XattrBlock = cursor++;
                groups[groupIndex].UsedBlocks++;
            }
            foreach (var run in node.Runs)
            {
                var logical = run.LogicalStart;
                var remaining = run.Count;
                while (remaining > 0)
                {
                    if (!EnsureSpace()) return false;
                    var available = (long)groupIndex * blocksPerGroup + groups[groupIndex].BlocksInGroup - cursor;
                    var count = checked((ushort)Math.Min(Math.Min(remaining, available), MaxExtentLength));
                    node.Extents.Add((checked((uint)logical), checked((ulong)cursor), count));
                    groups[groupIndex].UsedBlocks += count;
                    cursor += count;
                    logical += count;
                    remaining -= count;
                }
            }

            var epb = EntriesPerBlock(blockSize);
            var leaves = node.Extents.Count > 4 ? (node.Extents.Count + epb - 1) / epb : 0;
            node.LeafBlocks = new long[leaves];
            node.IndexBlocks = leaves > 4 ? new long[(leaves + epb - 1) / epb] : [];
            for (var i = 0; i < node.LeafBlocks.Length; i++)
            {
                if (!EnsureSpace()) return false;
                node.LeafBlocks[i] = cursor++;
                groups[groupIndex].UsedBlocks++;
            }
            for (var i = 0; i < node.IndexBlocks.Length; i++)
            {
                if (!EnsureSpace()) return false;
                node.IndexBlocks[i] = cursor++;
                groups[groupIndex].UsedBlocks++;
            }

            var gi = checked((int)((node.Inode - 1) / (uint)groups[0].InodesInGroup));
            groups[gi].UsedInodes++;
            groups[gi].Nodes.Add(node);
            if (node.IsDirectory) groups[gi].UsedDirectories++;
        }
        return true;
    }

    private static void ZeroRegion(Stream output, long offset, long length)
    {
        var zeros = new byte[1 << 16];
        output.Position = offset;
        while (length > 0)
        {
            var count = (int)Math.Min(zeros.Length, length);
            output.Write(zeros, 0, count);
            length -= count;
        }
    }

    private static void WriteAt(Stream output, long baseOffset, long offset, byte[] data, int count)
    {
        output.Position = checked(baseOffset + offset);
        output.Write(data, 0, count);
    }

    private static Node BuildTree(string sourceRoot, IReadOnlyCollection<string> excludedFiles)
    {
        var root = new Node
        {
            Name = string.Empty,
            RelativePath = string.Empty,
            FullPath = sourceRoot,
            IsDirectory = true,
            IsSymlink = false,
        };
        ApplyPreservedAttributes(root);
        Populate(root, excludedFiles);
        return root;
    }

    private static void Populate(Node node, IReadOnlyCollection<string> excludedFiles)
    {
        var children = Directory.EnumerateFileSystemEntries(node.FullPath)
            .Where(path => (UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan.Current is { } plan ? plan.Includes(path) : !UotanStudio.Firmware.Core.Repacking.RepackMetadata.IsMetadataFile(path)))
            .OrderBy(static p => Path.GetFileName(p), StringComparer.Ordinal);
        var excludedSet = excludedFiles.Count == 0
            ? null
            : new HashSet<string>(
                excludedFiles.Select(Path.GetFullPath),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var childPath in children)
        {
            var fullChildPath = Path.GetFullPath(childPath);
            if (excludedSet is not null && excludedSet.Contains(fullChildPath))
            {
                continue;
            }

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
                child.Mode = ModeSymbolicLink;
                child.Mtime = ToUnixSeconds(File.GetLastWriteTimeUtc(childPath));
                child.Ctime = ToUnixSeconds(File.GetCreationTimeUtc(childPath));
            }
            else if (isDirectory)
            {
                child.Size = 0;
                ReadUnixAttributes(childPath, child);
                child.Mode = (ushort)(child.Mode | 0x4000);
            }
            else
            {
                child.Size = new FileInfo(childPath).Length;
                ReadUnixAttributes(childPath, child);
                child.Mode = (ushort)(child.Mode | 0x8000);
            }

            ApplyPreservedAttributes(child);
            node.Children.Add(child);
            if (isDirectory)
            {
                Populate(child, excludedFiles);
            }
        }
    }

    private static void ApplyPreservedAttributes(Node node)
    {
        if (UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan.Current is not { } plan) return;
        var entry = plan.Get(node.FullPath);
        node.Name = UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan.Name(entry.Path);
        node.RelativePath = entry.Path;
        node.Mode = entry.Mode;
        node.Uid = entry.Uid;
        node.Gid = entry.Gid;
        node.Xattrs = entry.Xattrs;
        node.SourceMetadata = entry.Metadata;
        node.IsSymlink = (entry.Mode & 0xF000) == 0xA000;
        if (node.IsSymlink)
        {
            node.SymlinkTarget = entry.SymlinkTarget ?? "";
            node.Size = Encoding.UTF8.GetByteCount(node.SymlinkTarget);
        }
        node.OriginalInode = entry.Inode;
        node.SemanticFlags = entry.Flags & FileSystemEditPlan.Ext4SemanticFlagMask;
        if ((entry.Mode & 0xF000) == 0x8000)
        {
            node.FullPath = plan.ContentSource(node.FullPath);
            node.Size = new FileInfo(node.FullPath).Length;
        }
        node.DeviceId = entry.DeviceId;
        if (node.IsSpecial) node.Size = 0;
    }

    private static uint AssignInodes(Node root, List<Node> nodes)
    {
        var links = new Dictionary<ulong, Node>();
        root.Inode = RootInodeNumber;
        nodes.Add(root);
        var nextInode = FirstUsableInode;
        var maxInode = RootInodeNumber;
        for (var i = 0; i < nodes.Count; i++)
        {
            foreach (var child in nodes[i].Children)
            {
                if (!child.IsDirectory && child.OriginalInode != 0 && links.TryGetValue(child.OriginalInode, out var canonical))
                {
                    child.Inode = canonical.Inode;
                    canonical.LinkCount = checked((ushort)(canonical.LinkCount + 1));
                    continue;
                }
                if (!child.IsDirectory && child.OriginalInode != 0) links.Add(child.OriginalInode, child);
                child.Inode = nextInode;
                maxInode = nextInode;
                nextInode++;
                nodes.Add(child);
            }
        }

        return maxInode;
    }

    /// <summary>
    /// Reads Unix file attributes (mode low12 bits, uid, gid, mtime, ctime).
    /// <para>从源文件 Unix 属性读取 mode（低12位权限位）、uid、gid、mtime、ctime。</para>
    /// </summary>
    private static void ReadUnixAttributes(string path, Node node)
    {
        var fi = new FileInfo(path);
        node.Mtime = ToUnixSeconds(fi.LastWriteTimeUtc);
        node.Ctime = ToUnixSeconds(fi.CreationTimeUtc);
        node.Uid = 0;
        node.Gid = 0;

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var unixMode = File.GetUnixFileMode(path);
                node.Mode = (ushort)(unixMode & (UnixFileMode)0x0FFF);
            }
            catch
            {

                node.Mode = (fi.Attributes & FileAttributes.ReadOnly) != 0 ? (ushort)0x124 : (ushort)0x1A4;
                if ((fi.Attributes & FileAttributes.Directory) != 0)
                {
                    node.Mode = 0x1ED;
                }
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "getfattr",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add("security.selinux");
                psi.ArgumentList.Add("--only-values");
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(path);
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    // Drain stderr so the child cannot block on a full error pipe while we copy
                    // stdout; the label is optional, so only a clean 0-exit stdout value is kept.
                    _ = process.StandardError.ReadToEndAsync();
                    using var ms = new MemoryStream();
                    process.StandardOutput.BaseStream.CopyTo(ms);
                    if (process.WaitForExit(2000) && process.ExitCode == 0 && ms.Length > 0)
                    {
                        node.Xattrs ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
                        node.Xattrs["security.selinux"] = ms.ToArray();
                    }
                }
            }
            catch
            {
                // A missing or failing getfattr is expected on hosts without SELinux xattrs;
                // the inode is still built, just without the security.selinux label.
            }
        }
        else
        {

            node.Mode = (fi.Attributes & FileAttributes.ReadOnly) != 0 ? (ushort)0x124 : (ushort)0x1A4;

            if ((fi.Attributes & FileAttributes.Directory) != 0)
            {
                node.Mode = 0x1ED;
            }
        }
    }

    private static uint ToUnixSeconds(DateTime utc)
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var span = utc - epoch;
        return span.TotalSeconds > 0 ? (uint)span.TotalSeconds : 0;
    }

    private static void PlanDirectoryBlocks(List<Node> nodes, int blockSize, byte[] uuid, bool enableMetadataCsum)
    {

        var hashSeed = new uint[4];
        foreach (var node in nodes)
        {
            if (!node.IsDirectory)
            {
                continue;
            }

            var linearBlocks = BuildDirectoryBlocks(node, blockSize, uuid, enableMetadataCsum);
            if (linearBlocks.Count == 1 || (node.SemanticFlags & 0x40000000) != 0)
            {
                node.DirectoryBlocks = linearBlocks;
                node.Size = (long)linearBlocks.Count * blockSize;
                node.DataBlockCount = linearBlocks.Count;
                node.Runs.Add((0, linearBlocks.Count));
                continue;
            }

            var children = new List<(string Name, uint Inode, byte FileType)>(node.Children.Count);
            foreach (var child in node.Children)
            {

                children.Add((child.Name, child.Inode, FileTypeOf(child)));
            }

            var plan = Ext4Htree.BuildHtree(
                node.Inode,
                node.Parent?.Inode ?? node.Inode,
                children,
                blockSize,
                hashSeed,
                enableMetadataCsum,
                uuid, node.SourceMetadata?.Generation ?? 0);

            var blocks = new List<byte[]>(1 + plan.LeafBlocks.Count + plan.IndexBlocks.Count) { plan.RootBlock };
            blocks.AddRange(plan.LeafBlocks);
            blocks.AddRange(plan.IndexBlocks);

            node.DirectoryBlocks = blocks;
            node.Size = (long)blocks.Count * blockSize;
            node.DataBlockCount = blocks.Count;
            node.Runs.Add((0, blocks.Count));
            node.IsIndexed = true;
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

            node.DataBlockCount = totalBlocks;
            node.Runs.Add((0, totalBlocks));
        }

        var filesToScan = nodes.Where(n => enableSparse && n.AttributeValue is null && !n.IsSymlink && !n.IsInlineData && !n.IsDirectory && n.Size > 0).ToList();
        if (filesToScan.Count > 0)
        {
            Core.FirmwareParallelism.ForEach(filesToScan, node =>
            {
                node.HoleBlocks = DetectHoles(node.FullPath, node.Size, blockSize);
            });

            foreach (var node in filesToScan)
            {
                var totalBlocks = (node.Size + blockSize - 1) / blockSize;
                node.DataBlockCount = totalBlocks - (node.HoleBlocks?.Count ?? 0);
                node.Runs.Clear();
                node.Runs.AddRange(BuildRuns(totalBlocks, node.HoleBlocks));
            }
        }
    }

    private static void PlanLeafBlocks(List<Node> nodes, int blockSize)
    {
        var epb = EntriesPerBlock(blockSize);
        foreach (var node in nodes)
        {
            node.LeafBlocks = [];
            node.IndexBlocks = [];
            if (node.Runs.Count == 0 || node.IsInlineData)
            {
                continue;
            }

            var extentCount = (int)node.Runs.Sum(static run => (run.Count + MaxExtentLength - 1) / MaxExtentLength);

            if (extentCount <= 4)
            {

                node.ExtentTreeDepth = 0;
                continue;
            }

            var leafCount = (extentCount + epb - 1) / epb;
            if (leafCount <= 4)
            {

                node.ExtentTreeDepth = 1;
                node.LeafBlocks = new long[leafCount];
            }
            else
            {

                node.ExtentTreeDepth = 2;
                node.LeafBlocks = new long[leafCount];
                var indexCount = (leafCount + epb - 1) / epb;
                node.IndexBlocks = new long[indexCount];
            }
        }
    }

    /// <summary>
    /// Builds the linear dirent blocks of one directory. The "." and ".." records are synthesised here,
    /// so the dirent charset rule is applied to the child names as they are collected and only the
    /// length rule is applied to the records while they are packed.
    /// <para>构建单个目录的线性目录项块。“.” 与 “..” 记录在这里合成，因此目录项字符集规则在收集子项名称时
    /// 就已应用，打包记录时只应用长度规则。</para>
    /// </summary>
    private static List<byte[]> BuildDirectoryBlocks(Node dir, int blockSize, byte[] uuid, bool enableMetadataCsum)
    {
        var entries = new List<(string Name, byte Type, uint Inode)>
        {
            (".", Ext4DirectoryEntry.TypeDirectory, dir.Inode),
            ("..", Ext4DirectoryEntry.TypeDirectory, dir.Parent?.Inode ?? dir.Inode),
        };
        foreach (var child in dir.Children)
        {
            FileSystemNameLimits.ValidateEntryName(child.Name, "EXT4");
            entries.Add((child.Name, FileTypeOf(child), child.Inode));
        }

        var tailSize = enableMetadataCsum ? DirEntryTailSize : 0;
        var endCap = blockSize - tailSize;
        var blocks = new List<byte[]>();
        var block = new byte[blockSize];
        var offset = 0;
        var lastEntryOffset = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            var (name, fileType, inode) = entries[i];
            FileSystemNameLimits.Validate(name, "EXT4");
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var naturalLength = (8 + nameBytes.Length + 3) & ~3;
            if (lastEntryOffset >= 0 && offset + naturalLength > endCap)
            {
                CloseDirectoryBlock(blocks, ref block, blockSize, tailSize, endCap, lastEntryOffset, dir.Inode, uuid, dir.SourceMetadata?.Generation ?? 0);
                offset = 0;
                lastEntryOffset = -1;
            }

            WriteU32(block, offset, inode);
            WriteU16(block, offset + 4, (ushort)naturalLength);

            block[offset + 6] = (byte)nameBytes.Length;
            block[offset + 7] = fileType;
            nameBytes.CopyTo(block.AsSpan(offset + 8));
            lastEntryOffset = offset;
            offset += naturalLength;
        }

        if (lastEntryOffset >= 0)
        {
            CloseDirectoryBlock(blocks, ref block, blockSize, tailSize, endCap, lastEntryOffset, dir.Inode, uuid, dir.SourceMetadata?.Generation ?? 0);
        }

        return blocks;
    }

    /// <summary>
    /// Closes one directory block: stretches the last real entry's rec_len up to the dir_tail and, when enabled, appends the 12-byte dir_entry_tail checksum structure.
    /// <para>关闭一个目录块：将最后一条真实目录项的 rec_len 延伸至 dir_tail 前，需要时写入 12 字节 dir_entry_tail 校验和尾部。</para>
    /// </summary>
    private static void CloseDirectoryBlock(
        List<byte[]> blocks, ref byte[] block, int blockSize, int tailSize, int endCap,
        int lastEntryOffset, uint dirInode, byte[] uuid, uint generation)
    {
        WriteU16(block, lastEntryOffset + 4, (ushort)(endCap - lastEntryOffset));
        if (tailSize > 0)
        {
            WriteDirectoryTail(block, endCap, dirInode, uuid, generation);
        }

        blocks.Add(block);
        block = new byte[blockSize];
    }

    /// <summary>
    /// Writes the directory block tail with its CRC32c checksum.
    /// <para>写入目录块尾部结构及其 CRC32c 校验和。</para>
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private static void WriteDirectoryTail(byte[] block, int endCap, uint dirInode, byte[] uuid, uint generation)
    {
        WriteU32(block, endCap, 0);
        WriteU16(block, endCap + 4, 12);
        block[endCap + 6] = 0x00;
        block[endCap + 7] = 0xDE;

        var crc = ComputeCrc32(uuid, Crc32c.InitialSeed);
        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, dirInode);
        crc = ComputeCrc32Continue(crc, le);
        BinaryPrimitives.WriteUInt32LittleEndian(le, generation);
        crc = ComputeCrc32Continue(crc, le);
        crc = ComputeCrc32Continue(crc, block.AsSpan(0, endCap));
        WriteU32(block, endCap + 8, crc);
    }

    private static byte FileTypeOf(Node node)
        => node.IsDirectory ? Ext4DirectoryEntry.TypeDirectory : node.IsSymlink ? Ext4DirectoryEntry.TypeSymlink : (byte)((node.Mode & InodeTypeBits.FormatMask) switch
        {
            InodeTypeBits.CharDevice => 3,
            InodeTypeBits.BlockDevice => 4,
            InodeTypeBits.Fifo => 5,
            InodeTypeBits.Socket => 6,
            _ => Ext4DirectoryEntry.TypeRegular
        });

    private static void WriteSuperblock(
        Span<byte> block, int blockSize, int inodeSize, uint inodesPerGroup, long totalBlocks, long usedBlocks, int usedInodes, byte[] uuid, int blocksPerGroup, bool enableMetadataCsum, uint journalInum, bool hasJournal,
        int reservedGdtBlocks, ReadOnlySpan<byte> journalIBlock, long journalSize, string volumeName, bool readOnly,
        bool sharedBlocks, ushort? casefoldEncoding, ushort casefoldEncodingFlags)
    {
        block.Clear();
        var totalInodes = inodesPerGroup * ((totalBlocks + blocksPerGroup - 1) / blocksPerGroup);
        WriteU32(block, 0x00, (uint)Math.Min(totalInodes, uint.MaxValue));
        WriteU32(block, 0x04, (uint)totalBlocks);

        var reservedBlocks = totalBlocks / 20;
        WriteU32(block, 0x08, (uint)reservedBlocks);
        WriteU32(block, 0x0C, (uint)(totalBlocks - usedBlocks));
        WriteU32(block, 0x10, (uint)Math.Max(0, totalInodes - usedInodes));
        WriteU32(block, 0x14, 0);
        WriteU32(block, 0x18, (uint)(Log2(blockSize) - 10));
        WriteU32(block, 0x1C, (uint)(Log2(blockSize) - 10));
        WriteU32(block, 0x20, (uint)blocksPerGroup);
        WriteU32(block, 0x24, (uint)blocksPerGroup);
        WriteU32(block, 0x28, inodesPerGroup);
        WriteU32(block, 0x2C, FixedTimestamp);
        WriteU32(block, 0x30, FixedTimestamp);
        WriteU16(block, 0x34, 0);
        WriteU16(block, 0x36, 0xFFFF);
        WriteU16(block, 0x38, 0xEF53);
        WriteU16(block, 0x3A, 1);
        WriteU16(block, 0x3C, 1);
        WriteU16(block, 0x3E, 0);
        WriteU32(block, 0x40, FixedTimestamp);

        WriteU32(block, 0x44, 0);
        WriteU32(block, 0x48, 0);
        WriteU32(block, 0x4C, 1);
        WriteU16(block, 0x50, 0);
        WriteU16(block, 0x52, 0);
        WriteU32(block, 0x54, FirstUsableInode);
        WriteU16(block, 0x58, (ushort)inodeSize);
        WriteU16(block, SbBlockGroupNr, 0);

        WriteU32(block, 0x5C, FeatureCompatExtAttr | FeatureCompatResizeInodeBit | FeatureCompatDirIndex | (hasJournal ? FeatureCompatHasJournal : 0u));
        WriteU32(block, SbFeatureIncompat, FeatureIncompatFileType | FeatureIncompatExtents | FeatureIncompat64Bit | FeatureIncompatFlexBg
            | (casefoldEncoding is not null ? FeatureIncompatCasefold : 0u));
        WriteU32(block, SbFeatureRoCompat, FeatureRoCompatSparseSuper | FeatureRoCompatLargeFile | FeatureRoCompatHugeFile
            | FeatureRoCompatDirNlink | FeatureRoCompatExtraIsize
            | (enableMetadataCsum ? FeatureRoCompatMetadataChecksum : 0u)
            | (readOnly ? FeatureRoCompatReadOnly : 0u) | (sharedBlocks ? FeatureRoCompatSharedBlocks : 0u));
        Encoding.UTF8.GetBytes(volumeName, block[0x78..0x88]);
        uuid.AsSpan().CopyTo(block[0x68..0x78]);

        WriteU16(block, 0xCE, checked((ushort)reservedGdtBlocks));
        if (hasJournal)
        {

            WriteU32(block, 0xE0, journalInum);

            journalIBlock.CopyTo(block[0x10C..]);
            WriteU32(block, 0x148, (uint)((ulong)journalSize >> 32));
            WriteU32(block, 0x14C, (uint)(journalSize & 0xFFFFFFFF));
        }

        block[0xFC] = Ext4Htree.HashVersionHalfMd4;
        block[0xFD] = hasJournal ? (byte)1 : (byte)0;
        WriteU16(block, 0xFE, 64);
        WriteU32(block, 0x100, 0);
        WriteU32(block, 0x104, 0);
        WriteU32(block, 0x108, FixedTimestamp);
        WriteU32(block, 0x150, (uint)(totalBlocks >> 32));
        WriteU32(block, 0x154, (uint)(reservedBlocks >> 32));
        WriteU32(block, 0x158, (uint)((totalBlocks - usedBlocks) >> 32));
        WriteU16(block, 0x15C, Ext4ExtraIsize);
        WriteU16(block, 0x15E, Ext4ExtraIsize);
        WriteU32(block, 0x160, 0);
        if (enableMetadataCsum)
        {
            block[0x175] = 1;
        }
        if (casefoldEncoding is { } encoding)
        {
            WriteU16(block, 0x27C, encoding);
            WriteU16(block, 0x27E, casefoldEncodingFlags);
        }

        var crc = ComputeCrc32(block[..SbChecksum], Crc32c.InitialSeed);
        WriteU32(block, SbChecksum, crc);
    }

    /// <summary>
    /// Yields the e2fsprogs ext2fs_list_backups backup-group sequence for sparse_super (1,3,5,7,9,25,27,49,…) below groupCount — identical to the ascending group-number walk with bg_has_super used by the fsck resize-inode check.
    /// <para>生成 e2fsprogs ext2fs_list_backups 的备份组序列（sparse_super：1,3,5,7,9,25,27,49,…）， 小于 groupCount 时依次产出（与组号升序遍历 + bg_has_super 的校验顺序一致）。</para>
    /// </summary>
    private static IEnumerable<int> ListBackupGroups(int groupCount)
    {
        long three = 1, five = 5, seven = 7;
        while (true)
        {
            var min = three;
            if (five < min)
            {
                min = five;
            }

            if (seven < min)
            {
                min = seven;
            }

            if (min >= groupCount)
            {
                yield break;
            }

            yield return (int)min;
            if (min == three)
            {
                three = min * 3;
            }
            else if (min == five)
            {
                five = min * 5;
            }
            else
            {
                seven = min * 7;
            }
        }
    }

    /// <summary>
    /// Writes the resize inode and reserved GDT blocks with ordered backup pointers.
    /// <para>写入 resize inode 和保留 GDT 块，并按序填入备份指针。</para>
    /// </summary>
    private static void WriteResizeInodeMetadata(
        Stream output, long baseOffset, List<GroupInfo> groups, int blocksPerGroup, int groupCount,
        long gdtBlocks, int reservedGdtBlocks, int blockSize, int inodeSize, int inodesPerGroup, byte[] uuid,
        bool enableMetadataCsum, long dindirBlock)
    {
        var apb = blockSize / 4;

        var dindir = new byte[blockSize];
        for (var i = 0; i < reservedGdtBlocks; i++)
        {
            var slot = (int)((gdtBlocks + i) % apb);
            BinaryPrimitives.WriteUInt32LittleEndian(dindir.AsSpan(slot * 4, 4), (uint)(1 + gdtBlocks + i));
        }

        WriteAt(output, baseOffset, dindirBlock * blockSize, dindir, blockSize);

        var primary = new byte[blockSize];
        for (var j = 0; j < reservedGdtBlocks; j++)
        {
            Array.Clear(primary);
            var primaryBlock = 1 + gdtBlocks + j;
            var slot = 0;
            foreach (var grp in ListBackupGroups(groupCount))
            {
                if (slot >= apb)
                {
                    break;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(
                    primary.AsSpan(slot * 4, 4), (uint)(primaryBlock + (long)grp * blocksPerGroup));
                slot++;
            }

            WriteAt(output, baseOffset, primaryBlock * blockSize, primary, blockSize);
        }

        var inode = new byte[inodeSize];
        var size = checked((long)blockSize * ((long)apb * apb + apb + 12));

        var backupEntryCount = 0;
        foreach (var _ in ListBackupGroups(groupCount))
        {
            backupEntryCount++;
        }

        var blockCount = 1L + reservedGdtBlocks + (long)reservedGdtBlocks * backupEntryCount;
        WriteU16(inode, 0, ModeJournalFile);
        WriteU32(inode, 4, (uint)(size & 0xFFFFFFFF));
        WriteU32(inode, 8, FixedTimestamp);
        WriteU32(inode, 12, FixedTimestamp);
        WriteU32(inode, 16, FixedTimestamp);
        WriteU16(inode, 26, 1);
        WriteU32(inode, 28, (uint)(blockCount * (blockSize / 512)));
        WriteU32(inode, 92, (uint)dindirBlock);
        if (inodeSize >= 256)
        {
            WriteU32(inode, 108, (uint)((ulong)size >> 32));
        }

        if (inodeSize > 128)
        {
            WriteU16(inode, 128, Ext4ExtraIsize);
        }

        if (enableMetadataCsum)
        {
            WriteInodeChecksum(inode, 7, uuid);
        }

        var group = groups[(6 / inodesPerGroup)];
        var inodeOffset = checked(group.InodeTableBlock * blockSize + (long)(6 % inodesPerGroup) * inodeSize);
        WriteAt(output, baseOffset, inodeOffset, inode, inodeSize);
    }

    private static void WriteGroupDescriptor(
        Span<byte> desc, GroupInfo group, int groupIndex, byte[] uuid,
        byte[] blockBitmap, byte[] inodeBitmap, bool enableMetadataCsum, long gdtBlocks, int reservedGdtBlocks, int inodeTableBlocks,
        int blocksPerGroup, HashSet<int>? backupGroups = null)
    {

        desc.Clear();
        WriteU32(desc, 0, (uint)group.BlockBitmapBlock);
        WriteU32(desc, 4, (uint)group.InodeBitmapBlock);
        WriteU32(desc, 8, (uint)group.InodeTableBlock);

        var hasBackupMeta = group.GroupIndex == 0 || backupGroups is not null && backupGroups.Contains(group.GroupIndex);
        var freeBlocks = group.BlocksInGroup
            - ((hasBackupMeta ? 1 + gdtBlocks + reservedGdtBlocks : 0) + 2L + inodeTableBlocks)
            - group.UsedBlocks - group.ExtraUsedBlocks.Count;

        var freeInodes = CountFreeInodes(inodeBitmap, group.InodesInGroup);
        WriteU16(desc, 12, (ushort)freeBlocks);
        WriteU16(desc, 14, (ushort)freeInodes);
        WriteU16(desc, 16, (ushort)group.UsedDirectories);
        WriteU16(desc, 18, 0);
        WriteU32(desc, 20, 0);
        WriteU16(desc, 24, 0);
        WriteU16(desc, 26, 0);
        WriteU16(desc, 28, 0);
        WriteU16(desc, 30, 0);

        WriteU32(desc, 32, (uint)((ulong)group.BlockBitmapBlock >> 32));
        WriteU32(desc, 36, (uint)((ulong)group.InodeBitmapBlock >> 32));
        WriteU32(desc, 40, (uint)((ulong)group.InodeTableBlock >> 32));
        WriteU16(desc, 44, (ushort)(freeBlocks >> 16));
        WriteU16(desc, 46, (ushort)(freeInodes >> 16));
        WriteU16(desc, 48, (ushort)(group.UsedDirectories >> 16));
        WriteU16(desc, 50, 0);
        WriteU32(desc, 52, 0);
        WriteU16(desc, 56, 0);
        WriteU16(desc, 58, 0);

        if (!enableMetadataCsum)
        {
            return;
        }

        var seed = ComputeCrc32(uuid, Crc32c.InitialSeed);
        var inodeBitmapCsumLength = Math.Max(1, group.InodesInGroup / 8);
        var blockCsum = ComputeCrc32(blockBitmap.AsSpan(0, Math.Max(1, blocksPerGroup / 8)), seed);
        var inodeCsum = ComputeCrc32(inodeBitmap.AsSpan(0, inodeBitmapCsumLength), seed);
        WriteU16(desc, 24, (ushort)(blockCsum & 0xFFFF));
        WriteU16(desc, 26, (ushort)(inodeCsum & 0xFFFF));
        WriteU16(desc, 56, (ushort)(blockCsum >> 16));
        WriteU16(desc, 58, (ushort)(inodeCsum >> 16));

        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, (uint)groupIndex);
        var csum = ComputeCrc32(le, seed);
        csum = ComputeCrc32Continue(csum, desc);
        WriteU16(desc, 30, (ushort)(csum & 0xFFFF));
    }

    private static void WriteBlockBitmap(byte[] bitmap, long usedBlocks)
    {
        for (var i = 0L; i < usedBlocks; i++)
        {
            bitmap[(int)(i >> 3)] |= (byte)(1 << (int)(i & 7));
        }
    }

    private static void WriteBlockBitmapForGroup(byte[] bitmap, GroupInfo group, int blocksPerGroup, int groupCount, long gdtBlocks, int reservedGdtBlocks, int inodeTableBlocks, HashSet<int>? backupGroups = null)
    {
        var groupStart = (long)group.GroupIndex * blocksPerGroup;

        if (group.GroupIndex == 0 || (backupGroups is not null && backupGroups.Contains(group.GroupIndex)))
        {
            MarkBlockUsed(bitmap, 0);
            for (var b = 1L; b <= gdtBlocks + reservedGdtBlocks; b++)
            {
                MarkBlockUsed(bitmap, b);
            }
        }

        MarkBlockUsed(bitmap, group.BlockBitmapBlock - groupStart);
        MarkBlockUsed(bitmap, group.InodeBitmapBlock - groupStart);
        for (var b = 0; b < inodeTableBlocks; b++)
        {
            MarkBlockUsed(bitmap, group.InodeTableBlock - groupStart + b);
        }

        foreach (var extra in group.ExtraUsedBlocks)
        {
            MarkBlockUsed(bitmap, extra - groupStart);
        }

        var dataStart = group.FirstDataBlock - groupStart;
        for (var i = 0L; i < group.UsedBlocks; i++)
        {
            MarkBlockUsed(bitmap, dataStart + i);
        }

        for (var bit = group.BlocksInGroup; bit < bitmap.Length * 8L; bit++)
        {
            MarkBlockUsed(bitmap, bit);
        }
    }

    private static void MarkBlockUsed(byte[] bitmap, long localBlock)
    {
        if (localBlock >= 0 && localBlock < bitmap.Length * 8L)
        {
            bitmap[(int)(localBlock >> 3)] |= (byte)(1 << (int)(localBlock & 7));
        }
    }

    private static void WriteInodeBitmapForGroup(byte[] bitmap, GroupInfo group, int blockSize, int inodeSize, uint journalInode = 0)
    {

        bitmap.AsSpan().Clear();

        var inodesPerGroup = group.InodesInGroup;
        var usedLocals = new HashSet<int>();
        foreach (var node in group.Nodes)
        {
            usedLocals.Add((int)((node.Inode - 1) % (uint)inodesPerGroup));
        }

        if (journalInode > 0 && ((journalInode - 1) / (uint)inodesPerGroup) == (uint)group.GroupIndex)
        {
            usedLocals.Add((int)((journalInode - 1) % (uint)inodesPerGroup));
        }

        for (var local = 0; local < inodesPerGroup; local++)
        {
            var isReserved = group.GroupIndex == 0 && local < (int)(FirstUsableInode - 1);
            if (isReserved || usedLocals.Contains(local))
            {
                bitmap[local >> 3] |= (byte)(1 << (local & 7));
            }
        }

        var inodeTableSlots = ((inodesPerGroup + (blockSize / inodeSize) - 1) / (blockSize / inodeSize)) * (blockSize / inodeSize);
        for (var local = inodeTableSlots; local < bitmap.Length * 8; local++)
        {
            bitmap[local >> 3] |= (byte)(1 << (local & 7));
        }
    }

    /// <summary>
    /// Counts the free (cleared) inode bits among the first inodesPerGroup bits.
    /// <para>统计位图前 inodesPerGroup 位中空闲（清 0）的 inode 数。</para>
    /// </summary>
    private static int CountFreeInodes(byte[] inodeBitmap, int inodesPerGroup)
    {
        var free = 0;
        for (var local = 0; local < inodesPerGroup; local++)
        {
            if ((inodeBitmap[local >> 3] & (1 << (local & 7))) == 0)
            {
                free++;
            }
        }

        return free;
    }
}
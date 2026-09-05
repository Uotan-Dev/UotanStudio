using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Ext4.Core;
using UotanStudio.Firmware.Ext4.Journal;

namespace UotanStudio.Firmware.Ext4;

/// <summary>
/// Fully managed ext4 image builder that packs a source directory into a valid ext4
/// filesystem image (4096-byte blocks, 256-byte inodes) with extents, block/inode
/// bitmaps and fast symlinks.
/// <para>全托管 ext4 镜像构建器：将源目录打包为有效的 ext4 文件系统镜像
/// （4096 字节块、256 字节 inode），包含 extent、块/inode 位图与快速符号链接。</para>
/// <para>Layout is computed in a first pass and the image is streamed out in a second pass:
/// the output stream is <see cref="Stream.SetLength(long)"/>-preallocated and each region
/// (superblock, group descriptor, bitmaps, inodes, leaf extent blocks and data blocks) is
/// seeked to and written individually, so no full in-memory image array is ever built.
/// 布局在第一遍扫描中计算，第二遍将镜像流式写出：先对输出流
/// <see cref="Stream.SetLength(long)"/> 预分配，再逐个定位写入超级块、组描述符、
/// 位图、inode、extent 叶块与数据块，全程不构造完整镜像数组。</para>
/// </summary>
public sealed partial class Ext4Builder
{
    private const ushort ModeRegularFile = 0x81A4; // S_IFREG | 0644
    private const ushort ModeDirectory = 0x41ED;   // S_IFDIR | 0755
    private const ushort ModeSymbolicLink = 0xA1FF; // S_IFLNK | 0777
    private const ushort ModeJournalFile = 0x8180;  // S_IFREG | 0600 (mke2fs journal inode)
    private const uint FlagExtents = 0x0008_0000;
    private const uint FlagInlineData = 0x1000_0000;

    /// <summary>ext4 内部日志 inode 号（EXT4_JOURNAL_INO）。<para>Ext4 internal journal inode number (EXT4_JOURNAL_INO).</para></summary>
    private const uint JournalInodeNumber = 8;

    /// <summary>默认内部日志块数（对齐 mke2fs 默认 1024 块日志）。<para>Default internal journal length in blocks (matches mke2fs's default 1024-block journal).</para></summary>
    private const int DefaultJournalBlocks = 1024;

    /// <summary>EXT4_FEATURE_COMPAT_HAS_JOURNAL。<para>EXT4_FEATURE_COMPAT_HAS_JOURNAL.</para></summary>
    private const uint FeatureCompatHasJournal = 0x0004;

    /// <summary>标准 256 字节 inode 扩展区大小（i_extra_isize）。<para>Standard extended inode region size (i_extra_isize).</para></summary>
    private const ushort Ext4ExtraIsize = 28;

    /// <summary>inode 使用高位校验和（i_checksum_hi）所需的最小 extra_isize（EXT4_INODE_CSUM_HI_EXTRA_END）。
    /// <para>Minimum i_extra_isize for the inode to use the high checksum word (EXT4_INODE_CSUM_HI_EXTRA_END).</para></summary>
    private const ushort Ext4InodeChecksumHiExtraEnd = 4;
    private const ushort ExtentMagic = 0xF30A;
    private const uint MaxExtentLength = 32768;
    private const uint FixedTimestamp = 1_700_000_000;
    private const uint RootInodeNumber = 2;
    private const uint FirstUsableInode = 11;

    /// <summary>ext4 dir_entry_tail 大小（metadata_csum 目录块尾部校验和结构）。<para>ext4 dir_entry_tail size (metadata_csum directory-tail checksum structure).</para></summary>
    private const int DirEntryTailSize = 12;

    /// <summary>标准块组大小：8 * blockSize 块（4K 块时为 32768 = 128MB）。
    /// <para>Standard blocks-per-group: 8 * blockSize (32768 for 4K = 128MB).</para></summary>
    private const int BlocksPerGroupMultiplier = 8;

    private static readonly byte[] FixedUuid = [0x53, 0xAA, 0x0F, 0x4D, 0x2C, 0x7A, 0x11, 0xEF, 0x8A, 0xB5, 0x00, 0x0C, 0x29, 0xC0, 0xA1, 0xB2];

    /// <summary>
    /// 每个块组的元数据布局信息。
    /// <para>Per-group metadata layout information.</para>
    /// </summary>
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
    }

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
        public ushort Mode { get; set; }
        public uint Uid { get; set; }
        public uint Gid { get; set; }
        public uint Mtime { get; set; }
        public uint Ctime { get; set; }
        public long DataBlockCount { get; set; }
        public long DataStartBlock { get; set; }
        public long[] LeafBlocks { get; set; } = [];
        public long[] IndexBlocks { get; set; } = [];
        public int ExtentTreeDepth { get; set; }
        public bool IsInlineData { get; set; }
        public bool IsIndexed { get; set; }
        public HashSet<int>? HoleBlocks { get; set; }
        public List<(long LogicalStart, long Count)> Runs { get; } = [];
        public List<byte[]> DirectoryBlocks { get; set; } = [];
        public Dictionary<string, byte[]>? Xattrs { get; set; }
    }

    /// <summary>
    /// Builds an ext4 image from <paramref name="sourceDirectory"/> and returns it as a byte array.
    /// <para>从 <paramref name="sourceDirectory"/> 构建 ext4 镜像并以字节数组返回。</para>
    /// </summary>
    public static byte[] Build(string sourceDirectory, Ext4Options? options = null)
    {
        var effective = Validate(sourceDirectory, options);
        using var image = new MemoryStream();
        BuildCore(sourceDirectory, image, effective, 0, freshDestination: true, excludedFiles: []);
        if (image.TryGetBuffer(out var buffer) && buffer.Offset == 0 && buffer.Count == buffer.Array!.Length)
        {
            return buffer.Array;
        }

        return buffer.AsSpan().ToArray();
    }

    /// <summary>
    /// Builds an ext4 image from <paramref name="sourceDirectory"/> and writes it to <paramref name="outputPath"/>.
    /// <para>从 <paramref name="sourceDirectory"/> 构建 ext4 镜像并写入 <paramref name="outputPath"/>。</para>
    /// </summary>
    public static void Build(string sourceDirectory, string outputPath, Ext4Options? options = null)
    {
        var effective = Validate(sourceDirectory, options);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        // Snapshot the source before creating a destination parent that may be inside it.
        var plannedRoot = BuildTree(Path.GetFullPath(sourceDirectory), [fullOutputPath, temporaryPath]);
        Directory.CreateDirectory(directory);

        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.None))
            {
                BuildCore(sourceDirectory, output, effective, 0, freshDestination: true, excludedFiles: [fullOutputPath, temporaryPath], plannedRoot);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* Cleanup must not replace the build failure. */ }
        }
    }

    /// <summary>
    /// Builds an ext4 image from <paramref name="sourceDirectory"/> and streams it to
    /// <paramref name="output"/>. Seekable streams are filled in place from their current
    /// position; non-seekable streams are built through a bounded temporary file and copied.
    /// <para>从 <paramref name="sourceDirectory"/> 构建 ext4 镜像并流式写入
    /// <paramref name="output"/>。可寻址流从当前位置原位写入；不可寻址流通过有界临时文件构建后复制。</para>
    /// </summary>
    public static void Build(string sourceDirectory, Stream output, Ext4Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        var effective = Validate(sourceDirectory, options);
        var excludedFiles = output is FileStream fileStream ? new[] { Path.GetFullPath(fileStream.Name) } : [];
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"uotan-ext4-{Guid.NewGuid():N}.tmp");
        // Plan before probing SetLength, and exclude staging even when the source is the temp tree.
        var stagingExclusions = excludedFiles.Append(Path.GetFullPath(temporaryPath)).ToArray();
        var plannedRoot = BuildTree(Path.GetFullPath(sourceDirectory), stagingExclusions);
        if (output.CanSeek && SupportsResizeWithoutMutation(output))
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
            // Setting the existing length is a non-mutating capability probe.
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

    private static Ext4Options Validate(string sourceDirectory, Ext4Options? options)
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

        return options;
    }

    private static void BuildCore(string sourceDirectory, Stream output, Ext4Options options, long baseOffset, bool freshDestination, IReadOnlyCollection<string> excludedFiles, Node? plannedRoot = null)
    {
        var blockSize = options.BlockSize;
        var inodeSize = options.InodeSize;

        // 文件系统 UUID 必须先于布局规划确定：目录块校验和（dir_tail）、块/Inode 位图校验和、
        // 组描述符校验和与 inode 校验和全部以 crc32c(~0, uuid) 为种子，须使用写入超级块的同 UUID。
        // The filesystem UUID must be fixed before layout planning: directory-tail, block/inode bitmap,
        // group-descriptor and inode checksums all use crc32c(~0, uuid) as their seed, so they must share
        // the exact UUID that ends up in the superblock.
        // 默认使用固定 UUID 保证可重现构建：相同输入两次 Build 产出逐字节相同的镜像
        //（byte[]/Path/Stream 三种 API 彼此一致）。调用方可通过 options.Uuid 覆盖。
        // A fixed default UUID keeps builds reproducible: two builds of the same input produce
        // byte-identical images (the byte[]/Path/Stream APIs agree). Callers may override via options.Uuid.
        var uuid = options.Uuid is { Length: 16 } ? options.Uuid : FixedUuid;
        var enableMetadataCsum = options.EnableMetadataCsum;

        // ---- First pass: scan and plan the layout (no image allocation). ----
        // 第一遍：扫描并规划布局（不分配镜像）。
        var root = plannedRoot ?? BuildTree(Path.GetFullPath(sourceDirectory), excludedFiles);
        var nodes = new List<Node>();
        var maxInode = AssignInodes(root, nodes);

        PlanDirectoryBlocks(nodes, blockSize, uuid, enableMetadataCsum);
        PlanDataBlocks(nodes, blockSize, options.EnableSparse);
        PlanLeafBlocks(nodes, blockSize);

        // Standard blocks-per-group（4K 块时为 32768 = 128MB）。
        var blocksPerGroup = BlocksPerGroupMultiplier * blockSize;
        var inodesPerInodeBlock = blockSize / inodeSize;
        // 每组 inode 数：至少容纳所有 inode，向上取整到 2 的幂，最小 16。
        var inodesPerGroup = (int)RoundUpPowerOfTwo(Math.Max(maxInode, 16));
        var inodeTableBlocks = (inodesPerGroup + inodesPerInodeBlock - 1) / inodesPerInodeBlock;
        // 每组元数据开销：1 块位图 + 1 inode 位图 + inode 表。
        var metaPerGroup = 2L + inodeTableBlocks;

        // 计算总数据块需求。
        var totalDataBlocks = nodes.Sum(static n => n.DataBlockCount)
            + nodes.Sum(static n => (long)n.LeafBlocks.Length)
            + nodes.Sum(static n => (long)n.IndexBlocks.Length);

        // 内部日志区域块数：默认 1024 块（对齐 mke2fs 的默认 1024 块日志），计入布局预估。
        // Internal journal area in blocks: 1024 by default (mke2fs's default journal size), accounted
        // for in the layout estimate below so the reserved region is never short of space.
        var journalBlocks = options.IncludeJournal ? DefaultJournalBlocks : 0;
        var includeJournal = journalBlocks > 0;

        // 初估组数：至少容纳所有 inode，且容纳所有数据块。
        // 组 0 额外开销：超级块(1 块) + GDT(至少 1 块) + metaPerGroup。
        var gdtBlocks = 1L; // 初始估1块，后续按组数修正
        var group0Extra = 1L + gdtBlocks; // 超级块 + GDT
        var estimatedTotal = group0Extra + metaPerGroup + totalDataBlocks + journalBlocks;
        var groupCount = Math.Max(1, (int)((estimatedTotal + blocksPerGroup - 1) / blocksPerGroup));
        groupCount = Math.Max(groupCount, (int)Math.Ceiling((double)maxInode / inodesPerGroup));

        // 修正 GDT 大小（每组描述符 64 字节，s_desc_size=64）。
        gdtBlocks = Math.Max(1, (long)Math.Ceiling(groupCount * 64.0 / blockSize));

        // 重新计算精确的总块数。
        var group0Metadata = 1L + gdtBlocks + metaPerGroup; // 超级块 + GDT + 组 0 元数据
        var totalBlocksNeeded = group0Metadata + totalDataBlocks + journalBlocks + (groupCount - 1) * metaPerGroup;
        // 确保组数足够。
        if (totalBlocksNeeded > (long)groupCount * blocksPerGroup)
        {
            groupCount = (int)((totalBlocksNeeded + blocksPerGroup - 1) / blocksPerGroup);
            gdtBlocks = Math.Max(1, (long)Math.Ceiling(groupCount * 32.0 / blockSize));
            group0Metadata = 1L + gdtBlocks + metaPerGroup;
            totalBlocksNeeded = group0Metadata + totalDataBlocks + journalBlocks + (groupCount - 1) * metaPerGroup;
        }

        var totalBlocks = (long)groupCount * blocksPerGroup;
        var usedBlocks = totalBlocksNeeded;

        // ---- 构建组信息并分配块。Build group info and allocate blocks. ----
        var groups = new List<GroupInfo>();

        for (var g = 0; g < groupCount; g++)
        {
            var group = new GroupInfo { GroupIndex = g };
            var groupStart = (long)g * blocksPerGroup;

            if (g == 0)
            {
                // 组 0：超级块(偏移1024) + GDT(块1) + 块位图(块1+gdtBlocks) + inode位图 + inode表。
                group.BlockBitmapBlock = groupStart + 1 + gdtBlocks;
                group.InodeBitmapBlock = group.BlockBitmapBlock + 1;
                group.InodeTableBlock = group.InodeBitmapBlock + 1;
                group.FirstDataBlock = group.InodeTableBlock + inodeTableBlocks;
            }
            else
            {
                // 其他组：块位图 + inode 位图 + inode 表。
                group.BlockBitmapBlock = groupStart;
                group.InodeBitmapBlock = groupStart + 1;
                group.InodeTableBlock = groupStart + 2;
                group.FirstDataBlock = groupStart + 2 + inodeTableBlocks;
            }

            group.BlocksInGroup = blocksPerGroup;
            group.InodesInGroup = inodesPerGroup;
            groups.Add(group);
        }

        // 分配数据块到各组，并将日志区域紧跟在所有文件数据之后预留。
        // Allocate node data across groups, then reserve the journal region right after all file data.
        AllocateDataBlocksMultiGroup(nodes, groups, blocksPerGroup, journalBlocks, out var journalStart);
        if (includeJournal)
        {
            // 日志 inode（8 号）位于组 0，占用 1 个 inode。
            // Journal inode (#8) lives in group 0 and consumes one inode slot there.
            groups[0].UsedInodes++;
        }

        // ---- Second pass: stream the image out. ----
        // 第二遍：流式写出镜像。
        var finalSize = checked(totalBlocks * blockSize);
        var requiredEnd = checked(baseOffset + finalSize);
        var oldLength = output.Length;
        if (freshDestination || requiredEnd > oldLength)
        {
            output.SetLength(requiredEnd);
        }
        if (!freshDestination)
        {
            var overlapLength = Math.Max(0, Math.Min(oldLength, requiredEnd) - baseOffset);
            ZeroRegion(output, baseOffset, overlapLength);
        }

        // 先构建各组的块位图与 inode 位图（内存中），以便：
        //   1. 由位图推导空闲 inode 总数（含保留 inode 1..10 的影响）写入超级块；
        //   2. 组描述符中写入 bg_block_bitmap_csum / bg_inode_bitmap_csum（metadata_csum 必需）；
        //   3. 位图在 GDT 写出后原样落盘，确保位图与描述符内容一致。
        // Bitmaps are built up-front so that (1) the superblock's free-inode total is derived from the
        // bitmap (reserved inodes 1..10 included), (2) their CRC32c checksums can be stored in the group
        // descriptors before those are flushed, and (3) the exact same bytes are written to the bitmap
        // blocks afterwards.
        var bitmaps = new List<(byte[] Block, byte[] Inode)>(groupCount);
        var gdtBuffer = new byte[checked((int)(gdtBlocks * blockSize))];
        var totalFreeInodes = 0L;
        for (var g = 0; g < groupCount; g++)
        {
            var group = groups[g];
            var blockBitmap = new byte[blockSize];
            WriteBlockBitmapForGroup(blockBitmap, group, blocksPerGroup, groupCount, gdtBlocks, inodeTableBlocks);
            var inodeBitmap = new byte[blockSize];
            WriteInodeBitmapForGroup(inodeBitmap, group, includeJournal ? JournalInodeNumber : 0u);
            bitmaps.Add((blockBitmap, inodeBitmap));
            totalFreeInodes += CountFreeInodes(inodeBitmap, group.InodesInGroup);

            var desc = gdtBuffer.AsSpan(g * 64, 64);
            WriteGroupDescriptor(desc, group, g, uuid, blockBitmap, inodeBitmap, enableMetadataCsum, gdtBlocks, inodeTableBlocks);
        }

        // 写超级块（组 0 + 备份组 1, 3, 5, 7…）。
        var totalInodes = (long)inodesPerGroup * groupCount;
        var superblock = new byte[1024];
        WriteSuperblock(superblock, blockSize, inodeSize, (uint)inodesPerGroup, totalBlocks, usedBlocks,
            (int)Math.Max(0, totalInodes - totalFreeInodes), uuid, blocksPerGroup, options.EnableMetadataCsum,
            includeJournal ? JournalInodeNumber : 0u, includeJournal);
        WriteAt(output, baseOffset, 1024, superblock, superblock.Length);
        // 备份超级块：组 1, 3, 5, 7（3 的幂、5 的幂、7 的幂且 < groupCount）。
        foreach (var bg in BackupSuperblockGroups(groupCount))
        {
            WriteAt(output, baseOffset, bg * blocksPerGroup * blockSize + 1024, superblock, superblock.Length);
        }

        // 写 GDT（组描述符表）。
        WriteAt(output, baseOffset, blockSize, gdtBuffer, gdtBuffer.Length);
        // 备份 GDT。
        foreach (var bg in BackupSuperblockGroups(groupCount))
        {
            WriteAt(output, baseOffset, bg * blocksPerGroup * blockSize + blockSize, gdtBuffer, gdtBuffer.Length);
        }

        // 写各组的块位图、inode 位图、inode 表。
        for (var g = 0; g < groupCount; g++)
        {
            var group = groups[g];
            var (blockBitmap, inodeBitmap) = bitmaps[g];
            WriteAt(output, baseOffset, group.BlockBitmapBlock * blockSize, blockBitmap, blockBitmap.Length);
            WriteAt(output, baseOffset, group.InodeBitmapBlock * blockSize, inodeBitmap, inodeBitmap.Length);
        }

        // 写 inode 数据。
        foreach (var node in nodes)
        {
            WriteInode(output, baseOffset, node, blockSize, inodeSize, groups, inodesPerGroup, uuid, options.EnableMetadataCsum);
        }

        // 写内部日志：JBD2 超级块位于日志区域第 0 块，随后为干净的零填充空日志。
        // Write the internal journal: the JBD2 superblock lives in journal block 0 and the rest of the
        // reserved area is a clean all-zero empty log.
        if (includeJournal)
        {
            WriteJournalArea(output, baseOffset, journalStart, journalBlocks, blockSize, uuid);
            WriteJournalInode(output, baseOffset, groups, inodesPerGroup, blockSize, inodeSize, uuid,
                journalStart, journalBlocks, options.EnableMetadataCsum);
        }

        // 写节点数据块。
        foreach (var node in nodes)
        {
            WriteNodeData(output, baseOffset, node, blockSize);
        }

        output.Position = checked(baseOffset + finalSize);
    }

    /// <summary>
    /// 返回需要备份超级块的块组索引列表（组 1, 3, 5, 7 且 < groupCount）。
    /// <para>Returns block group indices that need a backup superblock (groups 1, 3, 5, 7 and < groupCount).</para>
    /// </summary>
    private static IEnumerable<int> BackupSuperblockGroups(int groupCount)
    {
        // ext4 备份超级块位于组 1, 以及 3^n、5^n、7^n 的组（且 >= 1 且 < groupCount）。
        if (groupCount <= 1) yield break;
        yield return 1;
        for (var p = 3; p < groupCount; p *= 3) yield return p;
        for (var p = 5; p < groupCount; p *= 5) yield return p;
        for (var p = 7; p < groupCount; p *= 7) yield return p;
    }

    /// <summary>
    /// 将节点的数据块分配到各组（尽量在同一组内连续分配），并在所有文件数据之后为内部日志
    /// 预留一段连续块区（<paramref name="journalBlocks"/> &gt; 0 时）。
    /// <para>Allocates node data blocks across groups (contiguous within a group when possible), then,
    /// when <paramref name="journalBlocks"/> &gt; 0, reserves a contiguous region right after all file
    /// data for the internal journal.</para>
    /// </summary>
    private static void AllocateDataBlocksMultiGroup(List<Node> nodes, List<GroupInfo> groups, int blocksPerGroup, int journalBlocks, out long journalStart)
    {
        journalStart = -1;
        var groupIndex = 0;
        var cursor = groups[0].FirstDataBlock;

        foreach (var node in nodes)
        {
            if (node.DataBlockCount > 0)
            {
                // 尝试在同一组内分配所有数据块；不够时跨组。
                node.DataStartBlock = cursor;
                cursor += node.DataBlockCount;
                // 检查是否跨越组边界，若是则推进到下一组。
                var groupStart = (long)groups[groupIndex].GroupIndex * blocksPerGroup;
                var groupEnd = groupStart + blocksPerGroup;
                if (cursor > groupEnd && groupIndex + 1 < groups.Count)
                {
                    groupIndex++;
                    cursor = groups[groupIndex].FirstDataBlock;
                    node.DataStartBlock = cursor;
                    cursor += node.DataBlockCount;
                }
            }

            for (var i = 0; i < node.LeafBlocks.Length; i++)
            {
                node.LeafBlocks[i] = cursor;
                cursor++;
                var groupStart = (long)groups[groupIndex].GroupIndex * blocksPerGroup;
                if (cursor >= groupStart + blocksPerGroup && groupIndex + 1 < groups.Count)
                {
                    groupIndex++;
                    cursor = groups[groupIndex].FirstDataBlock;
                }
            }

            for (var i = 0; i < node.IndexBlocks.Length; i++)
            {
                node.IndexBlocks[i] = cursor;
                cursor++;
                var groupStart = (long)groups[groupIndex].GroupIndex * blocksPerGroup;
                if (cursor >= groupStart + blocksPerGroup && groupIndex + 1 < groups.Count)
                {
                    groupIndex++;
                    cursor = groups[groupIndex].FirstDataBlock;
                }
            }
        }

        // 记录每组已用块数。
        foreach (var node in nodes)
        {
            var allBlocks = new List<long>();
            if (node.DataBlockCount > 0)
            {
                for (var i = 0L; i < node.DataBlockCount; i++)
                {
                    allBlocks.Add(node.DataStartBlock + i);
                }
            }
            allBlocks.AddRange(node.LeafBlocks);
            allBlocks.AddRange(node.IndexBlocks);
            foreach (var block in allBlocks)
            {
                var gi = (int)(block / blocksPerGroup);
                if (gi >= 0 && gi < groups.Count)
                {
                    groups[gi].UsedBlocks++;
                }
            }
        }

        // 统计每组 inode 和目录数。
        foreach (var node in nodes)
        {
            var gi = (int)((node.Inode - 1) / (uint)groups[0].InodesInGroup);
            if (gi >= 0 && gi < groups.Count)
            {
                groups[gi].UsedInodes++;
                groups[gi].Nodes.Add(node);
                if (node.IsDirectory)
                {
                    groups[gi].UsedDirectories++;
                }
            }
        }

        // 在文件数据之后预留日志区域。日志区必须是同一组内的一段连续块：
        // 当前组剩余空间不足时整段移动到下一组（每组数据区至少 ~blocksPerGroup-6 块，远超默认 1024）。
        // Reserve the journal region right after file data. It must be one contiguous run inside a
        // single group: when the current group's remaining data area is too small, the whole region
        // moves to the next group (every group's data area is ~blocksPerGroup-6 blocks, far more than
        // the default 1024).
        if (journalBlocks > 0)
        {
            var groupStart = (long)groups[groupIndex].GroupIndex * blocksPerGroup;
            var groupEnd = groupStart + blocksPerGroup;
            if (cursor + journalBlocks > groupEnd)
            {
                if (groupIndex + 1 >= groups.Count)
                {
                    throw new InvalidOperationException(
                        "Not enough free blocks to hold the internal journal; disable IncludeJournal or shrink the source data.");
                }

                groupIndex++;
                cursor = groups[groupIndex].FirstDataBlock;
            }

            journalStart = cursor;
            groups[groupIndex].UsedBlocks += journalBlocks;
            cursor += journalBlocks;
        }
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
        Populate(root, excludedFiles);
        return root;
    }

    private static void Populate(Node node, IReadOnlyCollection<string> excludedFiles)
    {
        var children = Directory.EnumerateFileSystemEntries(node.FullPath)
            .OrderBy(static p => Path.GetFileName(p), StringComparer.Ordinal);
        foreach (var childPath in children)
        {
            // Apply exclusions before probing attributes or classifying links/directories: an excluded
            // dangling symlink or inaccessible directory must never be touched.
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var fullChildPath = Path.GetFullPath(childPath);
            if (excludedFiles.Any(excluded => string.Equals(fullChildPath, excluded, comparison)))
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
                child.Mode = 0xA1FF; // S_IFLNK | 0777
                child.Mtime = ToUnixSeconds(File.GetLastWriteTimeUtc(childPath));
                child.Ctime = ToUnixSeconds(File.GetCreationTimeUtc(childPath));
            }
            else if (isDirectory)
            {
                child.Size = 0;
                ReadUnixAttributes(childPath, child);
                child.Mode = (ushort)(child.Mode | 0x4000); // S_IFDIR
            }
            else
            {
                child.Size = new FileInfo(childPath).Length;
                ReadUnixAttributes(childPath, child);
                child.Mode = (ushort)(child.Mode | 0x8000); // S_IFREG
            }

            node.Children.Add(child);
            if (isDirectory)
            {
                Populate(child, excludedFiles);
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

    /// <summary>
    /// 从源文件 Unix 属性读取 mode（低12位权限位）、uid、gid、mtime、ctime。
    /// Windows 上 uid/gid 默认0，mode 从 ACL 推断或回退为0644/0755。
    /// <para>Reads Unix file attributes (mode low12 bits, uid, gid, mtime, ctime).
    /// On Windows uid/gid default to0; mode is inferred from ACL or falls back to0644/0755.</para>
    /// </summary>
    private static void ReadUnixAttributes(string path, Node node)
    {
        var fi = new FileInfo(path);
        node.Mtime = ToUnixSeconds(fi.LastWriteTimeUtc);
        node.Ctime = ToUnixSeconds(fi.CreationTimeUtc);
        node.Uid = 0;
        node.Gid = 0;

        // Unix/Linux: 读取文件系统权限位。
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var unixMode = File.GetUnixFileMode(path);
                node.Mode = (ushort)(unixMode & (UnixFileMode)0x0FFF);
            }
            catch
            {
                // 回退到默认值。
                node.Mode = (fi.Attributes & FileAttributes.ReadOnly) != 0 ? (ushort)0x124 : (ushort)0x1A4;
                if ((fi.Attributes & FileAttributes.Directory) != 0)
                {
                    node.Mode = 0x1ED;
                }
            }

            // Read security.selinux xattr from source (skip on Windows).
            // 从源文件读取 security.selinux xattr（Windows 上跳过）。
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "getfattr",
                    Arguments = $"-n security.selinux --only-values -- \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    using var ms = new MemoryStream();
                    process.StandardOutput.BaseStream.CopyTo(ms);
                    process.WaitForExit(2000);
                    if (process.ExitCode == 0 && ms.Length > 0)
                    {
                        node.Xattrs ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
                        node.Xattrs["security.selinux"] = ms.ToArray();
                    }
                }
            }
            catch
            {
                // getfattr not available or failed; skip.
            }
        }
        else
        {
            // Windows 或读取失败：按只读标志推断。
            node.Mode = (fi.Attributes & FileAttributes.ReadOnly) != 0 ? (ushort)0x124 : (ushort)0x1A4; // 0444 : 0644
            // 目录权限在调用方已 OR S_IFDIR，此处只设低12位。
            if ((fi.Attributes & FileAttributes.Directory) != 0)
            {
                node.Mode = 0x1ED; // 0755
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
        // htree 哈希种子：超级块 s_hash_seed 全零时 e2fsprogs/内核使用默认 MD4 初始化向量，
        // 因此这里也传全零种子即可得到一致的 Half-MD4 哈希。
        // Htree hash seed: with an all-zero superblock s_hash_seed both e2fsprogs and the kernel fall
        // back to the default MD4 init vector, so passing an all-zero seed yields identical Half-MD4 hashes.
        var hashSeed = new uint[4];
        foreach (var node in nodes)
        {
            if (!node.IsDirectory)
            {
                continue;
            }

            // 小目录（全部条目可放入单个线性块）保持既有路径。
            // Small directories (all entries fit in one linear block) keep the existing layout.
            var linearBlocks = BuildDirectoryBlocks(node, blockSize, uuid, enableMetadataCsum);
            if (linearBlocks.Count == 1)
            {
                node.DirectoryBlocks = linearBlocks;
                node.Size = (long)linearBlocks.Count * blockSize;
                node.DataBlockCount = linearBlocks.Count;
                node.Runs.Add((0, linearBlocks.Count));
                continue;
            }

            // 大目录：构建 htree（dx_root 位于逻辑块 0，叶块/索引块随后，叶块覆盖全部条目）。
            // Large directories get an htree (dx_root at logical block 0, then leaf/index blocks;
            // the leaf blocks still cover every directory entry for linear scans).
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
                hashSeed);

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
        // 所有普通文件（含小文件）均以 extent + 真实数据块写入。不生成内联数据（INLINE_DATA）inode：
        // e2fsprogs 的 debugfs/rdump 对 inline inode 按其 60 字节 + system.data xattr 的容量整块读出，
        // 与文件真实 i_size 不符，无法忠实还原源文件；普通 extent 文件可被 e2fsck/debugfs 精确读回。
        // Every regular file (including tiny ones) is stored as an extent-backed regular file. No inline-data
        // (INLINE_DATA) inodes are produced: e2fsprogs' debugfs/rdump reads inline inodes by their 60-byte
        // i_block + system.data xattr capacity rather than the real i_size, so it cannot faithfully restore
        // the source file. Plain extent files round-trip exactly through e2fsck/debugfs.
        foreach (var node in nodes)
        {
            if (node.IsDirectory || (node.IsSymlink && node.Size < 60))
            {
                continue;
            }

            var totalBlocks = (node.Size + blockSize - 1) / blockSize;

            // 并行空洞检测：对多个独立文件同时扫描。
            // Parallel hole detection: scan multiple independent files simultaneously.
            node.DataBlockCount = totalBlocks;
            node.Runs.Add((0, totalBlocks));
        }

        // 对需要空洞检测的文件并行执行。
        var filesToScan = nodes.Where(n => enableSparse && !n.IsSymlink && !n.IsInlineData && !n.IsDirectory && n.Size > 0).ToList();
        if (filesToScan.Count > 0)
        {
            Parallel.ForEach(filesToScan, node =>
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
            if (node.Runs.Count == 0 || node.IsInlineData)
            {
                continue;
            }

            var extentCount = (int)node.Runs.Sum(static run => (run.Count + MaxExtentLength - 1) / MaxExtentLength);

            if (extentCount <= 4)
            {
                // depth 0: 全部 extent 直接存于 inode i_block（60字节）。
                node.ExtentTreeDepth = 0;
                continue;
            }

            var leafCount = (extentCount + epb - 1) / epb;
            if (leafCount <= 4)
            {
                // depth 1: inode i_block 存 4 个索引项，每项指向一个叶块。
                node.ExtentTreeDepth = 1;
                node.LeafBlocks = new long[leafCount];
            }
            else
            {
                // depth 2: inode i_block 存索引项→二级索引块→叶块。
                node.ExtentTreeDepth = 2;
                node.LeafBlocks = new long[leafCount];
                var indexCount = (leafCount + epb - 1) / epb;
                node.IndexBlocks = new long[indexCount];
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

            for (var i = 0; i < node.IndexBlocks.Length; i++)
            {
                node.IndexBlocks[i] = cursor;
                cursor++;
            }
        }
    }

    private static List<byte[]> BuildDirectoryBlocks(Node dir, int blockSize, byte[] uuid, bool enableMetadataCsum)
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

        // metadata_csum 开启时每个目录块尾部需保留 12 字节 dir_entry_tail。
        // When metadata_csum is on, each directory block reserves 12 bytes for the dir_entry_tail.
        var tailSize = enableMetadataCsum ? DirEntryTailSize : 0;
        var endCap = blockSize - tailSize; // 最后一条真实目录项 rec_len 延伸到此处
        var blocks = new List<byte[]>();
        var block = new byte[blockSize];
        var offset = 0;
        var lastEntryOffset = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            var (name, _, inode) = entries[i];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var naturalLength = (8 + nameBytes.Length + 3) & ~3;
            if (lastEntryOffset >= 0 && offset + naturalLength > endCap)
            {
                CloseDirectoryBlock(blocks, ref block, blockSize, tailSize, endCap, lastEntryOffset, dir.Inode, uuid);
                offset = 0;
                lastEntryOffset = -1;
            }

            WriteU32(block, offset, inode);
            WriteU16(block, offset + 4, (ushort)naturalLength);
            // 未启用 FILETYPE 特性，按传统 ext2 目录项写入 __u16 name_len（高字节为 0，即无 filetype），
            // e2fsck 与内部解析器均可正确读取。
            // Without the FILETYPE feature, entries use the classic ext2 layout: a __u16 name_len whose
            // high byte is 0 (i.e. no file type), which both e2fsck and the in-house parser read correctly.
            WriteU16(block, offset + 6, (ushort)nameBytes.Length);
            nameBytes.CopyTo(block.AsSpan(offset + 8));
            lastEntryOffset = offset;
            offset += naturalLength;
        }

        if (lastEntryOffset >= 0)
        {
            CloseDirectoryBlock(blocks, ref block, blockSize, tailSize, endCap, lastEntryOffset, dir.Inode, uuid);
        }

        return blocks;
    }

    /// <summary>
    /// 关闭一个目录块：将最后一条真实目录项的 rec_len 延伸至 dir_tail 前，需要时写入
    /// 12 字节 dir_entry_tail 校验和尾部。
    /// <para>Closes one directory block: stretches the last real entry's rec_len up to the dir_tail
    /// and, when enabled, appends the 12-byte dir_entry_tail checksum structure.</para>
    /// </summary>
    private static void CloseDirectoryBlock(
        List<byte[]> blocks, ref byte[] block, int blockSize, int tailSize, int endCap,
        int lastEntryOffset, uint dirInode, byte[] uuid)
    {
        WriteU16(block, lastEntryOffset + 4, (ushort)(endCap - lastEntryOffset));
        if (tailSize > 0)
        {
            WriteDirectoryTail(block, endCap, dirInode, uuid);
        }

        blocks.Add(block);
        block = new byte[blockSize];
    }

    /// <summary>
    /// 写入目录块尾部的 struct ext2_dir_entry_tail（12 字节）：
    /// det_reserved_zero1(4)=0、det_rec_len(2)=12、det_reserved_name_len(2)=0xDE00（name_len=0, file_type=0xDE）、
    /// det_checksum(4)=crc32c：seed=crc32c(~0,uuid)，续算 LE32 inode 号、LE32 i_generation(0) 与
    /// 从块首到 dir_tail 之前的全部字节（对照 e2fsprogs ext2fs_dirent_csum）。
    /// <para>Writes the 12-byte struct ext2_dir_entry_tail at the end of a directory block:
    /// det_reserved_zero1(4)=0, det_rec_len(2)=12, det_reserved_name_len(2)=0xDE00 (name_len=0, file_type=0xDE),
    /// det_checksum(4)=crc32c: seed=crc32c(~0,uuid), then LE32 inode number, LE32 i_generation (0) and the
    /// block bytes from its start up to (excluding) the tail, matching e2fsprogs ext2fs_dirent_csum.</para>
    /// </summary>
    private static void WriteDirectoryTail(byte[] block, int endCap, uint dirInode, byte[] uuid)
    {
        WriteU32(block, endCap, 0);
        WriteU16(block, endCap + 4, 12);
        block[endCap + 6] = 0x00; // name_len（0xDE00 的低字节）
        block[endCap + 7] = 0xDE; // file_type（0xDE00 的高字节）

        var crc = ComputeCrc32(uuid, 0xFFFF_FFFF);
        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, dirInode);
        crc = ComputeCrc32Continue(crc, le);
        BinaryPrimitives.WriteUInt32LittleEndian(le, 0); // i_generation（构建器恒为 0）
        crc = ComputeCrc32Continue(crc, le);
        crc = ComputeCrc32Continue(crc, block.AsSpan(0, endCap));
        WriteU32(block, endCap + 8, crc);
    }

    private static byte FileTypeOf(Node node)
        => node.IsDirectory ? Ext4DirectoryEntry.TypeDirectory : node.IsSymlink ? Ext4DirectoryEntry.TypeSymlink : Ext4DirectoryEntry.TypeRegular;

    private static void WriteSuperblock(
        Span<byte> block, int blockSize, int inodeSize, uint inodesPerGroup, long totalBlocks, long usedBlocks, int usedInodes, byte[] uuid, int blocksPerGroup, bool enableMetadataCsum, uint journalInum, bool hasJournal)
    {
        block.Clear();
        var totalInodes = inodesPerGroup * ((totalBlocks + blocksPerGroup - 1) / blocksPerGroup);
        WriteU32(block, 0x00, (uint)Math.Min(totalInodes, uint.MaxValue)); // s_inodes_count
        WriteU32(block, 0x04, (uint)totalBlocks);                         // s_blocks_count_lo
        WriteU32(block, 0x08, 0);                                          // s_r_blocks_count_lo
        WriteU32(block, 0x0C, (uint)(totalBlocks - usedBlocks));           // s_free_blocks_count_lo
        WriteU32(block, 0x10, (uint)Math.Max(0, totalInodes - usedInodes)); // s_free_inodes_count
        WriteU32(block, 0x14, 0);                                          // s_first_data_block
        WriteU32(block, 0x18, (uint)(Log2(blockSize) - 10));              // s_log_block_size
        WriteU32(block, 0x1C, (uint)(Log2(blockSize) - 10));              // s_log_frag_size
        WriteU32(block, 0x20, (uint)blocksPerGroup);                      // s_blocks_per_group
        WriteU32(block, 0x24, (uint)blocksPerGroup);                      // s_frags_per_group
        WriteU32(block, 0x28, inodesPerGroup);                            // s_inodes_per_group
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
        WriteU16(block, 0x5A, 0);
        WriteU32(block, 0x5C, 0x0020 | (hasJournal ? FeatureCompatHasJournal : 0u)); // EXT4_FEATURE_COMPAT_DIR_INDEX [+ HAS_JOURNAL]
        WriteU32(block, 0x60, 0x00C0);                                    // EXT4_FEATURE_INCOMPAT_EXTENTS(0x40) | EXT4_FEATURE_INCOMPAT_64BIT(0x80)
        WriteU32(block, 0x64, enableMetadataCsum ? 0x0400u : 0u);         // EXT4_FEATURE_RO_COMPAT_METADATA_CSUM
        Encoding.ASCII.GetBytes("UOTANSTUDIO", block[0x78..0x88]);
        uuid.AsSpan().CopyTo(block[0x68..0x78]);
        if (hasJournal)
        {
            // s_journal_inum（0xE0，小端）：内部日志 inode 号（EXT4_JOURNAL_INO = 8）。
            // s_journal_inum (0xE0, little-endian): internal journal inode number (EXT4_JOURNAL_INO = 8).
            WriteU32(block, 0xE0, journalInum);
        }

        block[0xFC] = Ext4Htree.HashVersionHalfMd4;   // s_def_hash_version = HALF_MD4 (hash seed stays all-zero)
        block[0xFD] = 0;                              // s_jnl_backup_type
        WriteU16(block, 0xFE, 64);                    // s_desc_size (64-byte descriptors)
        WriteU32(block, 0x100, 0);
        WriteU32(block, 0x104, 0);
        WriteU32(block, 0x150, (uint)(totalBlocks >> 32)); // s_blocks_count_hi
        WriteU32(block, 0x154, 0); // s_r_blocks_count_hi
        WriteU32(block, 0x158, (uint)((totalBlocks - usedBlocks) >> 32)); // s_free_blocks_count_hi
        if (enableMetadataCsum)
        {
            block[0x175] = 1; // s_checksum_type = EXT2_CHECKSUM_CRC32C
        }

        // 超级块校验和：CRC32c（crc32c_le 约定，初始 ~0、无最终异或）覆盖前 1020 字节（s_checksum 位于 0x3FC）。
        // Superblock checksum: raw CRC32c register (crc32c_le convention, init ~0, no final XOR) over the
        // first 1020 bytes (s_checksum at offset 0x3FC), matching e2fsprogs ext2fs_superblock_csum.
        var crc = ComputeCrc32(block[..0x3FC], 0xFFFF_FFFF);
        WriteU32(block, 0x3FC, crc);
    }

    private static void WriteGroupDescriptor(
        Span<byte> desc, uint inodesPerGroup, long totalBlocks, long usedBlocks, int usedInodes, int usedDirectories)
    {
        desc.Clear();
        WriteU32(desc, 0, 2); // block bitmap (block 2)
        WriteU32(desc, 4, 3); // inode bitmap (block 3)
        WriteU32(desc, 8, 4); // inode table (block 4)
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

    private static void WriteGroupDescriptor(
        Span<byte> desc, GroupInfo group, int groupIndex, byte[] uuid,
        byte[] blockBitmap, byte[] inodeBitmap, bool enableMetadataCsum, long gdtBlocks, int inodeTableBlocks)
    {
        // 64 字节组描述符布局（ext4_group_desc，s_desc_size=64）。
        desc.Clear();
        WriteU32(desc, 0, (uint)group.BlockBitmapBlock);          // bg_block_bitmap_lo
        WriteU32(desc, 4, (uint)group.InodeBitmapBlock);          // bg_inode_bitmap_lo
        WriteU32(desc, 8, (uint)group.InodeTableBlock);           // bg_inode_table_lo

        // 空闲块数必须与块位图一致：位图标记了组内元数据块（超级块/GDT/块位图/inode 位图/inode 表）
        // 与数据块，故空闲块 = 组内块数 − 元数据 − 数据。
        // Free-block count must match the block bitmap: the bitmap marks the group's metadata blocks
        // (superblock/GDT/bitmaps/inode table) plus data, so free = group size − metadata − data.
        var freeBlocks = group.BlocksInGroup - ((group.GroupIndex == 0 ? 1 + gdtBlocks : 0) + 2L + inodeTableBlocks) - group.UsedBlocks;
        // 空闲 inode 数以 inode 位图为准（含保留 inode 1..10），与 e2fsck 统计一致。
        // Free-inode count follows the inode bitmap (including reserved inodes 1..10), matching e2fsck.
        var freeInodes = CountFreeInodes(inodeBitmap, group.InodesInGroup);
        WriteU16(desc, 12, (ushort)freeBlocks);                   // bg_free_blocks_count_lo
        WriteU16(desc, 14, (ushort)freeInodes);                   // bg_free_inodes_count_lo
        WriteU16(desc, 16, (ushort)group.UsedDirectories);        // bg_used_dirs_count_lo
        WriteU16(desc, 18, 0);                                    // bg_flags
        WriteU32(desc, 20, 0);                                    // bg_exclude_bitmap_lo
        WriteU16(desc, 24, 0);                                    // bg_block_bitmap_csum_lo (below)
        WriteU16(desc, 26, 0);                                    // bg_inode_bitmap_csum_lo (below)
        WriteU16(desc, 28, 0);                                    // bg_itable_unused_lo
        WriteU16(desc, 30, 0);                                    // bg_checksum (below)
        // 64-bit 扩展字段（偏移32起）。
        WriteU32(desc, 32, (uint)((ulong)group.BlockBitmapBlock >> 32));  // bg_block_bitmap_hi
        WriteU32(desc, 36, (uint)((ulong)group.InodeBitmapBlock >> 32));  // bg_inode_bitmap_hi
        WriteU32(desc, 40, (uint)((ulong)group.InodeTableBlock >> 32));   // bg_inode_table_hi
        WriteU16(desc, 44, (ushort)(freeBlocks >> 16));           // bg_free_blocks_count_hi
        WriteU16(desc, 46, (ushort)(freeInodes >> 16));           // bg_free_inodes_count_hi
        WriteU16(desc, 48, (ushort)(group.UsedDirectories >> 16));// bg_used_dirs_count_hi
        WriteU16(desc, 50, 0);                                    // bg_itable_unused_hi
        WriteU32(desc, 52, 0);                                    // bg_exclude_bitmap_hi
        WriteU16(desc, 56, 0);                                    // bg_block_bitmap_csum_hi (below)
        WriteU16(desc, 58, 0);                                    // bg_inode_bitmap_csum_hi (below)

        if (!enableMetadataCsum)
        {
            return;
        }

        // 块/inode 位图校验和：CRC32c，种子 = crc32c(~0, uuid)（未启用 CSUM_SEED 特性）。
        // 注意 inode 位图校验和只覆盖 inodes_per_group/8 字节（e2fsck pass5 使用同一长度），
        // 块位图校验和则覆盖整块（clusters_per_group/8 = 块大小）。
        // Block/inode bitmap checksums: CRC32c seeded with crc32c(~0, uuid) because CSUM_SEED is off.
        // The inode-bitmap checksum covers only inodes_per_group/8 bytes (same length e2fsck pass5 uses),
        // while the block-bitmap checksum covers the whole block (clusters_per_group/8 == block size).
        var seed = ComputeCrc32(uuid, 0xFFFF_FFFF);
        var inodeBitmapCsumLength = Math.Max(1, group.InodesInGroup / 8);
        var blockCsum = ComputeCrc32(blockBitmap, seed);
        var inodeCsum = ComputeCrc32(inodeBitmap.AsSpan(0, inodeBitmapCsumLength), seed);
        WriteU16(desc, 24, (ushort)(blockCsum & 0xFFFF));      // bg_block_bitmap_csum_lo
        WriteU16(desc, 26, (ushort)(inodeCsum & 0xFFFF));      // bg_inode_bitmap_csum_lo
        WriteU16(desc, 56, (ushort)(blockCsum >> 16));         // bg_block_bitmap_csum_hi
        WriteU16(desc, 58, (ushort)(inodeCsum >> 16));         // bg_inode_bitmap_csum_hi

        // 组描述符校验和（对照 e2fsprogs ext2fs_group_desc_csum 的 metadata_csum 分支）：
        // seed = crc32c(~0, uuid)，续算 4 字节小端组号与整条 64 字节描述符
        // （bg_checksum 计算前为 0）。
        // Group descriptor checksum (e2fsprogs ext2fs_group_desc_csum, metadata_csum branch):
        // seed = crc32c(~0, uuid), then the LE32 group number and the whole 64-byte descriptor
        // (bg_checksum stays zero while computing).
        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, (uint)groupIndex);
        var csum = ComputeCrc32(le, seed);
        csum = ComputeCrc32Continue(csum, desc);
        WriteU16(desc, 30, (ushort)(csum & 0xFFFF));           // bg_checksum
    }

    private static void WriteBlockBitmap(byte[] bitmap, long usedBlocks)
    {
        for (var i = 0L; i < usedBlocks; i++)
        {
            bitmap[(int)(i >> 3)] |= (byte)(1 << (int)(i & 7));
        }
    }

    private static void WriteBlockBitmapForGroup(byte[] bitmap, GroupInfo group, int blocksPerGroup, int groupCount, long gdtBlocks, int inodeTableBlocks)
    {
        var groupStart = (long)group.GroupIndex * blocksPerGroup;

        // 组 0：标记超级块(块0)、GDT(块1..gdtBlocks)。
        if (group.GroupIndex == 0)
        {
            MarkBlockUsed(bitmap, 0);
            for (var b = 1L; b <= gdtBlocks; b++)
            {
                MarkBlockUsed(bitmap, b);
            }
        }

        // 标记块位图、inode 位图、inode 表块。
        MarkBlockUsed(bitmap, group.BlockBitmapBlock - groupStart);
        MarkBlockUsed(bitmap, group.InodeBitmapBlock - groupStart);
        for (var b = 0; b < inodeTableBlocks; b++)
        {
            MarkBlockUsed(bitmap, group.InodeTableBlock - groupStart + b);
        }

        // 标记已用数据块。
        var dataStart = group.FirstDataBlock - groupStart;
        for (var i = 0L; i < group.UsedBlocks; i++)
        {
            MarkBlockUsed(bitmap, dataStart + i);
        }
    }

    private static void MarkBlockUsed(byte[] bitmap, long localBlock)
    {
        if (localBlock >= 0 && localBlock < bitmap.Length * 8L)
        {
            bitmap[(int)(localBlock >> 3)] |= (byte)(1 << (int)(localBlock & 7));
        }
    }

    private static void WriteInodeBitmapForGroup(byte[] bitmap, GroupInfo group, uint journalInode = 0)
    {
        // inode 位图块整体置 1：超出 inodes_per_group 的填充位必须为 1（e2fsck 要求），
        // 组内已用 inode 也保持 1；随后只清空空闲 inode 位。
        // Mark the whole inode bitmap block used, including the padding bits beyond inodes_per_group
        // (e2fsck requires them set); used inodes stay set. Only the free inode slots are cleared.
        bitmap.AsSpan().Fill(0xFF);

        var inodesPerGroup = group.InodesInGroup;
        var usedLocals = new HashSet<int>();
        foreach (var node in group.Nodes)
        {
            usedLocals.Add((int)((node.Inode - 1) % (uint)inodesPerGroup));
        }

        // 日志 inode（属于组 0）也标记为已用。
        // The journal inode (group 0) is marked used as well.
        if (journalInode > 0 && ((journalInode - 1) / (uint)inodesPerGroup) == (uint)group.GroupIndex)
        {
            usedLocals.Add((int)((journalInode - 1) % (uint)inodesPerGroup));
        }

        // 保留 inode（1..FirstUsableInode-1，含坏块 inode 1 与保留位）必须保持已用，与 mke2fs/e2fsck 一致；
        // 只清空从 FirstUsableInode 起的空闲 inode 位。
        // Reserved inodes (1..FirstUsableInode-1, including the bad-block inode) stay marked used, as
        // mke2fs/e2fsck expect; only free inode slots from FirstUsableInode onward are cleared.
        for (var local = (int)(FirstUsableInode - 1); local < inodesPerGroup; local++)
        {
            if (!usedLocals.Contains(local))
            {
                bitmap[local >> 3] &= (byte)~(1 << (local & 7));
            }
        }
    }

    /// <summary>
    /// 统计位图前 <paramref name="inodesPerGroup"/> 位中空闲（清 0）的 inode 数。
    /// <para>Counts the free (cleared) inode bits among the first <paramref name="inodesPerGroup"/> bits.</para>
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
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Core;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Journal;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

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

    private const ushort Ext4ExtraIsize = 28;

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
    /// Builds an ext4 image from sourceDirectory and returns it as a byte array.
    /// <para>从 sourceDirectory 构建 ext4 镜像并以字节数组返回。</para>
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
    /// Builds an ext4 image from sourceDirectory and writes it to outputPath.
    /// <para>从 sourceDirectory 构建 ext4 镜像并写入 outputPath。</para>
    /// </summary>
    public static void Build(string sourceDirectory, string outputPath, Ext4Options? options = null)
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
                BuildCore(sourceDirectory, output, effective, 0, freshDestination: true, excludedFiles: [fullOutputPath, temporaryPath], plannedRoot);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch {  }
        }
    }

    /// <summary>
    /// Builds an ext4 image from sourceDirectory and streams it to output.
    /// <para>从 sourceDirectory 构建 ext4 镜像并流式写入 output。</para>
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
            try { File.Delete(temporaryPath); } catch {  }
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


        var uuid = options.Uuid is { Length: 16 } ? options.Uuid : FixedUuid;
        var enableMetadataCsum = options.EnableMetadataCsum;


        var root = plannedRoot ?? BuildTree(Path.GetFullPath(sourceDirectory), excludedFiles);
        var nodes = new List<Node>();
        var maxInode = AssignInodes(root, nodes);

        PlanDirectoryBlocks(nodes, blockSize, uuid, enableMetadataCsum);
        PlanDataBlocks(nodes, blockSize, options.EnableSparse);
        PlanLeafBlocks(nodes, blockSize);


        var blocksPerGroup = BlocksPerGroupMultiplier * blockSize;
        var inodesPerInodeBlock = blockSize / inodeSize;

        var inodesPerGroup = (int)RoundUpPowerOfTwo(Math.Max(maxInode, 16));
        var inodeTableBlocks = (inodesPerGroup + inodesPerInodeBlock - 1) / inodesPerInodeBlock;

        var metaPerGroup = 2L + inodeTableBlocks;


        var totalDataBlocks = nodes.Sum(static n => n.DataBlockCount)
            + nodes.Sum(static n => (long)n.LeafBlocks.Length)
            + nodes.Sum(static n => (long)n.IndexBlocks.Length);


        var journalBlocks = options.IncludeJournal ? DefaultJournalBlocks : 0;
        var includeJournal = journalBlocks > 0;


        var gdtBlocks = 1L;
        var group0Extra = 1L + gdtBlocks;
        var estimatedTotal = group0Extra + metaPerGroup + totalDataBlocks + journalBlocks;
        var groupCount = Math.Max(1, (int)((estimatedTotal + blocksPerGroup - 1) / blocksPerGroup));
        groupCount = Math.Max(groupCount, (int)Math.Ceiling((double)maxInode / inodesPerGroup));


        gdtBlocks = Math.Max(1, (long)Math.Ceiling(groupCount * 64.0 / blockSize));


        var descsPerGdtBlock = blockSize / 64;
        var fsBlocks = (long)groupCount * blocksPerGroup;
        var maxGrowthBlocks = fsBlocks < ((1L << 32) / 1024) ? fsBlocks * 1024 : 0xFFFF_FFFFL;
        var reservedGroups = (maxGrowthBlocks + blocksPerGroup - 1) / blocksPerGroup;
        var reservedGdtBlocks = (int)Math.Min(blockSize / 4, (reservedGroups + descsPerGdtBlock - 1) / descsPerGdtBlock);


        var backupGroupCount = BackupSuperblockGroups(groupCount).Count();
        var group0Metadata = 1L + gdtBlocks + reservedGdtBlocks + metaPerGroup;
        var totalBlocksNeeded = group0Metadata + totalDataBlocks + journalBlocks
            + (groupCount - 1) * metaPerGroup + backupGroupCount * (1 + gdtBlocks + reservedGdtBlocks);

        if (totalBlocksNeeded > (long)groupCount * blocksPerGroup)
        {
            groupCount = (int)((totalBlocksNeeded + blocksPerGroup - 1) / blocksPerGroup);
            gdtBlocks = Math.Max(1, (long)Math.Ceiling(groupCount * 64.0 / blockSize));
            fsBlocks = (long)groupCount * blocksPerGroup;
            maxGrowthBlocks = fsBlocks < ((1L << 32) / 1024) ? fsBlocks * 1024 : 0xFFFF_FFFFL;
            reservedGroups = (maxGrowthBlocks + blocksPerGroup - 1) / blocksPerGroup;
            reservedGdtBlocks = (int)Math.Min(blockSize / 4, (reservedGroups + descsPerGdtBlock - 1) / descsPerGdtBlock);
            backupGroupCount = BackupSuperblockGroups(groupCount).Count();
            group0Metadata = 1L + gdtBlocks + reservedGdtBlocks + metaPerGroup;
            totalBlocksNeeded = group0Metadata + totalDataBlocks + journalBlocks
                + (groupCount - 1) * metaPerGroup + backupGroupCount * (1 + gdtBlocks + reservedGdtBlocks);
        }

        var totalBlocks = (long)groupCount * blocksPerGroup;
        var usedBlocks = totalBlocksNeeded;


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

            group.BlocksInGroup = blocksPerGroup;
            group.InodesInGroup = inodesPerGroup;
            groups.Add(group);
        }


        long resizeDindirBlock = -1;
        if (reservedGdtBlocks > 0)
        {
            resizeDindirBlock = groups[0].FirstDataBlock;
            groups[0].ExtraUsedBlocks.Add(resizeDindirBlock);
            groups[0].FirstDataBlock++;


            usedBlocks++;
        }

        AllocateDataBlocksMultiGroup(nodes, groups, blocksPerGroup, journalBlocks, out var journalStart);
        if (includeJournal)
        {


            groups[0].UsedInodes++;
        }


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
            WriteGroupDescriptor(desc, group, g, uuid, blockBitmap, inodeBitmap, enableMetadataCsum, gdtBlocks, reservedGdtBlocks, inodeTableBlocks, backupGroups);
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
            includeJournal ? JournalInodeNumber : 0u, includeJournal, reservedGdtBlocks, journalIBlock, journalSize);
        WriteAt(output, baseOffset, 1024, superblock, superblock.Length);

        foreach (var bg in BackupSuperblockGroups(groupCount))
        {
            WriteAt(output, baseOffset, bg * blocksPerGroup * blockSize + 1024, superblock, superblock.Length);
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


        foreach (var node in nodes)
        {
            WriteNodeData(output, baseOffset, node, blockSize);
        }

        output.Position = checked(baseOffset + finalSize);
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
    /// Allocates node data blocks across groups (contiguous within a group when possible), then, when journalBlocks &gt; 0, reserves a contiguous region right after all file data for the internal journal.
    /// <para>将节点的数据块分配到各组（尽量在同一组内连续分配），并在所有文件数据之后为内部日志 预留一段连续块区（journalBlocks &gt; 0 时）。</para>
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

                node.DataStartBlock = cursor;
                cursor += node.DataBlockCount;

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
                child.Mode = 0xA1FF;
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
            if (linearBlocks.Count == 1)
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
                uuid);

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


        var tailSize = enableMetadataCsum ? DirEntryTailSize : 0;
        var endCap = blockSize - tailSize;
        var blocks = new List<byte[]>();
        var block = new byte[blockSize];
        var offset = 0;
        var lastEntryOffset = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            var (name, fileType, inode) = entries[i];
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


            block[offset + 6] = (byte)nameBytes.Length;
            block[offset + 7] = fileType;
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
    /// Closes one directory block: stretches the last real entry's rec_len up to the dir_tail and, when enabled, appends the 12-byte dir_entry_tail checksum structure.
    /// <para>关闭一个目录块：将最后一条真实目录项的 rec_len 延伸至 dir_tail 前，需要时写入 12 字节 dir_entry_tail 校验和尾部。</para>
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
    /// Writes the directory block tail with its CRC32c checksum.
    /// <para>写入目录块尾部结构及其 CRC32c 校验和。</para>
    /// </summary>
    private static void WriteDirectoryTail(byte[] block, int endCap, uint dirInode, byte[] uuid)
    {
        WriteU32(block, endCap, 0);
        WriteU16(block, endCap + 4, 12);
        block[endCap + 6] = 0x00;
        block[endCap + 7] = 0xDE;

        var crc = ComputeCrc32(uuid, 0xFFFF_FFFF);
        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, dirInode);
        crc = ComputeCrc32Continue(crc, le);
        BinaryPrimitives.WriteUInt32LittleEndian(le, 0);
        crc = ComputeCrc32Continue(crc, le);
        crc = ComputeCrc32Continue(crc, block.AsSpan(0, endCap));
        WriteU32(block, endCap + 8, crc);
    }

    private static byte FileTypeOf(Node node)
        => node.IsDirectory ? Ext4DirectoryEntry.TypeDirectory : node.IsSymlink ? Ext4DirectoryEntry.TypeSymlink : Ext4DirectoryEntry.TypeRegular;

    private static void WriteSuperblock(
        Span<byte> block, int blockSize, int inodeSize, uint inodesPerGroup, long totalBlocks, long usedBlocks, int usedInodes, byte[] uuid, int blocksPerGroup, bool enableMetadataCsum, uint journalInum, bool hasJournal,
        int reservedGdtBlocks, ReadOnlySpan<byte> journalIBlock, long journalSize)
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
        WriteU16(block, 0x5A, 0);


        WriteU32(block, 0x5C, 0x0038u | (hasJournal ? FeatureCompatHasJournal : 0u));
        WriteU32(block, 0x60, 0x02C2);
        WriteU32(block, 0x64, 0x006Bu | (enableMetadataCsum ? 0x0400u : 0u));
        Encoding.ASCII.GetBytes("UOTANSTUDIO", block[0x78..0x88]);
        uuid.AsSpan().CopyTo(block[0x68..0x78]);


        block[0xCF] = (byte)Math.Min(reservedGdtBlocks, 255);
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


        block[0x3FC] = 0; block[0x3FD] = 0; block[0x3FE] = 0; block[0x3FF] = 0;
        var crc = ComputeCrc32(block[..0x400], 0xFFFF_FFFF);
        WriteU32(block, 0x3FC, crc);
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
        HashSet<int>? backupGroups = null)
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


        var seed = ComputeCrc32(uuid, 0xFFFF_FFFF);
        var inodeBitmapCsumLength = Math.Max(1, group.InodesInGroup / 8);
        var blockCsum = ComputeCrc32(blockBitmap, seed);
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

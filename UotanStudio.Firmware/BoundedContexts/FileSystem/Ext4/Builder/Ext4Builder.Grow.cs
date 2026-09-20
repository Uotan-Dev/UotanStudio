using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Models;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;

public sealed partial class Ext4Builder
{
    // Superblock field offsets (relative to the start of the 1024-byte superblock).
    private const int SbInodesCountLo = 0x00;
    private const int SbBlocksCountLo = 0x04;
    private const int SbRBlocksCountLo = 0x08;
    private const int SbFreeBlocksCountLo = 0x0C;
    private const int SbFreeInodesCount = 0x10;
    private const int SbFeatureCompat = 0x5C;
    private const int SbBlockGroupNr = 0x5A;
    private const int SbReservedGdtBlocks = 0xCE;
    private const int SbBlocksCountHi = 0x150;
    private const int SbRBlocksCountHi = 0x154;
    private const int SbFreeBlocksCountHi = 0x158;
    private const int SbChecksum = 0x3FC;

    private const uint FeatureCompatResizeInode = 0x0008;
    private const uint FeatureCompatSparseSuper = 0x0010;
    private const uint FeatureRoCompatMetadataCsum = 0x0400;

    /// <summary>
    /// Enlarges an existing ext4 image by appending whole block groups, adding free capacity
    /// for later file insertion while preserving every existing inode, data block and directory.
    /// <para>通过追加整块块组扩大既有 ext4 镜像，为后续写入预留空闲容量，同时完整保留现有的 inode、数据块与目录。</para>
    /// </summary>
    /// <remarks>
    /// Grow mirrors the layout produced by <see cref="Ext4Builder"/>: it patches the superblock counters,
    /// rebuilds the group descriptor array, lays fresh block/inode bitmaps and zeroed inode tables for the
    /// appended groups, and rewrites every sparse-super backup (superblock + GDT) plus the resize inode so
    /// the enlarged group set stays internally consistent.
    /// <para>Grow 复用 <see cref="Ext4Builder"/> 的布局：修补超级块计数、重建块组描述符数组、为新增组写入位图与清零的
    /// inode 表，并重写所有 sparse-super 备份（超级块 + GDT）以及 resize inode，使扩大后的块组集合内部保持一致。</para>
    /// Current limits (surfaced as <see cref="NotSupportedException"/>, not silently violated):
    /// <list type="bullet">
    /// <item>Classic sparse-super images only (no <c>sparse_super2</c>).</item>
    /// <item>The appended groups must still fit the existing primary GDT block count — growing past it would
    /// require relocating the reserved GDT blocks, which is not implemented.</item>
    /// <item>Growth is quantised to whole block groups; the request is rounded up.</item>
    /// </list>
    /// <para>当前的限制（以 <see cref="NotSupportedException"/> 明确抛出，绝不静默违反）：仅支持传统 sparse-super
    /// （不支持 <c>sparse_super2</c>）；新增组必须仍落在既有主 GDT 块数之内（超出需要重定位保留 GDT，尚未实现）；
    /// 增长以整块组为单位，请求向上取整。</para>
    /// </remarks>
    /// <param name="imagePath">Path to the ext4 image file modified in place. / 就地修改的 ext4 镜像路径。</param>
    /// <param name="targetBlocks">Desired total block count; rounded up to a whole block group. / 目标总块数，向上取整到整块组。</param>
    public static void Grow(string imagePath, long targetBlocks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var full = Path.GetFullPath(imagePath);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        GrowCore(stream, targetBlocks);
    }

    /// <summary>
    /// Grows a seekable, writable ext4 stream by appending whole block groups.
    /// <para>通过追加整块块组扩大可随机读写的 ext4 流。</para>
    /// </summary>
    public static void Grow(Stream image, long targetBlocks)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanRead || !image.CanWrite || !image.CanSeek)
        {
            throw new ArgumentException("The image stream must be readable, writable and seekable.", nameof(image));
        }

        GrowCore(image, targetBlocks);
    }

    private static void GrowCore(Stream output, long targetBlocks)
    {
        if (targetBlocks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetBlocks), "The target block count must be positive.");
        }

        var block0 = ReadBlock0(output);
        var sb = Ext4Superblock.FromBytes(block0);

        var blockSize = sb.BlockSize;
        var blocksPerGroup = (long)sb.BlocksPerGroup;
        var inodesPerGroup = (long)sb.InodesPerGroup;
        var inodeSize = (long)sb.InodeSize;
        var descSize = (long)sb.EffectiveDescSize;
        var oldTotal = (long)sb.BlocksCount;

        if (descSize != 64)
        {
            throw new NotSupportedException($"Grow supports only 64-byte group descriptors (found {descSize}).");
        }

        if (inodeSize < 128)
        {
            throw new NotSupportedException($"Grow requires a positive inode size (found {inodeSize}).");
        }

        var featureCompat = BinaryPrimitives.ReadUInt32LittleEndian(block0.AsSpan(1024 + SbFeatureCompat, 4));
        var featureRoCompat = sb.FeatureRoCompat;
        if ((featureCompat & FeatureCompatSparseSuper) == 0)
        {
            throw new NotSupportedException("Grow requires the sparse_super compatibility feature.");
        }

        var reservedGdtBlocks = (long)BinaryPrimitives.ReadUInt16LittleEndian(block0.AsSpan(1024 + SbReservedGdtBlocks, 2));
        var metadataCsum = (featureRoCompat & FeatureRoCompatMetadataCsum) != 0;
        var hasResizeInode = (featureCompat & FeatureCompatResizeInode) != 0 && reservedGdtBlocks > 0;

        var oldGroupCount = (oldTotal + blocksPerGroup - 1) / blocksPerGroup;
        if (targetBlocks <= oldTotal)
        {
            // Nothing to append: the request is at or below the current size. Report an exact no-op so
            // callers can distinguish "already large enough" from a real error.
            // <para>无需追加：请求不大于当前容量。给出精确的空操作，便于调用方区分“已经足够大”与真正的错误。</para>
            return;
        }

        var newGroupCount = (targetBlocks + blocksPerGroup - 1) / blocksPerGroup;
        var newTotal = newGroupCount * blocksPerGroup;
        if (newTotal <= oldTotal)
        {
            return;
        }

        var descsPerGdtBlock = blockSize / descSize;
        var oldGdtBlocks = Math.Max(1, (oldGroupCount * descSize + blockSize - 1) / blockSize);
        var newGdtBlocks = Math.Max(1, (newGroupCount * descSize + blockSize - 1) / blockSize);
        if (newGdtBlocks != oldGdtBlocks)
        {
            throw new NotSupportedException(
                $"Grow to {newGroupCount} groups would expand the primary GDT from {oldGdtBlocks} to {newGdtBlocks} block(s); " +
                "relocating the reserved GDT blocks is not implemented.");
        }

        // The inode table must fit the descriptor + reserved-GDT metadata at the head of every group.
        // <para>inode 表必须能容纳每个块组头部的描述符 + 保留 GDT 元数据。</para>
        var inodeTableBlocks = (inodesPerGroup * inodeSize + blockSize - 1) / blockSize;
        var metaHead = 1L + oldGdtBlocks + reservedGdtBlocks;
        if (2 + inodeTableBlocks + (hasResizeInode ? 4 : 0) > blocksPerGroup)
        {
            throw new NotSupportedException("A block group cannot hold its bitmaps, inode table and resize-inode metadata.");
        }

        var newBackupGroups = new HashSet<int>();
        foreach (var g in BackupSuperblockGroups((int)newGroupCount))
        {
            newBackupGroups.Add(g);
        }

        var oldUsed = oldTotal - (long)sb.FreeBlocksCount;
        var oldUsedInodes = (long)sb.InodesCount - sb.FreeInodesCount;

        // Reconstruct group 0's descriptor locations so the shared resize-inode helper targets the
        // same fixed metadata blocks the original build used.
        // <para>重建组 0 的描述符位置，使共享的 resize inode 辅助方法指向与原构建相同的固定元数据块。</para>
        var group0 = new GroupInfo
        {
            GroupIndex = 0,
            BlockBitmapBlock = 1 + oldGdtBlocks + reservedGdtBlocks,
            InodeBitmapBlock = 1 + oldGdtBlocks + reservedGdtBlocks + 1,
            InodeTableBlock = 1 + oldGdtBlocks + reservedGdtBlocks + 2,
            FirstDataBlock = 1 + oldGdtBlocks + reservedGdtBlocks + 2 + inodeTableBlocks,
            BlocksInGroup = blocksPerGroup,
            InodesInGroup = (int)inodesPerGroup,
        };
        var resizeDindirBlock = -1L;
        if (hasResizeInode)
        {
            resizeDindirBlock = group0.FirstDataBlock;
        }

        // Read the existing primary GDT so every current group descriptor is preserved verbatim (its
        // block/inode bitmap checksums and usage counts remain untouched).
        // <para>读取现有主 GDT，逐字保留每个既有组描述符（其位图校验与用量计数保持不动）。</para>
        var gdtBuffer = new byte[checked((int)(oldGdtBlocks * blockSize))];
        output.Position = blockSize;
        output.ReadExactly(gdtBuffer, 0, gdtBuffer.Length);

        long addedMetaBlocks = 0;

        // Size the file first so appends land in freshly zeroed space; every new group region is then
        // written from a clean buffer regardless of any pre-existing bytes beyond the old filesystem.
        // <para>先扩容文件，使追加落在新清零的空间；随后每个新组区域都用干净缓冲写入，不受旧文件系统之外的既有字节影响。</para>
        var newFileLength = checked(newTotal * blockSize);
        if (output.Length < newFileLength)
        {
            output.SetLength(newFileLength);
        }

        var zeroBlock = new byte[(int)blockSize];
        for (var g = oldGroupCount; g < newGroupCount; g++)
        {
            var isBackup = newBackupGroups.Contains((int)g);
            var groupStart = g * blocksPerGroup;
            var group = new GroupInfo
            {
                GroupIndex = (int)g,
                BlockBitmapBlock = isBackup ? groupStart + metaHead : groupStart,
                InodeBitmapBlock = (isBackup ? groupStart + metaHead : groupStart) + 1,
                InodeTableBlock = (isBackup ? groupStart + metaHead : groupStart) + 2,
                FirstDataBlock = (isBackup ? groupStart + metaHead : groupStart) + 2 + inodeTableBlocks,
                BlocksInGroup = blocksPerGroup,
                InodesInGroup = (int)inodesPerGroup,
            };

            // Zero the whole group, then stamp the block/inode bitmaps and the empty inode table.
            // <para>先清零整块组，再写入块/inode 位图与空的 inode 表。</para>
            var groupBlocks = Math.Min(blocksPerGroup, newTotal - groupStart);
            for (var b = 0L; b < groupBlocks; b++)
            {
                WriteAt(output, 0, (groupStart + b) * blockSize, zeroBlock, zeroBlock.Length);
            }

            var blockBitmap = new byte[(int)blockSize];
            WriteBlockBitmapForGroup(blockBitmap, group, (int)blocksPerGroup, (int)newGroupCount, oldGdtBlocks, (int)reservedGdtBlocks, (int)inodeTableBlocks, newBackupGroups);
            WriteAt(output, 0, group.BlockBitmapBlock * blockSize, blockBitmap, (int)blockSize);

            var inodeBitmap = new byte[(int)blockSize];
            WriteInodeBitmapForGroup(inodeBitmap, group, (int)blockSize, (int)inodeSize, journalInode: 0);
            WriteAt(output, 0, group.InodeBitmapBlock * blockSize, inodeBitmap, (int)blockSize);

            for (var t = 0L; t < inodeTableBlocks; t++)
            {
                WriteAt(output, 0, (group.InodeTableBlock + t) * blockSize, zeroBlock, (int)blockSize);
            }

            var desc = gdtBuffer.AsSpan((int)(g * descSize), (int)descSize);
            WriteGroupDescriptor(desc, group, (int)g, sb.Uuid, blockBitmap, inodeBitmap, metadataCsum, oldGdtBlocks, (int)reservedGdtBlocks, (int)inodeTableBlocks, (int)blocksPerGroup, newBackupGroups);

            var freeBlocks = group.BlocksInGroup
                - ((isBackup ? metaHead : 0) + 2L + inodeTableBlocks);
            addedMetaBlocks += groupBlocks - freeBlocks;
        }

        // Rewrite the primary group descriptor table with the appended entries.
        // <para>用新增条目重写主块组描述符表。</para>
        WriteAt(output, 0, blockSize, gdtBuffer, gdtBuffer.Length);

        var newUsed = oldUsed + addedMetaBlocks;
        var newFree = newTotal - newUsed;
        var newInodes = newGroupCount * inodesPerGroup;
        var newFreeInodes = newInodes - oldUsedInodes;
        var newReserved = newTotal / 20;

        var primary = new byte[1024];
        block0.AsSpan(1024, 1024).CopyTo(primary);
        PatchSuperblock(primary, newTotal, newFree, newInodes, newFreeInodes, newReserved, metadataCsum);
        WriteAt(output, 0, 1024, primary, primary.Length);

        // Regenerate the resize inode and its reserved/primary GDT backup table for the new group set.
        // <para>按新的块组集合重建 resize inode 及其保留/主 GDT 备份表。</para>
        if (hasResizeInode)
        {
            // Rebuild a group list the shared helper indexes by inode number (only group 0 holds inode 6).
            // <para>重建辅助方法按 inode 号索引的组列表（仅组 0 持有 inode 6）。</para>
            var groupsForResize = new List<GroupInfo> { group0 };
            WriteResizeInodeMetadata(output, 0, groupsForResize, (int)blocksPerGroup, (int)newGroupCount,
                oldGdtBlocks, (int)reservedGdtBlocks, (int)blockSize, (int)inodeSize, (int)inodesPerGroup, sb.Uuid,
                metadataCsum, resizeDindirBlock);
        }

        // Write every sparse-super backup (superblock + GDT) for the enlarged group set.
        // <para>为扩大后的块组集合写入所有 sparse-super 备份（超级块 + GDT）。</para>
        foreach (var bg in BackupSuperblockGroups((int)newGroupCount))
        {
            var copy = (byte[])primary.Clone();
            WriteU16(copy, SbBlockGroupNr, checked((ushort)bg));
            WriteU32(copy, SbChecksum, ComputeCrc32(copy.AsSpan(0, SbChecksum), 0xFFFF_FFFF));
            var bgStart = (long)bg * blocksPerGroup * blockSize;
            WriteAt(output, 0, bgStart, copy, copy.Length);
            WriteAt(output, 0, bgStart + blockSize, gdtBuffer, gdtBuffer.Length);
        }

        output.Position = newFileLength;
    }

    private static void PatchSuperblock(byte[] sb, long totalBlocks, long freeBlocks, long inodes, long freeInodes, long reserved, bool metadataCsum)
    {
        _ = metadataCsum; // The checksum field is always recomputed below; csum_present only governs whether e2fsck trusts it.
        WriteU32(sb, SbBlocksCountLo, (uint)totalBlocks);
        WriteU32(sb, SbBlocksCountHi, (uint)(totalBlocks >> 32));
        WriteU32(sb, SbFreeBlocksCountLo, (uint)freeBlocks);
        WriteU32(sb, SbFreeBlocksCountHi, (uint)(freeBlocks >> 32));
        WriteU32(sb, SbInodesCountLo, checked((uint)Math.Min(inodes, uint.MaxValue)));
        WriteU32(sb, SbFreeInodesCount, checked((uint)Math.Min(Math.Max(freeInodes, 0), uint.MaxValue)));
        WriteU32(sb, SbRBlocksCountLo, (uint)reserved);
        WriteU32(sb, SbRBlocksCountHi, (uint)(reserved >> 32));
        WriteU32(sb, SbChecksum, ComputeCrc32(sb.AsSpan(0, SbChecksum), 0xFFFF_FFFF));
    }

    private static byte[] ReadBlock0(Stream stream)
    {
        var length = (int)Math.Min(stream.Length, 2048);
        if (length < 1024 + 256)
        {
            throw new InvalidDataException("Image is too small to contain an ext4 superblock.");
        }

        var buffer = new byte[2048];
        stream.Position = 0;
        stream.ReadExactly(buffer, 0, length);
        return buffer;
    }
}
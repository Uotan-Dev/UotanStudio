using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Core;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Journal;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

public sealed partial class Ext4Builder
{
    /// <summary>
    /// Writes the journal area: a journal superblock at journalStart, with the remaining blocks zeroed out.
    /// <para>写出日志区域：在 journalStart 处写入日志超级块，剩余块清零。</para>
    /// </summary>
    private static void WriteJournalArea(Stream output, long baseOffset, long journalStart, int journalBlocks, int blockSize, byte[] uuid)
    {


        var jsb = JournalSuperblock.CreateEmpty((uint)blockSize, (uint)journalBlocks, uuid, checksumV3: false).ToBytes();
        var block = new byte[blockSize];
        jsb.CopyTo(block, 0);
        WriteAt(output, baseOffset, checked(journalStart * blockSize), block, blockSize);

        if (journalBlocks > 1)
        {
            var logStart = checked(baseOffset + (journalStart + 1) * blockSize);
            ZeroRegion(output, logStart, (long)(journalBlocks - 1) * blockSize);
        }
    }

    /// <summary>
    /// Writes the journal inode (#8, regular file with extents): a single contiguous extent maps the whole journal area, i_size = journalBlocks × block size, with the inode checksum computed under metadata_csum.
    /// <para>写出日志 inode（inode 8，普通文件、extents）：单条连续 extent 映射日志区域全部物理块， i_size = journalBlocks × 块大小，并按 metadata_csum 计算 inode 校验和。</para>
    /// </summary>
    private static void WriteJournalInode(Stream output, long baseOffset, List<GroupInfo> groups, int inodesPerGroup,
        int blockSize, int inodeSize, byte[] uuid, long journalStart, int journalBlocks, bool enableMetadataCsum)
    {
        var group = groups[(int)((JournalInodeNumber - 1) / (uint)inodesPerGroup)];
        var localInode = (long)((JournalInodeNumber - 1) % (uint)inodesPerGroup);
        var inodeOffset = checked(group.InodeTableBlock * blockSize + localInode * inodeSize);
        var inode = new byte[inodeSize];
        var size = checked((long)journalBlocks * blockSize);
        var sectorBlocks = (ulong)journalBlocks * ((ulong)blockSize / 512);

        WriteU16(inode, 0, ModeJournalFile);
        WriteU16(inode, 2, 0);
        WriteU32(inode, 4, (uint)(size & 0xFFFFFFFF));
        WriteU32(inode, 8, FixedTimestamp);
        WriteU32(inode, 12, FixedTimestamp);
        WriteU32(inode, 16, FixedTimestamp);
        WriteU32(inode, 20, 0);
        WriteU16(inode, 24, 0);
        WriteU16(inode, 26, 1);
        WriteU32(inode, 28, (uint)sectorBlocks);
        WriteU32(inode, 32, FlagExtents);
        if (inodeSize >= 256)
        {
            WriteU32(inode, 108, (uint)((ulong)size >> 32));
        }


        var iblock = inode.AsSpan(40, 60);
        WriteExtentHeader(iblock, 1, 0, 4);
        WriteExtentEntry(iblock, 12, (0u, (ulong)journalStart, (ushort)journalBlocks));

        if (inodeSize > 128)
        {
            WriteU16(inode, 128, Ext4ExtraIsize);
        }

        if (enableMetadataCsum)
        {
            WriteInodeChecksum(inode, JournalInodeNumber, uuid);
        }

        WriteAt(output, baseOffset, inodeOffset, inode, inodeSize);
    }

    /// <summary>
    /// Writes a single inode: handles fast symlink / inline data / extent tree based on node type, sets mode, uid, gid, size, timestamps, and flags, then calls WriteInodeChecksum and WriteInodeXattrs.
    /// <para>写出单个 inode：根据节点类型处理快速符号链接 / 内联数据 / extent 树， 设置 mode、uid、gid、size、时间戳、flags，并调用 WriteInodeChecksum 和 WriteInodeXattrs。</para>
    /// </summary>
    private static void WriteInode(Stream output, long baseOffset, Node node, int blockSize, int inodeSize, List<GroupInfo> groups, int inodesPerGroup, byte[] uuid, bool enableMetadataCsum)
    {


        var inodeTableStartBlock = groups[(int)((node.Inode - 1) / (uint)inodesPerGroup)].InodeTableBlock;
        var localInodeIndex = (long)((node.Inode - 1) % (uint)inodesPerGroup);
        var inodeOffset = checked(inodeTableStartBlock * blockSize + localInodeIndex * inodeSize);
        var inode = new byte[inodeSize];
        var isFastSymlink = node.IsSymlink && node.Size < 60;

        var mode = node.Mode != 0 ? node.Mode : (node.IsDirectory ? ModeDirectory : node.IsSymlink ? ModeSymbolicLink : ModeRegularFile);
        var metaBlocks = new List<(long BlockAddress, byte[] Data)>();
        var flags = 0u;
        if (isFastSymlink)
        {
            var target = Encoding.UTF8.GetBytes(node.SymlinkTarget ?? string.Empty);
            target.AsSpan().CopyTo(inode.AsSpan(40, Math.Min(target.Length, 60)));
        }
        else if (node.IsInlineData)
        {
            flags = FlagInlineData;

            var fi = new FileInfo(node.FullPath);
            const int xattrEntry = 128 + Ext4ExtraIsize + 4;
            var maxLen = 60 + Math.Max(0, inodeSize - xattrEntry - 24);
            if (fi.Length > maxLen || inodeSize < xattrEntry + 24)
                throw new InvalidDataException("EXT4 inline data does not fit its system.data xattr.");
            var readLen = checked((int)fi.Length);
            var content = new byte[readLen];
            using (var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.ReadExactly(content);
            }
            var iBlockLen = Math.Min(content.Length, 60);
            if (iBlockLen > 0)
            {
                content.AsSpan(0, iBlockLen).CopyTo(inode.AsSpan(40));
            }
            WriteU16(inode, 128, Ext4ExtraIsize);
            WriteU32(inode, xattrEntry - 4, 0xEA020000);
            inode[xattrEntry] = 4;
            inode[xattrEntry + 1] = 7;
            "data"u8.CopyTo(inode.AsSpan(xattrEntry + 16));
            var extraLen = Math.Max(0, content.Length - 60);
            var valueOffset = (inodeSize - extraLen) & ~3;
            WriteU16(inode, xattrEntry + 2, checked((ushort)(valueOffset - xattrEntry)));
            WriteU32(inode, xattrEntry + 8, (uint)extraLen);
            if (extraLen > 0) content.AsSpan(60).CopyTo(inode.AsSpan(valueOffset));
        }
        else
        {
            flags = FlagExtents;
            metaBlocks = WriteExtentTree(
                inode.AsSpan(40, 60), node.Runs, node.DataStartBlock,
                node.LeafBlocks, node.IndexBlocks, blockSize);
        }

        if (node.IsDirectory && node.IsIndexed)
        {


            flags |= Ext4Htree.FlagIndex;
        }

        WriteU16(inode, 0, mode);
        WriteU16(inode, 2, (ushort)(node.Uid & 0xFFFF));
        WriteU32(inode, 4, (uint)(node.Size & 0xFFFFFFFF));
        WriteU32(inode, 8, node.Mtime != 0 ? node.Mtime : FixedTimestamp);
        WriteU32(inode, 12, node.Ctime != 0 ? node.Ctime : FixedTimestamp);
        WriteU32(inode, 16, node.Mtime != 0 ? node.Mtime : FixedTimestamp);
        WriteU32(inode, 20, 0);
        WriteU16(inode, 24, (ushort)(node.Gid & 0xFFFF));
        WriteU16(inode, 26, node.IsDirectory ? (ushort)(2 + node.Children.Count(static c => c.IsDirectory)) : (ushort)1);
        var allocatedBlocks = isFastSymlink || node.IsInlineData ? 0 : node.DataBlockCount + node.LeafBlocks.Length + node.IndexBlocks.Length;
        WriteU32(inode, 28, (uint)(allocatedBlocks * (blockSize / 512)));
        WriteU32(inode, 32, flags);
        if (inodeSize >= 256)
        {
            WriteU32(inode, 108, (uint)(node.Size >> 32));
        }


        if (inodeSize > 128 && !node.IsInlineData && !isFastSymlink)
        {
            WriteU16(inode, 128, Ext4ExtraIsize);
        }


        if (node.Xattrs is { Count: > 0 } && !node.IsInlineData && !isFastSymlink && inodeSize > 160)
        {
            WriteInodeXattrs(inode, inodeSize, node.Xattrs);
        }


        if (enableMetadataCsum)
        {
            WriteInodeChecksum(inode, node.Inode, uuid);
        }

        WriteAt(output, baseOffset, inodeOffset, inode, inodeSize);
        foreach (var (blockAddress, data) in metaBlocks)
        {
            var metaOffset = checked(blockAddress * blockSize);
            WriteAt(output, baseOffset, metaOffset, data, blockSize);
        }
    }

    /// <summary>
    /// Computes and writes the CRC32c inode checksum, matching e2fsprogs ext2fs_inode_csum: seed = crc32c(~0, uuid), then the LE32 inode number, LE32 i_generation and the whole inode are fed in (with checksum_lo at 124 / checksum_hi at 130 cleared first).
    /// <para>计算并写入 inode 的 CRC32c 校验和。</para>
    /// </summary>
    private static void WriteInodeChecksum(byte[] inode, uint inodeNumber, byte[] uuid)
    {


        var crc = ComputeCrc32(uuid, 0xFFFF_FFFF);

        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, inodeNumber);
        crc = ComputeCrc32Continue(crc, le);

        BinaryPrimitives.WriteUInt32LittleEndian(le, BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(100, 4)));
        crc = ComputeCrc32Continue(crc, le);

        var extraIsize = inode.Length > 128 ? BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(128, 2)) : (ushort)0;
        var hasHigh = inode.Length > 128 && extraIsize >= Ext4InodeChecksumHiExtraEnd;


        BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(124, 2), 0);
        if (hasHigh)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(130, 2), 0);
        }

        crc = ComputeCrc32Continue(crc, inode);

        WriteU16(inode, 124, (ushort)(crc & 0xFFFF));
        if (hasHigh)
        {
            WriteU16(inode, 130, (ushort)(crc >> 16));
        }
    }

    /// <summary>
    /// Writes inline extended attributes into the inode extra space: ibody xattr header, entries, and values.
    /// <para>将内联 xattr 写入 inode 额外空间：ibody xattr 头 + 条目 + 值。</para>
    /// </summary>
    internal static void WriteInodeXattrs(byte[] inode, int inodeSize, Dictionary<string, byte[]> xattrs)
    {


        const int extraIsiz = 28;
        var xattrStart = 128 + extraIsiz;
        var available = inodeSize - xattrStart - 4;
        if (available < 8)
        {
            return;
        }


        var entries = new List<(string Name, byte NameIndex, byte[] Value)>();
        foreach (var kv in xattrs)
        {
            if (TryMapXattrName(kv.Key, out var nameIndex, out var shortName))
            {
                entries.Add((shortName, nameIndex, kv.Value));
            }
        }

        if (entries.Count == 0)
        {
            return;
        }


        var entrySectionSize = 4;
        var totalValueSize = 0L;
        foreach (var (name, _, value) in entries)
        {
            var nameLen = Encoding.UTF8.GetByteCount(name);
            entrySectionSize += (16 + nameLen + 3) & ~3;
            totalValueSize += (value.Length + 3L) & ~3L;
        }

        if (entrySectionSize + totalValueSize > available)
        {
            return;
        }


        WriteU16(inode, 128, extraIsiz);


        var hdrOff = xattrStart;
        WriteU32(inode, hdrOff, 0xEA020000);


        var pos = xattrStart + 4;
        var valueOff = entrySectionSize;
        foreach (var (name, nameIndex, value) in entries)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var valuePadded = (value.Length + 3) & ~3;
            inode[pos] = (byte)nameBytes.Length;
            inode[pos + 1] = nameIndex;
            WriteU16(inode, pos + 2, (ushort)valueOff);
            WriteU32(inode, pos + 4, 0);
            WriteU32(inode, pos + 8, (uint)value.Length);
            // Kernel ibody attributes keep e_hash zero unless the value uses an EA inode.
            WriteU32(inode, pos + 12, 0);
            nameBytes.CopyTo(inode.AsSpan(pos + 16));
            var entrySize = (16 + nameBytes.Length + 3) & ~3;
            pos += entrySize;
            valueOff += valuePadded;
        }


        inode[pos] = 0;


        var valuePos = xattrStart + 4 + entrySectionSize;
        foreach (var (_, _, value) in entries)
        {
            value.CopyTo(inode, valuePos);
            valuePos += (value.Length + 3) & ~3;
        }
    }

    /// <summary>
    /// Maps user, trusted, security and POSIX ACL xattrs to their disk namespaces.
    /// <para>将 user、trusted、security 及 POSIX ACL 属性映射到磁盘命名空间。</para>
    /// </summary>
    private static bool TryMapXattrName(string fullName, out byte nameIndex, out string shortName)
    {
        if (fullName is "system.posix_acl_access" or "system.posix_acl_default")
        {
            nameIndex = fullName == "system.posix_acl_access" ? (byte)2 : (byte)3;
            shortName = string.Empty;
            return true;
        }
        (string Prefix, byte Index)[] namespaces =
        [
            ("user.", 1),
            ("trusted.", 4),
            ("security.", 6),
            ("system.", 7),
        ];
        foreach (var (prefix, index) in namespaces)
        {
            if (fullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = fullName[prefix.Length..];
                if (rest.Length == 0)
                {
                    continue;
                }


                if (index == 7)
                {
                    continue;
                }

                nameIndex = index;
                shortName = rest;
                return true;
            }
        }

        nameIndex = 0;
        shortName = string.Empty;
        return false;
    }

}
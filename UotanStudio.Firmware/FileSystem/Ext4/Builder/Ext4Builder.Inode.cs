using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Ext4.Journal;
using UotanStudio.Hash.Crc32c;

namespace UotanStudio.Firmware.FileSystem.Ext4.Builder;

/// <summary>
/// EXT4 builder part that lays out inode records: the internal journal area and inode 8, per-node inode encoding with block counts and <c>metadata_csum</c> checksums, and the inline extended-attribute area.
/// <para>EXT4 构建器的组成部分，负责 inode 记录的排布：内部日志区域与 inode 8、按节点编码 inode 的块计数与
/// <c>metadata_csum</c> 校验和，以及内联扩展属性区。</para>
/// </summary>
public sealed partial class Ext4Builder
{
    private const int InodeModeOffset = 0;
    private const int InodeUidLowOffset = 2;
    private const int InodeSizeLowOffset = 4;
    private const int InodeGidLowOffset = 24;
    private const int InodeChecksumLowOffset = 124;
    private const int InodeChecksumHighOffset = 130;

    /// <summary>
    /// Offset of the slot holding a device node's device number as written by this builder (the second direct-block slot, matching the parser's first-nonzero-slot read).
    /// <para>本构建器写入设备号的槽位偏移（第二个直接块槽，与解析器“首个非零槽”的读取方式对应）。</para>
    /// </summary>
    private const int InodeDeviceIdOffset = 44;

    /// <summary>
    /// Start of the inline xattr area inside the inode: the base inode size plus <see cref="Ext4ExtraIsize"/>, where the xattr header magic begins.
    /// <para>inode 内联 xattr 区的起始偏移：基础 inode 大小加 <see cref="Ext4ExtraIsize"/>，即 xattr 头部幻数所在位置。</para>
    /// </summary>
    private const int InodeInlineXattrHeaderOffset = 128 + Ext4ExtraIsize;

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

        WriteU16(inode, InodeModeOffset, ModeJournalFile);
        WriteU16(inode, InodeUidLowOffset, 0);
        WriteU32(inode, InodeSizeLowOffset, (uint)(size & 0xFFFFFFFF));
        WriteU32(inode, 8, FixedTimestamp);
        WriteU32(inode, 12, FixedTimestamp);
        WriteU32(inode, 16, FixedTimestamp);
        WriteU32(inode, 20, 0);
        WriteU16(inode, InodeGidLowOffset, 0);
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
        if (node.IsSpecial)
        {
            flags = 0;
            if ((node.Mode & 0xF000) is 0x2000 or 0x6000) WriteU32(inode, InodeDeviceIdOffset, node.DeviceId);
        }
        else if (isFastSymlink)
        {
            var target = Encoding.UTF8.GetBytes(node.SymlinkTarget ?? string.Empty);
            target.AsSpan().CopyTo(inode.AsSpan(40, Math.Min(target.Length, 60)));
        }
        else if (node.IsInlineData)
        {
            flags = FlagInlineData;

            var fi = new FileInfo(node.FullPath);
            const int xattrEntry = InodeInlineXattrHeaderOffset + 4;
            var maxLen = 60 + Math.Max(0, inodeSize - xattrEntry - 24);
            if (fi.Length > maxLen || inodeSize < xattrEntry + 24)
                throw new FileSystemCorruptedException("EXT4 inline data does not fit its system.data xattr.");
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
            WriteU32(inode, InodeInlineXattrHeaderOffset, Ext4Xattr.Magic);
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
                inode.AsSpan(40, 60), node.Extents,
                node.LeafBlocks, node.IndexBlocks, blockSize);
        }

        if (node.IsDirectory && node.IsIndexed)
        {

            flags |= Ext4Htree.FlagIndex;
        }

        WriteU16(inode, InodeModeOffset, mode);
        WriteU16(inode, InodeUidLowOffset, (ushort)(node.Uid & 0xFFFF));
        WriteU32(inode, InodeSizeLowOffset, (uint)(node.Size & 0xFFFFFFFF));
        WriteU32(inode, 8, node.Mtime != 0 ? node.Mtime : FixedTimestamp);
        WriteU32(inode, 12, node.Ctime != 0 ? node.Ctime : FixedTimestamp);
        WriteU32(inode, 16, node.Mtime != 0 ? node.Mtime : FixedTimestamp);
        WriteU32(inode, 20, 0);
        WriteU16(inode, InodeGidLowOffset, (ushort)(node.Gid & 0xFFFF));
        WriteU16(inode, 120, (ushort)(node.Uid >> 16));
        WriteU16(inode, 122, (ushort)(node.Gid >> 16));
        WriteU16(inode, 26, node.IsDirectory ? (ushort)(2 + node.Children.Count(static c => c.IsDirectory)) : node.LinkCount);
        var allocatedBlocks = isFastSymlink || node.IsInlineData ? 0 : node.DataBlockCount + node.LeafBlocks.Length + node.IndexBlocks.Length;
        var allocatedSectors = checked((ulong)(allocatedBlocks + node.AttributeValueBlocks + (node.ExternalXattrs is null ? 0 : 1)) * (uint)(blockSize / 512));
        WriteInodeBlockCount(inode, allocatedSectors, node.Inode);
        WriteU32(inode, 32, flags | node.SemanticFlags);
        if (inodeSize >= 256)
        {
            WriteU32(inode, 108, (uint)(node.Size >> 32));
        }

        if (inodeSize >= 128 + Ext4ExtraIsize)
        {
            WriteU16(inode, 128, Ext4ExtraIsize);
        }

        WriteSourceMetadata(inode, node.SourceMetadata);
        if (node.AttributeValue is { } attributeValue)
        {
            WriteU32(inode, 8, ComputeCrc32(attributeValue, ComputeCrc32(uuid, uint.MaxValue)));
            WriteU32(inode, 12, 0);
            WriteU32(inode, 36, 1);
        }
        if (node.ExternalXattrs is { } external)
        {
            WriteU32(inode, 104, (uint)node.XattrBlock);
            WriteU16(inode, 118, (ushort)((ulong)node.XattrBlock >> 32));
            if (enableMetadataCsum)
            {
                Span<byte> address = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(address, (ulong)node.XattrBlock);
                var checksum = ComputeCrc32Continue(ComputeCrc32(uuid, uint.MaxValue), address);
                WriteU32(external, 16, 0);
                WriteU32(external, 16, ComputeCrc32Continue(checksum, external));
            }
            WriteAt(output, baseOffset, node.XattrBlock * blockSize, external, blockSize);
        }
        else if (node.Xattrs is { Count: > 0 } && !node.IsInlineData)
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
            if (enableMetadataCsum)
            {
                var crc = ComputeCrc32(uuid, Crc32c.InitialSeed);
                var identity = new byte[8];
                WriteU32(identity, 0, node.Inode);
                WriteU32(identity, 4, node.SourceMetadata?.Generation ?? 0);
                crc = ComputeCrc32Continue(crc, identity);
                var tailOffset = 12 + EntriesPerBlock(blockSize) * 12;
                crc = ComputeCrc32Continue(crc, data.AsSpan(0, tailOffset));
                WriteU32(data, tailOffset, crc);
            }
            var metaOffset = checked(blockAddress * blockSize);
            WriteAt(output, baseOffset, metaOffset, data, blockSize);
        }
    }

    internal static void WriteInodeBlockCount(Span<byte> inode, ulong allocatedSectors, uint inodeNumber)
    {
        if (allocatedSectors > 0x0000_FFFF_FFFF_FFFFUL)
        {
            throw new InvalidOperationException($"EXT4 inode {inodeNumber} block count exceeds the 48-bit on-disk limit.");
        }
        WriteU32(inode, 28, (uint)allocatedSectors);
        WriteU16(inode, 116, (ushort)(allocatedSectors >> 32));
    }

    /// <summary>
    /// Computes and writes the CRC32c inode checksum: seed = crc32c(~0, uuid), then the LE32 inode number, LE32 i_generation and the whole inode with checksum_lo at 124 / checksum_hi at 130 cleared first.
    /// <para>计算并写入 inode 的 CRC32c 校验和：以 crc32c(~0, uuid) 为种子，依次喂入 LE32 inode 号、LE32 i_generation 与整个 inode（校验和字段 124/130 先清零）。</para>
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private static void WriteInodeChecksum(byte[] inode, uint inodeNumber, byte[] uuid)
    {

        var crc = ComputeCrc32(uuid, Crc32c.InitialSeed);

        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, inodeNumber);
        crc = ComputeCrc32Continue(crc, le);

        BinaryPrimitives.WriteUInt32LittleEndian(le, BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(100, 4)));
        crc = ComputeCrc32Continue(crc, le);

        var extraIsize = inode.Length > 128 ? BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(128, 2)) : (ushort)0;
        var hasHigh = inode.Length > 128 && extraIsize >= Ext4InodeChecksumHiExtraEnd;

        BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(InodeChecksumLowOffset, 2), 0);
        if (hasHigh)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(InodeChecksumHighOffset, 2), 0);
        }

        crc = ComputeCrc32Continue(crc, inode);

        WriteU16(inode, InodeChecksumLowOffset, (ushort)(crc & 0xFFFF));
        if (hasHigh)
        {
            WriteU16(inode, InodeChecksumHighOffset, (ushort)(crc >> 16));
        }
    }

    /// <summary>
    /// Writes inline extended attributes into the inode extra space: ibody xattr header, entries, and values.
    /// <para>将内联 xattr 写入 inode 额外空间：ibody xattr 头 + 条目 + 值。</para>
    /// </summary>
    internal static void WriteInodeXattrs(byte[] inode, int inodeSize, Dictionary<string, byte[]> xattrs)
    {

        const int extraIsiz = Ext4ExtraIsize;
        var xattrStart = InodeInlineXattrHeaderOffset;
        var available = inodeSize - xattrStart - 4;
        if (available < 8)
        {
            throw new FileSystemCorruptedException("EXT4 inode has no room for extended attributes.");
        }

        var entries = new List<(string Name, byte NameIndex, byte[] Value)>();
        foreach (var kv in xattrs)
        {
            if (TryMapXattrName(kv.Key, out var nameIndex, out var shortName))
            {
                FileSystemNameLimits.Validate(shortName, "EXT4");
                entries.Add((shortName, nameIndex, kv.Value));
            }
            else throw new FileSystemUnsupportedFeatureException($"Unsupported EXT4 xattr namespace: {kv.Key}");
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
            throw new FileSystemCorruptedException("EXT4 inode xattrs exceed available capacity; no attributes were discarded.");

        WriteU16(inode, 128, extraIsiz);

        var hdrOff = xattrStart;
        WriteU32(inode, hdrOff, Ext4Xattr.Magic);

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
            ("lustre.", 5),
            ("security.", 6),
            ("system.", 7),
            ("encryption.", 9),
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
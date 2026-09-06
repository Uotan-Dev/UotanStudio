using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Ext4.Core;
using UotanStudio.Firmware.Ext4.Journal;

namespace UotanStudio.Firmware.Ext4;

public sealed partial class Ext4Builder
{
    /// <summary>
    /// 写出日志区域：在 journalStart 处写入日志超级块，剩余块清零。
    /// <para>Writes the journal area: a journal superblock at <paramref name="journalStart"/>,
    /// with the remaining blocks zeroed out.</para>
    /// </summary>
    /// <param name="output">输出流 / The output stream.</param>
    /// <param name="baseOffset">文件系统基址偏移 / Filesystem base offset in the stream.</param>
    /// <param name="journalStart">日志起始块号 / Starting block number of the journal area.</param>
    /// <param name="journalBlocks">日志块数 / Number of blocks in the journal area.</param>
    /// <param name="blockSize">块大小（字节） / Block size in bytes.</param>
    /// <param name="uuid">文件系统 UUID / Filesystem UUID (16 bytes).</param>
    private static void WriteJournalArea(Stream output, long baseOffset, long journalStart, int journalBlocks, int blockSize, byte[] uuid)
    {
        // mke2fs 1.47 默认不启用 JBD2 校验和；镜像内置日志保持相同布局，e2fsck 才将其视为干净日志。
        // mke2fs 1.47 defaults to no JBD2 checksums; the built-in journal mirrors that layout so that
        // e2fsck treats it as a clean journal.
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
    /// 写出日志 inode（inode 8，普通文件、extents）：单条连续 extent 映射日志区域全部物理块，
    /// i_size = journalBlocks × 块大小，并按 metadata_csum 计算 inode 校验和。
    /// <para>Writes the journal inode (#8, regular file with extents): a single contiguous extent maps
    /// the whole journal area, i_size = journalBlocks × block size, with the inode checksum computed
    /// under metadata_csum.</para>
    /// </summary>
    /// <param name="output">输出流 / The output stream.</param>
    /// <param name="baseOffset">文件系统基址偏移 / Filesystem base offset in the stream.</param>
    /// <param name="groups">块组信息列表 / List of block group descriptors.</param>
    /// <param name="inodesPerGroup">每组 inode 数 / Number of inodes per group.</param>
    /// <param name="blockSize">块大小（字节） / Block size in bytes.</param>
    /// <param name="inodeSize">inode 大小（字节） / Inode size in bytes.</param>
    /// <param name="uuid">文件系统 UUID / Filesystem UUID (16 bytes).</param>
    /// <param name="journalStart">日志起始块号 / Starting block number of the journal.</param>
    /// <param name="journalBlocks">日志块数 / Number of journal blocks.</param>
    /// <param name="enableMetadataCsum">是否启用元数据校验和 / Whether metadata_csum is enabled.</param>
    private static void WriteJournalInode(Stream output, long baseOffset, List<GroupInfo> groups, int inodesPerGroup,
        int blockSize, int inodeSize, byte[] uuid, long journalStart, int journalBlocks, bool enableMetadataCsum)
    {
        var group = groups[(int)((JournalInodeNumber - 1) / (uint)inodesPerGroup)];
        var localInode = (long)((JournalInodeNumber - 1) % (uint)inodesPerGroup);
        var inodeOffset = checked(group.InodeTableBlock * blockSize + localInode * inodeSize);
        var inode = new byte[inodeSize];
        var size = checked((long)journalBlocks * blockSize);
        var sectorBlocks = (ulong)journalBlocks * ((ulong)blockSize / 512);

        WriteU16(inode, 0, ModeJournalFile);                 // S_IFREG | 0600
        WriteU16(inode, 2, 0);                               // uid
        WriteU32(inode, 4, (uint)(size & 0xFFFFFFFF));       // i_size_lo
        WriteU32(inode, 8, FixedTimestamp);                  // atime
        WriteU32(inode, 12, FixedTimestamp);                 // ctime
        WriteU32(inode, 16, FixedTimestamp);                 // mtime
        WriteU32(inode, 20, 0);                              // dtime
        WriteU16(inode, 24, 0);                              // gid
        WriteU16(inode, 26, 1);                              // links_count
        WriteU32(inode, 28, (uint)sectorBlocks);             // i_blocks_lo (512-byte units)
        WriteU32(inode, 32, FlagExtents);                    // i_flags: EXTENTS
        if (inodeSize >= 256)
        {
            WriteU32(inode, 108, (uint)((ulong)size >> 32)); // i_size_hi
        }

        // 单条 extent：逻辑块 0..journalBlocks-1 → 物理块 journalStart..journalStart+journalBlocks-1。
        // Single extent: logical blocks 0..journalBlocks-1 → physical blocks journalStart... .
        var iblock = inode.AsSpan(40, 60);
        WriteExtentHeader(iblock, 1, 0, 4);
        WriteExtentEntry(iblock, 12, (0u, (ulong)journalStart, (ushort)journalBlocks));

        if (inodeSize > 128)
        {
            WriteU16(inode, 128, Ext4ExtraIsize);            // i_extra_isize
        }

        if (enableMetadataCsum)
        {
            WriteInodeChecksum(inode, JournalInodeNumber, uuid);
        }

        WriteAt(output, baseOffset, inodeOffset, inode, inodeSize);
    }

    /// <summary>
    /// 写出单个 inode：根据节点类型处理快速符号链接 / 内联数据 / extent 树，
    /// 设置 mode、uid、gid、size、时间戳、flags，并调用 <see cref="WriteInodeChecksum"/> 和
    /// <see cref="WriteInodeXattrs"/>。
    /// <para>Writes a single inode: handles fast symlink / inline data / extent tree based on node type,
    /// sets mode, uid, gid, size, timestamps, and flags, then calls <see cref="WriteInodeChecksum"/>
    /// and <see cref="WriteInodeXattrs"/>.</para>
    /// </summary>
    /// <param name="output">输出流 / The output stream.</param>
    /// <param name="baseOffset">文件系统基址偏移 / Filesystem base offset in the stream.</param>
    /// <param name="node">要写出的文件系统节点 / The filesystem node to write.</param>
    /// <param name="blockSize">块大小（字节） / Block size in bytes.</param>
    /// <param name="inodeSize">inode 大小（字节） / Inode size in bytes.</param>
    /// <param name="groups">块组信息列表 / List of block group descriptors.</param>
    /// <param name="inodesPerGroup">每组 inode 数 / Number of inodes per group.</param>
    /// <param name="uuid">文件系统 UUID / Filesystem UUID (16 bytes).</param>
    /// <param name="enableMetadataCsum">是否启用元数据校验和 / Whether metadata_csum is enabled.</param>
    private static void WriteInode(Stream output, long baseOffset, Node node, int blockSize, int inodeSize, List<GroupInfo> groups, int inodesPerGroup, byte[] uuid, bool enableMetadataCsum)
    {
        // inode 偏移：inode 表起始块 + inode 在表内的偏移。
        // Inode offset: inode table start block + inode offset within table.
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
            // 内联数据：写入 i_block 区域（60字节，偏移40）+ 额外 inode 空间（偏移128起）。
            var fi = new FileInfo(node.FullPath);
            var maxLen = 60 + (inodeSize > 128 ? inodeSize - 128 : 0);
            var readLen = (int)Math.Min(fi.Length, maxLen);
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
            if (content.Length > 60 && inodeSize > 128)
            {
                var extraLen = Math.Min(content.Length - 60, inodeSize - 128);
                content.AsSpan(60, extraLen).CopyTo(inode.AsSpan(128));
            }
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
            // EXT4_INDEX_FL：目录使用 htree 索引。
            // EXT4_INDEX_FL: the directory is indexed with an htree.
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

        // 256 字节 inode 且未用扩展区存数据时，声明 28 字节扩展字段（覆盖 i_checksum_hi@130），
        // 使 inode 校验和可写满 32 位；内联数据/快速符号链接不使用扩展区，保持 extra_isize=0。
        // For 256-byte inodes whose extra area is free, declare the 28-byte extended region (which covers
        // i_checksum_hi at 130) so the inode checksum can use the full 32 bits. Inline-data inodes and
        // fast symlinks leave extra_isize at 0 because their extra area carries content.
        if (inodeSize > 128 && !node.IsInlineData && !isFastSymlink)
        {
            WriteU16(inode, 128, Ext4ExtraIsize);
        }

        // Write inline xattrs if present (skip on inline-data inodes where the
        // extra inode space is already occupied by file content).
        // 如果存在内联 xattr 则写入（跳过内联数据 inode，其额外 inode 空间已被文件内容占用）。
        if (node.Xattrs is { Count: > 0 } && !node.IsInlineData && !isFastSymlink && inodeSize > 160)
        {
            WriteInodeXattrs(inode, inodeSize, node.Xattrs);
        }

        // inode 尾部校验和（metadata_csum，对照 e2fsprogs ext2fs_inode_csum）。
        // Inode checksum (metadata_csum), computed like e2fsprogs ext2fs_inode_csum.
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
    /// 计算并写入 inode 的 CRC32c 校验和。算法对照 e2fsprogs ext2fs_inode_csum：
    /// seed = crc32c(~0, uuid)，续算 4 字节小端 inode 号、4 字节小端 i_generation 与整块 inode
    /// （checksum_lo@124、checksum_hi@130 在计算前清零）；inode 超过 128 字节且 extra_isize ≥ 4 时
    /// lo@124 = crc 低 16 位、hi@130 = 高 16 位，否则仅写 lo。
    /// <para>Computes and writes the CRC32c inode checksum, matching e2fsprogs ext2fs_inode_csum:
    /// seed = crc32c(~0, uuid), then the LE32 inode number, LE32 i_generation and the whole inode are
    /// fed in (with checksum_lo at 124 / checksum_hi at 130 cleared first). When the inode is larger
    /// than 128 bytes and extra_isize ≥ 4, lo@124 holds the low 16 bits and hi@130 the high 16 bits;
    /// otherwise only lo is written.</para>
    /// </summary>
    /// <param name="inode">inode 字节数组 / The inode byte array to write the checksum into.</param>
    /// <param name="inodeNumber">inode 编号 / The inode number.</param>
    /// <param name="uuid">文件系统 UUID / Filesystem UUID (16 bytes).</param>
    private static void WriteInodeChecksum(byte[] inode, uint inodeNumber, byte[] uuid)
    {
        // fs csum_seed = crc32c_le(~0, uuid)（未启用 metadata_csum_seed 特性时）。
        // Filesystem checksum seed: crc32c_le(~0, uuid) when the csum_seed feature is off.
        var crc = ComputeCrc32(uuid, 0xFFFF_FFFF);

        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(le, inodeNumber);
        crc = ComputeCrc32Continue(crc, le);

        BinaryPrimitives.WriteUInt32LittleEndian(le, BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(100, 4)));
        crc = ComputeCrc32Continue(crc, le);

        var extraIsize = inode.Length > 128 ? BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(128, 2)) : (ushort)0;
        var hasHigh = inode.Length > 128 && extraIsize >= Ext4InodeChecksumHiExtraEnd;

        // 清零校验和字段后对整块 inode 续算 CRC。
        // Clear the checksum fields, then continue the CRC over the whole inode.
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
    /// 将内联 xattr 写入 inode 额外空间：ibody xattr 头 + 条目 + 值。
    /// 完整属性名按 e2fsprogs ext2_xattr.h 的命名空间索引映射（security.selinux →
    /// index 6 + 短名 "selinux"），e_hash 按内核 ext4_xattr_hash_entry 算法计算
    /// （名字逐字节，值按 LE32 字续算，尾随字节逐字节）。
    /// <para>Writes inline extended attributes into the inode extra space: ibody xattr header,
    /// entries, and values. Full attribute names are mapped to their e2fsprogs ext2_xattr.h
    /// namespace index (security.selinux → index 6 + short name "selinux"), and e_hash follows the
    /// kernel's ext4_xattr_hash_entry algorithm (name bytes, then the value in LE32 words, then
    /// trailing bytes).</para>
    /// </summary>
    /// <param name="inode">inode 字节数组 / The inode byte array.</param>
    /// <param name="inodeSize">inode 大小（字节） / Inode size in bytes.</param>
    /// <param name="xattrs">要写入的扩展属性键值对 / Dictionary of attribute name to value bytes.</param>
    private static void WriteInodeXattrs(byte[] inode, int inodeSize, Dictionary<string, byte[]> xattrs)
    {
        // Extended inode fields occupy bytes 128..155 (28 bytes for a standard ext4 inode).
        // 扩展 inode 字段占据 128..155（标准 ext4 inode 为 28 字节）。
        const int extraIsiz = 28;
        var xattrStart = 128 + extraIsiz;
        var available = inodeSize - xattrStart - 4; // minus the 4-byte ibody header
        if (available < 8)
        {
            return;
        }

        // 映射并收集可写入的条目（无法映射命名空间的属性跳过）。
        // Map and collect writable entries (unmappable namespaces are skipped).
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

        // Compute total entry section size.
        var entrySectionSize = 4; // null terminator (4 bytes: zero name_len + padding)
        var totalValueSize = 0L;
        foreach (var (name, _, value) in entries)
        {
            var nameLen = Encoding.UTF8.GetByteCount(name);
            entrySectionSize += (16 + nameLen + 3) & ~3;
            entrySectionSize += (value.Length + 3) & ~3;
            totalValueSize += value.Length;
        }

        if (entrySectionSize + totalValueSize > available)
        {
            return; // does not fit / 空间不足
        }

        // Write i_extra_isize.
        WriteU16(inode, 128, extraIsiz);

        // Write xattr ibody header.
        var hdrOff = xattrStart;
        WriteU32(inode, hdrOff, 0xEA020000); // EXT4_XATTR_MAGIC

        // Write entries and collect value data.
        var pos = xattrStart + 4;
        var valueOff = entrySectionSize; // values start after entries (relative to entries start)
        foreach (var (name, nameIndex, value) in entries)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var valuePadded = (value.Length + 3) & ~3;
            inode[pos] = (byte)nameBytes.Length;          // e_name_len
            inode[pos + 1] = nameIndex;                   // e_name_index（命名空间索引）
            WriteU16(inode, pos + 2, (ushort)valueOff);   // e_value_offs (relative to entries start)
            WriteU32(inode, pos + 4, (uint)value.Length); // e_value_size
            WriteU32(inode, pos + 8, XattrEntryHash(nameBytes, value)); // e_hash（内核算法）
            nameBytes.CopyTo(inode.AsSpan(pos + 12));
            var entrySize = (16 + nameBytes.Length + 3) & ~3;
            pos += entrySize;
            valueOff += valuePadded;
        }

        // Null-terminator entry (e_name_len = 0).
        inode[pos] = 0;

        // Write values after the entry section.
        var valuePos = xattrStart + 4 + entrySectionSize;
        foreach (var (_, _, value) in entries)
        {
            value.CopyTo(inode, valuePos);
            valuePos += (value.Length + 3) & ~3;
        }
    }

    /// <summary>
    /// 把完整 xattr 属性名映射为 (命名空间索引, 短名)，对照 e2fsprogs ext2_xattr.h：
    /// user.→1、trusted.→4、security.→6、system.→7（system 仅限两个 ACL 名）。
    /// <para>Maps a full xattr name to (namespace index, short name) per e2fsprogs ext2_xattr.h:
    /// user.→1, trusted.→4, security.→6, system.→7 (system limited to the two ACL names).</para>
    /// </summary>
    private static bool TryMapXattrName(string fullName, out byte nameIndex, out string shortName)
    {
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

                // system 命名空间仅承载两个已知 ACL 名；system.data（inline data）由内核内部使用。
                // The system namespace only carries the two known ACL names; system.data (inline
                // data) is kernel-internal.
                if (index == 7 && rest is not ("posix_acl_access" or "posix_acl_default"))
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

    /// <summary>
    /// 计算单个 xattr 条目的 e_hash（内核 fs/ext4/xattr.c ext4_xattr_hash_entry）：
    /// 名字按（有符号）字节续算，随后值按 LE32 字、尾随 <c>size % 4</c> 字节续算。
    /// <para>Computes a single xattr entry's e_hash (kernel fs/ext4/xattr.c
    /// ext4_xattr_hash_entry): the name is fed byte-wise (signed char), then the value in LE32
    /// words followed by any trailing bytes.</para>
    /// </summary>
    private static uint XattrEntryHash(byte[] name, byte[] value)
    {
        uint hash = 0;
        foreach (var b in name)
        {
            hash = (hash << 5) ^ (hash >> 27) ^ (uint)(sbyte)b;
        }

        var words = value.Length / 4;
        for (var i = 0; i < words; i++)
        {
            hash = (hash << 5) ^ (hash >> 27) ^ System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(i * 4, 4));
        }

        for (var i = words * 4; i < value.Length; i++)
        {
            hash = (hash << 5) ^ (hash >> 27) ^ (uint)(sbyte)value[i];
        }

        return hash;
    }
}
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Models;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// EROFS builder part that selects the compact or extended inode header, fills the inode fields for every data layout, and measures or writes the bin-packed directory blocks.
/// <para>EROFS 构建器的组成部分，负责在紧凑与扩展 inode 头之间选择、按各种数据布局填充 inode 字段，
/// 并测量与写出贪心装箱的目录块。</para>
/// </summary>
public sealed partial class ErofsBuilder
{
    private static long EstimateDirPayloadLength(BuildNode node)
        => 2 * ErofsDirectoryEntry.DirentSize + 1 + 2
           + node.Children.Sum(child => (long)ErofsDirectoryEntry.DirentSize + Encoding.UTF8.GetByteCount(child.Name));

    /// <summary>
    /// Builds the directory entries: "." and ".." first (mkfs keeps dot entries by default and fsck enforces their presence), then children ordered by their UTF-8 bytes (matches the mergesort in mkfs and fsck's strictly-increasing check). The dirent name rule guards only those children, so the synthesised dot records stay legal.
    /// <para>构造目录条目：`. ` 与 `..` 在前（mkfs 默认不省略点条目，fsck 强制检查二者存在）， 随后子条目按 UTF-8 字节序排序（与 mkfs mergesort / fsck 递增校验一致）。目录项名称规则只校验这些子条目，因此合成的点条目保持合法。</para>
    /// </summary>
    private static List<DirEntry> BuildDirEntries(BuildNode node)
    {
        var dotNid = node.Nid;
        var dotDotNid = node.Parent?.Nid ?? node.Nid;
        var entries = new List<DirEntry>(node.Children.Count + 2)
        {
            new DirEntry(new BuildNode { Nid = dotNid, IsDirectory = true, Name = "." }, "."u8.ToArray()),
            new DirEntry(new BuildNode { Nid = dotDotNid, IsDirectory = true, Name = ".." }, ".."u8.ToArray()),
        };
        entries.AddRange(node.Children
            .Select(child =>
            {
                FileSystemNameLimits.ValidateEntryName(child.Name, "EROFS");
                return new DirEntry(child, Encoding.UTF8.GetBytes(child.Name));
            })
            .OrderBy(e => e.NameBytes, ByteArrayComparer.Instance));
        return entries;
    }

    /// <summary>
    /// Derives the dirent file type from a BuildNode.
    /// <para>根据 BuildNode 推导 dirent 文件类型。</para>
    /// </summary>
    private static byte DirentFileType(BuildNode node)
    {
        if (node.IsDirectory) return ErofsInode.FileTypeDirectory;
        if (node.IsSymlink) return ErofsInode.FileTypeSymlink;
        if (node.IsDevice)
        {
            var type = node.DeviceMode & ErofsInode.ModeTypeMask;
            if (type == ErofsInode.ModeCharacterDevice) return ErofsInode.FileTypeCharacterDevice;
            if (type == ErofsInode.ModeBlockDevice) return ErofsInode.FileTypeBlockDevice;
            if (type == ErofsInode.ModeFifo) return ErofsInode.FileTypeFifo;
            if (type == ErofsInode.ModeSocket) return ErofsInode.FileTypeSocket;
        }

        return ErofsInode.FileTypeRegularFile;
    }

    /// <summary>
    /// Single-block directory payload: the dirent array first, then the contiguous name area (de[i].nameoff = name-area start + the sum of preceding name lengths), isomorphic to the kernel fill_dentries / mkfs fill_dirblock.
    /// <para>目录单块负载：dirent 数组在前、名字区紧随（de[i].nameoff = 名字区起点 + 之前名字长 之和）。</para>
    /// </summary>
    private static byte[] BuildDirPayload(BuildNode node)
    {
        var sorted = BuildDirEntries(node);
        var totalNames = sorted.Sum(e => e.NameBytes.Length);
        var payload = new byte[sorted.Count * ErofsDirectoryEntry.DirentSize + totalNames];
        var nameOff = sorted.Count * ErofsDirectoryEntry.DirentSize;
        for (var i = 0; i < sorted.Count; i++)
        {
            var (child, nameBytes) = (sorted[i].Child, sorted[i].NameBytes);
            var slot = payload.AsSpan(i * ErofsDirectoryEntry.DirentSize, ErofsDirectoryEntry.DirentSize);
            BinaryPrimitives.WriteUInt64LittleEndian(slot, child.Nid);
            BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(8), (ushort)nameOff);
            slot[10] = DirentFileType(child);
            slot[11] = 0;
            nameBytes.CopyTo(payload.AsSpan(nameOff, nameBytes.Length));
            nameOff += nameBytes.Length;
        }

        return payload;
    }

    private readonly record struct DirEntry(BuildNode Child, byte[] NameBytes);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var n = Math.Min(x.Length, y.Length);
            for (var i = 0; i < n; i++)
            {
                var d = x[i].CompareTo(y[i]);
                if (d != 0) return d;
            }

            return x.Length.CompareTo(y.Length);
        }
    }

    /// <summary>
    /// Computes the exact directory payload length and block count with the same greedy bin-packing
    /// as WriteDirBlocks: entries never cross a block, and a block-boundary gap is inserted before
    /// the next block (matching upstream erofs_prepare_dir_file's d_size accumulation). The final
    /// block is not padded, so the length is not necessarily a multiple of the block size.
    /// <para>按与 WriteDirBlocks 完全相同的贪心装箱计算目录负载的精确长度与块数：条目不跨块，
    /// 跨块前插入块边界空余（与上游 erofs_prepare_dir_file 的 d_size 累加一致）。末块不补齐，
    /// 因此长度不一定是块大小的整数倍。</para>
    /// </summary>
    private static (long Blocks, long Size) MeasureDirLayout(List<DirEntry> sorted, int blockSize)
    {
        var blocks = 0L;
        var size = 0L;
        var index = 0;
        while (index < sorted.Count)
        {
            var direntCount = 0;
            var namesLength = 0;
            while (index < sorted.Count)
            {
                var entry = sorted[index];
                var need = (direntCount + 1) * ErofsDirectoryEntry.DirentSize
                           + namesLength + entry.NameBytes.Length;
                if (need > blockSize)
                {
                    break;
                }

                direntCount++;
                namesLength += entry.NameBytes.Length;
                index++;
            }

            if (direntCount == 0)
            {

                index++;
            }

            size += direntCount * (long)ErofsDirectoryEntry.DirentSize + namesLength;
            blocks++;
            if (index < sorted.Count)
            {
                size = AlignUp(size, blockSize);
            }
        }

        return (blocks, size);
    }

    /// <summary>
    /// Writes sorted directory entries into blocks with separate entry and name areas.
    /// <para>将排序后的目录条目按块写入，每块包含独立的条目区与名称区。</para>
    /// </summary>
    private static void WriteDirBlocks(Stream output, BuildNode node, int blockSize, CancellationToken token)
    {
        var sorted = BuildDirEntries(node);
        var index = 0;
        for (var block = 0L; block < node.DirBlocks; block++)
        {
            token.ThrowIfCancellationRequested();
            if (index >= sorted.Count)
            {
                break;
            }

            var direntCount = 0;
            var namesLength = 0;
            while (index < sorted.Count)
            {
                var entry = sorted[index];
                var need = (direntCount + 1) * ErofsDirectoryEntry.DirentSize
                           + namesLength + entry.NameBytes.Length;
                if (need > blockSize)
                {
                    break;
                }

                direntCount++;
                namesLength += entry.NameBytes.Length;
                index++;
            }

            if (direntCount == 0)
            {
                break;
            }

            var blockData = new byte[blockSize];
            var nameOff = direntCount * ErofsDirectoryEntry.DirentSize;
            for (var i = 0; i < direntCount; i++)
            {
                var (child, nameBytes) = (sorted[index - direntCount + i].Child, sorted[index - direntCount + i].NameBytes);
                var slot = blockData.AsSpan(i * ErofsDirectoryEntry.DirentSize, ErofsDirectoryEntry.DirentSize);
                BinaryPrimitives.WriteUInt64LittleEndian(slot, child.Nid);
                BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(8), (ushort)nameOff);
                slot[10] = DirentFileType(child);
                slot[11] = 0;
                nameBytes.CopyTo(blockData.AsSpan(nameOff, nameBytes.Length));
                nameOff += nameBytes.Length;
            }

            WriteAt(output, (node.DirBlockAddr + block) * blockSize, blockData, blockData.Length);
        }
    }

    private static long GetInodeRecordSize(BuildNode node, int blockSize)
    {
        var header = GetInodeHeaderSize(node) + node.XattrIbody.Length;

        if (node.IsSymlink)
        {
            return header + node.Size;
        }

        if (node.IsDevice)
        {
            return header;
        }

        if (node.IsDirectory)
        {
            return node.DirectoryInline ? header + EstimateDirPayloadLength(node) : header;
        }

        if (node.Pclusters is not null)
        {
            var tailSize = node.InlineCompressedTail ? node.Pclusters[^1].CompressedLength : 0;
            if (node.CompactedIndexes)
            {

                return AlignUp(header, 8) + ZErofsMapHeader.Size + 8L * CeilDiv(node.ClusterCount, 2L) + tailSize;
            }

            return AlignUp(header, 8) + ZErofsMapHeader.Size + 8L + node.ClusterCount * ZErofsLclusterIndex.Size + tailSize;
        }

        if (node.IsFragment)
        {

            return AlignUp(header, 8) + ZErofsMapHeader.Size;
        }

        return header + node.InlineData.Length;
    }

    /// <summary>
    /// Rejects a symlink whose inline inode record cannot fit one metablock, measuring the record with
    /// <see cref="GetInodeRecordSize"/> so the header choice, inline xattrs and payload bytes match the
    /// write pass exactly. The kernel resolves an inline symlink target inside the mapped metablock that
    /// holds the inode, so a record longer than one block is unreadable rather than a long link, and
    /// <c>mkfs.erofs</c> refuses such input instead of truncating the target. <paramref name="node"/>
    /// keeps its size equal to the UTF-8 byte count of the target, which is the single measurement used
    /// for both the record bound and the inline payload.
    /// <para>拒绝 inode 记录无法容纳单个 metablock 的符号链接，并使用 <see cref="GetInodeRecordSize"/>
    /// 度量记录长度，使头部选择、inline xattr 与负载字节与写出阶段完全一致。内核在承载该 inode 的
    /// 已映射 metablock 内解析 inline 符号链接目标，因此超过一个块的记录不是长链接而是不可读数据，
    /// <c>mkfs.erofs</c> 同样是拒绝输入而不是截断目标。节点的 size 保持等于目标的 UTF-8 字节数，
    /// 这一份度量同时用于记录上限与 inline 负载。</para>
    /// </summary>
    /// <param name="node">Symlink node about to carry its target as inline payload.
    /// <para>即将把目标作为 inline 负载的符号链接节点。</para></param>
    /// <param name="blockSize">Image block size in bytes from the validated options.
    /// <para>来自经校验选项的镜像块大小（字节）。</para></param>
    /// <exception cref="InvalidOperationException">The symlink inode record needs more than one block.
    /// <para>符号链接的 inode 记录超过一个块。</para></exception>
    private static void ValidateSymlinkRecordCapacity(BuildNode node, int blockSize)
    {
        var targetBytes = Encoding.UTF8.GetByteCount(node.SymlinkTarget);
        node.Size = targetBytes;
        var recordSize = GetInodeRecordSize(node, blockSize);
        if (recordSize <= blockSize)
        {
            return;
        }

        throw new InvalidOperationException(
            $"EROFS symlink '{node.Name}' has a {targetBytes}-byte target that does not fit the {blockSize}-byte block (inode record {recordSize} bytes).");
    }

    private static long GetInodeHeaderSize(BuildNode node)
    {
        if (node.SourceInode is { Extended: true } || node.RequiresExtendedTime) return ErofsInode.ExtendedSize;
        if (node.Uid > ushort.MaxValue || node.Gid > ushort.MaxValue || node.LinkCount > ushort.MaxValue) return ErofsInode.ExtendedSize;

        if (node.IsDirectory)
        {
            var subdirs = (ulong)node.Children.Count(child => child.IsDirectory);
            return node.Size > uint.MaxValue || 2 + subdirs > ushort.MaxValue
                ? ErofsInode.ExtendedSize
                : ErofsInode.CompactSize;
        }

        return node.Size > uint.MaxValue ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
    }

    private static ErofsInode BuildInode(BuildNode node, int blockSize, ulong now, bool legacyCompatibility)
    {
        var inode = new ErofsInode { AllowNlinkOneBit = !legacyCompatibility };
        if (GetInodeHeaderSize(node) == ErofsInode.ExtendedSize)
        {
            inode.Advise |= ErofsInode.FlagExtended;
        }

        var effectiveTime = node.SourceInode?.Mtime ?? (node.Mtime > 0 ? (ulong)node.Mtime : now);
        inode.CTime = effectiveTime;
        inode.CTimeNsec = node.SourceInode?.MtimeNanoseconds ?? 0;
        inode.Uid = node.Uid;
        inode.Gid = node.Gid;
        inode.Generation = inode.IsExtended ? 0 : checked((uint)(effectiveTime - now));

        if (node.XattrIbody.Length > 0)
        {
            inode.XattrICount = ErofsXattr.GetICountForIbodyLength(node.XattrIbody.Length);
        }

        if (node.IsSymlink)
        {
            inode.Mode = (ushort)(ErofsInode.ModeSymlink | (node.HasModeOverride ? (node.ModeOverride & 0xFFF) : 0x1FF));
            inode.Nlink = node.LinkCount;
            inode.Size = (ulong)node.Size;
            inode.Advise |= ErofsInode.DataLayoutInline;
            inode.U = uint.MaxValue;
            node.InlineData = Encoding.UTF8.GetBytes(node.SymlinkTarget);
            return inode;
        }

        if (node.IsDevice)
        {
            inode.Mode = node.DeviceMode;
            inode.Nlink = node.LinkCount;
            inode.Size = 0;
            inode.Advise |= ErofsInode.DataLayoutFlat;
            inode.U = node.DeviceRdev;
            return inode;
        }

        if (node.IsDirectory)
        {
            inode.Mode = (ushort)(ErofsInode.ModeDirectory | (node.HasModeOverride ? (node.ModeOverride & 0xFFF) : 0x1ED));
            inode.Nlink = (ulong)(2 + node.Children.Count(child => child.IsDirectory));
            inode.Size = (ulong)node.DirPayloadSize;
            if (node.DirectoryInline)
            {

                inode.Advise |= ErofsInode.DataLayoutInline;
                inode.U = uint.MaxValue;
            }
            else
            {
                inode.Advise |= ErofsInode.DataLayoutFlat;
                inode.U = (uint)node.DirBlockAddr;
            }
        }
        else
        {
            inode.Mode = (ushort)(ErofsInode.ModeRegularFile | (node.HasModeOverride ? (node.ModeOverride & 0xFFF) : 0x1A4));
            inode.Nlink = node.LinkCount;
            inode.Size = (ulong)node.Size;
            if (node.Pclusters is not null)
            {

                inode.Advise |= node.CompactedIndexes
                    ? ErofsInode.DataLayoutCompactIndexes
                    : ErofsInode.DataLayoutCompressed;
                inode.U = node.CompressedBlocks;
            }
            else if (node.IsFragment)
            {

                inode.Advise |= ErofsInode.DataLayoutCompressed;
                inode.U = 0;
            }
            else if (node.FlatBlocks > 0)
            {

                inode.Advise |= node.InlineData.Length > 0 ? ErofsInode.DataLayoutInline : ErofsInode.DataLayoutFlat;
                inode.U = (uint)node.FlatBlockAddr;
            }
            else if (node.InlineData.Length > 0)
            {

                inode.Advise |= ErofsInode.DataLayoutInline;
                inode.U = uint.MaxValue;
            }
            else
            {

                inode.Advise |= ErofsInode.DataLayoutFlat;
                inode.U = 0;
            }
        }

        return inode;
    }
}
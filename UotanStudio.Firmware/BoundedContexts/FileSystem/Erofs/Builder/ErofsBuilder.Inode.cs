using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed partial class ErofsBuilder
{
    private static long EstimateDirPayloadLength(BuildNode node)
        => 2 * ErofsDirectoryEntry.DirentSize + 1 + 2
           + node.Children.Sum(child => (long)ErofsDirectoryEntry.DirentSize + Encoding.UTF8.GetByteCount(child.Name));

    /// <summary>
    /// Builds the directory entries: "." and ".." first (mkfs keeps dot entries by default and fsck enforces their presence), then children ordered by their UTF-8 bytes (matches the mergesort in mkfs and fsck's strictly-increasing check).
    /// <para>构造目录条目：`. ` 与 `..` 在前（mkfs 默认不省略点条目，fsck 强制检查二者存在）， 随后子条目按 UTF-8 字节序排序（与 mkfs mergesort / fsck 递增校验一致）。</para>
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
            .Select(child => new DirEntry(child, Encoding.UTF8.GetBytes(child.Name)))
            .OrderBy(e => e.NameBytes, ByteArrayComparer.Instance));
        return entries;
    }

    /// <summary>
    /// Single-block directory payload: the dirent array first, then the contiguous name area (de[i].nameoff = name-area start + the sum of preceding name lengths), isomorphic to the kernel fill_dentries / mkfs fill_dirblock.
    /// <para>目录单块负载：dirent 数组在前、名字区紧随（de[i].nameoff = 名字区起点 + 之前名字长 之和）。</para>
    /// </summary>

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
    /// Computes the exact number of directory blocks using the same greedy bin-packing as WriteDirBlocks (each block = a dirent array followed by its name area; names never cross blocks and block-tail slack is not reused by later entries).
    /// <para>按与 WriteDirBlocks 完全相同的贪心装箱计算多块目录所需块数 （每块 = dirent 数组 + 紧随的名字区；名字不跨块，块尾空余不参与后续打包）。</para>
    /// </summary>
    private static long CountDirBlocks(List<DirEntry> sorted, int blockSize)
    {
        var blocks = 0L;
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

            blocks++;
        }

        return blocks;
    }

    /// <summary>
    /// Writes sorted directory entries into blocks with separate entry and name areas.
    /// <para>将排序后的目录条目按块写入，每块包含独立的条目区与名称区。</para>
    /// </summary>
    private static void WriteDirBlocks(Stream output, BuildNode node, int blockSize)
    {
        var sorted = BuildDirEntries(node);
        var index = 0;
        for (var block = 0L; block < node.DirBlocks; block++)
        {
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

    private static ErofsInode BuildInode(BuildNode node, int blockSize, ulong now)
    {
        var inode = new ErofsInode();
        if (GetInodeHeaderSize(node) == ErofsInode.ExtendedSize)
        {
            inode.Advise |= ErofsInode.FlagExtended;
        }


        var effectiveTime = node.SourceInode?.Mtime ?? (node.Mtime > 0 ? (ulong)node.Mtime : now);
        inode.CTime = effectiveTime;
        inode.CTimeNsec = node.SourceInode?.MtimeNanoseconds ?? 0;
        inode.Uid = node.Uid;
        inode.Gid = node.Gid;
        // Compact i_mtime is relative to the superblock epoch. Offset 12 is not a generation number.
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
            inode.Size = node.DirectoryInline
                ? (ulong)EstimateDirPayloadLength(node)
                : (ulong)(node.DirBlocks * blockSize);
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
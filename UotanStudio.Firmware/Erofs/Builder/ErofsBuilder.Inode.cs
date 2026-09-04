using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

public sealed partial class ErofsBuilder
{
    private static long EstimateDirPayloadLength(BuildNode node)
        => 2 * ErofsDirectoryEntry.DirentSize + 1 + 2
           + node.Children.Sum(child => (long)ErofsDirectoryEntry.DirentSize + Encoding.UTF8.GetByteCount(child.Name));

    /// <summary>
    /// 构造目录条目：`. ` 与 `..` 在前（mkfs 默认不省略点条目，fsck 强制检查二者存在），
    /// 随后子条目按 UTF-8 字节序排序（与 mkfs mergesort / fsck 递增校验一致）。
    /// <para>Builds the directory entries: "." and ".." first (mkfs keeps dot entries by default
    /// and fsck enforces their presence), then children ordered by their UTF-8 bytes (matches the
    /// mergesort in mkfs and fsck's strictly-increasing check).</para>
    /// </summary>
    private static List<DirEntry> BuildDirEntries(BuildNode node)
    {
        var dotNid = node.Nid;
        var dotDotNid = node.Parent?.Nid ?? node.Nid; // root 的 .. 指向自身。Root's .. points at itself.
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
    /// 目录单块负载：dirent 数组在前、名字区紧随（de[i].nameoff = 名字区起点 + 之前名字长
    /// 之和）。与内核 fill_dentries / mkfs fill_dirblock 同构。
    /// <para>Single-block directory payload: the dirent array first, then the contiguous name area
    /// (de[i].nameoff = name-area start + the sum of preceding name lengths), isomorphic to the
    /// kernel fill_dentries / mkfs fill_dirblock.</para>
    /// </summary>

    /// <summary>根据 BuildNode 推导 dirent 文件类型。Derives the dirent file type from a BuildNode.</summary>
    private static byte DirentFileType(BuildNode node)
    {
        if (node.IsDirectory) return ErofsInode.FileTypeDirectory;
        if (node.IsSymlink) return ErofsInode.FileTypeSymlink;
        if (node.IsDevice)
        {
            var type = node.DeviceMode & ErofsInode.ModeTypeMask;
            if (type == ErofsInode.ModeCharacterDevice) return ErofsInode.FileTypeCharacterDevice;
            if (type == ErofsInode.ModeBlockDevice) return ErofsInode.FileTypeBlockDevice;
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

    /// <summary>按 UTF-8 字节序比较名字（与内核 memcmp / fsck 校验一致）。Compares names by UTF-8 bytes.</summary>
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
    /// 按与 <see cref="WriteDirBlocks"/> 完全相同的贪心装箱计算多块目录所需块数
    /// （每块 = dirent 数组 + 紧随的名字区；名字不跨块，块尾空余不参与后续打包）。
    /// <para>Computes the exact number of directory blocks using the same greedy bin-packing as
    /// <see cref="WriteDirBlocks"/> (each block = a dirent array followed by its name area; names
    /// never cross blocks and block-tail slack is not reused by later entries).</para>
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
                    break; // 放不下该条目：封块。Cannot fit: seal the block.
                }

                direntCount++;
                namesLength += entry.NameBytes.Length;
                index++;
            }

            if (direntCount == 0)
            {
                // 防御：单个条目超过一块时仍消耗一块并推进（实际不可能：dirent+名字 ≤ 12+255）。
                // Guard: a single oversized entry still consumes a block (unreachable in practice:
                // a dirent + name is at most 12 + 255 bytes).
                index++;
            }

            blocks++;
        }

        return blocks;
    }

    /// <summary>
    /// 多块目录：把排序后的条目按块切分，每块是独立的“dirent 数组 + 名字区”
    /// （与单块内联负载 <see cref="BuildDirPayload"/> 同构：de[0].nameoff = dirent 数组字节数，
    /// 名字紧随数组连续存放、不跨块；块尾空余置零）。
    /// <para>Multi-block directory: sorted entries are split into independent blocks, each holding a
    /// "dirent array + name area" (isomorphic to the single-block inline payload
    /// <see cref="BuildDirPayload"/>: de[0].nameoff is the dirent-array byte count, names follow the
    /// array contiguously and never cross blocks; the block tail is zeroed).</para>
    /// </summary>
    private static void WriteDirBlocks(Stream output, BuildNode node, int blockSize)
    {
        var sorted = BuildDirEntries(node);
        var index = 0;
        for (var block = 0L; block < node.DirBlocks; block++)
        {
            if (index >= sorted.Count)
            {
                break; // 无剩余条目：尾部块保持全零。No entries left: leave the tail block zeroed.
            }

            // 贪心装填本块（与 CountDirBlocks 同一规则，保证写满 DirBlocks 且不越界）。
            // Greedily fill this block (same rule as CountDirBlocks, so all DirBlocks are written
            // without ever exceeding the reserved data range).
            var direntCount = 0;
            var namesLength = 0;
            while (index < sorted.Count)
            {
                var entry = sorted[index];
                var need = (direntCount + 1) * ErofsDirectoryEntry.DirentSize
                           + namesLength + entry.NameBytes.Length;
                if (need > blockSize)
                {
                    break; // 放不下该条目：封块。Cannot fit: seal the block.
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

        // 符号链接：目标字符串内联在 inode 之后。Symlink: target string inlined after inode.
        if (node.IsSymlink)
        {
            return header + node.Size;
        }

        // 设备节点：无内联数据。Device nodes: no inline data.
        if (node.IsDevice)
        {
            return header;
        }

        if (node.IsDirectory)
        {
            return node.DirBlocks <= 1 ? header + EstimateDirPayloadLength(node) : header;
        }

        if (node.Pclusters is not null)
        {
            if (node.CompactedIndexes)
            {
                // compacted 4B amortized 记录 = inode + 8B map header + 位打包索引表
                //（每 2 簇一个 8B pack：2×encodebits 位 + pack 尾 4B blkaddr）。
                // Compacted 4B-amortized record = inode + 8B map header + the bit-packed index
                // table (one 8-byte pack per two clusters: 2×encodebits bits + a 4-byte trailing
                // pack-start block address).
                return header + ZErofsMapHeader.Size + 8L * CeilDiv(node.ClusterCount, 2L);
            }

            // 压缩记录 = inode + map header(8B) + 保留(8B) + nclusters × 8B 索引。
            // Compressed record = inode + 8B map header + 8B pad + nclusters × 8B index.
            return header + ZErofsMapHeader.Size + 8L + node.ClusterCount * ZErofsLclusterIndex.Size;
        }

        if (node.IsFragment)
        {
            // fragment inode 记录 = inode + xattr + 8 对齐填充 + 8B fragment map header（读端按
            // inode+xattr 后 8 字节对齐读取 header，故记录须含填充）。
            // Fragment record = inode + xattr + 8-byte alignment pad + the 8-byte fragment map
            // header (the reader locates the header 8-byte aligned after inode + xattrs, so the
            // record must cover the pad).
            return AlignUp(header, 8) + ZErofsMapHeader.Size;
        }

        // 未压缩文件：整块部分在数据区，尾部内联在 inode 之后。
        // Uncompressed file: whole blocks live in the data area, the remainder follows the inode.
        return header + node.InlineData.Length;
    }

    private static long GetInodeHeaderSize(BuildNode node)
    {
        // compact inode(32B) 是 mkfs 默认（odm 文件/目录均为 compact）；仅当 size 超 32 位、
        // 目录 nlink 超 16 位时使用 extended(64B)。
        // The compact 32-byte inode is the mkfs default (odm files/dirs are compact); the extended
        // 64-byte form is only needed for sizes beyond 32 bits or 16-bit directory nlinks.
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
        if (node.Size > uint.MaxValue)
        {
            inode.Advise |= ErofsInode.FlagExtended;
        }

        // 使用源文件 mtime（非零时），否则回退到构建时间。Use source mtime if available, else build time.
        var effectiveTime = node.Mtime > 0 ? (ulong)node.Mtime : now;
        inode.CTime = effectiveTime;
        inode.CTimeNsec = 0;
        inode.Uid = node.Uid;
        inode.Gid = node.Gid;
        inode.Generation = (uint)effectiveTime;
        // inline xattr ibody 紧随 inode：XattrICount 与 ibody 长度满足
        // XattrTotalSize = 12 + 4 * (XattrICount - 1)（读端按该公式还原 ibody 大小）。
        // The inline xattr ibody follows the inode: XattrICount satisfies
        // XattrTotalSize = 12 + 4 * (XattrICount - 1) so the reader recovers the ibody size.
        if (node.XattrIbody.Length > 0)
        {
            inode.XattrICount = ErofsXattr.GetICountForIbodyLength(node.XattrIbody.Length);
        }

        // 符号链接：S_IFLNK|0777，目标字符串内联。Symlink: S_IFLNK|0777, target inlined.
        if (node.IsSymlink)
        {
            inode.Mode = ErofsInode.ModeSymlink | 0x1FF; // S_IFLNK | 0777
            inode.Nlink = 1;
            inode.Size = (ulong)node.Size;
            inode.Advise |= ErofsInode.DataLayoutInline;
            inode.U = uint.MaxValue; // EROFS_NULL_ADDR
            node.InlineData = Encoding.UTF8.GetBytes(node.SymlinkTarget);
            return inode;
        }

        // 字符/块设备：DataLayoutFlat，设备号写入 i_u。Char/block device: flat, rdev in i_u.
        if (node.IsDevice)
        {
            inode.Mode = node.DeviceMode;
            inode.Nlink = 1;
            inode.Size = 0;
            inode.Advise |= ErofsInode.DataLayoutFlat;
            inode.U = node.DeviceRdev;
            return inode;
        }

        if (node.IsDirectory)
        {
            inode.Mode = ErofsInode.ModeDirectory | 0x1ED; // S_IFDIR | 0755
            inode.Nlink = (ulong)(2 + node.Children.Count(child => child.IsDirectory));
            inode.Size = node.DirBlocks <= 1
                ? (ulong)EstimateDirPayloadLength(node)
                : (ulong)(node.DirBlocks * blockSize);
            if (node.DirBlocks <= 1)
            {
                // 目录数据内联：i_u = 空洞哨兵（EROFS_NULL_ADDR）。
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
            inode.Mode = ErofsInode.ModeRegularFile | 0x1A4; // S_IFREG | 0644
            inode.Nlink = 1;
            inode.Size = (ulong)node.Size;
            if (node.Pclusters is not null)
            {
                // 压缩：i_u 存压缩后物理块数。索引布局按 <see cref="ErofsOptions.UseCompactedIndexes"/>
                // 选择：legacy full 8B 索引（EROFS_INODE_COMPRESSED_FULL，默认）或 compacted
                // 4B amortized 位打包索引（EROFS_INODE_COMPRESSED_COMPACT）。
                // Compressed: i_u holds the compressed physical block count. The index layout is
                // selected via ErofsOptions.UseCompactedIndexes: legacy full 8-byte indexes
                // (COMPRESSED_FULL, default) or compacted 4-byte amortized bit-packed indexes
                // (COMPRESSED_COMPACT).
                inode.Advise |= node.CompactedIndexes
                    ? ErofsInode.DataLayoutCompactIndexes
                    : ErofsInode.DataLayoutCompressed;
                inode.U = node.CompressedBlocks;
            }
            else if (node.IsFragment)
            {
                // 整文件汇入 packed inode：以压缩布局承载 fragment 元数据（8 字节 map header 的
                // h_clusterbits bit7 声明整文件 fragment，i_u 无物理块语义，恒 0）。
                // Whole file packed into the packed inode: the compressed layout carries the
                // fragment metadata (h_clusterbits bit 7 of the 8-byte map header marks a
                // whole-file fragment; i_u carries no physical-block meaning and stays 0).
                inode.Advise |= ErofsInode.DataLayoutCompressed;
                inode.U = 0;
            }
            else if (node.FlatBlocks > 0)
            {
                // 主体整块 flat：有尾部内联时为 FLAT_INLINE（tail-packing），否则纯 FLAT。
                // Whole blocks are flat; with an inline remainder this is FLAT_INLINE
                // (tail-packing), otherwise plain FLAT.
                inode.Advise |= node.InlineData.Length > 0 ? ErofsInode.DataLayoutInline : ErofsInode.DataLayoutFlat;
                inode.U = (uint)node.FlatBlockAddr;
            }
            else if (node.InlineData.Length > 0)
            {
                // 整个文件不足一块：全部内联，i_u = 空洞哨兵（EROFS_NULL_ADDR）。
                // The whole file is smaller than a block: fully inlined, i_u = hole sentinel.
                inode.Advise |= ErofsInode.DataLayoutInline;
                inode.U = uint.MaxValue;
            }
            else
            {
                // 空文件。Empty file.
                inode.Advise |= ErofsInode.DataLayoutFlat;
                inode.U = 0;
            }
        }

        return inode;
    }
}

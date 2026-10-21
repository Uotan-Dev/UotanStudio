using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using ErofsInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// EROFS builder part that selects the compact or extended inode header, fills the inode fields for every data layout, and measures or writes the bin-packed directory blocks.
/// <para>EROFS 构建器的组成部分，负责在紧凑与扩展 inode 头之间选择、按各种数据布局填充 inode 字段，
/// 并测量与写出贪心装箱的目录块。</para>
/// </summary>
public sealed partial class ErofsBuilder
{
    private static long EstimateDirPayloadLength(ErofsInode node)
        => 2 * ErofsDirectoryEntry.DirentSize + 1 + 2
           + node.Children.Sum(child => (long)ErofsDirectoryEntry.DirentSize + Encoding.UTF8.GetByteCount(child.Name));

    /// <summary>
    /// Builds the directory entries as one list of the synthesised "."/".." records and the children, then
    /// orders the whole list by UTF-8 name bytes. The dot entries participate in the same sort instead of
    /// staying pinned in front, because names must be strictly increasing; the dirent name rule guards only
    /// the children, so the synthesised dot records stay legal.
    /// <para>构造目录条目：把合成的 `.`、`..` 记录与子条目放进同一个列表，再对整表按 UTF-8 名称字节排序。
    /// 名称必须严格递增，因此点条目参与同一次排序而不能固定在最前。目录项名称规则只校验子条目，
    /// 因此合成的点条目保持合法。</para>
    /// </summary>
    private static List<DirEntry> BuildDirEntries(ErofsInode node)
    {
        var dotNid = node.Nid;
        var dotDotNid = node.Parent?.Nid ?? node.Nid;
        var entries = new List<DirEntry>(node.Children.Count + 2)
        {
            new DirEntry(new ErofsInode { Nid = dotNid, IsDirectory = true, Name = "." }, "."u8.ToArray()),
            new DirEntry(new ErofsInode { Nid = dotDotNid, IsDirectory = true, Name = ".." }, ".."u8.ToArray()),
        };
        entries.AddRange(node.Children.Select(child =>
        {
            FileSystemNameLimits.ValidateEntryName(child.Name, "EROFS");
            return new DirEntry(child, Encoding.UTF8.GetBytes(child.Name));
        }));
        return [.. entries.OrderBy(e => e.NameBytes, ByteArrayComparer.Instance)];
    }

    /// <summary>
    /// Derives the dirent file type from a ErofsInode.
    /// <para>根据 ErofsInode 推导 dirent 文件类型。</para>
    /// </summary>
    private static byte DirentFileType(ErofsInode node)
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
    /// Single-block directory payload: the dirent array first, then the contiguous name area
    /// (de[i].nameoff = name-area start + the sum of preceding name lengths).
    /// <para>目录单块负载：dirent 数组在前、名字区紧随（de[i].nameoff = 名字区起点 + 之前名字长 之和）。</para>
    /// </summary>
    private static byte[] BuildDirPayload(ErofsInode node)
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

    private readonly record struct DirEntry(ErofsInode Child, byte[] NameBytes);

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
    /// the next block. The final block is not padded, so the length is not necessarily a multiple
    /// of the block size.
    /// <para>按与 WriteDirBlocks 完全相同的贪心装箱计算目录负载的精确长度与块数：条目不跨块，
    /// 跨块前插入块边界空余。末块不补齐，因此长度不一定是块大小的整数倍。</para>
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
    private static void WriteDirBlocks(Stream output, ErofsInode node, int blockSize, CancellationToken token)
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

    private static long GetInodeRecordSize(ErofsInode node, int blockSize)
    {
        var header = GetInodeHeaderSize(node) + node.XattrIbody.Length;

        if (node.IsSymlink)
        {
            return header + (long)node.Size;
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
            if (UsesExtentIndexes(node))
            {

                var indexBase = checked((int)AlignUp(header, 8));
                var extentRecordSize = ZErofsMapHeader.ExtentRecordSize(node.ZAdvise);
                var extentCount = checked((int)node.ZExtents);
                return ZErofsMapHeader.ExtentMetadataSize(indexBase, extentRecordSize, extentCount) + tailSize;
            }

            if (node.CompactedIndexes)
            {
                var indexBase = AlignUp(header, 8) + ZErofsMapHeader.Size;
                if (node.Compacted2B)
                {
                    var (initial, twoByte, end) = Compacted2BSplit(indexBase, node.ClusterCount);
                    return indexBase + 8L * CeilDiv(initial, 2L) + 2L * twoByte + 8L * CeilDiv(end, 2L) + tailSize;
                }

                return indexBase + 8L * CeilDiv(node.ClusterCount, 2L) + tailSize;
            }

            return AlignUp(header, 8) + ZErofsMapHeader.Size + 8L + node.ClusterCount * ZErofsLclusterIndex.Size + tailSize;
        }

        if (node.ChunkIndex is not null)
        {

            var unit = node.UsesChunkIndexes ? ErofsInode.ChunkIndexEntrySize : ErofsInode.BlockMapEntrySize;
            return AlignUp(header, unit) + ((long)node.ChunkIndex.Length * unit);
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
    /// write pass exactly. An inline symlink target is resolved inside the mapped metablock that holds the
    /// inode, so a record longer than one block is unreadable rather than a long link, which is why such
    /// input is rejected instead of truncating the target. <paramref name="node"/>
    /// keeps its size equal to the UTF-8 byte count of the target, which is the single measurement used
    /// for both the record bound and the inline payload.
    /// <para>拒绝 inode 记录无法容纳单个 metablock 的符号链接，并使用 <see cref="GetInodeRecordSize"/>
    /// 度量记录长度，使头部选择、inline xattr 与负载字节与写出阶段完全一致。inline 符号链接目标是在承载该
    /// inode 的已映射 metablock 内解析的，因此超过一个块的记录不是长链接而是不可读数据，输入会被拒绝而不是
    /// 截断目标。节点的 size 保持等于目标的 UTF-8 字节数，
    /// 这一份度量同时用于记录上限与 inline 负载。</para>
    /// </summary>
    /// <param name="node">Symlink node about to carry its target as inline payload.
    /// <para>即将把目标作为 inline 负载的符号链接节点。</para></param>
    /// <param name="blockSize">Image block size in bytes from the validated options.
    /// <para>来自经校验选项的镜像块大小（字节）。</para></param>
    /// <exception cref="InvalidOperationException">The symlink inode record needs more than one block.
    /// <para>符号链接的 inode 记录超过一个块。</para></exception>
    private static void ValidateSymlinkRecordCapacity(ErofsInode node, int blockSize)
    {
        var targetBytes = Encoding.UTF8.GetByteCount(node.SymlinkTarget);
        node.Size = (ulong)targetBytes;
        var recordSize = GetInodeRecordSize(node, blockSize);
        if (recordSize <= blockSize)
        {
            return;
        }

        throw new InvalidOperationException(
            $"EROFS symlink '{node.Name}' has a {targetBytes}-byte target that does not fit the {blockSize}-byte block (inode record {recordSize} bytes).");
    }

    /// <summary>
    /// Reports the inode record width one node needs. A preserved source's recorded metadata creation type
    /// overrides both the metadata-driven width and the caller-declared format, because it names the form the
    /// source image actually used; a recorded compact form whose fields do not fit it fails loudly instead of
    /// silently widening, and a forced format that contradicts the recorded one fails rather than being
    /// ignored. Without a recorded type the automatic path keeps its existing precedence — an extended source
    /// record, an extended timestamp, a wide owner or link count, then the size and directory-subdirectory
    /// bounds.
    /// <para>报告单个节点所需的 inode 记录宽度。保留式源记录的元数据创建形态优先于按元数据推导的宽度与调用方声明的
    /// 形态，因为它指名源镜像实际使用的形式；记录的紧凑形式装不下其字段时显式报错而非静默加宽，而声明形态与记录
    /// 形态冲突时同样报错而不是忽略记录。没有记录形态时，自动路径保持既有优先级——扩展源记录、扩展时间戳、宽所有者
    /// 或链接数，随后是文件大小与目录子目录数上界。</para>
    /// </summary>
    /// <param name="node">Node about to be measured. <para>即将被度量的节点。</para></param>
    /// <returns>The record width in bytes. <para>记录宽度（字节）。</para></returns>
    /// <exception cref="FileSystemUnsupportedFeatureException">A recorded or forced compact record cannot hold
    /// one of the node's fields, or the forced format contradicts the recorded creation form.
    /// <para>记录或强制的紧凑记录装不下节点的某个字段，或强制形态与记录的创建形态冲突。</para></exception>
    private static long GetInodeHeaderSize(ErofsInode node)
    {
        if (node.MetadataCreationType is { } creationType)
        {
            if (node.InodeFormat != ErofsInodeFormat.Auto)
            {
                var forcedExtended = node.InodeFormat == ErofsInodeFormat.Extended;
                if (forcedExtended != creationType.IsExtended)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{node.FullPath}' was recorded with a "
                        + $"{(creationType.IsExtended ? "extended" : "compact")} metadata record, but the build "
                        + $"forces the {(forcedExtended ? "extended" : "compact")} form, so the recorded creation "
                        + "form cannot be honoured.");
                }
            }

            if (creationType.IsExtended)
            {
                return ErofsInode.ExtendedSize;
            }

            var recordedReason = CompactRecordRejection(node, recordedCompact: true);
            if (recordedReason is not null)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS preserved inode '{node.FullPath}' was recorded with a compact metadata record, but "
                    + $"its fields need the extended record: {recordedReason}.");
            }

            return ErofsInode.CompactSize;
        }

        if (node.InodeFormat == ErofsInodeFormat.Extended)
        {
            return ErofsInode.ExtendedSize;
        }

        if (node.InodeFormat == ErofsInodeFormat.Compact)
        {
            var reason = CompactRecordRejection(node);
            if (reason is not null)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS compact inodes were forced, but '{node.FullPath}' needs the extended record: {reason}.");
            }

            return ErofsInode.CompactSize;
        }

        if (node.SourceInode is { IsExtended: true } || node.RequiresExtendedTime) return ErofsInode.ExtendedSize;
        if (node.Uid > ushort.MaxValue || node.Gid > ushort.MaxValue || node.Nlink > ushort.MaxValue) return ErofsInode.ExtendedSize;

        if (node.IsDirectory)
        {
            var subdirs = (ulong)node.Children.Count(child => child.IsDirectory);
            return node.Size > uint.MaxValue || 2 + subdirs > ushort.MaxValue
                ? ErofsInode.ExtendedSize
                : ErofsInode.CompactSize;
        }

        return node.Size > uint.MaxValue ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
    }

    /// <summary>
    /// Reports why a node cannot be stored in the 32-byte compact record, or <see langword="null"/> when it
    /// fits. Every field the extended record widens is checked, so a forced compact build names the exact
    /// dimension that overflowed instead of failing with a generic message. A compact record whose width the
    /// source image recorded keeps the volume-wide fixed nanoseconds, so <paramref name="recordedCompact"/>
    /// does not let the presence of that volume-wide value demand the extended per-inode field; the delta
    /// itself is validated when the record is serialized.
    /// <para>报告节点为何无法存入 32 字节紧凑记录，能装下时为 <see langword="null"/>。检查扩展记录加宽的每个
    /// 字段，使强制紧凑构建指名溢出的确切维度，而不是给出笼统信息。源镜像记录了宽度为紧凑的记录时，其纳秒取自
    /// 卷级固定值，因此 <paramref name="recordedCompact"/> 不会仅因该卷级值存在就要求扩展的逐 inode 字段；
    /// 增量本身在序列化记录时校验。</para>
    /// </summary>
    /// <param name="node">Node being tested. <para>待判定的节点。</para></param>
    /// <param name="recordedCompact">Whether the compact width comes from a recorded creation type rather than
    /// a forced build option. <para>紧凑宽度是来自记录的创建形态而不是强制的构建选项。</para></param>
    /// <returns>The reason in English, or <see langword="null"/>. <para>英文原因，或 <see langword="null"/>。</para></returns>
    private static string? CompactRecordRejection(ErofsInode node, bool recordedCompact = false)
    {
        if (node.SourceInode is { IsExtended: true })
        {
            return "the source image recorded an extended inode";
        }

        if (!recordedCompact && node.RequiresExtendedTime)
        {
            return "its timestamp needs the extended form";
        }

        if (node.Uid > ushort.MaxValue || node.Gid > ushort.MaxValue)
        {
            return $"uid {node.Uid}/gid {node.Gid} exceed {ushort.MaxValue}";
        }

        if (node.Nlink > ushort.MaxValue)
        {
            return $"link count {node.Nlink} exceeds {ushort.MaxValue}";
        }

        if (node.Size > uint.MaxValue)
        {
            return $"size {node.Size} exceeds {uint.MaxValue}";
        }

        if (node.IsDirectory)
        {
            var subdirs = (ulong)node.Children.Count(child => child.IsDirectory);
            if (2 + subdirs > ushort.MaxValue)
            {
                return $"2 + {subdirs} subdirectories exceed {ushort.MaxValue}";
            }
        }

        return null;
    }

    /// <summary>
    /// Fills one node's on-disk fields and addressing state before it is serialized. The advise bit shared
    /// by <c>EROFS_I_NLINK_1</c> and dot-omitted directories follows the resolved declaration and is emitted
    /// only for a target that defines the bit, so a compact inode never carries a bit the declared feature set
    /// forbids nor one the target's advise mask would reject, while the superblock word and the
    /// per-node layout stay in agreement. A source inode whose recorded format bits are known is mirrored one
    /// inode at a time instead of taking the image-level answer, so a source that raised the bit on only part of
    /// its inodes is not flattened back to a single whole-image decision. A recorded source inode number is
    /// replayed so the rebuilt inode publishes the same 32-bit <c>stat</c> identity as the source, while a node
    /// the source recorded no number for keeps the number the writer assigned it.
    /// <para>在序列化之前填充单个节点的磁盘字段与寻址状态。<c>EROFS_I_NLINK_1</c> 与省略点项目录共用的
    /// advise 位跟随已解析的声明，并且只为定义该位的目标写出，因此紧凑 inode 不会携带声明特性集禁止的位，
    /// 也不会携带目标 advise 掩码会拒绝的位，超级块字与逐节点布局也始终一致。已知记录了源格式位的 inode 按
    /// 逐 inode 镜像，而不是套用镜像级结论，因此源只在部分 inode 上置起该位时不会被抹平为整幅镜像的单一决定。
    /// 已记录的源 inode 编号会被重放，使重建 inode 发布与源相同的 32 位 <c>stat</c> 身份；源未记录编号的
    /// 节点保持写入方分配给它的编号。</para>
    /// </summary>
    private static void BuildInode(ErofsInode node, int blockSize, ulong now, ErofsOptions options)
    {
        if (node.SourceInode is { Ino: > 0 } recordedIno)
        {
            node.Ino = recordedIno.Ino;
        }

        var advertiseNlinkOneAdvice = DeclaredNlinkOneAdvice(options) && KernelDefinesNlinkOneAdvice(options);
        node.AllowNlinkOneBit = node.SourceInode is { SourceIFormatRecorded: true } recordedFormat
            ? advertiseNlinkOneAdvice
                && (recordedFormat.IFormat & ErofsInode.AdviseBitNlinkOneOrDotOmitted) != 0
            : advertiseNlinkOneAdvice;
        node.IFormat = GetInodeHeaderSize(node) == ErofsInode.ExtendedSize ? ErofsInode.FlagExtended : (ushort)0;

        var effectiveTime = node.SourceInode?.CTime ?? (node.CTime is > 0 and <= long.MaxValue ? node.CTime : now);
        node.CTime = effectiveTime;
        node.CTimeNsec = node.SourceInode?.CTimeNsec ?? node.CTimeNsec;
        if (node.IsExtended)
        {
            node.Generation = 0;
        }
        else
        {
            if (effectiveTime < now || effectiveTime - now > uint.MaxValue)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS inode '{node.FullPath}' must use the compact record, but its mtime {effectiveTime} "
                    + $"is not a non-negative delta of at most {uint.MaxValue} seconds from the {now} epoch, "
                    + "which is the only timestamp form the compact record can store.");
            }

            node.Generation = (uint)(effectiveTime - now);
        }

        if (node.XattrIbody.Length > 0)
        {
            node.XattrICount = ErofsXattr.GetICountForIbodyLength(node.XattrIbody.Length);
        }

        node.XattrISize = node.XattrICount == 0 ? 0 : 12 + 4 * (node.XattrICount - 1);

        if (node.IsSymlink)
        {
            node.Mode = (ushort)(ErofsInode.ModeSymlink | (node.HasModeOverride ? (node.ModeOverride & 0xFFF) : 0x1FF));
            node.InlineData = Encoding.UTF8.GetBytes(node.SymlinkTarget);
            node.Size = (ulong)node.InlineData.Length;
            node.DataLayout = ErofsDataLayout.Inline;
            node.Idata = new ErofsFlatIdata { BlockAddress = uint.MaxValue };
            return;
        }

        if (node.IsDevice)
        {
            node.Mode = node.DeviceMode;
            node.Size = 0;
            node.DataLayout = ErofsDataLayout.Flat;
            node.Idata = new ErofsFlatIdata { BlockAddress = node.DeviceRdev };
            return;
        }

        if (node.IsDirectory)
        {
            node.Mode = (ushort)(ErofsInode.ModeDirectory | (node.HasModeOverride ? (node.ModeOverride & 0xFFF) : 0x1ED));
            node.Nlink = (ulong)(2 + node.Children.Count(child => child.IsDirectory));
            node.Size = (ulong)node.DirPayloadSize;
            if (node.DirectoryInline)
            {

                node.DataLayout = ErofsDataLayout.Inline;
                node.Idata = new ErofsFlatIdata { BlockAddress = uint.MaxValue };
            }
            else
            {
                node.DataLayout = ErofsDataLayout.Flat;
                node.Idata = new ErofsFlatIdata { BlockAddress = (uint)node.DirBlockAddr };
            }

            return;
        }

        node.Mode = (ushort)(ErofsInode.ModeRegularFile | (node.HasModeOverride ? (node.ModeOverride & 0xFFF) : 0x1A4));
        if (node.Pclusters is not null)
        {

            node.DataLayout = node.CompactedIndexes
                ? ErofsDataLayout.CompactIndexes
                : ErofsDataLayout.Compressed;
        }
        else if (node.ChunkIndex is not null)
        {

            var chunkFormat = node.ChunkFormatBlkBits;
            if (chunkFormat == 0 && node.SourceInode is { } chunkSource)
            {
                chunkFormat = chunkSource.ChunkFormatBlkBits;
            }

            var recordedChunkFormat = node.SourceInode?.ChunkFormat ?? (ushort)0;
            var needs48Bit = false;
            foreach (var startBlock in node.ChunkIndex!)
            {
                if (startBlock > uint.MaxValue)
                {
                    needs48Bit = true;
                    break;
                }
            }

            var wideFormNeeded = needs48Bit || options.ExtraDeviceCount > 0 || node.DeviceId != 0
                || (node.SourceInode?.UsesChunkIndexes ?? false);
            if (options.ChunkIndexKind == ErofsChunkIndexKind.BlockMap && wideFormNeeded)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS 4-byte chunk block maps were forced, but '{node.FullPath}' cannot be expressed by "
                    + "them: " + (needs48Bit
                        ? "a start block exceeds the 32-bit block map field"
                        : "its payload carries a device id or a device-shaped layout")
                    + ".");
            }

            if (options.ChunkIndexKind == ErofsChunkIndexKind.Indexes || wideFormNeeded
                || (recordedChunkFormat & ErofsInode.ChunkFormatIndexes) != 0)
            {
                chunkFormat |= ErofsInode.ChunkFormatIndexes;
            }

            if (needs48Bit || (recordedChunkFormat & ErofsInode.ChunkFormat48Bit) != 0)
            {
                chunkFormat |= ErofsInode.ChunkFormat48Bit;
            }

            if (node.Idata is not ErofsChunkIdata chunkIdata)
            {
                chunkIdata = new ErofsChunkIdata();
                node.Idata = chunkIdata;
            }

            chunkIdata.ChunkFormat = (ushort)chunkFormat;
            chunkIdata.DeviceId = node.DeviceId;
            node.DataLayout = ErofsDataLayout.ChunkBased;
        }
        else if (node.IsFragment)
        {

            node.DataLayout = ErofsDataLayout.Compressed;
            node.Idata = new ErofsCompressedIdata();
        }
        else if (node.FlatBlocks > 0)
        {

            node.DataLayout = node.InlineData.Length > 0 ? ErofsDataLayout.Inline : ErofsDataLayout.Flat;
            node.Idata = new ErofsFlatIdata { BlockAddress = (uint)node.FlatBlockAddr };
        }
        else if (node.InlineData.Length > 0)
        {

            node.DataLayout = ErofsDataLayout.Inline;
            node.Idata = new ErofsFlatIdata { BlockAddress = uint.MaxValue };
        }
        else
        {

            node.DataLayout = ErofsDataLayout.Flat;
            node.Idata = new ErofsFlatIdata { BlockAddress = 0 };
        }
    }

    /// <summary>
    /// Serializes one inode's complete metadata: the record, its inline xattrs, and every tail structure the
    /// plain metadata area and the metabox container body must both carry (an inline directory payload, a
    /// fragment map header, a chunk index table, a compressed index table, or an inline compressed tail).
    /// Writing through one method keeps the two placements byte-identical instead of letting the container
    /// body silently omit the structures only the plain area used to write.
    /// <para>序列化单个 inode 的完整元数据：记录、内联 xattr，以及普通元数据区与 metabox 容器本体都必须同样携带的
    /// 全部尾部结构（内联目录负载、fragment 映射头、chunk 索引表、压缩索引表或内联压缩尾部）。经由单一方法写出使两种
    /// 摆放逐字节一致，而不是让容器本体静默遗漏只有普通元数据区才会写出的结构。</para>
    /// </summary>
    /// <param name="output">Destination stream in the coordinates of <paramref name="inodeOffset"/>.
    /// <para>与 <paramref name="inodeOffset"/> 同坐标的目标流。</para></param>
    /// <param name="inodeOffset">Byte offset of the inode record in <paramref name="output"/>; a metabox
    /// resident passes its body-relative slot offset. <para>inode 记录在 <paramref name="output"/> 中的字节偏移；
    /// metabox 常驻节点传入其相对容器本体的槽位偏移。</para></param>
    /// <param name="node">Node whose metadata is written. <para>待写出元数据的节点。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes.
    /// <para>以字节计的文件系统块大小。</para></param>
    /// <param name="now">Superblock epoch seconds the record is stamped against.
    /// <para>记录所依据的超级块纪元秒。</para></param>
    /// <param name="options">Validated build options. <para>经校验的构建选项。</para></param>
    /// <param name="staging">Staging file holding accepted compressed frames, or <see langword="null"/> when
    /// this build staged none. <para>承载已接受压缩帧的暂存文件；本次构建未暂存任何帧时为
    /// <see langword="null"/>。</para></param>
    private static void WriteInodeMetadata(Stream output, long inodeOffset, ErofsInode node, int blockSize,
        ulong now, ErofsOptions options, Core.CompressionPayloadStaging? staging)
    {
        BuildInode(node, blockSize, now, options);
        Span<byte> record = stackalloc byte[ErofsInode.ExtendedSize];
        node.WriteTo(record);
        WriteAt(output, inodeOffset, record[..node.OnDiskSize]);

        if (node.XattrIbody.Length > 0)
        {
            WriteAt(output, inodeOffset + node.OnDiskSize, node.XattrIbody, node.XattrIbody.Length);
        }

        var cursor = inodeOffset + node.OnDiskSize + node.XattrISize;
        if (node.IsDirectory)
        {
            if (node.DirectoryInline)
            {
                var payload = BuildDirPayload(node);
                WriteAt(output, cursor, payload, payload.Length);
            }
        }
        else if (node.IsFragment)
        {
            WriteFragmentHeader(output, node, inodeOffset);
        }
        else if (node.ChunkIndex is not null)
        {
            WriteChunkIndexTable(output, node, inodeOffset);
        }
        else if (node.Pclusters is not null)
        {
            WriteCompressedIndexes(output, AlignUp(cursor, 8), node, blockSize, node.CompressionOptions ?? options);
        }
        else if (node.InlineData.Length > 0)
        {
            WriteAt(output, cursor, node.InlineData, node.InlineData.Length);
        }

        if (node.InlineCompressedTail && node.Pclusters is not null)
        {
            WriteInlineCompressedTail(output, inodeOffset, node, blockSize, options, staging);
        }
    }
}
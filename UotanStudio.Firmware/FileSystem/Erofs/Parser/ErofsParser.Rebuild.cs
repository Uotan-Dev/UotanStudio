using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UnifiedCompressedIdata = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsCompressedIdata;
using UnifiedDataLayout = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsDataLayout;
using UnifiedInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;

namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

/// <summary>
/// EROFS parser part that reads the verified source image back into builder input: per-inode compression and timestamp records, the compression metadata report, and the options an edited rebuild inherits.
/// <para>EROFS 解析器的组成部分，负责把已校验的源镜像读回为构建器输入：逐 inode 的压缩与时间戳记录、压缩元数据报告，
/// 以及编辑重建所继承的选项。</para>
/// </summary>
public sealed partial class ErofsParser
{
    internal JsonObject ReadCompressionMetadata()
    {
        var algorithms = new JsonArray();
        foreach (var pair in ReadCompressionOptions())
        {
            var options = pair.Value;
            algorithms.Add((JsonNode)new JsonObject
            {
                ["algorithm"] = pair.Key.ToString(),
                ["dictionarySize"] = pair.Key == ErofsCompressionAlgorithm.Lzma ? options.DictionarySize : null,
                ["windowBits"] = pair.Key is ErofsCompressionAlgorithm.Deflate or ErofsCompressionAlgorithm.Zstd ? options.WindowBits : null,
                ["lz4MaxDistance"] = pair.Key == ErofsCompressionAlgorithm.Lz4 ? options.Lz4MaxDistance : null,
                ["maxPclusterSize"] = pair.Key == ErofsCompressionAlgorithm.Lz4 ? options.MaxPclusterSize : null
            });
        }
        return new JsonObject
        {
            ["hasConfigurationRecords"] = _superblock.HasBigPcluster,
            ["algorithms"] = algorithms,
            ["compressionLevel"] = null,
            ["note"] = "EROFS does not store encoder level or LZ4 versus LZ4HC. Legacy images imply LZ4 only when compressed inodes exist. Edited rebuilds inherit source inode and codec parameters from the verified original image; physical layout is regenerated."
        };
    }

    /// <summary>
    /// Reads the source compression contracts, inode timestamps, declared superblock feature words,
    /// dot-omitted advise dimension and device table for an edited rebuild. The legacy baseline follows
    /// the upstream matrix (task 1.6a): mark legacy only when every observable modern signal is absent
    /// AND none of the eight modern feature bits are set. Modern erofs-utils defaults advertise only the
    /// lz4_0padding incompatibility and no layout-carrying feature bit, so treating "8 features all
    /// missing" as sufficient would misclassify them as legacy and force-off Fragments, TailPacking,
    /// Metabox, Dedup and DotOmitted downstream.
    /// <para>读取编辑重建所需的源压缩契约、inode 时间戳、已声明的超级块特性字、目录省略 advise 维度和设备表。
    /// legacy 判定按上游矩阵（任务 1.6a）：仅当所有可观察到的现代迹象均缺失且 8 个现代特性位全部未声明时
    /// 才把源标记为 legacy。现代 erofs-utils 默认镜像通常只带 lz4_0padding incompatibility 而非承载布局的
    /// 特性位，因此以「8 位全部未声明」作为充分条件会把它们误判为 legacy，进而在下游强制关闭
    /// Fragments/TailPacking/Metabox/Dedup 与 DotOmitted。</para>
    /// </summary>
    internal ErofsOptions ReadRebuildOptions()
    {
        var codecs = ReadCompressionOptions();
        var defaultAlgorithm = codecs.Keys.FirstOrDefault();
        var entries = new List<ErofsFileEntry>();
        var seenNids = new HashSet<ulong>();
        foreach (var entry in EnumerateEntries())
        {
            if (seenNids.Add(entry.Nid)) entries.Add(entry);
        }
        var records = Core.FirmwareParallelism.SelectArray(entries,
            (entry, _) => ReadRebuildInodeRecord(entry, codecs, defaultAlgorithm));
        var inodes = new Dictionary<ulong, UnifiedInode>(entries.Count);
        var compressed = false;
        var adviseObserved = false;
        var extendedObserved = false;
        var compactedIndexObserved = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var record = records[i];
            if (record.SourceCompressed && !compressed)
            {
                compressed = true;
                defaultAlgorithm = record.SourceAlgorithm;
            }
            if (!extendedObserved && (record.IFormat & UnifiedInode.FlagExtended) != 0)
            {
                extendedObserved = true;
            }
            if (!compactedIndexObserved && record.DataLayout == UnifiedDataLayout.CompactIndexes)
            {
                compactedIndexObserved = true;
            }
            if (!adviseObserved && (record.IFormat & UnifiedInode.AdviseBitNlinkOneOrDotOmitted) != 0)
            {
                adviseObserved = true;
            }
            inodes.Add(entries[i].Nid, record);
        }
        var byPath = inodes.Values
            .Where(record => record.SourcePath is { Length: > 0 })
            .GroupBy(record => record.SourcePath!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var selected = codecs.GetValueOrDefault(defaultAlgorithm) ?? new ErofsOptions { BlockSize = _blockSize };
        selected.SourceInodesByPath = byPath;
        selected.Compress = compressed || _superblock.AvailableComprAlgs != 0;
        selected.Uuid = [.. _superblock.Uuid];
        selected.VolumeName = _superblock.VolumeName;
        selected.SourceVolumeNameBytes = _superblock.VolumeNameBytes;
        selected.BuildTime = _superblock.BuildTime;
        selected.BuildTimeNanoseconds = _superblock.BuildTimeNsec;
        selected.BuildTimeOffset = _superblock.BuildTime32;
        selected.SuperblockChecksum = _superblock.HasCompatChecksum;
        selected.SourceMtimeFeature = _superblock.HasCompatMtime;
        selected.SourceXattrFilterFeature = _superblock.HasCompatXattrFilter;
        selected.SourceSharedEaInMetabox = (_superblock.FeatureCompat & ErofsSuperblock.CompatSharedEaInMetabox) != 0;
        selected.SourceXattrPrefixes = LoadXattrPrefixTable();

        selected.XattrNameFilter = _superblock.HasCompatXattrFilter;
        selected.IshareXattrs = _superblock.HasCompatIshareXattrs;
        if (_superblock.HasCompatIshareXattrs
            && _superblock.XattrPrefixCount != 0
            && selected.SourceXattrPrefixes is { } prefixTable)
        {
            var index = _superblock.IshareXattrPrefixId & ErofsXattr.LongPrefixMask;
            if (index < prefixTable.Count)
            {
                selected.IshareXattrPrefix = ErofsXattr.PrefixText(prefixTable[index]);
            }
        }
        selected.SourceMetaboxCompressed = _superblock.HasMetabox && _superblock.MetaBoxNid != 0
            && ReadInode(_superblock.MetaBoxNid).IsCompressed;
        if (MetaboxContainer is { } container)
        {
            selected.SourceMetaboxAlgorithm = container.Algorithm;
        }

        selected.SourceMetaBlkAddr = _superblock.MetaBlkAddr;
        selected.SourceDeviceTable = _deviceSlots?.Skip(1)
            .Select(slot => new ErofsSourceDevice([.. slot.Tag], slot.Blocks, slot.UniAddr))
            .ToArray();
        selected.Fragments = _superblock.PackedNid != 0;
        selected.CompressedTailPacking = _superblock.HasZtailpacking;
        selected.Metabox = _superblock.HasMetabox;
        selected.Dedup = _superblock.HasFragments;
        selected.TailPacking = inodes.Values.Any(i => i.DataLayout == UnifiedDataLayout.Inline);
        selected.SourceInodes = inodes;
        selected.SourceCompressionOptions = codecs;
        selected.Features = ErofsFeatureSet.FromWords(_superblock.FeatureCompat, _superblock.FeatureIncompat);
        selected.FeaturesAuthoritative = true;
        var hasModernSignal = adviseObserved
            || extendedObserved
            || _superblock.BuildTimeNsec != 0
            || _superblock.HasCompressionConfigs
            || compactedIndexObserved;
        selected.LegacyCompatibility = !selected.Features.Contains(ErofsFeature.BigPcluster)
                    && !selected.Features.Contains(ErofsFeature.Ztailpacking)
                    && !selected.Features.Contains(ErofsFeature.Metabox)
                    && !selected.Features.Contains(ErofsFeature.Fragments)
                    && !selected.Features.Contains(ErofsFeature.DeviceTable)
                    && !selected.Features.Contains(ErofsFeature.ChunkedFile)
                    && !selected.Features.Contains(ErofsFeature.XattrPrefixes)
                    && !selected.Features.Contains(ErofsFeature.FortyEightBit)
                    && !hasModernSignal;
        if (selected.LegacyCompatibility)
        {
            selected.Fragments = false;
            selected.CompressedTailPacking = false;
            selected.Metabox = false;
            selected.Dedup = false;
        }

        var droppedIncompatBits = 0u;
        if (selected.CompressedTailPacking && !selected.Features.Contains(ErofsFeature.Ztailpacking))
        {
            selected.CompressedTailPacking = false;
            droppedIncompatBits |= ErofsSuperblock.IncompatZtailpacking;
        }

        if ((selected.Fragments || selected.Dedup) && !selected.Features.Contains(ErofsFeature.Fragments))
        {
            selected.Fragments = false;
            selected.Dedup = false;
            droppedIncompatBits |= ErofsSuperblock.IncompatFragments;
        }

        if (selected.Metabox && !selected.Features.Contains(ErofsFeature.Metabox))
        {
            selected.Metabox = false;
            droppedIncompatBits |= ErofsSuperblock.IncompatMetabox;
        }

        selected.DotOmitted = !selected.LegacyCompatibility && adviseObserved
            ? ErofsTriStateFeature.Enabled
            : ErofsTriStateFeature.Disabled;
        selected.NlinkOneAdviseSupported = adviseObserved;
        if (TryReadExtraDeviceDescriptors(out var extraDevices))
        {
            selected.ExtraDeviceCount = extraDevices.Length;
            selected.ExtraDeviceImages = extraDevices;
        }
        else
        {
            selected.ExtraDeviceCount = 0;
            selected.ExtraDeviceImages = null;
            if (selected.Features.Contains(ErofsFeature.DeviceTable))
            {
                selected.Features = selected.Features.Without(ErofsFeature.DeviceTable);
                droppedIncompatBits |= ErofsSuperblock.IncompatDeviceTable;
            }
        }

        selected.DroppedIncompatBits = droppedIncompatBits;
        return selected;
    }

    /// <summary>
    /// Replays the source device table as builder descriptors. A declared <c>device_table</c> bit is only
    /// honoured when every extra slot yields a descriptor that passes <see cref="ErofsOptions.Validate"/>'s
    /// device rules, because the writer would otherwise advertise a table it cannot emit; note the shared
    /// incompat bit also names <c>compr_head2</c>, so dropping the table drops that alias too.
    /// <para>把源设备表重放为打包器描述符。已声明的 <c>device_table</c> 位只有在每个额外槽位都能通过
    /// <see cref="ErofsOptions.Validate"/> 设备规则时才被兑现，否则写入方会宣称一个无法写出的设备表；注意该
    /// incompat 位与 <c>compr_head2</c> 共用，移除设备表也会一并移除该别名。</para>
    /// </summary>
    /// <param name="devices">One descriptor per extra slot, sized in bytes; empty when the source declares no
    /// extra devices. <para>每个额外槽位一个描述符，以字节表示大小；源未声明额外设备时为空。</para></param>
    /// <returns><see langword="true"/> when the declared table can be re-emitted verbatim.
    /// <para>已声明的设备表可以逐字重新输出时为 <see langword="true"/>。</para></returns>
    private bool TryReadExtraDeviceDescriptors(out ErofsDeviceSpec[] devices)
    {
        devices = [];
        var count = _superblock.ExtraDevices;
        if (count == 0)
        {
            return !_superblock.HasDeviceTable;
        }

        var slots = _deviceSlots;
        if (slots is null || slots.Length <= count)
        {
            return false;
        }

        var synthesized = new ErofsDeviceSpec[count];
        for (var i = 0; i < count; i++)
        {
            var slot = slots[i + 1];
            var tag = Encoding.UTF8.GetStringTrimNul(slot.Tag);
            if (tag.Length == 0)
            {
                tag = $"dev{i + 1}";
            }

            if (Encoding.UTF8.GetByteCount(tag) >= ErofsDeviceSlot.TagSize
                || slot.Blocks == 0
                || slot.Blocks > (ulong)(long.MaxValue / _blockSize))
            {
                return false;
            }

            synthesized[i] = new ErofsDeviceSpec(tag, checked((long)slot.Blocks * _blockSize));
        }

        devices = synthesized;
        return true;
    }

    /// <summary>Decodes one inode's storage strategy; thread-safe because all source reads take the stream lock.
    /// Only the compacted index form folds lcluster bits into a packed 4-byte slot and is therefore capped at
    /// 14 bits; the full 8-byte index form has no such bound, so its logical cluster
    /// size is taken as stored, limited only by the range an <see cref="int"/> shift can hold.
    /// <para>解码单个 inode 的存储策略；所有源读取都在流锁下进行，因此线程安全。只有紧凑索引形式会把
    /// lcluster 位数折算进紧凑的 4 字节槽位，因此以 14 位为上限；完整 8 字节索引
    /// 没有该限制，其逻辑簇大小按存储值取用，仅受 <see cref="int"/> 位移可表示范围的约束。</para></summary>
    private UnifiedInode ReadRebuildInodeRecord(
        ErofsFileEntry entry,
        Dictionary<ErofsCompressionAlgorithm, ErofsOptions> codecs,
        ErofsCompressionAlgorithm fallbackAlgorithm)
    {
        var inode = ReadInode(entry.Nid);
        var algorithm = fallbackAlgorithm;
        var isCompressed = inode.IsCompressed;
        var logicalClusterSize = _blockSize;
        var fragmentShape = ErofsFragmentShape.None;
        if (isCompressed)
        {
            var offset = AlignUp(InodeOffset(entry.Nid) + inode.InlineDataOffset, 8);
            var ownHeaderBytes = ReadInodeMetadata(entry.Nid, offset, ZErofsMapHeader.Size, "rebuild compression header");
            var header = ZErofsMapHeader.FromBytes(ownHeaderBytes);
            inode.AttachMapHeader(ownHeaderBytes, _superblock.BlockSizeBits);
            if (header.IsFragmentInode)
            {
                fragmentShape = ErofsFragmentShape.Tail;
                if (_superblock.PackedNid == 0) throw new FileSystemCorruptedException("Missing EROFS packed inode.");
                var packed = ReadInode(_superblock.PackedNid);
                isCompressed = packed.IsCompressed;
                if (isCompressed)
                {
                    var packedOffset = AlignUp(InodeOffset(_superblock.PackedNid) + packed.InlineDataOffset, 8);
                    header = ZErofsMapHeader.FromBytes(ReadInodeMetadata(_superblock.PackedNid, packedOffset, ZErofsMapHeader.Size, "packed compression header"));
                    if (header.IsFragmentInode) throw new FileSystemCorruptedException("Recursive EROFS packed inode.");
                }
            }
            else if ((header.Advise & ZErofsMapHeader.AdviseFragmentPcluster) != 0)
            {
                if (_superblock.PackedNid == 0) throw new FileSystemCorruptedException("Missing EROFS packed inode.");
                fragmentShape = ErofsFragmentShape.Tail;
            }
            if (!isCompressed && fragmentShape == ErofsFragmentShape.Tail)
            {
                fragmentShape = ErofsFragmentShape.WholeFile;
            }
            if (isCompressed)
            {
                if (inode.DataLayout == UnifiedDataLayout.Compressed && header.HasExtents)
                {
                    var recordSize = ZErofsMapHeader.ExtentRecordSize(header.Advise);
                    var extentClusterBits = _superblock.BlockSizeBits
                        + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
                    if (extentClusterBits > 23)
                    {
                        throw new NotSupportedException("EROFS extent clusters exceed the supported size.");
                    }
                    if (inode.Idata is UnifiedCompressedIdata extentIdata)
                    {
                        extentIdata.ExtentClusterSize = 1 << extentClusterBits;
                    }
                    var extentStart = ZErofsMapHeader.ExtentAreaBase(offset, recordSize);
                    var recordOffset = checked(extentStart + (recordSize == 4 ? ZErofsMapHeader.ExtentPrefixBytes : 0));
                    var extent = ReadInodeMetadata(entry.Nid, recordOffset, recordSize, "rebuild extent");
                    var format = BinaryPrimitives.ReadUInt32LittleEndian(extent) >> ZErofsMapHeader.ExtentPlenFmtBit;
                    if (format is > 0 and <= (uint)ErofsCompressionAlgorithm.Zstd + 1)
                    {
                        algorithm = (ErofsCompressionAlgorithm)(format - 1);
                    }
                    logicalClusterSize = _blockSize;
                }
                else
                {
                    algorithm = (ErofsCompressionAlgorithm)header.Algorithm;
                }
                if (!codecs.ContainsKey(algorithm))
                    throw new FileSystemCorruptedException($"Undeclared EROFS compression algorithm {algorithm} for '{entry.Path}'.");
                if (!(inode.DataLayout == UnifiedDataLayout.Compressed && header.HasExtents))
                {
                    var logicalBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
                    if (logicalBits > 30)
                    {
                        throw new FileSystemCorruptedException(
                            $"EROFS logical cluster shift {logicalBits} exceeds the supported range.");
                    }
                    if (inode.DataLayout == UnifiedDataLayout.CompactIndexes && logicalBits > 14)
                    {
                        throw new NotSupportedException("EROFS compacted logical clusters exceed the supported 16384-byte limit.");
                    }
                    logicalClusterSize = 1 << logicalBits;
                }
            }
        }
        var chunkBased = inode.DataLayout == UnifiedDataLayout.ChunkBased;
        var chunkBits = chunkBased ? inode.ChunkFormatBlkBits : 0;
        if (chunkBased && (inode.ChunkFormat & ~UnifiedInode.ChunkFormatAll) != 0)
        {
            throw new FileSystemCorruptedException(
                $"EROFS chunk format 0x{inode.ChunkFormat:X4} for '{entry.Path}' raises undefined bits.");
        }

        if (chunkBased)
        {
            AttachSourceChunkIndex(entry.Nid, entry.Path, inode, chunkBits);
        }

        var secondaryAlgorithm = isCompressed ? ReadSecondaryAlgorithm(entry.Nid, inode) : null;
        if (secondaryAlgorithm is { } secondary && !codecs.ContainsKey(secondary))
        {
            throw new FileSystemCorruptedException(
                $"Undeclared EROFS HEAD2 compression algorithm {secondary} for '{entry.Path}'.");
        }

        inode.SourcePath = entry.Path;
        inode.SourceCompressed = isCompressed;
        inode.SourceAlgorithm = algorithm;
        inode.SourceFragmentShape = fragmentShape;
        inode.SecondaryAlgorithm = secondaryAlgorithm;
        AttachSourceXattrIbody(entry.Nid, inode, entry.Xattrs);
        if (inode.Idata is UnifiedCompressedIdata logicalIdata)
        {
            logicalIdata.LogicalClusterSize = logicalClusterSize;
        }

        return inode;
    }

    /// <summary>
    /// Copies the source inode's raw inline xattr body onto the inode a rebuild plans from, so a rebuild that
    /// inherits source inodes can re-emit the attribute set the source image recorded. The body is carried
    /// verbatim because it is index-relative to the source long-prefix table and may encode a shared-attribute
    /// id rather than entries; re-encoding it would corrupt it, and the prefix table and ishare prefix are
    /// themselves replayed. <c>XattrICount</c> is carried alongside rather than recomputed, because the
    /// length-derived count is wrong for the shared form.
    /// <para>把源 inode 的原始内联 xattr 体复制到重建所规划的那个 inode 上，使继承源 inode 的重建能够重新输出
    /// 源镜像记录的属性集。该体按原样携带，因为它相对于源长前缀表索引，且可能编码共享属性 id 而非条目；重新编码会
    /// 破坏它，而前缀表与 ishare 前缀本身已被重放。<c>XattrICount</c> 一并携带而非重算，因为由长度推导的计数
    /// 对共享形态并不正确。</para>
    /// </summary>
    /// <param name="nid">On-disk inode number the body belongs to.
    /// <para>该体所属的磁盘 inode 编号。</para></param>
    /// <param name="inode">Decoded source inode to extend.
    /// <para>待扩充的解码源 inode。</para></param>
    /// <param name="resolvedEntries">Attribute set the caller already resolved for this inode, including any
    /// entries a shared reference pointed at. A shared body names a slot in the source image's shared area,
    /// which the rebuild does not carry over, so its attributes can only be replayed as the resolved list.
    /// <para>调用方已为该 inode 解析出的属性集，含共享引用所指的条目。共享体指名源镜像共享区中的槽位，而重建不会
    /// 沿用该区域，因此其属性只能以已解析列表的形式重放。</para></param>
    private void AttachSourceXattrIbody(ulong nid, UnifiedInode inode,
        IReadOnlyList<ErofsXattrEntry>? resolvedEntries)
    {
        if (inode.XattrICount == 0 || inode.XattrISize <= 0)
        {
            return;
        }

        try
        {
            var metaboxResident = _hasMetabox && (nid & NidMetaboxFlag) != 0;
            var ibodyOffset = checked((metaboxResident ? MetaboxOffset(nid) : _superblock.GetInodeOffset(nid))
                + inode.OnDiskSize);
            inode.XattrIbody = metaboxResident
                ? ReadMetaboxBytes(ibodyOffset, inode.XattrISize, "source xattr ibody")
                : ReadBytes(ibodyOffset, inode.XattrISize, "source xattr ibody");
            var sharedIds = ErofsXattr.ParseSharedXattrIds(inode.XattrIbody, inode.XattrICount);
            inode.SharedXattrId = sharedIds.Length > 0 ? (int)sharedIds[0] : -1;
            if (resolvedEntries is { Count: > 0 })
            {
                inode.XattrEntries = [.. resolvedEntries];
            }
        }
        catch (FileSystemCorruptedException)
        {
            // A source whose inline attributes cannot be re-read still yields an inode; the attribute set is
            // simply not inherited, and the caller reports the image as read with fewer preserved dimensions.
        }
        catch (FormatException)
        {
            inode.XattrIbody = [];
            inode.SharedXattrId = -1;
        }
    }

    /// <summary>
    /// Records a chunk-based source inode's index table on the decoded node: the per-chunk start blocks
    /// and the device those blocks live on. Every index entry carries the same <c>device_id</c>, so the id
    /// is taken from the first entry and
    /// every other entry must agree. The recorded start blocks are the raw on-disk values: a device other
    /// than the primary image keeps its device-relative block, while device 0 keeps the filesystem-wide
    /// address that the extraction path resolves through the device table. A preserved rebuild re-emits
    /// exactly these values, which is what lets a blob-backed image be rebuilt without relocating its
    /// payload into the primary image.
    /// <para>把 chunk-based 源 inode 的索引表记录到解码节点上：逐 chunk 首块以及这些块所在的设备。每条索引表项
    /// 都携带相同的 <c>device_id</c>，因此设备号取自
    /// 第一条表项，其余表项必须一致。记录的首块是磁盘原值：主镜像之外的设备保留其设备内相对块号，device 0 则
    /// 保留解包路径经设备表解析的文件系统范围内地址。保留式重建逐字重新输出这些值，这正是 blob 支撑的镜像
    /// 能在不把负载迁移进主镜像的前提下被重建的原因。</para>
    /// </summary>
    /// <param name="nid">Inode number whose index table is read.
    /// <para>待读取索引表的 inode 编号。</para></param>
    /// <param name="path">Slash-separated image path, used in error messages.
    /// <para>'/' 分隔的镜像路径，用于错误信息。</para></param>
    /// <param name="inode">Decoded chunk-based node receiving the index and the device id.
    /// <para>接收索引与设备号的已解码 chunk-based 节点。</para></param>
    /// <param name="chunkBits">Chunk size as a log2 block count.
    /// <para>以块数 log2 表示的 chunk 大小。</para></param>
    /// <exception cref="FileSystemCorruptedException">The index table exceeds the metadata component limit
    /// or carries inconsistent device ids.
    /// <para>索引表超出元数据组件上限，或携带不一致的设备号。</para></exception>
    private void AttachSourceChunkIndex(ulong nid, string path, UnifiedInode inode, int chunkBits)
    {
        var chunkSize = (long)_blockSize << chunkBits;
        var countLong = (((long)inode.Size - 1) / chunkSize) + 1;
        if (countLong <= 0)
        {
            return;
        }

        var unit = inode.UsesChunkIndexes ? UnifiedInode.ChunkIndexEntrySize : UnifiedInode.BlockMapEntrySize;
        var indexBytes = checked(countLong * unit);
        if (indexBytes > MaxMetadataComponentSize)
        {
            throw new FileSystemCorruptedException(
                $"EROFS chunk index table size {indexBytes} for '{path}' exceeds the limit of "
                + $"{MaxMetadataComponentSize} bytes.");
        }

        var indexOffset = AlignUp(InodeOffset(nid) + inode.InlineDataOffset, unit);
        var index = ReadInodeMetadata(nid, indexOffset, (int)indexBytes, "chunk index table");
        inode.AttachChunkIndex(index);

        if (!inode.UsesChunkIndexes)
        {
            return;
        }

        var deviceMask = System.Numerics.BitOperations.RoundUpToPowerOf2((uint)_superblock.ExtraDevices + 1) - 1;
        uint? deviceId = null;
        for (var i = 0; i < countLong; i++)
        {
            var entryDevice = BinaryPrimitives.ReadUInt16LittleEndian(index.AsSpan((int)(i * unit) + 2, 2)) & deviceMask;
            if (deviceId is { } known && known != entryDevice)
            {
                throw new FileSystemCorruptedException(
                    $"EROFS chunk-based inode '{path}' mixes device ids {known} and {entryDevice}.");
            }

            deviceId = entryDevice;
        }

        inode.DeviceId = deviceId ?? 0;
    }

    /// <summary>
    /// Reads the secondary algorithm that a compressed inode's HEAD2 index rows name through the high nibble
    /// of <c>h_algorithmtype</c>, resolving a fragment inode to the packed inode that actually holds the
    /// indexes. Returns <see langword="null"/> when the source emitted no HEAD2 rows.
    /// <para>读取压缩 inode 的 HEAD2 索引行经 <c>h_algorithmtype</c> 高四位指名的第二算法；fragment inode
    /// 解析到实际承载索引的 packed inode。源未发出 HEAD2 行时返回 <see langword="null"/>。</para>
    /// </summary>
    private ErofsCompressionAlgorithm? ReadSecondaryAlgorithm(ulong nid, UnifiedInode inode)
    {
        if (inode.DataLayout != UnifiedDataLayout.CompactIndexes
            && inode.DataLayout != UnifiedDataLayout.Compressed)
        {
            return null;
        }

        if (_superblock.PackedNid != 0 && nid != _superblock.PackedNid)
        {
            var packed = ReadInode(_superblock.PackedNid);
            if (!packed.IsCompressed) return null;
            nid = _superblock.PackedNid;
            inode = packed;
        }

        var offset = AlignUp(InodeOffset(nid) + inode.InlineDataOffset, 8);
        var header = ZErofsMapHeader.FromBytes(ReadInodeMetadata(nid, offset, ZErofsMapHeader.Size, "rebuild compression header"));
        if (header.IsFragmentInode) return null;
        var algorithm = (ErofsCompressionAlgorithm)((header.AlgorithmType >> 4) & ZErofsMapHeader.AlgorithmTypeMask);
        return algorithm == ErofsCompressionAlgorithm.Lz4 ? null : algorithm;
    }

    private Dictionary<ErofsCompressionAlgorithm, ErofsOptions> ReadCompressionOptions()
    {
        var result = new Dictionary<ErofsCompressionAlgorithm, ErofsOptions>();
        if (!_superblock.HasBigPcluster)
        {
            result.Add(ErofsCompressionAlgorithm.Lz4, new ErofsOptions
            {
                BlockSize = _blockSize,
                MaxPclusterSize = _blockSize,
                Lz4MaxDistance = _superblock.AvailableComprAlgs
            });
            return result;
        }
        var bitmap = _superblock.AvailableComprAlgs;
        if ((bitmap & ~0xF) != 0) throw new NotSupportedException("Unknown EROFS compression configuration.");
        var offset = ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize
            + _superblock.SbExtSlots * ErofsSuperblock.ExtSlotSize;
        for (var id = 0; id < 4; id++)
        {
            if ((bitmap & (1 << id)) == 0) continue;
            offset = checked((int)AlignUp(offset, 4));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(offset, 2, "compression configuration length"));
            var expected = id < 2 ? 14 : 6;
            if (length != expected)
                throw new FileSystemCorruptedException($"Invalid EROFS compression configuration length for algorithm {id}: {length} (expected {expected}).");
            var bytes = ReadBytes(offset + 2, length, "compression configuration");
            var options = new ErofsOptions { Algorithm = (ErofsCompressionAlgorithm)id, BlockSize = _blockSize, MaxPclusterSize = _blockSize };
            switch (options.Algorithm)
            {
                case ErofsCompressionAlgorithm.Lz4:
                    options.Lz4MaxDistance = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
                    options.MaxPclusterSize = checked(Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2))) * _blockSize);
                    break;
                case ErofsCompressionAlgorithm.Lzma:
                    options.DictionarySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
                    if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != 0)
                        throw new NotSupportedException("Unknown EROFS MicroLZMA format.");
                    break;
                case ErofsCompressionAlgorithm.Deflate:
                    options.WindowBits = bytes[0];
                    break;
                case ErofsCompressionAlgorithm.Zstd:
                    if (bytes[0] != 0) throw new NotSupportedException("Unknown EROFS Zstandard format.");
                    options.WindowBits = bytes[1] + 10;
                    break;
            }
            options.Validate();
            result.Add(options.Algorithm, options);
            offset = checked(offset + 2 + length);
        }
        return result;
    }

    /// <summary>
    /// Restores the timestamp of a compact inode: it stores no per-inode sub-second field, only a delta
    /// against the volume-wide <c>s_epoch</c>, so its nanoseconds are the volume-wide <c>s_fixed_nsec</c>
    /// (<see cref="ErofsSuperblock.BuildTimeNsec"/>). An extended inode keeps the nanoseconds it stores
    /// itself. <para>还原紧凑 inode 的时间戳：它没有逐 inode 的亚秒字段，只有相对卷级 <c>s_epoch</c> 的
    /// 增量，因此其纳秒即卷级 <c>s_fixed_nsec</c>（<see cref="ErofsSuperblock.BuildTimeNsec"/>）。扩展
    /// inode 保留自身存储的纳秒。</para>
    /// </summary>
    private UnifiedInode ResolveTimestamp(UnifiedInode inode)
    {
        if (!inode.IsExtended)
        {
            inode.CTime = checked(_superblock.BuildTime + inode.Generation);
            inode.CTimeNsec = _superblock.BuildTimeNsec;
        }
        return inode;
    }
}
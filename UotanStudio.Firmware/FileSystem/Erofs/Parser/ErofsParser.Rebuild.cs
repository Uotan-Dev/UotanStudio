using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Models;

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

    /// <summary>Reads the source compression contracts, inode timestamps, declared superblock feature words,
    /// dot-omitted advise dimension and device table for an edited rebuild.</summary>
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
        var inodes = new Dictionary<ulong, ErofsSourceInode>(entries.Count);
        var compressed = false;
        var adviseObserved = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var record = records[i];
            if (record.Compressed && !compressed)
            {
                compressed = true;
                defaultAlgorithm = record.Algorithm;
            }
            if (!adviseObserved && record.NlinkOneOrDotOmitted)
            {
                adviseObserved = true;
            }
            inodes.Add(entries[i].Nid, record);
        }
        var byPath = inodes.Values
            .Where(record => record.Path is { Length: > 0 })
            .GroupBy(record => record.Path!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var selected = codecs.GetValueOrDefault(defaultAlgorithm) ?? new ErofsOptions { BlockSize = _blockSize };
        selected.SourceInodesByPath = byPath;
        selected.Compress = compressed || _superblock.HasBigPcluster && _superblock.AvailableComprAlgs != 0;
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
        selected.SourceMetaboxCompressed = _superblock.HasMetabox && _superblock.MetaBoxNid != 0
            && ReadInode(_superblock.MetaBoxNid).IsCompressed;
        selected.SourceDeviceTable = _deviceSlots?.Skip(1)
            .Select(slot => new ErofsSourceDevice([.. slot.Tag], slot.Blocks, slot.UniAddr))
            .ToArray();
        selected.Fragments = _superblock.PackedNid != 0;
        selected.CompressedTailPacking = _superblock.HasZtailpacking;
        selected.Metabox = _superblock.HasMetabox;
        selected.Dedup = _superblock.HasFragments;
        selected.TailPacking = inodes.Values.Any(i => i.InlineData);
        selected.SourceInodes = inodes;
        selected.SourceCompressionOptions = codecs;
        selected.Features = ErofsFeatureSet.FromWords(_superblock.FeatureCompat, _superblock.FeatureIncompat);
        selected.FeaturesAuthoritative = true;
        selected.LegacyCompatibility = !selected.Features.Contains(ErofsFeature.BigPcluster)
            && !selected.Features.Contains(ErofsFeature.Ztailpacking)
            && !selected.Features.Contains(ErofsFeature.Metabox)
            && _superblock.PackedNid == 0;
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
            if (tag.Length == 0
                || Encoding.UTF8.GetByteCount(tag) >= ErofsDeviceSlot.TagSize
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

    /// <summary>Decodes one inode's storage strategy; thread-safe because all source reads take the stream lock.</summary>
    private ErofsSourceInode ReadRebuildInodeRecord(
        ErofsFileEntry entry,
        Dictionary<ErofsCompressionAlgorithm, ErofsOptions> codecs,
        ErofsCompressionAlgorithm fallbackAlgorithm)
    {
        var inode = ReadInode(entry.Nid);
        var algorithm = fallbackAlgorithm;
        var isCompressed = inode.IsCompressed;
        var logicalClusterSize = _blockSize;
        int? sourceExtentClusterSize = null;
        int? sourceExtentRecordSize = null;
        if (isCompressed)
        {
            var offset = AlignUp(InodeOffset(entry.Nid) + inode.InlineDataOffset, 8);
            var header = ZErofsMapHeader.FromBytes(ReadInodeMetadata(entry.Nid, offset, ZErofsMapHeader.Size, "rebuild compression header"));
            if (header.IsFragmentInode)
            {
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
            if (isCompressed)
            {
                if (inode.DataLayout == ErofsInode.DataLayoutCompressed && header.HasExtents)
                {
                    var recordSize = 4 << ((header.Advise >> ZErofsMapHeader.ExtRecSizeBit)
                        & ZErofsMapHeader.ExtRecSizeMask);
                    var extentClusterBits = _superblock.BlockSizeBits
                        + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
                    if (extentClusterBits > 23)
                    {
                        throw new NotSupportedException("EROFS extent clusters exceed the supported size.");
                    }
                    sourceExtentClusterSize = 1 << extentClusterBits;
                    sourceExtentRecordSize = recordSize;
                    var extentStart = checked((long)AlignUp(offset + ZErofsMapHeader.Size, recordSize));
                    var recordOffset = checked(extentStart + (recordSize == 4 ? 8 : 0));
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
                if (!(inode.DataLayout == ErofsInode.DataLayoutCompressed && header.HasExtents))
                {
                    var logicalBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
                    if (logicalBits > 14) throw new NotSupportedException("EROFS logical clusters exceed the supported 16384-byte limit.");
                    logicalClusterSize = 1 << logicalBits;
                }
            }
        }
        return new(inode.CTime, inode.CTimeNsec, inode.IsExtended, isCompressed,
            algorithm, inode.DataLayout == ErofsInode.DataLayoutCompactIndexes,
            inode.DataLayout == ErofsInode.DataLayoutInline, logicalClusterSize, entry.Path,
            sourceExtentClusterSize, sourceExtentRecordSize,
            (inode.Advise & ErofsInode.AdviseBitNlinkOneOrDotOmitted) != 0);
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

    private ErofsInode ResolveTimestamp(ErofsInode inode)
    {
        if (!inode.IsExtended)
        {
            inode.CTime = checked(_superblock.BuildTime + inode.Generation);
            inode.CTimeNsec = _superblock.BuildTimeNsec;
        }
        return inode;
    }
}
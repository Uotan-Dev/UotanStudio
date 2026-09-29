using System.Buffers.Binary;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.FileSystem.Erofs;
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

    /// <summary>Reads the source compression contracts and inode timestamps for an edited rebuild.</summary>
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
        var records = new ErofsSourceInode?[entries.Count];
        Core.FirmwareParallelism.ForEach(entries, entry =>
        {
            records[entries.IndexOf(entry)] = ReadRebuildInodeRecord(entry, codecs, defaultAlgorithm);
        });
        var inodes = new Dictionary<ulong, ErofsSourceInode>(entries.Count);
        var compressed = false;
        foreach (var (entry, record) in entries.Zip(records))
        {
            if (record is null) continue;
            if (record.Compressed && !compressed)
            {
                compressed = true;
                defaultAlgorithm = record.Algorithm;
            }
            inodes.Add(entry.Nid, record);
        }
        var byPath = inodes.Values
            .Where(record => record.Path is { Length: > 0 })
            .GroupBy(record => record.Path!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var selected = codecs.GetValueOrDefault(defaultAlgorithm) ?? new ErofsOptions { BlockSize = _blockSize };
        selected.SourceInodesByPath = byPath;
        selected.Compress = compressed || _superblock.HasBigPcluster && _superblock.AvailableComprAlgs != 0;
        selected.Uuid = _superblock.Uuid.ToArray();
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
            .Select(slot => new ErofsSourceDevice(slot.Tag.ToArray(), slot.Blocks, slot.UniAddr))
            .ToArray();
        selected.Fragments = _superblock.PackedNid != 0;
        selected.CompressedTailPacking = _superblock.HasZtailpacking;
        selected.Metabox = _superblock.HasMetabox;
        selected.Dedup = _superblock.HasFragments;
        selected.TailPacking = inodes.Values.Any(i => i.InlineData);
        selected.SourceInodes = inodes;
        selected.SourceCompressionOptions = codecs;
        selected.LegacyCompatibility = !_superblock.HasBigPcluster
            && !_superblock.HasZtailpacking
            && !_superblock.HasMetabox
            && _superblock.PackedNid == 0;
        if (selected.LegacyCompatibility)
        {
            selected.Fragments = false;
            selected.CompressedTailPacking = false;
            selected.Metabox = false;
            selected.Dedup = false;
        }
        return selected;
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
            sourceExtentClusterSize, sourceExtentRecordSize);
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
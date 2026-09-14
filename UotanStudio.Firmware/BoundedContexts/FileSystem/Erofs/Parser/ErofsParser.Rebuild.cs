using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed partial class ErofsParser
{
    internal JsonObject ReadCompressionMetadata()
    {
        var algorithms = new JsonArray();
        foreach (var pair in ReadCompressionOptions())
        {
            var options = pair.Value;
            algorithms.Add(new JsonObject
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
        var inodes = new Dictionary<ulong, ErofsSourceInode>();
        var compressed = false;
        var defaultAlgorithm = codecs.Keys.FirstOrDefault();
        foreach (var entry in EnumerateEntries())
        {
            if (inodes.ContainsKey(entry.Nid)) continue;
            var inode = ReadInode(entry.Nid);
            var algorithm = defaultAlgorithm;
            var isCompressed = inode.IsCompressed;
            var logicalClusterSize = _blockSize;
            if (isCompressed)
            {
                var offset = AlignUp(_superblock.GetInodeOffset(entry.Nid) + inode.InlineDataOffset, 8);
                var header = ZErofsMapHeader.FromBytes(ReadBytes(offset, ZErofsMapHeader.Size, "rebuild compression header"));
                if (header.IsFragmentInode)
                {
                    if (_superblock.PackedNid == 0) throw new InvalidDataException("Missing EROFS packed inode.");
                    var packed = ReadInode(_superblock.PackedNid);
                    isCompressed = packed.IsCompressed;
                    if (isCompressed)
                    {
                        var packedOffset = AlignUp(_superblock.GetInodeOffset(_superblock.PackedNid) + packed.InlineDataOffset, 8);
                        header = ZErofsMapHeader.FromBytes(ReadBytes(packedOffset, ZErofsMapHeader.Size, "packed compression header"));
                        if (header.IsFragmentInode) throw new InvalidDataException("Recursive EROFS packed inode.");
                    }
                }
                if (isCompressed)
                {
                    algorithm = (ErofsCompressionAlgorithm)header.Algorithm;
                    if (!codecs.ContainsKey(algorithm))
                        throw new InvalidDataException($"Undeclared EROFS compression algorithm {algorithm} for '{entry.Path}'.");
                    var logicalBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
                    if (logicalBits > 14) throw new NotSupportedException("EROFS logical clusters exceed the supported 16384-byte limit.");
                    logicalClusterSize = 1 << logicalBits;
                    if (!compressed) defaultAlgorithm = algorithm;
                    compressed = true;
                }
            }
            inodes.Add(entry.Nid, new(inode.CTime, inode.CTimeNsec, inode.IsExtended, isCompressed,
                algorithm, inode.DataLayout == ErofsInode.DataLayoutCompactIndexes,
                inode.DataLayout == ErofsInode.DataLayoutInline, logicalClusterSize));
        }
        var selected = codecs.GetValueOrDefault(defaultAlgorithm) ?? new ErofsOptions { BlockSize = _blockSize };
        selected.Compress = compressed || _superblock.HasBigPcluster && _superblock.AvailableComprAlgs != 0;
        selected.Uuid = _superblock.Uuid.ToArray();
        selected.VolumeName = _superblock.VolumeName;
        selected.SourceVolumeNameBytes = _superblock.VolumeNameBytes;
        selected.BuildTime = _superblock.BuildTime;
        selected.BuildTimeNanoseconds = _superblock.BuildTimeNsec;
        selected.BuildTimeOffset = _superblock.BuildTime32;
        selected.SuperblockChecksum = _superblock.HasCompatChecksum;
        selected.SourceMtimeFeature = _superblock.HasCompatMtime;
        selected.Fragments = _superblock.PackedNid != 0;
        selected.CompressedTailPacking = _superblock.HasZtailpacking;
        selected.Metabox = _superblock.HasMetabox;
        selected.Dedup = _superblock.HasFragments;
        selected.TailPacking = inodes.Values.Any(i => i.InlineData);
        selected.SourceInodes = inodes;
        selected.SourceCompressionOptions = codecs;
        return selected;
    }

    private Dictionary<ErofsCompressionAlgorithm, ErofsOptions> ReadCompressionOptions()
    {
        var result = new Dictionary<ErofsCompressionAlgorithm, ErofsOptions>();
        // COMPR_CFGS and BIG_PCLUSTER share bit 1. Without it the superblock union is a distance.
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
            if (length < (id < 2 ? 14 : 6) || length > 4096)
                throw new InvalidDataException("Invalid EROFS compression configuration length.");
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
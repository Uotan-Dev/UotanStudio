using System.Buffers.Binary;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;

public sealed partial class ErofsParser
{
    internal JsonObject ReadCompressionMetadata()
    {
        var algorithms = new JsonArray();
        foreach (var pair in ReadCompressionOptions())
        {
            var options = pair.Value;
            // 显式转为 JsonNode 命中非泛型 Add 重载；JsonArray.Add<T>(T) 会用反射
            // 包装非基元类型，NativeAOT 下抛异常（IL2026/IL3050）。
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
        // Directory traversal is cheap; per-inode records need random reads across the image, so
        // the record decoding is fanned out over FirmwareParallelism (ReadBytes is stream-lock safe).
        // 目录遍历开销小；逐 inode 记录需要跨镜像随机读，因此把记录解码分发给
        // FirmwareParallelism 并行执行（ReadBytes 已有流锁保护）。
        var entries = new List<ErofsFileEntry>();
        var seenNids = new HashSet<ulong>();
        foreach (var entry in EnumerateEntries())
        {
            if (seenNids.Add(entry.Nid)) entries.Add(entry);
        }
        var records = new ErofsSourceInode?[entries.Count];
        SharedKernel.FirmwareParallelism.ForEach(entries, entry =>
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
        // Path-indexed projection so plain directory repacks can bind per-inode strategies
        // without an edit plan (paths are stable across nid shifts).
        // 按路径索引的投影，让纯目录重打包在没有 editPlan 时也能绑定逐 inode 策略
        // （路径不随 nid 漂移）。
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
        selected.Fragments = _superblock.PackedNid != 0;
        selected.CompressedTailPacking = _superblock.HasZtailpacking;
        selected.Metabox = _superblock.HasMetabox;
        selected.Dedup = _superblock.HasFragments;
        selected.TailPacking = inodes.Values.Any(i => i.InlineData);
        selected.SourceInodes = inodes;
        selected.SourceCompressionOptions = codecs;
        // A source without the big pcluster / COMPR_CFGS feature used the legacy (pre-5.13) layout.
        // Carry that forward so the rebuilt superblock does not advertise unsupported features to
        // older (5.4 / 5.10) kernels, which would refuse to mount the image. A source that carries
        // post-5.4 incompatible features (fragments / ztailpacking / metabox) genuinely needs them
        // to preserve its packed data, so those images are rebuilt with the modern layout instead
        // of being silently stripped.
        // 源镜像未声明 big pcluster / COMPR_CFGS 说明其采用 legacy（pre-5.13）布局。重建时保留该
        // 特征，避免重建后的超级块向较旧内核（5.4 / 5.10）声明其不支持的特性而导致挂载失败。
        // 若源镜像带有 5.4 之后引入的 incompat 特性（fragments / ztailpacking / metabox），其打包
        // 数据确实依赖这些特性，此时应按现代布局重建，而不是静默裁剪。
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
            }
        }
        return new(inode.CTime, inode.CTimeNsec, inode.IsExtended, isCompressed,
            algorithm, inode.DataLayout == ErofsInode.DataLayoutCompactIndexes,
            inode.DataLayout == ErofsInode.DataLayoutInline, logicalClusterSize, entry.Path);
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
            // Exact upstream record sizes: lz4/lzma cfgs are 14 bytes (+2 length), deflate/zstd 6 (+2).
            // 上游记录的精确长度：lz4/lzma 配置 14 字节（另加 2 字节长度），deflate/zstd 6 字节。
            var expected = id < 2 ? 14 : 6;
            if (length != expected)
                throw new InvalidDataException($"Invalid EROFS compression configuration length for algorithm {id}: {length} (expected {expected}).");
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
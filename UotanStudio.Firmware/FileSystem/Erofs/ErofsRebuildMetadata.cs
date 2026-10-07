using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Erofs;

/// <summary>
/// Captures and restores the verified EROFS rebuild profile. The original image is not retained,
/// so the structured metadata document must carry the superblock features, compression records and
/// per-inode storage strategy that <see cref="ErofsParser.ReadRebuildOptions"/> derived from it.
/// <para>采集并还原经校验的 EROFS 重建档案。档案不保留原始镜像，因此结构化元数据文档必须携带
/// <see cref="ErofsParser.ReadRebuildOptions"/> 从源镜像推导出的超级块特性、压缩记录与逐 inode 存储策略。</para>
/// </summary>
internal static class ErofsRebuildMetadata
{
    /// <summary>Projects parsed rebuild options into their persisted form.<para>将解析得到的重建选项投影为持久化形式。</para></summary>
    internal static ErofsRebuildParameters Capture(ErofsOptions options)
    {
        var parameters = new ErofsRebuildParameters
        {
            BlockSize = options.BlockSize,
            Compress = options.Compress,
            Algorithm = options.Algorithm.ToString(),
            CompressionLevel = options.CompressionLevel,
            TailPacking = options.TailPacking,
            CompressedTailPacking = options.CompressedTailPacking,
            UseCompactedIndexes = options.UseCompactedIndexes,
            Fragments = options.Fragments,
            Dedup = options.Dedup,
            Metabox = options.Metabox,
            ExtraDeviceCount = options.ExtraDeviceCount,
            SuperblockChecksum = options.SuperblockChecksum,
            MtimeFeature = options.SourceMtimeFeature,
            XattrFilterFeature = options.SourceXattrFilterFeature,
            SourceSharedEaInMetabox = options.SourceSharedEaInMetabox,
            SourceMetaboxCompressed = options.SourceMetaboxCompressed,
            BuildTime = options.BuildTime,
            BuildTimeNanoseconds = options.BuildTimeNanoseconds,
            BuildTimeOffset = options.BuildTimeOffset,
            VolumeNameBytes = options.SourceVolumeNameBytes,
            DictionarySize = options.DictionarySize,
            WindowBits = options.WindowBits,
            Lz4MaxDistance = options.Lz4MaxDistance,
            MaxPclusterSize = options.MaxPclusterSize,
            LegacyCompatibility = options.LegacyCompatibility,
        };
        if (options.SourceCompressionOptions is { } codecs)
        {
            foreach (var codec in codecs.OrderBy(pair => pair.Key))
            {
                parameters.Codecs.Add(new ErofsRebuildCodec
                {
                    Algorithm = codec.Key.ToString(),
                    BlockSize = codec.Value.BlockSize,
                    MaxPclusterSize = codec.Value.MaxPclusterSize,
                    DictionarySize = codec.Value.DictionarySize,
                    WindowBits = codec.Value.WindowBits,
                    Lz4MaxDistance = codec.Value.Lz4MaxDistance,
                    CompressionLevel = codec.Value.CompressionLevel,
                });
            }
        }

        if (options.SourceDeviceTable is { } devices)
        {
            foreach (var device in devices)
            {
                parameters.SourceDevices.Add(new ErofsRebuildDevice
                {
                    Tag = [.. device.Tag],
                    Blocks = device.Blocks,
                    UniAddr = device.UniAddr
                });
            }
        }

        if (options.SourceInodes is { } inodes)
        {
            foreach (var inode in inodes.OrderBy(pair => pair.Key))
            {
                parameters.Inodes.Add(new ErofsRebuildInode
                {
                    Nid = inode.Key,
                    Path = inode.Value.Path,
                    MTime = inode.Value.Mtime,
                    MTimeNanoseconds = inode.Value.MtimeNanoseconds,
                    Extended = inode.Value.Extended,
                    Compressed = inode.Value.Compressed,
                    Algorithm = inode.Value.Algorithm.ToString(),
                    CompactIndexes = inode.Value.CompactIndexes,
                    InlineData = inode.Value.InlineData,
                    LogicalClusterSize = inode.Value.LogicalClusterSize,
                    SourceExtentClusterSize = inode.Value.SourceExtentClusterSize,
                    SourceExtentRecordSize = inode.Value.SourceExtentRecordSize,
                });
            }
        }

        return parameters;
    }

    /// <summary>Rebuilds the EROFS options from the persisted profile; the profile is untrusted input and is validated.<para>根据持久化档案重建 EROFS 选项；该档案属于不可信输入并会校验。</para></summary>
    internal static ErofsOptions Restore(ErofsRebuildParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var options = new ErofsOptions
        {
            BlockSize = parameters.BlockSize > 0 ? parameters.BlockSize : 4096,
            Compress = parameters.Compress,
            Algorithm = ParseAlgorithm(parameters.Algorithm),
            CompressionLevel = parameters.CompressionLevel > 1 ? Math.Clamp(parameters.CompressionLevel, 1, 9) : 9,
            Lz4HighCompression = true,
            TailPacking = parameters.TailPacking,
            CompressedTailPacking = parameters.CompressedTailPacking,
            UseCompactedIndexes = parameters.UseCompactedIndexes,
            Fragments = parameters.Fragments,
            Dedup = parameters.Dedup,
            Metabox = parameters.Metabox,
            ExtraDeviceCount = parameters.ExtraDeviceCount,
            SuperblockChecksum = parameters.SuperblockChecksum,
            SourceMtimeFeature = parameters.MtimeFeature,
            SourceXattrFilterFeature = parameters.XattrFilterFeature,
            SourceSharedEaInMetabox = parameters.SourceSharedEaInMetabox,
            SourceMetaboxCompressed = parameters.SourceMetaboxCompressed,
            BuildTime = parameters.BuildTime,
            BuildTimeNanoseconds = parameters.BuildTimeNanoseconds,
            BuildTimeOffset = parameters.BuildTimeOffset,
            SourceVolumeNameBytes = parameters.VolumeNameBytes is { Length: 16 } ? parameters.VolumeNameBytes : null,
            DictionarySize = parameters.DictionarySize,
            WindowBits = parameters.WindowBits,
            Lz4MaxDistance = checked((ushort)Math.Clamp(parameters.Lz4MaxDistance, 0, ushort.MaxValue)),
            MaxPclusterSize = parameters.MaxPclusterSize,
            LegacyCompatibility = parameters.LegacyCompatibility,
            OptimizeCompression = true,
        };

        if (parameters.SourceDevices is { Count: > 0 } devices)
        {
            if (devices.Count > ushort.MaxValue)
                throw new FileSystemCorruptedException("EROFS source device table exceeds the on-disk limit.");
            options.SourceDeviceTable = [.. devices.Select(device =>
            {
                if (device is null || device.Tag is not { Length: ErofsDeviceSlot.TagSize }
                    || device.Blocks == 0 || device.Blocks >= 1UL << 48 || device.UniAddr >= 1UL << 48)
                    throw new FileSystemCorruptedException("Invalid EROFS source device descriptor.");
                return new ErofsSourceDevice([.. device.Tag], device.Blocks, device.UniAddr);
            })];
        }

        if (parameters.Codecs.Count > 0)
        {
            var codecs = new Dictionary<ErofsCompressionAlgorithm, ErofsOptions>();
            foreach (var codec in parameters.Codecs)
            {
                var algorithm = ParseAlgorithm(codec.Algorithm);
                codecs[algorithm] = new ErofsOptions
                {
                    Algorithm = algorithm,
                    BlockSize = codec.BlockSize,
                    MaxPclusterSize = codec.MaxPclusterSize,
                    DictionarySize = codec.DictionarySize,
                    WindowBits = codec.WindowBits,
                    Lz4MaxDistance = checked((ushort)Math.Clamp(codec.Lz4MaxDistance, 0, ushort.MaxValue)),
                    CompressionLevel = codec.CompressionLevel > 1 ? Math.Clamp(codec.CompressionLevel, 1, 9) : 9,
                    Lz4HighCompression = true,
                    OptimizeCompression = true,
                };
            }

            options.SourceCompressionOptions = codecs;
            if (!codecs.ContainsKey(options.Algorithm))
            {
                options.Algorithm = codecs.Keys.OrderBy(algorithm => algorithm).First();
            }
        }

        if (parameters.Inodes.Count > 0)
        {
            var inodes = new Dictionary<ulong, ErofsSourceInode>(parameters.Inodes.Count);
            Dictionary<string, ErofsSourceInode>? byPath = null;
            foreach (var inode in parameters.Inodes)
            {
                var record = new ErofsSourceInode(
                    inode.MTime,
                    inode.MTimeNanoseconds,
                    inode.Extended,
                    inode.Compressed,
                    ParseAlgorithm(inode.Algorithm),
                    inode.CompactIndexes,
                    inode.InlineData,
                    inode.LogicalClusterSize,
                    inode.Path,
                    inode.SourceExtentClusterSize,
                    inode.SourceExtentRecordSize);
                inodes[inode.Nid] = record;
                if (inode.Path is { Length: > 0 } path)
                {
                    byPath ??= new(StringComparer.Ordinal);
                    byPath[path] = record;
                }
            }

            options.SourceInodes = inodes;
            options.SourceInodesByPath = byPath;
        }

        return options;
    }

    private static ErofsCompressionAlgorithm ParseAlgorithm(string? name)
        => Enum.TryParse<ErofsCompressionAlgorithm>(name, ignoreCase: false, out var algorithm) && Enum.IsDefined(algorithm)
            ? algorithm
            : throw new FileSystemCorruptedException($"Unknown EROFS compression algorithm '{name}' in preserved metadata.");
}
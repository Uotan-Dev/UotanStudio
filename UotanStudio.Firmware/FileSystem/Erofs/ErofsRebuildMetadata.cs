using UotanStudio.Firmware.FileSystem.Erofs.Builder;
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

    /// <summary>
    /// Captures the persisted profile together with what the rebuild search needs: the authoritative 64-bit
    /// source extent and, only while <see cref="ErofsBuildDefaults.RebuildOptimization"/> is enabled, the
    /// bounded candidate estimates sampled from the parser that is still live at extraction. Sampling decodes
    /// and re-encodes a capped number of clusters, so it is skipped entirely when the search is off and the
    /// snapshot stays as it was before this feature. A candidate deviation that is not finite is persisted as
    /// the largest representable deviation, because a zero-length source cluster would otherwise make the
    /// whole preservation document unserializable.
    /// <para>采集持久化档案以及重建搜索所需的内容：权威 64 位源范围，以及仅在
    /// <see cref="ErofsBuildDefaults.RebuildOptimization"/> 开启时、由解包阶段仍活动的解析器采样得到的有界候选估算。
    /// 采样会解码并重编码有上限数量的簇，因此搜索关闭时整体跳过，快照与引入该功能之前保持一致。非有限的候选
    /// 偏差以可表示的最大偏差持久化，否则长度为零的源簇会让整份保留文档无法序列化。</para>
    /// </summary>
    /// <param name="options">Options derived from the verified source image. <para>由已校验源镜像推导出的选项。</para></param>
    /// <param name="source">Parser still holding the frozen source image; ownership stays with the caller.
    /// <para>仍持有冻结源镜像的解析器；所有权保留在调用方。</para></param>
    /// <param name="sourceFilesystemBytes">Source extent in bytes computed from the 64-bit block count; a
    /// non-positive value is stored as <see langword="null"/> so the rebuild search declines to run.
    /// <para>由 64 位块数算得的源范围字节数；非正值以 <see langword="null"/> 存储，使重建搜索拒绝执行。</para></param>
    /// <param name="token">Cancels the cluster sampling. <para>取消簇采样。</para></param>
    /// <returns>The profile with the recorded extent and any sampled estimates.
    /// <para>记录了范围与采样估算（若有）的档案。</para></returns>
    internal static ErofsRebuildParameters Capture(ErofsOptions options, ErofsParser source,
        long sourceFilesystemBytes, CancellationToken token)
    {
        var parameters = Capture(options);
        parameters.SourceFilesystemBytes = sourceFilesystemBytes > 0 ? sourceFilesystemBytes : null;
        if (!ErofsBuildDefaults.RebuildOptimization || options.SourceCompressionOptions is not { Count: > 0 })
            return parameters;
        foreach (var estimate in source.AnalyzeCompression(options, token))
        {
            parameters.CompressionEstimates.Add(new ErofsRebuildCompressionEstimate
            {
                Algorithm = estimate.Options.Algorithm.ToString(),
                Samples = estimate.Samples,
                SampleBytes = estimate.SampleBytes,
                Candidates = [.. estimate.Candidates.Select(candidate => new ErofsRebuildCompressionCandidate
                {
                    Level = candidate.Level,
                    HighCompression = candidate.HighCompression,
                    Bytes = candidate.Bytes,
                    Error = double.IsFinite(candidate.Error) ? candidate.Error : double.MaxValue,
                    ExactMatches = candidate.ExactMatches,
                })],
            });
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
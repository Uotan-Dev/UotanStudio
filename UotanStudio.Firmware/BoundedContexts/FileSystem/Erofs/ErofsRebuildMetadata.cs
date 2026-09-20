using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

/// <summary>
/// Captures and restores the verified EROFS rebuild profile. The original image is not retained,
/// so the structured metadata document must carry the superblock features, compression records and
/// per-inode storage strategy that <see cref="ErofsParser.ReadRebuildOptions"/> derived from it.
/// <para>采集并还原经校验的 EROFS 重建档案。原始镜像不再保留，因此结构化元数据文档必须携带
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
            // The EROFS disk format does not record the encoder level, so a preserved profile
            // always carries the neutral default; rebuild at the maximum managed level to keep
            // the output close to the source image instead of the lossy level-1 baseline.
            // EROFS 磁盘格式不记录编码级别，保留档案总是中性默认值；重建时使用托管最大级别，
            // 使输出尽量接近源镜像，而不是有损的级别 1 基线。
            CompressionLevel = parameters.CompressionLevel > 1 ? Math.Clamp(parameters.CompressionLevel, 1, 9) : 9,
            // Same rationale as the level: the disk format records no LZ4 encoder identity, and the
            // dense layouts upstream packers produce come from their HC search. A preserved rebuild
            // targets the source density, so it defaults to the HC encoder; the fast matcher would
            // measurably inflate the result.
            // 与压缩级别同理：磁盘格式不记录 LZ4 编码器身份，而上游打包器产出的 dense 布局来自其
            // HC 搜索。保留式重建以源镜像密度为目标，因此默认使用 HC 编码器；快速匹配器会明显增大
            // 产物。
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
            BuildTime = parameters.BuildTime,
            BuildTimeNanoseconds = parameters.BuildTimeNanoseconds,
            BuildTimeOffset = parameters.BuildTimeOffset,
            SourceVolumeNameBytes = parameters.VolumeNameBytes is { Length: 16 } ? parameters.VolumeNameBytes : null,
            DictionarySize = parameters.DictionarySize,
            WindowBits = parameters.WindowBits,
            Lz4MaxDistance = checked((ushort)Math.Clamp(parameters.Lz4MaxDistance, 0, ushort.MaxValue)),
            MaxPclusterSize = parameters.MaxPclusterSize,
            LegacyCompatibility = parameters.LegacyCompatibility,
            // A preserved rebuild exists to reproduce the source image, and upstream packers fill each
            // physical block with byte-granular (dense) pclusters. Without the same layout the rebuilt
            // image is measurably larger than its source, so the layout planner is always enabled here.
            // 保留式重建的目标是复现源镜像，而上游打包器会用按字节粒度（dense）的 pcluster 填满每个
            // 物理块。若不采用同一布局，重建镜像会明显大于源镜像，因此这里始终启用该布局规划器。
            OptimizeCompression = true,
        };

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
                    // Same rationale as the primary level: EROFS stores no encoder level, so a
                    // neutral 1 in the profile means "unknown" and is rebuilt at the maximum.
                    // 与主级别同理：EROFS 不存编码级别，档案中的中性 1 表示"未知"，按最大级别重建。
                    CompressionLevel = codec.CompressionLevel > 1 ? Math.Clamp(codec.CompressionLevel, 1, 9) : 9,
                    Lz4HighCompression = true,
                    // Per-node planning reads these codec objects (node.CompressionOptions), so the
                    // dense layout has to be enabled on them too, not only on the primary options.
                    // 逐节点规划读取的就是这些 codec 对象（node.CompressionOptions），因此 dense 布局
                    // 也必须在其上启用，而不能只设主选项。
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
                    inode.Path);
                inodes[inode.Nid] = record;
                // Newer documents carry the image-absolute path; it survives file additions and
                // removals (which shift nids), so the builder can bind per-inode strategies in
                // plain directory repacks where no edit plan maps paths back to nids.
                // 新文档携带镜像内绝对路径；路径不随文件增删漂移（nid 会），因此无 editPlan 的
                // 纯目录重打包也能按路径绑定逐 inode 策略。
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
            : throw new InvalidDataException($"Unknown EROFS compression algorithm '{name}' in preserved metadata.");
}
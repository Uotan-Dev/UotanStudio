using System.Text;
using UotanStudio.Firmware.Core.Text;
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
    /// <summary>Projects parsed rebuild options into their persisted form. The raw superblock feature words and
    /// the dot-omitted dimension are stored only when the options carry a declaration, so a profile without one
    /// keeps both fields absent.<para>将解析得到的重建选项投影为持久化形式。仅在选项携带声明时才保存原始超级块
    /// 特性字与 dot-omitted 维度，未携带声明的档案会让两个字段保持缺失。</para></summary>
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
            DroppedIncompatBits = options.DroppedIncompatBits,
        };
        if (!options.Features.IsAuto)
        {
            parameters.FeatureCompat = options.Features.CompatBits;
            parameters.FeatureIncompat = options.Features.IncompatBits;
        }

        if (options.DotOmitted != ErofsTriStateFeature.Auto)
        {
            parameters.DotOmitted = options.DotOmitted.ToString();
        }

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

    /// <summary>
    /// Rebuilds the EROFS options from the persisted profile; the profile is untrusted input and is validated.
    /// A profile carrying the recorded feature words makes them the authoritative declaration together with the
    /// stored dot-omitted dimension; a snapshot predating those fields keeps the declaration derived, so such a
    /// rebuild is byte-identical to the profile-less path.
    /// <para>根据持久化档案重建 EROFS 选项；该档案属于不可信输入并会校验。记录了特性字的档案会让这两个字连同
    /// 保存的 dot-omitted 维度成为权威声明；早于这些字段的快照仍按推导式声明，使该重建与无档案路径逐字节一致。</para>
    /// </summary>
    /// <param name="parameters">Persisted profile read from the metadata document.
    /// <para>从元数据文档读出的持久化档案。</para></param>
    /// <returns>The options a rebuild consumes, with the inherited declaration replayed when the profile carries
    /// one. <para>重建所消费的选项；档案携带声明时一并重放。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The document holds an unknown algorithm or dot-omitted
    /// name, an out-of-range field, or a device table that cannot be re-emitted.
    /// <para>文档含未知的算法名或 dot-omitted 名称、超出范围字段，或无法重新输出的设备表。</para></exception>
    /// <exception cref="FileSystemUnsupportedFeatureException">The inherited incompat word declares a feature the
    /// managed writer can only parse. <para>继承的 incompat 字声明了写入方仅能解析的特性。</para></exception>
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

        if (parameters.FeatureCompat is { } featureCompat && parameters.FeatureIncompat is { } featureIncompat)
        {
            RejectUnemittableIncompat(featureIncompat);
            options.Features = ErofsFeatureSet.FromWords(featureCompat, featureIncompat);
            options.FeaturesAuthoritative = true;
            options.DotOmitted = ParseDotOmitted(parameters.DotOmitted);
        }

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

        if (parameters.ExtraDeviceCount > 0)
        {
            options.ExtraDeviceImages = ReadExtraDeviceDescriptors(parameters, options.BlockSize);
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

    /// <summary>
    /// Rejects an inherited incompat word naming a structure the managed writer can only parse, so a
    /// preserved rebuild never declares a feature it cannot lay out on disk.
    /// <para>拒绝继承的 incompat 字中写入方仅能解析、无法重新输出的结构位，使保留式重建不会声明无法落盘的
    /// 特性。</para>
    /// </summary>
    /// <param name="incompatBits">Incompat word recorded by the extraction.
    /// <para>解包阶段记录的 incompat 字。</para></param>
    /// <exception cref="FileSystemUnsupportedFeatureException">The declaration names at least one read-only
    /// incompat feature. <para>声明至少涉及一个只读 incompat 特性。</para></exception>
    private static void RejectUnemittableIncompat(uint incompatBits)
    {
        var names = ErofsFeatureTable.ReadOnlyIncompatNames(incompatBits);
        if (names.Length == 0)
        {
            return;
        }

        var guidance = string.Join("; ", ErofsFeatureTable.Entries
            .Where(row => !row.IsCompat && row.Capability == ErofsFeatureCapability.ReadOnlyForEmit
                && (incompatBits & row.Bit) != 0)
            .Select(row => $"{ErofsFeatureTable.NameOf(row.Feature)} requires at least Linux "
                + $"{ErofsFeatureTable.DescribeKernel(row.Feature)} and cannot be re-emitted; clear it with "
                + $"ErofsFeatures=!{ErofsFeatureTable.NameOf(row.Feature)}"));
        throw new FileSystemUnsupportedFeatureException(
            $"EROFS source image declares read-only incompatible features {names}. {guidance}.");
    }

    /// <summary>
    /// Reads the stored <c>ErofsTriStateFeature</c> member name for the dot-omitted advise dimension, which
    /// the extraction recorded as text so the document stays locale-free.
    /// <para>读取为 dot-omitted advise 维度保存的 <c>ErofsTriStateFeature</c> 成员名；解包阶段以文本记录，
    /// 使文档不依赖语言环境。</para>
    /// </summary>
    /// <param name="name">Stored member name, or <see langword="null"/> when the snapshot predates the field.
    /// <para>保存的成员名；快照早于该字段时为 <see langword="null"/>。</para></param>
    /// <returns>The declared dimension; <see cref="ErofsTriStateFeature.Auto"/> when nothing was stored.
    /// <para>声明的维度；未记录时返回 <see cref="ErofsTriStateFeature.Auto"/>。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The stored text is not a member name.
    /// <para>保存的文本不是成员名。</para></exception>
    private static ErofsTriStateFeature ParseDotOmitted(string? name)
    {
        if (name is null)
        {
            return ErofsTriStateFeature.Auto;
        }

        if (Enum.TryParse<ErofsTriStateFeature>(name, ignoreCase: false, out var resolved) && Enum.IsDefined(resolved))
        {
            return resolved;
        }

        throw new FileSystemCorruptedException(
            $"Unknown EROFS dot-omitted declaration '{name}' in preserved metadata.");
    }

    /// <summary>
    /// Replays the preserved device table as the descriptors the writer needs, so a declared
    /// <c>device_table</c> bit always comes with the on-disk slots behind it.
    /// <para>把保留的设备表重放为写入方需要的描述符，使已声明的 <c>device_table</c> 位总是带着其背后真正落盘
    /// 的槽位。</para>
    /// </summary>
    /// <param name="parameters">Untrusted preserved profile. <para>不可信的保留档案。</para></param>
    /// <param name="blockSize">Block size of the rebuild, used to turn slot blocks into bytes.
    /// <para>重建的块大小，用于把槽位块数换算为字节数。</para></param>
    /// <returns>One descriptor per declared extra device.
    /// <para>每个已声明的额外设备一个描述符。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The declared count has no recorded slot, or a slot
    /// cannot be named and sized on disk. <para>声明的数量缺少记录的槽位，或某槽位无法在磁盘上命名与定长。</para></exception>
    private static ErofsDeviceSpec[] ReadExtraDeviceDescriptors(ErofsRebuildParameters parameters, int blockSize)
    {
        if (parameters.ExtraDeviceCount > parameters.SourceDevices.Count)
        {
            throw new FileSystemCorruptedException(
                "EROFS preserved metadata declares extra devices it never recorded a slot for.");
        }

        var descriptors = new ErofsDeviceSpec[parameters.ExtraDeviceCount];
        for (var i = 0; i < descriptors.Length; i++)
        {
            var device = parameters.SourceDevices[i];
            var tag = Encoding.UTF8.GetStringTrimNul(device.Tag);
            if (device.Blocks == 0 || device.Blocks > (ulong)(long.MaxValue / blockSize)
                || tag.Length == 0 || Encoding.UTF8.GetByteCount(tag) >= ErofsDeviceSlot.TagSize)
            {
                throw new FileSystemCorruptedException("EROFS preserved device table cannot be re-emitted.");
            }

            descriptors[i] = new ErofsDeviceSpec(tag, checked((long)device.Blocks * blockSize));
        }

        return descriptors;
    }

    private static ErofsCompressionAlgorithm ParseAlgorithm(string? name)
        => Enum.TryParse<ErofsCompressionAlgorithm>(name, ignoreCase: false, out var algorithm) && Enum.IsDefined(algorithm)
            ? algorithm
            : throw new FileSystemCorruptedException($"Unknown EROFS compression algorithm '{name}' in preserved metadata.");
}
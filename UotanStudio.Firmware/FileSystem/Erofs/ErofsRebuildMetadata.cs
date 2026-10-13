using System.Text;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UnifiedChunkIdata = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsChunkIdata;
using UnifiedCompressedIdata = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsCompressedIdata;
using UnifiedDataLayout = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsDataLayout;
using UnifiedFlatIdata = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsFlatIdata;
using UnifiedIdata = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.IErofsIdata;
using UnifiedInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;
using UnifiedInodeLayer = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInodeLayer;

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
    /// keeps both fields absent. The fragment dimensions and the metabox requirement are stored for every
    /// profile, and every recorded inode carries its fragment shape and secondary algorithm as enumeration
    /// member names so the document stays locale-free.
    /// <para>将解析得到的重建选项投影为持久化形式。仅在选项携带声明时才保存原始超级块特性字与 dot-omitted
    /// 维度，未携带声明的档案会让两个字段保持缺失。碎片维度与 metabox 要求对每个档案一律保存，且每条已记录
    /// inode 都以枚举成员名携带其 fragment 形态与第二算法，使文档不依赖语言环境。</para></summary>
    /// <param name="options">Options derived from the verified source image.
    /// <para>由已校验源镜像推导出的选项。</para></param>
    /// <returns>The persisted profile holding every inherited dimension.
    /// <para>携带全部继承维度的持久化档案。</para></returns>
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
            AllFilesAsFragments = options.AllFilesAsFragments,
            Dedup = options.Dedup,
            DedupeMode = options.DedupeMode.ToString(),
            Metabox = options.Metabox,
            MetaboxRequired = options.MetaboxRequired,
            ExtraDeviceCount = options.ExtraDeviceCount,
            SuperblockChecksum = options.SuperblockChecksum,
            MtimeFeature = options.SourceMtimeFeature,
            XattrFilterFeature = options.SourceXattrFilterFeature,
            XattrNameFilter = options.XattrNameFilter,
            IshareXattrPrefix = options.IshareXattrPrefix,
            IshareXattrs = options.IshareXattrs,
            SourceSharedEaInMetabox = options.SourceSharedEaInMetabox,
            SourceMetaboxCompressed = options.SourceMetaboxCompressed,
            MetaBlkAddr = options.SourceMetaBlkAddr == 0 ? null : options.SourceMetaBlkAddr,
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

        if (options.SourceXattrPrefixes is { Count: > 0 } prefixes)
        {
            parameters.XattrPrefixes = [.. prefixes.Select(prefix => new ErofsRebuildXattrPrefix
            {
                BaseIndex = prefix.BaseIndex,
                Infix = prefix.Infix,
            })];
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
                var source = inode.Value;
                var extentState = source.Idata as UnifiedCompressedIdata;
                parameters.Inodes.Add(new ErofsRebuildInode
                {
                    Nid = inode.Key,
                    Path = source.SourcePath,
                    MTime = source.CTime,
                    MTimeNanoseconds = source.CTimeNsec,
                    Extended = source.IsExtended,
                    Compressed = source.SourceCompressed,
                    Algorithm = source.SourceAlgorithm.ToString(),
                    CompactIndexes = source.DataLayout == UnifiedDataLayout.CompactIndexes,
                    InlineData = source.DataLayout == UnifiedDataLayout.Inline,
                    LogicalClusterSize = source.LogicalClusterSize,
                    SourceExtentClusterSize = extentState is { HasExtents: true } ? extentState.ExtentClusterSize : null,
                    SourceExtentRecordSize = extentState is { HasExtents: true } ? extentState.ExtentRecordSize : null,
                    FragmentShape = source.SourceFragmentShape.ToString(),
                    SecondaryAlgorithm = source.SecondaryAlgorithm?.ToString(),
                    ChunkBits = source.ChunkFormatBlkBits,
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
    /// rebuild is byte-identical to the profile-less path. A profile without the all-fragments entry reads that
    /// advise from the recorded fragments dimension, while a profile without the metabox requirement or without
    /// the per-inode fragment text restores those dimensions as cleared and single-algorithm.
    /// <para>根据持久化档案重建 EROFS 选项；该档案属于不可信输入并会校验。记录了特性字的档案会让这两个字连同
    /// 保存的 dot-omitted 维度成为权威声明；早于这些字段的快照仍按推导式声明，使该重建与无档案路径逐字节一致。
    /// 未记录 all-fragments 的档案从已记录的 fragments 维度读取该 advise；未记录 metabox 要求或未记录逐 inode
    /// fragment 文本的档案则把这些维度还原为关闭与单算法。</para>
    /// </summary>
    /// <param name="parameters">Persisted profile read from the metadata document.
    /// <para>从元数据文档读出的持久化档案。</para></param>
    /// <returns>The options a rebuild consumes, with the inherited declaration replayed when the profile carries
    /// one. <para>重建所消费的选项；档案携带声明时一并重放。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The document holds an unknown algorithm, dot-omitted,
    /// fragment-shape or secondary-algorithm name, an out-of-range field, a device table that cannot be
    /// re-emitted, a recorded inode algorithm without a configuration record behind it, or a metabox
    /// requirement recorded without the metabox declaration it needs.
    /// <para>文档含未知的算法名、dot-omitted 名称、fragment 形态名或第二算法名、超出范围字段、无法重新输出的
    /// 设备表、缺少配置记录的已记录 inode 算法，或缺少 metabox 声明的 metabox 要求。</para></exception>
    /// <exception cref="FileSystemUnsupportedFeatureException">The inherited incompat word declares a feature the
    /// managed writer can only parse. <para>继承的 incompat 字声明了写入方仅能解析的特性。</para></exception>
    internal static ErofsOptions Restore(ErofsRebuildParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.MetaboxRequired is true && !parameters.Metabox)
        {
            throw new FileSystemCorruptedException(
                "EROFS preserved metadata requires a metabox container while the recorded image declares no " +
                "metabox feature, so no layout can honour both.");
        }

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
            AllFilesAsFragments = parameters.AllFilesAsFragments ?? parameters.Fragments,
            Dedup = parameters.Dedup,
            DedupeMode = ParseDedupeMode(parameters.DedupeMode),
            Metabox = parameters.Metabox,
            MetaboxRequired = parameters.MetaboxRequired ?? false,
            ExtraDeviceCount = parameters.ExtraDeviceCount,
            SuperblockChecksum = parameters.SuperblockChecksum,
            SourceMtimeFeature = parameters.MtimeFeature,
            SourceXattrFilterFeature = parameters.XattrFilterFeature,
            XattrNameFilter = parameters.XattrNameFilter ?? parameters.XattrFilterFeature,
            IshareXattrPrefix = parameters.IshareXattrPrefix,
            IshareXattrs = parameters.IshareXattrs ?? false,
            SourceSharedEaInMetabox = parameters.SourceSharedEaInMetabox,
            SourceMetaboxCompressed = parameters.SourceMetaboxCompressed,
            SourceMetaBlkAddr = parameters.MetaBlkAddr ?? 0,
            BuildTime = parameters.BuildTime,
            BuildTimeNanoseconds = parameters.BuildTimeNanoseconds,
            BuildTimeOffset = parameters.BuildTimeOffset,
            SourceVolumeNameBytes = parameters.VolumeNameBytes is { Length: 16 } ? parameters.VolumeNameBytes : null,
            DictionarySize = parameters.DictionarySize,
            WindowBits = parameters.WindowBits,
            Lz4MaxDistance = checked((ushort)Math.Clamp(parameters.Lz4MaxDistance, 0, ushort.MaxValue)),
            MaxPclusterSize = parameters.MaxPclusterSize,
            LegacyCompatibility = parameters.LegacyCompatibility,
        };

        if (parameters.FeatureCompat is { } featureCompat && parameters.FeatureIncompat is { } featureIncompat)
        {
            options.Features = ErofsFeatureSet.FromWords(featureCompat, featureIncompat);
            options.FeaturesAuthoritative = true;
            options.DotOmitted = ParseDotOmitted(parameters.DotOmitted);
        }

        if (parameters.XattrPrefixes is { Count: > 0 } prefixes)
        {
            options.SourceXattrPrefixes = [.. prefixes.Select(prefix => (prefix.BaseIndex, prefix.Infix))];
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
            var inodes = new Dictionary<ulong, UnifiedInode>(parameters.Inodes.Count);
            Dictionary<string, UnifiedInode>? byPath = null;
            foreach (var inode in parameters.Inodes)
            {
                var algorithm = ParseAlgorithm(inode.Algorithm);
                if (inode.Compressed && !CanReencodeWithRecords(options, algorithm))
                {
                    throw new FileSystemCorruptedException(
                        $"EROFS preserved inode '{inode.Path}' names compression algorithm '{inode.Algorithm}' " +
                        "that carries no configuration record, so the rebuilt superblock would advertise an " +
                        "algorithm the image cannot decode.");
                }

                var hasExtents = inode.SourceExtentRecordSize is not null || inode.SourceExtentClusterSize is not null;
                var layout = inode.Compressed
                    ? inode.CompactIndexes ? UnifiedDataLayout.CompactIndexes : UnifiedDataLayout.Compressed
                    : inode.InlineData ? UnifiedDataLayout.Inline
                    : inode.ChunkBits != 0 ? UnifiedDataLayout.ChunkBased
                    : UnifiedDataLayout.Flat;
                UnifiedIdata idata = layout switch
                {
                    UnifiedDataLayout.Compressed or UnifiedDataLayout.CompactIndexes => new UnifiedCompressedIdata
                    {
                        CompactedIndexes = layout == UnifiedDataLayout.CompactIndexes,
                        LogicalClusterSize = inode.LogicalClusterSize,
                        HasExtents = hasExtents,
                        ExtentRecordSize = inode.SourceExtentRecordSize ?? 0,
                        ExtentClusterSize = inode.SourceExtentClusterSize ?? 0,
                    },
                    UnifiedDataLayout.ChunkBased => new UnifiedChunkIdata { ChunkFormat = (ushort)inode.ChunkBits },
                    _ => new UnifiedFlatIdata(),
                };

                var node = new UnifiedInode
                {
                    Layer = UnifiedInodeLayer.Source,
                    Nid = inode.Nid,
                    SourcePath = inode.Path,
                    CTime = inode.MTime,
                    CTimeNsec = inode.MTimeNanoseconds,
                    IFormat = inode.Extended ? UnifiedInode.FlagExtended : (ushort)0,
                    DataLayout = layout,
                    Idata = idata,
                    SourceCompressed = inode.Compressed,
                    SourceAlgorithm = algorithm,
                    SourceFragmentShape = ParseFragmentShape(inode.FragmentShape),
                    SecondaryAlgorithm = ParseSecondaryAlgorithm(inode.SecondaryAlgorithm),
                };
                inodes[inode.Nid] = node;
                if (inode.Path is { Length: > 0 } path)
                {
                    byPath ??= new(StringComparer.Ordinal);
                    byPath[path] = node;
                }
            }

            options.SourceInodes = inodes;
            options.SourceInodesByPath = byPath;
        }

        return options;
    }

    /// <summary>
    /// Reports whether a recorded inode algorithm can be re-encoded by the rebuild, which is precisely whether
    /// it has a configuration record behind it. The superblock bitmap is built from the encodable algorithms,
    /// so an inode naming one without a record would leave the bitmap holding a bit the configuration area
    /// never answers, and both a kernel and <c>fsck.erofs</c> consume the records strictly in ascending bit
    /// order (erofs-utils emits them the same way, in <c>lib/compress.c</c>). A profile carrying no records
    /// encodes every payload with the single selected algorithm, so that name is the only acceptable one.
    /// <para>判断已记录的 inode 算法能否被本次重建重新编码，判据就是它背后是否有配置记录。超级块位图由可编码
    /// 的算法集合构成，因此缺少记录的 inode 会让位图携带配置区永远不会应答的位，而内核与
    /// <c>fsck.erofs</c> 都严格按位升序消费这些记录（erofs-utils 在 <c>lib/compress.c</c> 中也按同一顺序写出）。
    /// 不携带任何记录的档案会用唯一选定的算法编码全部负载，所以只有该名称可以接受。</para>
    /// </summary>
    /// <param name="options">Options already carrying the restored codec records.
    /// <para>已携带还原编解码器记录的选项。</para></param>
    /// <param name="algorithm">Algorithm named by a recorded inode.
    /// <para>已记录 inode 指名的算法。</para></param>
    /// <returns><see langword="true"/> when a record exists for the algorithm.
    /// <para>该算法存在对应配置记录时为 <see langword="true"/>。</para></returns>
    private static bool CanReencodeWithRecords(ErofsOptions options, ErofsCompressionAlgorithm algorithm)
        => options.SourceCompressionOptions is { } records
            ? records.ContainsKey(algorithm)
            : algorithm == options.Algorithm;

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
    /// Reads the stored <c>ErofsFragmentShape</c> member name for the packed-inode placement of one preserved
    /// inode, which the extraction recorded as text so the document stays locale-free. A profile without an
    /// entry for the dimension describes a payload that sits in its own blocks or pclusters.
    /// <para>读取为单个保留 inode 的 packed inode 存放形态保存的 <c>ErofsFragmentShape</c> 成员名；解包阶段以
    /// 文本记录，使文档不依赖语言环境。未记录该维度的档案描述的是存放在自身块或 pcluster 中的负载。</para>
    /// </summary>
    /// <param name="name">Stored member name, or <see langword="null"/> when the profile carries no entry for
    /// the dimension. <para>保存的成员名；档案未携带该维度时为 <see langword="null"/>。</para></param>
    /// <returns>The recorded shape; <see cref="ErofsFragmentShape.None"/> when nothing was stored.
    /// <para>记录的形态；未保存时为 <see cref="ErofsFragmentShape.None"/>。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The stored text is not a member name.
    /// <para>保存的文本不是成员名。</para></exception>
    private static ErofsFragmentShape ParseFragmentShape(string? name)
    {
        if (name is null)
        {
            return ErofsFragmentShape.None;
        }

        if (Enum.TryParse<ErofsFragmentShape>(name, ignoreCase: false, out var shape) && Enum.IsDefined(shape))
        {
            return shape;
        }

        throw new FileSystemCorruptedException(
            $"Unknown EROFS fragment shape '{name}' in preserved metadata.");
    }

    /// <summary>
    /// Reads the stored <c>ErofsDedupeMode</c> member name for the dedupe dimension of one preserved profile.
    /// A profile that predates the field restores the repository's existing whole-file comparison, which is
    /// exactly what such a profile was captured with.
    /// <para>读取单个保留档案去重维度保存的 <c>ErofsDedupeMode</c> 成员名。早于该字段的档案还原为仓库既有的
    /// 整文件比较，因为这正是该档案被采集时所使用的语义。</para>
    /// </summary>
    /// <param name="name">Stored member name, or <see langword="null"/> when absent.
    /// <para>保存的成员名；缺失时为 <see langword="null"/>。</para></param>
    /// <returns>The recorded mode; <see cref="ErofsDedupeMode.WholeFile"/> when nothing was stored.
    /// <para>记录的模式；未保存时为 <see cref="ErofsDedupeMode.WholeFile"/>。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The stored text is not a member name.
    /// <para>保存的文本不是成员名。</para></exception>
    private static ErofsDedupeMode ParseDedupeMode(string? name)
    {
        if (name is null)
        {
            return ErofsDedupeMode.WholeFile;
        }

        if (Enum.TryParse<ErofsDedupeMode>(name, ignoreCase: false, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        throw new FileSystemCorruptedException(
            $"Unknown EROFS dedupe mode '{name}' in preserved metadata.");
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

    /// <summary>
    /// Reads the stored <c>ErofsCompressionAlgorithm</c> member name of the secondary algorithm a preserved
    /// inode names through the high nibble of <c>h_algorithmtype</c>, which the extraction recorded as text so
    /// the document stays locale-free. A profile without an entry leaves the inode with one algorithm, so the
    /// rebuilt index rows keep the single-algorithm form.
    /// <para>读取为保留 inode 经 <c>h_algorithmtype</c> 高四位指名的第二算法保存的
    /// <c>ErofsCompressionAlgorithm</c> 成员名；解包阶段以文本记录，使文档不依赖语言环境。未记录该项的档案让
    /// inode 只携带一个算法，因此重建出的索引行保持单算法形态。</para>
    /// </summary>
    /// <param name="name">Stored member name, or <see langword="null"/> when the profile carries no entry for
    /// the secondary algorithm. <para>保存的成员名；档案未记录第二算法时为 <see langword="null"/>。</para></param>
    /// <returns>The named algorithm, or <see langword="null"/> when nothing was stored.
    /// <para>指名的算法；未记录时为 <see langword="null"/>。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The stored text is not a member name.
    /// <para>保存的文本不是成员名。</para></exception>
    private static ErofsCompressionAlgorithm? ParseSecondaryAlgorithm(string? name)
    {
        if (name is null)
        {
            return null;
        }

        if (Enum.TryParse<ErofsCompressionAlgorithm>(name, ignoreCase: false, out var algorithm)
            && Enum.IsDefined(algorithm))
        {
            return algorithm;
        }

        throw new FileSystemCorruptedException(
            $"Unknown EROFS secondary compression algorithm '{name}' in preserved metadata.");
    }
}
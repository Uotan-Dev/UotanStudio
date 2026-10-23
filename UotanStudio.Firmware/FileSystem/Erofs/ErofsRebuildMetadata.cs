using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Metadata;
using UnifiedChunkIdata = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsChunkIdata;
using UnifiedCompressedIdata = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsCompressedIdata;
using UnifiedDataLayout = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsDataLayout;
using UnifiedFlatIdata = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsFlatIdata;
using UnifiedIdata = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.IErofsIdata;
using UnifiedInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;

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
    /// the dot-omitted dimension are stored only when the options carry a declaration, and the words are stored
    /// exactly as the source declared them so a rebuild replays them verbatim. The fragment dimensions and the
    /// metabox requirement are stored for every profile, and every recorded inode carries its fragment shape,
    /// secondary algorithm and metadata creation type as enumeration member names so the document stays
    /// locale-free. The source inode number is recorded
    /// so the output republishes the 32-bit <c>stat</c> identity of the source, and is left out for a source that
    /// recorded none. Dimensions the format stores as raw bytes are recorded verbatim as base64 rather than as
    /// text, because the on-disk device-slot tag may hold a digest and the chunk index table is a packed array:
    /// the slot tag, the complete chunk format word with its index table, the per-inode <c>i_format</c> word with
    /// the compacted-2B and interlaced shapes, and the superblock's extended-slot and directory-block geometry.
    /// <para>将解析得到的重建选项投影为持久化形式。仅在选项携带声明时才保存原始超级块特性字与 dot-omitted
    /// 维度，且特性字按源镜像声明原样保存，使重建逐字重放。碎片维度与 metabox 要求对每个档案一律保存，且每条已记录
    /// inode 都以枚举成员名携带其 fragment 形态、第二算法与元数据创建类型，使文档不依赖语言环境。源 inode
    /// 编号亦被记录，使输出重新发布源的 32 位 <c>stat</c> 身份；源未记录编号时则不写入。格式以原始字节保存的
    /// 维度按 base64 逐字记录而不做文本化，因为磁盘设备槽 tag 可能存放摘要，chunk 索引表则是打包数组：槽位 tag、
    /// 含索引表的完整 chunk 格式字、逐 inode 的 <c>i_format</c> 字与紧凑 2 字节、交错形态，以及超级块的扩展槽
    /// 与目录块几何。</para></summary>
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
            Lz4HighCompression = options.Lz4HighCompression,
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
            SourceMetaboxAlgorithm = options.SourceMetaboxAlgorithm?.ToString(),
            MetaBlkAddr = options.SourceMetaBlkAddr == 0 ? null : options.SourceMetaBlkAddr,
            SbExtSlots = options.SourceSbExtSlots,
            DirBlkBits = options.SourceDirBlkBits,
            BuildTime = options.BuildTime,
            BuildTimeNanoseconds = options.BuildTimeNanoseconds,
            BuildTimeOffset = options.BuildTimeOffset,
            VolumeNameBytes = options.SourceVolumeNameBytes,
            DictionarySize = options.DictionarySize,
            WindowBits = options.WindowBits,
            Lz4MaxDistance = options.Lz4MaxDistance,
            MaxPclusterSize = options.MaxPclusterSize,
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

        parameters.NlinkOneAdviseSupported = options.NlinkOneAdviseSupported;

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
                    Lz4HighCompression = codec.Value.Lz4HighCompression,
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
                    TagBase64 = Convert.ToBase64String(device.Tag),
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
                    Ino = source.Ino == 0 ? null : source.Ino,
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
                    MetadataCreationType = source.MetadataCreationType?.ToString(),
                    ChunkBits = source.ChunkFormatBlkBits,
                    ChunkFormat = source.ChunkFormat,
                    ChunkIndexBase64 = EncodeChunkIndexTable(source),
                    SourceIFormat = source.IFormat,
                    Compacted2B = (source.ZAdvise & ZErofsMapHeader.AdviseCompacted2B) != 0,
                    InterlacedPcluster = (source.ZAdvise & ZErofsMapHeader.AdviseInterlacedPcluster) != 0,
                    Xattrs = source.XattrEntries is { Count: > 0 } attributes
                        ? [.. attributes.Select(attribute => new ErofsRebuildXattr
                        {
                            NameIndex = attribute.NameIndex,
                            Name = attribute.Name,
                            Value = Convert.ToBase64String(attribute.Value),
                        })]
                        : null,
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
    /// A profile carrying the recorded feature words makes them the authoritative declaration, replayed
    /// verbatim without adding or clearing a bit, together with the stored dot-omitted dimension; a snapshot
    /// predating those fields keeps the declaration derived, so such a
    /// rebuild is byte-identical to the profile-less path. A profile without the all-fragments entry reads that
    /// advise from the recorded fragments dimension, while a profile without the metabox requirement or without
    /// the per-inode fragment text restores those dimensions as cleared and single-algorithm. A profile whose
    /// inode records carry no inode number restores them as zero, which is what such a snapshot was captured
    /// with. The raw dimensions are restored from their own fields and degrade to the previous derivation when
    /// absent: a device slot replays its recorded tag bytes instead of re-encoding text, a chunk-based inode
    /// takes the recorded chunk format word and index table, an inode takes the recorded <c>i_format</c> word
    /// with the compacted-2B and interlaced shapes, and the superblock takes the recorded extended-slot and
    /// directory-block geometry only when the document carries them. The per-inode metadata creation type is
    /// parsed from its recorded member name, so the rebuild honours both where the source record lived and
    /// whether it was compact or extended; a profile without the entry keeps the derived form.
    /// <para>根据持久化档案重建 EROFS 选项；该档案属于不可信输入并会校验。记录了特性字的档案会让这两个字成为
    /// 权威声明，逐字重放且不新增也不清除任何位，并连同保存的 dot-omitted 维度一并还原；早于这些字段的快照仍按
    /// 推导式声明，使该重建与无档案路径逐字节一致。
    /// 未记录 all-fragments 的档案从已记录的 fragments 维度读取该 advise；未记录 metabox 要求或未记录逐 inode
    /// fragment 文本的档案则把这些维度还原为关闭与单算法。未记录 inode 编号的档案把编号还原为零，这正是该类快照
    /// 被采集时的语义。原始字节维度由各自的字段还原，缺失时退化为先前的推导：设备槽重放其记录的 tag 字节而不是
    /// 重新编码文本，chunk-based inode 取记录的 chunk 格式字与索引表，inode 取记录的 <c>i_format</c> 字以及紧凑
    /// 2 字节与交错形态，超级块仅在文档携带时取记录的扩展槽与目录块几何。逐 inode 的元数据创建类型按其记录的
    /// 成员名解析，使重建同时遵循源记录的位置与紧凑/扩展宽度；未携带该项的档案保持推导形态。</para>
    /// </summary>
    /// <param name="parameters">Persisted profile read from the metadata document.
    /// <para>从元数据文档读出的持久化档案。</para></param>
    /// <returns>The options a rebuild consumes, with the inherited declaration replayed when the profile carries
    /// one. <para>重建所消费的选项；档案携带声明时一并重放。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The document holds an unknown algorithm, dot-omitted,
    /// fragment-shape, secondary-algorithm or metadata-creation-type name, an out-of-range field, a device table
    /// that cannot be re-emitted, a device tag or chunk index table that is not base64 or does not fit its
    /// on-disk field, an <c>i_format</c> word that raises bits the format does not define, a recorded inode
    /// algorithm without a configuration record behind it, or a metabox requirement recorded without the metabox
    /// declaration it needs.
    /// <para>文档含未知的算法名、dot-omitted 名称、fragment 形态名、第二算法名或元数据创建类型名、超出范围字段、
    /// 无法重新输出的设备表、不是 base64 或超出其磁盘字段宽度的设备 tag 与 chunk 索引表、置有格式未定义位的
    /// <c>i_format</c> 字、缺少配置记录的已记录 inode 算法，或缺少 metabox 声明的 metabox 要求。</para></exception>
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
            CompressionLevel = Math.Clamp(parameters.CompressionLevel, 1, 9),
            Lz4HighCompression = parameters.Lz4HighCompression
                ?? InferLz4HighCompression(parameters.CompressionLevel),
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
            SourceMetaboxAlgorithm = parameters.SourceMetaboxAlgorithm is { } metaboxAlgorithm
                ? ParseAlgorithm(metaboxAlgorithm)
                : null,
            SourceMetaBlkAddr = parameters.MetaBlkAddr ?? 0,
            SourceSbExtSlots = parameters.SbExtSlots,
            SourceDirBlkBits = parameters.DirBlkBits,
            BuildTime = parameters.BuildTime,
            BuildTimeNanoseconds = parameters.BuildTimeNanoseconds,
            BuildTimeOffset = parameters.BuildTimeOffset,
            SourceVolumeNameBytes = parameters.VolumeNameBytes is { Length: 16 } ? parameters.VolumeNameBytes : null,
            DictionarySize = parameters.DictionarySize,
            WindowBits = parameters.WindowBits,
            Lz4MaxDistance = checked((ushort)Math.Clamp(parameters.Lz4MaxDistance, 0, ushort.MaxValue)),
            MaxPclusterSize = parameters.MaxPclusterSize,
        };

        options.RejectIncompleteFeatures = true;

        if (parameters.FeatureCompat is { } featureCompat && parameters.FeatureIncompat is { } featureIncompat)
        {
            options.Features = ErofsFeatureSet.FromWords(featureCompat, featureIncompat);
            options.FeaturesAuthoritative = true;
            options.DotOmitted = ParseDotOmitted(parameters.DotOmitted);
        }

        options.NlinkOneAdviseSupported = parameters.NlinkOneAdviseSupported ?? false;

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
                if (device is null || device.Blocks == 0 || device.Blocks >= 1UL << 48 || device.UniAddr >= 1UL << 48)
                    throw new FileSystemCorruptedException("Invalid EROFS source device descriptor.");
                return new ErofsSourceDevice(PadDeviceTag(ReadDeviceTagBytes(device)), device.Blocks, device.UniAddr);
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
                    CompressionLevel = Math.Clamp(codec.CompressionLevel, 1, 9),
                    Lz4HighCompression = codec.Lz4HighCompression
                        ?? InferLz4HighCompression(codec.CompressionLevel),
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

                var chunkFormat = inode.ChunkFormat ?? (ushort)inode.ChunkBits;
                var chunkIndex = ReadChunkIndexTable(inode, chunkFormat);
                var hasExtents = inode.SourceExtentRecordSize is not null || inode.SourceExtentClusterSize is not null;
                var layout = inode.Compressed
                    ? inode.CompactIndexes ? UnifiedDataLayout.CompactIndexes : UnifiedDataLayout.Compressed
                    : inode.InlineData ? UnifiedDataLayout.Inline
                    : chunkFormat != 0 || chunkIndex is not null ? UnifiedDataLayout.ChunkBased
                    : UnifiedDataLayout.Flat;
                UnifiedIdata idata = layout switch
                {
                    UnifiedDataLayout.Compressed or UnifiedDataLayout.CompactIndexes => new UnifiedCompressedIdata
                    {
                        CompactedIndexes = layout == UnifiedDataLayout.CompactIndexes,
                        Compiled2B = inode.Compacted2B is true && layout == UnifiedDataLayout.CompactIndexes,
                        InterlacedPlainPclusters = inode.InterlacedPcluster is true,
                        LogicalClusterSize = inode.LogicalClusterSize,
                        HasExtents = hasExtents,
                        ExtentRecordSize = inode.SourceExtentRecordSize ?? 0,
                        ExtentClusterSize = inode.SourceExtentClusterSize ?? 0,
                    },
                    UnifiedDataLayout.ChunkBased => new UnifiedChunkIdata
                    {
                        ChunkFormat = chunkFormat,
                        ChunkIndex = chunkIndex?.Entries,
                        DeviceId = chunkIndex?.DeviceId ?? 0,
                    },
                    _ => new UnifiedFlatIdata(),
                };

                var node = new UnifiedInode
                {
                    Layer = ErofsInodeLayer.Source,
                    Nid = inode.Nid,
                    Ino = inode.Ino ?? 0,
                    SourcePath = inode.Path,
                    CTime = inode.MTime,
                    CTimeNsec = inode.MTimeNanoseconds,
                    IFormat = ResolveIFormat(inode),
                    SourceIFormatRecorded = inode.SourceIFormat is not null,
                    ZAdvise = ComposeMapAdvise(inode),
                    DataLayout = layout,
                    Idata = idata,
                    SourceCompressed = inode.Compressed,
                    SourceAlgorithm = algorithm,
                    SourceFragmentShape = ParseFragmentShape(inode.FragmentShape),
                    SecondaryAlgorithm = ParseSecondaryAlgorithm(inode.SecondaryAlgorithm),
                    MetadataCreationType = ParseMetadataCreationType(inode.MetadataCreationType),
                    XattrEntries = inode.Xattrs is { Count: > 0 } stored
                        ? [.. stored.Select(attribute => new ErofsXattrEntry
                        {
                            NameIndex = attribute.NameIndex,
                            Name = attribute.Name,
                            Value = Convert.FromBase64String(attribute.Value),
                        })]
                        : null,
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
    /// Infers the LZ4 encoder identity for a profile that predates the recorded dimension: a level above the
    /// fast encoder's only value can only have been produced by the HC search, so such a profile keeps the
    /// encoder it was captured with instead of silently downgrading to the fast matcher.
    /// <para>为早于该记录维度的档案推断 LZ4 编码器身份：高于快速编码器唯一取值的等级只能由 HC 搜索产生，
    /// 因此这类档案保留其被采集时所用的编码器，而不是静默降级到快速匹配器。</para>
    /// </summary>
    /// <param name="compressionLevel">The recorded primary encoder level.
    /// <para>记录的主编码级别。</para></param>
    /// <returns><see langword="true"/> when the level can only come from the HC encoder.
    /// <para>该等级只能来自 HC 编码器时为 <see langword="true"/>。</para></returns>
    private static bool InferLz4HighCompression(int compressionLevel) => compressionLevel > 1;

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
    /// <c>device_table</c> bit always comes with the on-disk slots behind it. Every descriptor carries the
    /// slot's raw tag bytes in <see cref="ErofsDeviceSpec.TagBytes"/>, because the on-disk tag field may hold a
    /// digest rather than text; the label on <see cref="ErofsDeviceSpec.Tag"/> is only the readable name the
    /// existing validation requires.
    /// <para>把保留的设备表重放为写入方需要的描述符，使已声明的 <c>device_table</c> 位总是带着其背后真正落盘
    /// 的槽位。每个描述符都在 <see cref="ErofsDeviceSpec.TagBytes"/> 中携带槽位的原始 tag 字节，因为磁盘 tag
    /// 字段可能存放摘要而非文本；<see cref="ErofsDeviceSpec.Tag"/> 上的标签只是既有校验所需的可读名称。</para>
    /// </summary>
    /// <param name="parameters">Untrusted preserved profile. <para>不可信的保留档案。</para></param>
    /// <param name="blockSize">Block size of the rebuild, used to turn slot blocks into bytes.
    /// <para>重建的块大小，用于把槽位块数换算为字节数。</para></param>
    /// <returns>One descriptor per declared extra device.
    /// <para>每个已声明的额外设备一个描述符。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The declared count has no recorded slot, or a slot tag
    /// cannot be named and sized on disk. <para>声明的数量缺少记录的槽位，或某槽位 tag 无法在磁盘上命名与定长。</para></exception>
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
            if (device.Blocks == 0 || device.Blocks > (ulong)(long.MaxValue / blockSize))
            {
                throw new FileSystemCorruptedException("EROFS preserved device table cannot be re-emitted.");
            }

            var tag = ReadDeviceTagBytes(device);
            descriptors[i] = new ErofsDeviceSpec(DeviceTagName(tag, i + 1), checked((long)device.Blocks * blockSize))
            {
                TagBytes = tag,
            };
        }

        return descriptors;
    }

    /// <summary>
    /// Reads one device slot's tag as raw bytes: the recorded base64 field is authoritative, and the padded
    /// <see cref="ErofsRebuildDevice.Tag"/> array is the fallback a snapshot predating that field carries. A tag
    /// is never re-encoded, so a digest keeps every byte; only the on-disk slot width bounds it, and a value that
    /// exceeds it is refused instead of being truncated.
    /// <para>读取一个设备槽 tag 的原始字节：记录的 base64 字段为权威，早于该字段的快照则回退到已补零的
    /// <see cref="ErofsRebuildDevice.Tag"/> 数组。tag 从不被重新编码，因此摘要的每个字节都得以保留；仅磁盘槽位
    /// 宽度构成上限，超出的值会被拒绝而不是被截断。</para>
    /// </summary>
    /// <param name="device">Untrusted recorded device slot. <para>不可信的已记录设备槽。</para></param>
    /// <returns>The slot tag bytes, at most <see cref="ErofsDeviceSlot.TagSize"/> long.
    /// <para>槽位 tag 字节，最长为 <see cref="ErofsDeviceSlot.TagSize"/>。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The recorded tag is not base64, is empty, or does not fit
    /// the on-disk slot; a snapshot without the base64 field must carry the fixed-width slot array.
    /// <para>记录的 tag 不是 base64、为空，或无法放入磁盘槽位；未携带 base64 字段的快照必须携带定宽槽位数组。</para></exception>
    private static byte[] ReadDeviceTagBytes(ErofsRebuildDevice device)
    {
        if (device.TagBase64 is { Length: > 0 } recorded)
        {
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(recorded);
            }
            catch (FormatException)
            {
                throw new FileSystemCorruptedException(
                    $"EROFS preserved device tag is not base64 within the {ErofsDeviceSlot.TagSize}-byte slot.");
            }

            if (decoded.Length == 0 || decoded.Length > ErofsDeviceSlot.TagSize)
            {
                throw new FileSystemCorruptedException(
                    $"EROFS preserved device tag records {decoded.Length} bytes, outside the " +
                    $"{ErofsDeviceSlot.TagSize}-byte slot.");
            }

            return decoded;
        }

        if (device.Tag is not { Length: ErofsDeviceSlot.TagSize })
        {
            throw new FileSystemCorruptedException(
                "EROFS preserved device slot carries neither a base64 tag nor the fixed-width tag array.");
        }

        return [.. device.Tag];
    }

    /// <summary>
    /// Grows a device tag to the fixed on-disk slot width so the raw device table keeps the byte identity the
    /// reader observed, including the zero padding that follows a shorter tag.
    /// <para>把设备 tag 扩展到磁盘定宽槽位，使原始设备表保持读取方观测到的字节身份，包括较短 tag 之后的零填充。</para>
    /// </summary>
    /// <param name="tag">Decoded tag bytes, at most one slot wide.
    /// <para>已解码的 tag 字节，最长为槽位宽度。</para></param>
    /// <returns>A new array of exactly <see cref="ErofsDeviceSlot.TagSize"/> bytes.
    /// <para>长度恰为 <see cref="ErofsDeviceSlot.TagSize"/> 的新数组。</para></returns>
    private static byte[] PadDeviceTag(byte[] tag)
    {
        if (tag.Length == ErofsDeviceSlot.TagSize)
        {
            return tag;
        }

        var padded = new byte[ErofsDeviceSlot.TagSize];
        tag.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>
    /// Labels one device slot for the builder descriptor. The on-disk tag field is a fixed 64-byte array that
    /// upstream fills with a digest for blob-backed devices, so text is taken only while the bytes before the
    /// first NUL decode back to themselves and fit the field; otherwise a positional placeholder is used. The
    /// slot bytes always travel verbatim in <see cref="ErofsDeviceSpec.TagBytes"/>, so a binary tag is neither
    /// rejected nor re-encoded.
    /// <para>为构建器描述符标注一个设备槽。磁盘 tag 字段是固定 64 字节数组，上游会为 blob 支撑的设备填入摘要，
    /// 因此仅当首个 NUL 之前的字节能原样解码回自身且落在字段宽度内时才取其文本，否则使用位置占位名。槽位字节始终
    /// 按原样经 <see cref="ErofsDeviceSpec.TagBytes"/> 传递，因此二进制 tag 既不会被拒绝也不会被重新编码。</para>
    /// </summary>
    /// <param name="tag">Decoded slot tag bytes. <para>已解码的槽位 tag 字节。</para></param>
    /// <param name="ordinal">One-based position of the extra device, used for the placeholder label.
    /// <para>额外设备从 1 开始的位置，用于占位标签。</para></param>
    /// <returns>A non-empty label shorter than the on-disk tag field.
    /// <para>非空且短于磁盘 tag 字段的标签。</para></returns>
    private static string DeviceTagName(byte[] tag, int ordinal)
    {
        var terminator = Array.IndexOf(tag, (byte)0);
        var text = terminator < 0 ? tag.AsSpan() : tag.AsSpan(0, terminator);
        if (text.Length is > 0 and < ErofsDeviceSlot.TagSize)
        {
            var label = Encoding.UTF8.GetString(text);
            if (Encoding.UTF8.GetBytes(label).AsSpan().SequenceEqual(text))
            {
                return label;
            }
        }

        return $"dev{ordinal}";
    }

    /// <summary>
    /// Reads the stored <c>i_format</c> word of one preserved inode. A profile without the field keeps the
    /// extended-layout derivation the document used before it existed, while a recorded word is validated
    /// against the bits the format defines so a hand-authored document cannot make the rebuild emit an inode a
    /// reader rejects.
    /// <para>读取单个保留 inode 保存的 <c>i_format</c> 字。未携带该字段的档案保持该字段出现之前的扩展布局推导，
    /// 而已记录的字会按格式定义的位校验，使手工编写的文档无法让重建输出读取方会拒绝的 inode。</para>
    /// </summary>
    /// <param name="inode">Untrusted recorded inode. <para>不可信的已记录 inode。</para></param>
    /// <returns>The format word the restored node carries.
    /// <para>还原节点所携带的格式字。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The recorded word raises bits the format does not define.
    /// <para>记录的字置有格式未定义的位。</para></exception>
    private static ushort ResolveIFormat(ErofsRebuildInode inode)
    {
        if (inode.SourceIFormat is not { } recorded)
        {
            return inode.Extended ? UnifiedInode.FlagExtended : (ushort)0;
        }

        if ((recorded & ~UnifiedInode.AdviseAll) != 0)
        {
            throw new FileSystemCorruptedException(
                $"EROFS preserved inode '{inode.Path}' records undefined i_format bits " +
                $"0x{recorded & ~UnifiedInode.AdviseAll:X4}.");
        }

        return recorded;
    }

    /// <summary>
    /// Rebuilds the map-header advise word of one preserved inode from the recorded shapes. Bit 0 is shared: it
    /// means <c>EXTENTS</c> under the compressed layout and <c>COMPACTED_2B</c> under the compact-index layout
    /// (<c>include/erofs_fs.h</c> gives both the same value), so the bit is replayed as recorded and the layout
    /// decides how a reader interprets it. A profile without either entry keeps a zero advise word, which is what
    /// such a snapshot was captured with.
    /// <para>由记录的形态重建单个保留 inode 的映射头 advise 字。第 0 位是共用的：在压缩布局下表示
    /// <c>EXTENTS</c>，在紧凑索引布局下表示 <c>COMPACTED_2B</c>（<c>include/erofs_fs.h</c> 给两者同一个值），
    /// 因此该位按记录重放，由布局决定读取方如何解释它。未记录任一项的档案保持零 advise 字，这正是该类快照被采集时
    /// 的语义。</para>
    /// </summary>
    /// <param name="inode">Untrusted recorded inode. <para>不可信的已记录 inode。</para></param>
    /// <returns>The replayed advise word, zero when neither shape was recorded.
    /// <para>重放的 advise 字；两种形态都未记录时为零。</para></returns>
    private static ushort ComposeMapAdvise(ErofsRebuildInode inode)
    {
        var advise = (ushort)0;
        if (inode.Compacted2B is true)
        {
            advise |= ZErofsMapHeader.AdviseCompacted2B;
        }

        if (inode.InterlacedPcluster is true)
        {
            advise |= ZErofsMapHeader.AdviseInterlacedPcluster;
        }

        return advise;
    }

    /// <summary>
    /// Serializes one chunk-based inode's index table to its raw on-disk bytes. The entry width follows the
    /// recorded chunk format word: the eight-byte index form stores the high 16 bits of the start block beside
    /// the device id and the low 32 bits, while the four-byte block map stores the start block alone
    /// (<c>include/erofs_fs.h</c>).
    /// <para>把单个 chunk-based inode 的索引表序列化为其磁盘原始字节。表项宽度由记录的 chunk 格式字决定：8 字节
    /// 索引形态把首块的高 16 位与设备号、低 32 位并列存放，4 字节块映射则只存放首块
    /// （<c>include/erofs_fs.h</c>）。</para>
    /// </summary>
    /// <param name="source">Decoded source inode. <para>已解码的源 inode。</para></param>
    /// <returns>The index table bytes as base64, or <see langword="null"/> when the inode is not chunk-based or
    /// carries no index table. <para>索引表字节的 base64；inode 非 chunk-based 或未携带索引表时为 <see langword="null"/>。</para></returns>
    private static string? EncodeChunkIndexTable(UnifiedInode source)
    {
        if (source.Idata is not UnifiedChunkIdata chunk || chunk.ChunkIndex is not { Length: > 0 } entries)
        {
            return null;
        }

        var entrySize = source.UsesChunkIndexes ? UnifiedInode.ChunkIndexEntrySize : UnifiedInode.BlockMapEntrySize;
        var table = new byte[checked(entries.Length * entrySize)];
        for (var i = 0; i < entries.Length; i++)
        {
            var slot = table.AsSpan(i * entrySize, entrySize);
            if (source.UsesChunkIndexes)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(slot, (ushort)(entries[i] >> 32));
                BinaryPrimitives.WriteUInt16LittleEndian(slot[2..], (ushort)chunk.DeviceId);
                BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], (uint)entries[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(slot, (uint)entries[i]);
            }
        }

        return Convert.ToBase64String(table);
    }

    /// <summary>
    /// Decodes one preserved inode's chunk index table into the start blocks the builder replays, together with
    /// the device every entry names. The entry width comes from the chunk format word, so a table whose length
    /// is not a whole number of entries, or whose entries disagree on the device, is refused instead of being
    /// partially applied.
    /// <para>把单个保留 inode 的 chunk 索引表解码为构建器重放的首块，以及每条表项指名的设备。表项宽度取自 chunk
    /// 格式字，因此表长度不是整数条表项、或各表项设备号不一致的表会被拒绝，而不是被部分应用。</para>
    /// </summary>
    /// <param name="inode">Untrusted recorded inode. <para>不可信的已记录 inode。</para></param>
    /// <param name="chunkFormat">Chunk format word of the restored inode.
    /// <para>还原 inode 的 chunk 格式字。</para></param>
    /// <returns>The decoded entries and device id, or <see langword="null"/> when no table was recorded.
    /// <para>已解码的表项与设备号；未记录索引表时为 <see langword="null"/>。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The recorded table is not base64, is empty, is not a whole
    /// number of entries, or mixes device ids. <para>记录的表不是 base64、为空、不是整数条表项，或混用设备号。</para></exception>
    private static (ulong[] Entries, uint DeviceId)? ReadChunkIndexTable(ErofsRebuildInode inode, ushort chunkFormat)
    {
        if (inode.ChunkIndexBase64 is not { Length: > 0 } recorded)
        {
            return null;
        }

        byte[] table;
        try
        {
            table = Convert.FromBase64String(recorded);
        }
        catch (FormatException)
        {
            throw new FileSystemCorruptedException(
                $"EROFS preserved inode '{inode.Path}' records a chunk index table that is not base64.");
        }

        var wide = (chunkFormat & UnifiedInode.ChunkFormatIndexes) != 0;
        var entrySize = wide ? UnifiedInode.ChunkIndexEntrySize : UnifiedInode.BlockMapEntrySize;
        if (table.Length == 0 || table.Length % entrySize != 0)
        {
            throw new FileSystemCorruptedException(
                $"EROFS preserved inode '{inode.Path}' records a chunk index table of {table.Length} bytes, " +
                $"which is not a whole number of {entrySize}-byte entries.");
        }

        var entries = new ulong[table.Length / entrySize];
        uint deviceId = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var slot = table.AsSpan(i * entrySize, entrySize);
            if (!wide)
            {
                entries[i] = BinaryPrimitives.ReadUInt32LittleEndian(slot);
                continue;
            }

            entries[i] = BinaryPrimitives.ReadUInt32LittleEndian(slot[4..])
                | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(slot) << 32);
            var entryDevice = BinaryPrimitives.ReadUInt16LittleEndian(slot[2..]);
            if (i == 0)
            {
                deviceId = entryDevice;
            }
            else if (entryDevice != deviceId)
            {
                throw new FileSystemCorruptedException(
                    $"EROFS preserved inode '{inode.Path}' mixes chunk index device ids {deviceId} and " +
                    $"{entryDevice}.");
            }
        }

        return (entries, deviceId);
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

    /// <summary>
    /// Reads the stored <c>ErofsInodeMetadataCreationType</c> member name recording both the location and the
    /// width of one preserved inode's metadata record, which the extraction recorded as text so the document
    /// stays locale-free. A profile without an entry leaves the creation form to the rebuild's own derivation.
    /// <para>读取为单个保留 inode 元数据记录的位置与宽度保存的 <c>ErofsInodeMetadataCreationType</c> 成员名；
    /// 解包阶段以文本记录，使文档不依赖语言环境。未记录该项的档案让创建形态由重建自行推导。</para>
    /// </summary>
    /// <param name="name">Stored member name, or <see langword="null"/> when the profile carries no entry for the
    /// creation form. <para>保存的成员名；档案未记录创建形态时为 <see langword="null"/>。</para></param>
    /// <returns>The recorded creation form, or <see langword="null"/> when nothing was stored.
    /// <para>记录的创建形态；未保存时为 <see langword="null"/>。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The stored text is not a member name.
    /// <para>保存的文本不是成员名。</para></exception>
    private static ErofsInodeMetadataCreationType? ParseMetadataCreationType(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (ErofsInodeMetadataCreationType.TryParse(name, out var creationType))
        {
            return creationType;
        }

        throw new FileSystemCorruptedException(
            $"Unknown EROFS inode metadata creation type '{name}' in preserved metadata.");
    }
}
using System.Globalization;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.
/// <para>保留源镜像语义，并把编辑后的宿主文件树解析为使用原始或继承 Linux 属性的文件系统构建器。</para>
/// </summary>
public static partial class FileSystemPreservation
{
    /// <summary>
    /// Reports whether the caller overrode EROFS compression, edited the inherited feature declaration, or changed
    /// the packed layout. The rebuild optimizer replays the source algorithm's recorded size estimates, so it can
    /// only be reused while the compression settings, the declaration the estimates were sampled under, and the
    /// fragment and metabox layout the estimates were sampled under have all stayed.
    /// <para>判断调用方是否覆盖了 EROFS 压缩参数、编辑了继承来的特性声明，或改变了打包布局。重建优化器复用
    /// 源算法记录的体积估算，因此只有压缩设置、采样估算所依据的声明，以及其所依据的 fragment 与 metabox
    /// 布局都保持不变时才能继续使用。</para>
    /// </summary>
    private static bool HasCompressionOverride(IReadOnlyDictionary<string, string> options)
        => options.ContainsKey("ErofsAlgorithm") || options.ContainsKey("ErofsCompressionLevel")
            || options.ContainsKey("ErofsFeatures") || options.ContainsKey("ErofsDotOmitted")
            || options.ContainsKey("ErofsFragments") || options.ContainsKey("ErofsMetabox");

    /// <summary>
    /// Builds one edited container and returns the EROFS rebuild report when the bounded candidate search ran or
    /// the build edited the inherited feature declaration, so the caller can merge the search and declaration keys
    /// into the equivalence report. Returns <see langword="null"/> for every outcome that changed neither.
    /// An EROFS compression override reaches every codec record as well as the top-level options because a
    /// preserved rebuild encodes compressed inodes through those records, where a top-level-only policy is dropped.
    /// <para>构建一个编辑后的容器，并在有界候选搜索执行、或构建编辑了继承来的特性声明时返回 EROFS 重建报告，
    /// 便于调用方把搜索键与声明键并入等价报告。两者都未改变的任何结果返回 <see langword="null"/>。EROFS 压缩
    /// 覆盖会同时写入每个编解码器记录与顶层选项，因为保留式重建是经由这些记录编码压缩 inode 的，只改顶层的
    /// 策略在其中会被丢弃。</para>
    /// </summary>
    /// <param name="inputDirectory">Extracted tree to rebuild. <para>待重建的解包目录。</para></param>
    /// <param name="output">Stream receiving the image. <para>接收镜像的流。</para></param>
    /// <param name="format">Filesystem format name. <para>文件系统格式名。</para></param>
    /// <param name="fsConfig">Owner, group and mode overrides keyed by image path.
    /// <para>按镜像内路径索引的属主、属组与权限覆盖。</para></param>
    /// <param name="fileContexts">SELinux label overrides keyed by image path.
    /// <para>按镜像内路径索引的 SELinux 标签覆盖。</para></param>
    /// <param name="rebuild">Recorded rebuild parameters; <see langword="null"/> when the snapshot stored none.
    /// <para>记录的重建参数；快照未存储时为 <see langword="null"/>。</para></param>
    /// <param name="options">Packing options merged from the snapshot and any overrides.
    /// <para>由快照与覆盖合并得到的打包选项。</para></param>
    /// <param name="publicationOutput">File stream receiving the published image, used by the sparse converters.
    /// <para>接收已发布镜像的文件流，供稀疏转换器使用。</para></param>
    /// <param name="temporaryDirectory">Directory for scratch files. <para>暂存文件目录。</para></param>
    /// <param name="token">Cancels the build. <para>取消构建。</para></param>
    /// <param name="progress">Optional staged reporter; the build is reported as
    /// <see cref="FileSystemWorkStage.BuildingImage"/>, where EROFS reports its own payload measurements and the
    /// EXT4 and F2FS stream builds report written image bytes against the estimated source size. May be
    /// <see langword="null"/>. <para>可选的分阶段回调；构建上报为
    /// <see cref="FileSystemWorkStage.BuildingImage"/>，其中 EROFS 上报自身的负载计量，EXT4 与 F2FS 流式构建按估算
    /// 源大小上报已写镜像字节。可为 <see langword="null"/>。</para></param>
    private static Dictionary<string, string>? BuildEdited(string inputDirectory, Stream output, string format,
        IReadOnlyDictionary<string, FsConfigEntry> fsConfig, IReadOnlyDictionary<string, string> fileContexts,
        RebuildParameters? rebuild, IReadOnlyDictionary<string, string> options, FileStream publicationOutput,
        string temporaryDirectory, CancellationToken token, IProgress<FileSystemByteProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (format != "Android Sparse Image (simg)"
            && options.TryGetValue("OutputSparseContainer", out var outerSparse)
            && IsTrue(outerSparse))
        {
            var nestedOptions = new Dictionary<string, string>(options, StringComparer.Ordinal)
            {
                ["OutputSparseContainer"] = "0"
            };
            var temporary = TempFileHelper.CreateScratchPathIn(temporaryDirectory, ".uotan-f2fs-raw");
            using var raw = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
            var nestedReport = BuildEdited(inputDirectory, raw, format, fsConfig, fileContexts, rebuild, nestedOptions,
                publicationOutput, temporaryDirectory, token, progress);
            raw.Flush(flushToDisk: true);
            raw.Position = 0;
            SparseContainerAccess.Resolve().ConvertRawToSparseValidated(raw, output, 4096, raw.Length, token, publicationOutput);
            return nestedReport;
        }
        if (format == "Android Sparse Image (simg)")
        {
            if (rebuild is null || rebuild.InnerFormat is not string inner || rebuild.SparseBlockSize is not int sparseBlock)
                throw new FileSystemCorruptedException("Sparse image rebuild parameters are missing. Re-extract the source image before rebuilding it.");
            var temporary = TempFileHelper.CreateScratchPathIn(temporaryDirectory, ".uotan-edited-raw");
            using var raw = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
            var innerReport = BuildEdited(inputDirectory, raw, inner, fsConfig, fileContexts, rebuild, options,
                publicationOutput, temporaryDirectory, token, progress);
            raw.Flush(flushToDisk: true);
            raw.Position = 0;
            SparseContainerAccess.Resolve().ConvertRawToSparseValidated(raw, output, (uint)sparseBlock, raw.Length, token,
                publicationOutput);
            return innerReport;
        }
        var buildingImage = FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage);
        switch (format)
        {
            case "EXT4":
                {
                    var rebuildBlock = rebuild?.BlockSize ?? 4096;
                    if ((rebuildBlock & (rebuildBlock - 1)) != 0 || rebuildBlock is < 1024 or > 65536)
                        throw new FileSystemCorruptedException("Invalid EXT4 rebuild block size.");
                    var capacityPolicy = ParseExt4CapacityPolicy(options);
                    var blockSharingMode = options.ContainsKey("BlockSharingMode")
                        ? ParseExt4BlockSharingMode(options)
                        : rebuild?.Ext4SharedBlocks == true
                            ? Ext4BlockSharingMode.ShareDuplicateData
                            : Ext4BlockSharingMode.Independent;
                    var ext4Options = new Ext4Options
                    {
                        BlockSize = rebuildBlock,
                        InodeSize = rebuild?.InodeSize ?? 256,
                        CasefoldEncoding = rebuild?.Ext4Encoding,
                        CasefoldEncodingFlags = rebuild?.Ext4EncodingFlags ?? 0,
                        Uuid = rebuild?.Uuid,
                        VolumeName = options.GetValueOrDefault("VolumeName", rebuild?.VolumeName ?? "UOTANSTUDIO"),
                        EnableSparse = IsTrue(options.GetValueOrDefault("EnableSparse")),
                        TargetSizeBytes = ParseTargetSize(options),
                        CapacityMode = capacityPolicy == Ext4PreservedCapacityPolicy.Minimize
                            ? Ext4CapacityMode.Minimize
                            : Ext4CapacityMode.Default,
                        ReadOnly = options.TryGetValue("ReadOnly", out var readOnly)
                            ? IsTrue(readOnly)
                            : rebuild?.Ext4ReadOnly == true || rebuild?.Ext4SharedBlocks == true,
                        BlockSharingMode = blockSharingMode,
                        CancellationToken = token,
                    };
                    ext4Options.FsConfig = fsConfig;
                    ext4Options.FileContexts = fileContexts;
                    var ext4Sink = MeterWrites(inputDirectory, output, buildingImage, token, out var ext4Meter);
                    Ext4Builder.Build(inputDirectory, ext4Sink, ext4Options);
                    ext4Meter?.Complete();
                    break;
                }
            case "EROFS":
                {
                    var profile = rebuild?.Erofs;
                    var erofsOptions = profile is not null
                        ? ErofsRebuildMetadata.Restore(profile)
                        : new ErofsOptions();
                    if (rebuild?.BlockSize is { } blockSize)
                    {
                        erofsOptions.BlockSize = blockSize;
                    }

                    erofsOptions.Uuid = rebuild?.Uuid;
                    var volumeName = options.GetValueOrDefault("VolumeName", rebuild?.VolumeName ?? "erofs");
                    if (volumeName != rebuild?.VolumeName)
                    {
                        erofsOptions.SourceVolumeNameBytes = null;
                    }

                    erofsOptions.VolumeName = volumeName;
                    erofsOptions.FsConfig = fsConfig;
                    erofsOptions.FileContexts = fileContexts;
                    var hasAlgorithm = options.ContainsKey("ErofsAlgorithm");
                    var hasCompressionLevel = options.ContainsKey("ErofsCompressionLevel");
                    if (hasAlgorithm || hasCompressionLevel)
                    {
                        var (algorithm, lz4HighCompression) = ErofsAlgorithmSelectors.Parse(
                            options.GetValueOrDefault("ErofsAlgorithm"));
                        var compressionLevel = IntegerOption(options, "ErofsCompressionLevel", 9);
                        if (hasAlgorithm)
                        {
                            erofsOptions.Algorithm = algorithm;
                            erofsOptions.Lz4HighCompression = lz4HighCompression;
                        }

                        if (hasCompressionLevel)
                        {
                            erofsOptions.CompressionLevel = compressionLevel;
                        }

                        if (erofsOptions.SourceCompressionOptions is { } codecs)
                        {
                            foreach (var codec in codecs.Values)
                            {
                                if (hasAlgorithm)
                                {
                                    codec.Lz4HighCompression = lz4HighCompression;
                                }

                                if (hasCompressionLevel)
                                {
                                    codec.CompressionLevel = compressionLevel;
                                }
                            }
                        }
                    }

                    (uint Compat, uint Incompat)? inheritedWords = null;
                    if (profile is { FeatureCompat: { } inheritedCompat, FeatureIncompat: { } inheritedIncompat })
                    {
                        inheritedWords = (inheritedCompat, inheritedIncompat);
                    }

                    FileSystemPackingOptions.ApplyErofsFeatureKeys(options, erofsOptions, inheritedWords);
                    if (ErofsBuildDefaults.RebuildOptimization
                        && profile is not null
                        && profile.CompressionEstimates.Count > 0
                        && profile.SourceFilesystemBytes > 0
                        && !HasCompressionOverride(options))
                    {
                        var searchReport = ErofsRebuildOptimizer.Build(inputDirectory, output, erofsOptions, profile,
                            token, buildingImage);
                        AddFeatureDeclarationReport(searchReport, erofsOptions);
                        return searchReport;
                    }

                    new ErofsBuilder().Build(inputDirectory, output, erofsOptions, buildingImage);
                    var declarationReport = new Dictionary<string, string>(StringComparer.Ordinal);
                    AddFeatureDeclarationReport(declarationReport, erofsOptions);
                    return declarationReport.Count > 0 ? declarationReport : null;
                }
            case "F2FS":
                {
                    var f2fsOptions = new F2fsOptions
                    {
                        BlockSize = rebuild?.BlockSize ?? 4096,
                        SegmentSize = rebuild?.SegmentSize ?? 2L * 1024 * 1024,
                        Uuid = rebuild?.Uuid is { Length: 16 } uuid ? new Guid(uuid) : null,
                        VolumeName = options.GetValueOrDefault("VolumeName", rebuild?.VolumeName ?? "f2fs"),
                        EnableCompression = options.TryGetValue("EnableCompression", out var compression)
                            ? IsTrue(compression)
                            : rebuild?.F2fsCompressionEnabled ?? false,
                        CompressionAlgorithm = ParseF2fsCompressionAlgorithm(options, rebuild),
                        CompressionLevel = IntegerOption(options, "CompressionLevel", 0),
                        CancellationToken = token,
                    };
                    f2fsOptions.FsConfig = fsConfig;
                    f2fsOptions.FileContexts = fileContexts;
                    if (rebuild is not null)
                    {
                        f2fsOptions.SourceFeature = rebuild.F2fsFeature;
                        f2fsOptions.SourceEncoding = rebuild.F2fsEncoding;
                        f2fsOptions.SourceEncodingFlags = rebuild.F2fsEncodingFlags;
                        f2fsOptions.SourceEncryptionLevel = rebuild.F2fsEncryptionLevel ?? 0;
                        f2fsOptions.SourceEncryptPasswordSalt = rebuild.F2fsEncryptSalt;
                        f2fsOptions.SourceHotExtensionCount = rebuild.F2fsHotExtensionCount;
                        f2fsOptions.CasefoldDirectoriesPresent = rebuild.F2fsCasefoldDirectoriesPresent;
                        f2fsOptions.VerityContentPresent = rebuild.F2fsVerityFilesPresent;
                        f2fsOptions.QuotaInodes =
                        [
                            .. rebuild.F2fsQuotaInodes.Select(static quota => new F2fsQuotaInode(
                                quota.Type, quota.Ino, quota.Mode, quota.Uid, quota.Gid, quota.Flags,
                                quota.Metadata, quota.Content))
                        ];
                    }

                    var f2fsSink = MeterWrites(inputDirectory, output, buildingImage, token, out var f2fsMeter);
                    F2fsBuilder.Build(inputDirectory, f2fsSink, f2fsOptions);
                    f2fsMeter?.Complete();
                    if (rebuild?.F2fsFeature is { } sourceFeature)
                    {
                        return new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["F2fsFeature"] = $"0x{sourceFeature:X8}",
                            ["F2fsDroppedFeatures"] = $"0x{f2fsOptions.DroppedFeatureBits:X8}",
                            ["F2fsQuotaInodes"] = rebuild.F2fsQuotaInodes.Count.ToString(CultureInfo.InvariantCulture)
                        };
                    }

                    break;
                }
            default: throw new FileSystemUnsupportedFeatureException("Editing this filesystem container is not supported.");
        }

        return null;
    }

    /// <summary>
    /// Wraps a build destination so the caller's reporter follows the image bytes the EXT4 and F2FS stream builds
    /// write, matching the denominator those builders use on their path overloads. The EROFS builder reports its own
    /// payload measurements instead, so it is never wrapped.
    /// <para>包装构建目标，使调用方的回调跟随 EXT4 与 F2FS 流式构建写出的镜像字节，与这些构建器在路径重载上使用的
    /// 分母一致。EROFS 构建器自行上报负载计量，因此不做包装。</para>
    /// </summary>
    /// <param name="directory">Tree whose estimated source bytes form the denominator.
    /// <para>其估算源字节构成分母的目录树。</para></param>
    /// <param name="output">Build destination the wrapper forwards to.
    /// <para>包装器转发到的构建目标。</para></param>
    /// <param name="progress">Stage-tagged reporter receiving <c>(bytes written, estimated source bytes)</c>.
    /// <para>接收 <c>(已写字节, 估算源字节)</c> 的带阶段回调。</para></param>
    /// <param name="token">Token checked by every counted write. <para>每次被计量的写入都会检查的令牌。</para></param>
    /// <param name="meter">Receives the counter so the caller can report its final sample; <see langword="null"/>
    /// when <paramref name="progress"/> is <see langword="null"/>. <para>接收计数器，便于调用方上报最后一个样本；
    /// <paramref name="progress"/> 为 <see langword="null"/> 时为 <see langword="null"/>。</para></param>
    /// <returns><paramref name="output"/> itself when no reporter is attached, otherwise a counting wrapper.
    /// <para>未附加回调时返回 <paramref name="output"/> 本身，否则返回计数包装流。</para></returns>
    private static Stream MeterWrites(string directory, Stream output,
        IProgress<(long Processed, long Total)>? progress, CancellationToken token,
        out ExtractionByteCounter? meter)
    {
        if (progress is null)
        {
            meter = null;
            return output;
        }

        meter = new ExtractionByteCounter(EstimateDirectoryBytes(directory), progress, token);
        return meter.Wrap(output);
    }

    /// <summary>
    /// Summed regular-file size of a directory tree, used as the denominator for builders whose own progress
    /// contract is <c>(bytes written, estimated source bytes)</c>. Returns one for an empty tree so the reported
    /// fraction stays finite.
    /// <para>目录树的常规文件大小之和，用作进度契约为 <c>(已写字节, 估算源字节)</c> 的构建器的分母。空树返回 1，
    /// 使上报比例保持有限。</para>
    /// </summary>
    /// <param name="directory">Directory to measure. <para>待度量的目录。</para></param>
    /// <returns>The summed byte count, or one when the tree holds no measurable file.
    /// <para>字节总数；目录树没有可度量文件时为 1。</para></returns>
    private static long EstimateDirectoryBytes(string directory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
        }

        return total == 0 ? 1 : total;
    }

    /// <summary>
    /// Records the feature bits the builder could not honor and the bits it had to add, as raw hex words because
    /// the preservation report carries no locale-dependent prose. A build that reconciled the declaration exactly
    /// adds no key, so consumers only see these keys when the published words differ from the declaration.
    /// <para>把写入方无法兑现的位与必须新增的位记为原始十六进制字，因为保留报告不携带任何依赖语言环境的文案。
    /// 与声明完全一致的构建不会添加任何键，因此消费方只在已发布特性字与声明不同时才会看到这些键。</para>
    /// </summary>
    /// <param name="report">Report dictionary receiving the keys. <para>接收这些键的报告字典。</para></param>
    /// <param name="options">Completed builder options. <para>已完成的构建选项。</para></param>
    private static void AddFeatureDeclarationReport(IDictionary<string, string> report, ErofsOptions options)
    {
        if (options.DroppedCompatBits != 0)
        {
            report["ErofsCompatBitsDropped"] = $"0x{options.DroppedCompatBits:X8}";
        }

        if (options.AddedIncompatBits != 0)
        {
            report["ErofsIncompatBitsAdded"] = $"0x{options.AddedIncompatBits:X8}";
        }
    }

    private static long? ParseTargetSize(IReadOnlyDictionary<string, string> options)
    {
        if (!string.Equals(options.GetValueOrDefault("CapacityPolicy"), "Explicit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = options.GetValueOrDefault("TargetSizeBytes");
        return string.IsNullOrEmpty(value)
            ? throw new FileSystemCorruptedException("EXT4 explicit capacity requires a target size in bytes.")
            : long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Ext4PreservedCapacityPolicy ParseExt4CapacityPolicy(IReadOnlyDictionary<string, string> options)
    {
        var value = options.GetValueOrDefault("CapacityPolicy", "Inherit");
        return Enum.TryParse<Ext4PreservedCapacityPolicy>(value, ignoreCase: true, out var policy)
            && Enum.IsDefined(policy)
                ? policy
                : throw new FileSystemUnsupportedFeatureException($"Unsupported EXT4 capacity policy '{value}'.");
    }

    private static Ext4BlockSharingMode ParseExt4BlockSharingMode(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("BlockSharingMode", out var value))
        {
            return Ext4BlockSharingMode.Independent;
        }

        return Enum.TryParse<Ext4BlockSharingMode>(value, ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
                ? mode
                : throw new FileSystemUnsupportedFeatureException($"Unsupported EXT4 block sharing mode '{value}'.");
    }

    private enum Ext4PreservedCapacityPolicy
    {
        Inherit,
        Explicit,
        Minimize,
    }

    private static CompressionAlgorithm ParseF2fsCompressionAlgorithm(
        IReadOnlyDictionary<string, string> options,
        RebuildParameters? rebuild)
    {
        var value = options.TryGetValue("CompressionAlgorithm", out var selected)
            ? selected
            : rebuild?.F2fsCompressionAlgorithm;
        if (value is null)
        {
            if (rebuild?.F2fsCompressionEnabled == true
                && FileSystemEditPlan.Current?.Entries.Values.Any(entry => entry.Metadata?.F2fsCompression is not null) != true)
            {
                throw new NotSupportedException(
                    "The source F2FS image uses multiple compression algorithms; edited rebuild requires an explicit algorithm override.");
            }

            return CompressionAlgorithm.Lz4;
        }

        return Enum.TryParse<CompressionAlgorithm>(value, ignoreCase: true, out var algorithm)
            && Enum.IsDefined(algorithm)
            ? algorithm
            : throw new FileSystemUnsupportedFeatureException($"Unsupported F2FS compression algorithm '{value}'.");
    }

    private static int IntegerOption(IReadOnlyDictionary<string, string> options, string key, int fallback)
        => options.TryGetValue(key, out var value)
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;

    private static bool IsTrue(string? value)
        => value is "1" || bool.TryParse(value, out var enabled) && enabled;
}
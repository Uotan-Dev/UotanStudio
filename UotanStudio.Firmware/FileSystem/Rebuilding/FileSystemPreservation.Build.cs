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
    /// Reports whether the caller overrode EROFS compression. The rebuild optimizer replays the source algorithm's
    /// recorded size estimates, so it can only be reused while the compression settings are unchanged.
    /// <para>判断调用方是否覆盖了 EROFS 压缩参数。重建优化器复用源算法的体积估算，因此只有压缩设置未变时才能继续使用。</para>
    /// </summary>
    private static bool HasCompressionOverride(IReadOnlyDictionary<string, string> options)
        => options.ContainsKey("ErofsAlgorithm") || options.ContainsKey("ErofsCompressionLevel");

    /// <summary>
    /// Builds one edited container and returns the EROFS rebuild search report when the bounded candidate
    /// search ran, or <see langword="null"/> for every single-build outcome, so the caller can merge the
    /// search keys into the equivalence report.
    /// <para>构建一个编辑后的容器，并在有界候选搜索执行时返回 EROFS 重建搜索报告；所有单次构建的结果均返回
    /// <see langword="null"/>，便于调用方把搜索键并入等价报告。</para>
    /// </summary>
    private static Dictionary<string, string>? BuildEdited(string inputDirectory, Stream output, string format,
        IReadOnlyDictionary<string, FsConfigEntry> fsConfig, IReadOnlyDictionary<string, string> fileContexts,
        RebuildParameters? rebuild, IReadOnlyDictionary<string, string> options, FileStream publicationOutput,
        string temporaryDirectory, CancellationToken token)
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
                publicationOutput, temporaryDirectory, token);
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
                publicationOutput, temporaryDirectory, token);
            raw.Flush(flushToDisk: true);
            raw.Position = 0;
            SparseContainerAccess.Resolve().ConvertRawToSparseValidated(raw, output, (uint)sparseBlock, raw.Length, token,
                publicationOutput);
            return innerReport;
        }
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
                    Ext4Builder.Build(inputDirectory, output, ext4Options);
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
                    var (algorithm, lz4HighCompression) = ErofsAlgorithmSelectors.Parse(
                        options.GetValueOrDefault("ErofsAlgorithm"));
                    erofsOptions.Algorithm = algorithm;
                    erofsOptions.Lz4HighCompression = lz4HighCompression;
                    erofsOptions.CompressionLevel = IntegerOption(options, "ErofsCompressionLevel", 9);
                    if (ErofsBuildDefaults.RebuildOptimization
                        && profile is not null
                        && profile.CompressionEstimates.Count > 0
                        && profile.SourceFilesystemBytes > 0
                        && !HasCompressionOverride(options))
                    {
                        return ErofsRebuildOptimizer.Build(inputDirectory, output, erofsOptions, profile, token);
                    }

                    new ErofsBuilder().Build(inputDirectory, output, erofsOptions);
                    break;
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
                    F2fsBuilder.Build(inputDirectory, output, f2fsOptions);
                    break;
                }
            default: throw new FileSystemUnsupportedFeatureException("Editing this filesystem container is not supported.");
        }

        return null;
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
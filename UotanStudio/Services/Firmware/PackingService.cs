using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// Packing service that builds an image from a source directory according to recorded directory metadata
/// or the given packing settings.
/// <para>打包服务：依据记录的目录元数据或给定的打包设置，把源目录构建为镜像。</para>
/// </summary>
internal static class PackingService
{
    /// <summary>
    /// Builds an image from directory metadata when present, otherwise from the given packing settings,
    /// keeping the metadata of a preserved rebuild in the result.
    /// <para>有目录元数据时按其构建镜像，否则使用给定的打包设置；结果中保留受保留重建的元数据。</para>
    /// </summary>
    /// <param name="sourceDirectory">Directory whose contents are packed. <para>待打包内容的源目录。</para></param>
    /// <param name="outputPath">Path of the image to write. <para>待写出镜像的路径。</para></param>
    /// <param name="settings">Packing settings, null to fall back to the shared instance. <para>打包设置，为 null 时回退到共享实例。</para></param>
    /// <param name="progress">Optional byte-progress reporter, null when progress is not tracked. <para>可选的字节进度回调，不跟踪进度时为 null。</para></param>
    /// <returns>The build result, carrying a failure summary when packing is refused. <para>构建结果，打包被拒绝时携带失败摘要。</para></returns>
    public static FirmwareOpResult BuildImage(
        string sourceDirectory,
        string outputPath,
        ProjectSettingViewModel? settings = null,
        IProgress<FileSystemByteProgress>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var hasExplicitSettings = settings is not null;
            using var metadataScope = RepackMetadata.UseContext(
                UotanStudio.Services.Projects.ProjectContextService.Instance.CaptureMetadataContext(sourceDirectory));
            var metadata = RepackMetadata.Read(sourceDirectory);
            var imageProgress = FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage);
            if (metadata is not null)
            {
                if (!metadata.CanPackDirectory) return FirmwareOpResult.Fail(metadata.PackingNotes);
                var estimated = EstimateSourceBytes(sourceDirectory);
                imageProgress.Report((0, estimated));
                var reportsOwnBytes = metadata.FormatName is "EXT4" or "EROFS" or "F2FS";
                var focused = hasExplicitSettings
                    && metadata.FormatName is "EXT4" or "EROFS" or "F2FS" or "Android Sparse Image (simg)";
                if (focused)
                {
                    var targetFormat = settings!.TypeEXT4 ? "EXT4" : settings.TypeF2FS ? "F2FS" : "EROFS";
                    if (targetFormat == metadata.FormatName
                        && !(targetFormat == "F2FS" && settings.HasF2fsCompressionOverride)
                        && !(targetFormat == "F2FS" && settings.HasF2fsAndroidProfileOverride)
                        && !(targetFormat == "EXT4" && settings.HasExt4PolicyOverride)
                        && !(targetFormat == "EROFS" && settings.HasErofsFeatureOverride)
                        && !settings.HasImageFormatOverride)
                    {
                        var preserved = RegisteredFirmwareFormats.PackDirectoryAsync(metadata.FormatName, sourceDirectory, outputPath, progress: progress)
                            .GetAwaiter().GetResult();
                        if (!reportsOwnBytes) imageProgress.Report((estimated, estimated));
                        return preserved;
                    }

                    using var overrides = RepackMetadata.UsePackingOverrides(BuildOverrideOptions(settings, targetFormat));
                    var overridden = RegisteredFirmwareFormats.PackDirectoryAsync(targetFormat, sourceDirectory, outputPath, progress: progress)
                        .GetAwaiter().GetResult();
                    return overridden;
                }

                var result = RegisteredFirmwareFormats.PackDirectoryAsync(metadata.FormatName, sourceDirectory, outputPath, progress: progress)
                    .GetAwaiter().GetResult();
                if (!reportsOwnBytes) imageProgress.Report((estimated, estimated));
                return result;
            }

            settings ??= ProjectSettingViewModel.Instance;
            if (settings is null)
            {
                return FirmwareOpResult.Fail("Packing settings not available  打包设置不可用");
            }

            if (settings.TypeEXT4)
            {
                return BuildExt4(sourceDirectory, outputPath, settings, sw, imageProgress);
            }

            if (settings.TypeF2FS)
            {
                return BuildF2fs(sourceDirectory, outputPath, settings, sw, imageProgress);
            }

            return BuildErofs(sourceDirectory, outputPath, settings, sw, imageProgress);
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail($"Build failed: {ex.Message}  构建失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Estimates the progress denominator from the summed byte size of the source directory files.
    /// <para>以源目录内各文件字节大小之和估算进度分母。</para>
    /// </summary>
    /// <param name="sourceDirectory">Directory whose files are summed. <para>求和大小的目录。</para></param>
    /// <returns>The summed byte size, at least one. <para>字节大小总和，至少为一。</para></returns>
    private static long EstimateSourceBytes(string sourceDirectory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
        }
        return total == 0 ? 1 : total;
    }

    /// <summary>
    /// Maps packing-setting values to the option overrides used for one filesystem image build.
    /// <para>把打包设置的取值映射为单次文件系统镜像构建所用的选项覆盖。</para>
    /// </summary>
    /// <param name="settings">The packing settings to read. <para>待读取的打包设置。</para></param>
    /// <param name="format">Target filesystem name: EXT4, F2FS or EROFS. <para>目标文件系统名：EXT4、F2FS 或 EROFS。</para></param>
    /// <returns>The override options keyed by option name. <para>按选项名索引的覆盖值。</para></returns>
    internal static IReadOnlyDictionary<string, string> BuildOverrideOptions(ProjectSettingViewModel settings, string format)
    {
        var options = format switch
        {
            "EXT4" => new Dictionary<string, string>
            {
                ["EnableSparse"] = settings.FormatSPARSE ? "1" : "0",
                ["CapacityPolicy"] = settings.Ext4CapacityPolicy.ToString(),
                ["TargetSizeBytes"] = settings.Ext4TargetSizeBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["ReadOnly"] = settings.EXT4RO ? "1" : "0",
                ["BlockSharingMode"] = settings.Ext4BlockSharingMode.ToString(),
            },
            "F2FS" => new Dictionary<string, string>
            {
                ["EnableSparse"] = "1",
                ["OutputSparseContainer"] = settings.FormatSPARSE ? "1" : "0",
                ["EnableCompression"] = settings.F2fsCompressionEnabled ? "1" : "0",
                ["CompressionAlgorithm"] = settings.SelectedF2fsCompressionAlgorithm,
                ["CompressionLevel"] = settings.F2fsCompressionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["AndroidNativeProfile"] = settings.F2fsAndroidNativeProfile ? "1" : "0",
                ["AndroidImageSizeBytes"] = settings.F2fsAndroidImageSizeBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["NativeWslDistribution"] = settings.F2fsNativeWslDistribution,
            },
            _ => new Dictionary<string, string>
            {
                ["ErofsAlgorithm"] = settings.SelectedErofsAlgorithm,
                ["ErofsCompressionLevel"] = settings.EROFSComGrade.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };

        if (format == "EROFS"
            && settings.HasErofsFeatureOverride
            && !string.IsNullOrWhiteSpace(settings.ErofsFeatureOverrides))
        {
            options["ErofsFeatures"] = settings.ErofsFeatureOverrides.Trim();
        }

        return options;
    }

    private static FirmwareOpResult BuildExt4(
        string sourceDirectory,
        string outputPath,
        ProjectSettingViewModel settings,
        System.Diagnostics.Stopwatch sw,
        IProgress<(long Processed, long Total)>? progress)
    {
        if (settings.Ext4CapacityPolicy == Ext4CapacityPolicy.Explicit && settings.Ext4TargetSizeBytes is null)
        {
            throw new InvalidDataException("EXT4 explicit capacity requires a target size in bytes.");
        }

        var options = new Ext4Options
        {
            EnableSparse = settings.FormatSPARSE,
            TargetSizeBytes = settings.Ext4CapacityPolicy == Ext4CapacityPolicy.Explicit
                ? settings.Ext4TargetSizeBytes
                : null,
            CapacityMode = settings.Ext4CapacityPolicy == Ext4CapacityPolicy.Minimize
                ? Ext4CapacityMode.Minimize
                : Ext4CapacityMode.Default,
            ReadOnly = settings.EXT4RO,
            BlockSharingMode = settings.Ext4BlockSharingMode,
        };

        LogService.Instance.Write(LogLevel.Info, "Packing",
            $"Building EXT4 image ({(settings.EXT4Compress ? "compressed" : "uncompressed")}, " +
            $"{(settings.FormatSPARSE ? "sparse" : "raw")})  " +
            $"正在构建 EXT4 镜像（{(settings.EXT4Compress ? "压缩" : "未压缩")}，{(settings.FormatSPARSE ? "稀疏" : "原始")}）");

        Ext4Builder.Build(sourceDirectory, outputPath, options, progress);
        sw.Stop();

        var size = new FileInfo(outputPath).Length;
        var summary = $"EXT4 image built: {outputPath} ({size:N0} bytes, {sw.Elapsed.TotalSeconds:F1}s)  " +
                      $"EXT4 镜像已构建：{outputPath}（{size:N0} 字节，{sw.Elapsed.TotalSeconds:F1} 秒）";
        LogService.Instance.Write(LogLevel.Info, "Packing", summary);
        return new FirmwareOpResult { Success = true, Summary = summary };
    }

    private static FirmwareOpResult BuildErofs(
        string sourceDirectory,
        string outputPath,
        ProjectSettingViewModel settings,
        System.Diagnostics.Stopwatch sw,
        IProgress<(long Processed, long Total)>? progress)
    {
        var options = new ErofsOptions
        {
            LegacyCompatibility = UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuildDefaults.LegacyCompatibility,
        };
        (options.Algorithm, options.Lz4HighCompression) = ErofsAlgorithmSelectors.Parse(settings.SelectedErofsAlgorithm);
        if (options.Algorithm != ErofsCompressionAlgorithm.Lz4 || options.Lz4HighCompression)
        {
            options.LegacyCompatibility = false;
            options.MaxPclusterSize = ErofsOptions.MaximumPclusterSize;
        }

        options.CompressionLevel = settings.EROFSComGrade;

        if (settings.HasErofsFeatureOverride && !string.IsNullOrWhiteSpace(settings.ErofsFeatureOverrides))
        {
            LogService.Instance.Write(LogLevel.Warn, "Packing",
                $"EROFS feature declaration '{settings.ErofsFeatureOverrides.Trim()}' is ignored for a directory build; " +
                "it edits the declaration recorded from a preserved source image.  " +
                $"EROFS 特性声明 '{settings.ErofsFeatureOverrides.Trim()}' 在目录构建中会被忽略；它编辑的是自保留源镜像" +
                "记录下来的声明。");
        }

        LogService.Instance.Write(LogLevel.Info, "Packing",
            $"Building EROFS image (algo={settings.SelectedErofsAlgorithm}, level={options.CompressionLevel})  " +
            $"正在构建 EROFS 镜像（算法={settings.SelectedErofsAlgorithm}，级别={options.CompressionLevel}）");

        var builder = new ErofsBuilder();
        builder.Build(sourceDirectory, outputPath, options, progress);
        sw.Stop();

        var size = new FileInfo(outputPath).Length;
        var summary = $"EROFS image built: {outputPath} ({size:N0} bytes, {sw.Elapsed.TotalSeconds:F1}s)  " +
                      $"EROFS 镜像已构建：{outputPath}（{size:N0} 字节，{sw.Elapsed.TotalSeconds:F1} 秒）";
        LogService.Instance.Write(LogLevel.Info, "Packing", summary);
        return new FirmwareOpResult { Success = true, Summary = summary };
    }

    private static FirmwareOpResult BuildF2fs(
        string sourceDirectory,
        string outputPath,
        ProjectSettingViewModel settings,
        System.Diagnostics.Stopwatch sw,
        IProgress<(long Processed, long Total)>? progress)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        var rawOutput = settings.FormatSPARSE
            ? Path.Combine(Path.GetDirectoryName(fullOutputPath)!, "." + Path.GetFileName(fullOutputPath) + "." + Guid.NewGuid().ToString("N") + ".raw.tmp")
            : fullOutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        try
        {
            F2fsBuilder.Build(sourceDirectory, rawOutput, new F2fsOptions
            {
                EnableSparse = true,
                EnableCompression = settings.F2fsCompressionEnabled,
                CompressionAlgorithm = ParseF2fsCompressionAlgorithm(settings.SelectedF2fsCompressionAlgorithm),
                CompressionLevel = settings.F2fsCompressionLevel,
                AndroidNativeProfile = settings.F2fsAndroidNativeProfile,
                AndroidImageSizeBytes = settings.F2fsAndroidImageSizeBytes,
                NativeWslDistribution = settings.F2fsNativeWslDistribution,
            });
            if (settings.FormatSPARSE)
            {
                SparseImageConverter.ConvertRawToSparseAtomic(rawOutput, fullOutputPath);
            }
        }
        finally
        {
            if (settings.FormatSPARSE)
            {
                File.Delete(rawOutput);
            }
        }
        var size = new FileInfo(outputPath).Length;
        progress?.Report((size, size));
        sw.Stop();

        var summary = $"F2FS image built: {outputPath} ({size:N0} bytes, {sw.Elapsed.TotalSeconds:F1}s)";
        LogService.Instance.Write(LogLevel.Info, "Packing", summary);
        return new FirmwareOpResult { Success = true, Summary = summary };
    }

    private static CompressionAlgorithm ParseF2fsCompressionAlgorithm(string value)
        => Enum.TryParse<CompressionAlgorithm>(value, ignoreCase: true, out var algorithm)
            && Enum.IsDefined(algorithm)
            ? algorithm
            : throw new InvalidDataException($"Unsupported F2FS compression algorithm '{value}'.");
}
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// <para>打包服务，桥接 <see cref="ProjectSettingViewModel"/> 的面板设置到
/// EXT4/EROFS/boot 镜像的构建调用。</para>
/// Packing service, bridging <see cref="ProjectSettingViewModel"/> panel settings to
/// EXT4/EROFS/boot image build calls.
/// </summary>
internal static class PackingService
{
    /// <summary>
    /// Builds an image using directory metadata, or project settings when metadata is absent.
    /// <para>按目录元数据构建镜像；无元数据时使用项目打包设置。</para>
    /// </summary>
    public static (bool Success, string Summary) BuildImage(
        string sourceDirectory,
        string outputPath,
        ProjectSettingViewModel? settings = null,
        IProgress<FileSystemByteProgress>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var metadataScope = RepackMetadata.UseContext(
                UotanStudio.Services.Projects.ProjectContextService.Instance.CaptureMetadataContext(sourceDirectory));
            var metadata = RepackMetadata.Read(sourceDirectory);
            // 文件系统镜像重建上报真实字节进度；其他元数据分发分支按源目录估算做阶段式上报。
            // Filesystem image rebuilds report real byte progress; other metadata-dispatch branches
            // fall back to a stage-based estimate over the source directory size.
            var imageProgress = FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage);
            settings ??= ProjectSettingViewModel.Instance;
            if (metadata is not null)
            {
                if (!metadata.CanPackDirectory) return (false, metadata.PackingNotes);
                // 打包设置面板的稀疏/压缩等参数覆盖解包产出的元数据（仅文件系统镜像）。
                // Let the packing-settings panel override the unpacked metadata for filesystem images.
                var estimated = EstimateSourceBytes(sourceDirectory);
                imageProgress.Report((0, estimated));
                var focused = settings is not null && metadata.FormatName is "EXT4" or "EROFS";
                if (focused)
                {
                    // 仅当用户真正切换了文件系统格式（EROFS ↔ EXT4）时申请打包覆盖走全新目录打包；
                    // 目标格式与原格式相同时直接走保留元数据重建路径，精确还原
                    // SELinux/capability/hardlink/symlink，避免覆盖导致降级后丢失安全上下文。
                    // The target format chose in the panel; only a real format switch (EROFS ↔ EXT4)
                    // requests packing overrides (and a fresh build). When the target equals the source
                    // format we reuse the preserved-metadata rebuild to restore
                    // SELinux/capability/hardlink/symlink exactly instead of downgrading the output.
                    var targetFormat = settings!.TypeEXT4 ? "EXT4" : "EROFS";
                    if (targetFormat == metadata.FormatName)
                    {
                        var preserved = RegisteredFirmwareFormats.PackDirectoryAsync(metadata.FormatName, sourceDirectory, outputPath, progress: progress)
                            .GetAwaiter().GetResult();
                        imageProgress.Report((estimated, estimated));
                        return (preserved.Success, preserved.Summary);
                    }

                    using var overrides = RepackMetadata.UsePackingOverrides(BuildOverrideOptions(settings, targetFormat));
                    var overridden = RegisteredFirmwareFormats.PackDirectoryAsync(targetFormat, sourceDirectory, outputPath, progress: progress)
                        .GetAwaiter().GetResult();
                    imageProgress.Report((estimated, estimated));
                    return (overridden.Success, overridden.Summary);
                }

                var result = RegisteredFirmwareFormats.PackDirectoryAsync(metadata.FormatName, sourceDirectory, outputPath, progress: progress)
                    .GetAwaiter().GetResult();
                imageProgress.Report((estimated, estimated));
                return (result.Success, result.Summary);
            }

            if (settings is null)
            {
                return (false, "Packing settings not available  打包设置不可用");
            }

            if (settings.TypeEXT4)
            {
                return BuildExt4(sourceDirectory, outputPath, settings, sw, imageProgress);
            }

            return BuildErofs(sourceDirectory, outputPath, settings, sw, imageProgress);
        }
        catch (Exception ex)
        {
            return (false, $"Build failed: {ex.Message}  构建失败：{ex.Message}");
        }
    }

    /// <summary>Estimates the source directory byte total as the progress denominator.</summary>
    /// <summary>以源目录文件大小之和作为进度分母的估算值。</summary>
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
    /// Maps packing-settings panel values to option overrides for a filesystem image.
    /// <para>将打包设置面板的值映射为文件系统镜像的选项覆盖。</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildOverrideOptions(ProjectSettingViewModel settings, string format)
    {
        return format == "EXT4" ? new Dictionary<string, string>
        {
            ["EnableSparse"] = settings.FormatSPARSE ? "1" : "0",
            ["Ext4Compress"] = settings.EXT4Compress ? "1" : "0",
        } : new Dictionary<string, string>
        {
            ["ErofsAlgorithm"] = settings.SelectedErofsAlgorithm,
            ["ErofsCompressionLevel"] = settings.EROFSComGrade.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static (bool Success, string Summary) BuildExt4(
        string sourceDirectory,
        string outputPath,
        ProjectSettingViewModel settings,
        System.Diagnostics.Stopwatch sw,
        IProgress<(long Processed, long Total)>? progress)
    {
        var options = new Ext4Options
        {
            Ext4Compress = settings.EXT4Compress,
            EnableSparse = settings.FormatSPARSE,
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
        return (true, summary);
    }

    private static (bool Success, string Summary) BuildErofs(
        string sourceDirectory,
        string outputPath,
        ProjectSettingViewModel settings,
        System.Diagnostics.Stopwatch sw,
        IProgress<(long Processed, long Total)>? progress)
    {
        var options = new ErofsOptions
        {
            // Legacy layout follows the app-level setting (synced to the shared default at startup
            // and on change) so both the packing panel and repack honor the same user choice.
            // legacy 布局跟随应用层设置（启动与变更时同步到共享默认值），使打包面板与重打包
            // 遵循同一用户选择。
            LegacyCompatibility = UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder.ErofsBuildDefaults.LegacyCompatibility,
            // The panel selects the codec and, for LZ4, the block encoder through the shared selector
            // vocabulary so the picker drives the builder directly.
            // 面板通过共享的选择器词表选择编解码器，并在 LZ4 下选择块编码器，使下拉框直接驱动构建器。
        };
        (options.Algorithm, options.Lz4HighCompression) = ErofsAlgorithmSelectors.Parse(settings.SelectedErofsAlgorithm);
        // A modern codec implies the modern (5.13+) layout: legacy EROFS only encodes LZ4, so the
        // panel's legacy toggle is dropped for LZMA/DEFLATE/ZSTD selections rather than silently
        // rebuilding as LZ4. HC carries the same implication from a density angle — legacy caps
        // every pcluster at one physical block, so HC saves only bytes that always round up to
        // the same block count, and forcing modern is what makes HC actually shrink the image.
        // 选择现代编解码器即隐含现代（5.13+）布局：legacy erofs 仅编码 LZ4，因此对
        // LZMA/DEFLATE/ZSTD 面板的 legacy 开关会被放弃，而不是静默改回 LZ4 重建。HC 从密度角度
        // 也有同样的隐含要求：legacy 把每个 pcluster 限死在一个物理块，HC 省下的字节一定会被舍入
        // 到同样的块数，强制现代布局才能让 HC 真正缩小镜像。
        if (options.Algorithm != ErofsCompressionAlgorithm.Lz4 || options.Lz4HighCompression) options.LegacyCompatibility = false;
        options.CompressionLevel = settings.EROFSComGrade;

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
        return (true, summary);
    }
}
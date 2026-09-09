using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.SharedKernel;
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
        ProjectSettingViewModel? settings = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var metadata = RepackMetadata.Read(sourceDirectory);
            if (metadata is not null)
            {
                if (!metadata.CanPackDirectory) return (false, metadata.PackingNotes);
                var result = RegisteredFirmwareFormats.PackDirectoryAsync(metadata.FormatName, sourceDirectory, outputPath)
                    .GetAwaiter().GetResult();
                return (result.Success, result.Summary);
            }

            settings ??= ProjectSettingViewModel.Instance;
            if (settings is null)
            {
                return (false, "Packing settings not available  打包设置不可用");
            }

            if (settings.TypeEXT4)
            {
                return BuildExt4(sourceDirectory, outputPath, settings, sw);
            }

            return BuildErofs(sourceDirectory, outputPath, settings, sw);
        }
        catch (Exception ex)
        {
            return (false, $"Build failed: {ex.Message}  构建失败：{ex.Message}");
        }
    }

    private static (bool Success, string Summary) BuildExt4(
        string sourceDirectory,
        string outputPath,
        ProjectSettingViewModel settings,
        System.Diagnostics.Stopwatch sw)
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

        Ext4Builder.Build(sourceDirectory, outputPath, options);
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
        System.Diagnostics.Stopwatch sw)
    {
        var options = new ErofsOptions
        {
            Algorithm = ErofsCompressionAlgorithm.Lz4,
            CompressionLevel = settings.EROFSLZ4HC
                ? Math.Max(settings.EROFSComGrade, 4)
                : settings.EROFSComGrade,
        };

        LogService.Instance.Write(LogLevel.Info, "Packing",
            $"Building EROFS image (algo={options.Algorithm}, level={options.CompressionLevel})  " +
            $"正在构建 EROFS 镜像（算法={options.Algorithm}，级别={options.CompressionLevel}）");

        var builder = new ErofsBuilder();
        builder.Build(sourceDirectory, outputPath, options);
        sw.Stop();

        var size = new FileInfo(outputPath).Length;
        var summary = $"EROFS image built: {outputPath} ({size:N0} bytes, {sw.Elapsed.TotalSeconds:F1}s)  " +
                      $"EROFS 镜像已构建：{outputPath}（{size:N0} 字节，{sw.Elapsed.TotalSeconds:F1} 秒）";
        LogService.Instance.Write(LogLevel.Info, "Packing", summary);
        return (true, summary);
    }
}

using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
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
    /// <para>按当前打包设置将目录构建为镜像文件。
    /// 当源目录包含 header.json（boot 解包产物）时自动走 boot 重打包路径。</para>
    /// Builds a directory into an image file according to the current packing settings.
    /// Automatically uses the boot repack path when the directory contains header.json
    /// (boot unpack artifacts).
    /// </summary>
    public static (bool Success, string Summary) BuildImage(
        string sourceDirectory,
        string outputPath,
        ProjectSettingViewModel? settings = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // boot.img 原组件打包：检测 header.json 存在时走 BootImagePacker 路径。
            // Boot image repack: when header.json exists, use BootImagePacker path.
            if (File.Exists(Path.Combine(sourceDirectory, "header.json")))
            {
                return PackBootImage(sourceDirectory, outputPath, sw);
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

    /// <summary>
    /// <para>检测指定目录是否为 boot 解包产物（含 header.json）。</para>
    /// Detects whether the given directory is a boot unpack artifact (contains header.json).
    /// </summary>
    public static bool IsBootUnpackDirectory(string directory)
    {
        return File.Exists(Path.Combine(directory, "header.json"));
    }

    private static (bool Success, string Summary) PackBootImage(
        string sourceDirectory,
        string outputPath,
        System.Diagnostics.Stopwatch sw)
    {
        LogService.Instance.Write(LogLevel.Info, "Packing",
            $"Packing boot image from {sourceDirectory}  正在从组件目录打包 boot 镜像");

        var firmware = FirmwareService.Instance;
        var result = firmware.PackBootAsync(sourceDirectory, outputPath).GetAwaiter().GetResult();
        sw.Stop();

        if (result.Success)
        {
            var size = new FileInfo(outputPath).Length;
            var summary = $"{result.Summary} ({size:N0} bytes, {sw.Elapsed.TotalSeconds:F1}s)";
            LogService.Instance.Write(LogLevel.Info, "Packing", summary);
            return (true, summary);
        }

        LogService.Instance.Write(LogLevel.Error, "Packing", result.Summary);
        return (false, result.Summary);
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

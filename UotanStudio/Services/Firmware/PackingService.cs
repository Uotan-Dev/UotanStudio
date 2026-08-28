using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// <para>打包设置面板的后端执行服务：按面板设置（镜像类型 EXT4/EROFS、格式 RAW/SPARSE、
/// EXT4 压缩开关、EROFS lz4/lz4hc 与压缩等级）把解包目录打包为系统分区镜像。</para>
/// Back-end execution service for the packing settings panel: packs an unpack directory into a
/// system-partition image according to the panel settings (image type EXT4/EROFS, format RAW/SPARSE,
/// EXT4 compression switch, EROFS lz4/lz4hc and compression level).
/// </summary>
internal static class PackingService
{
    /// <summary>
    /// <para>按打包设置面板的当前设置，将源目录打包为 ext4 或 erofs 镜像（可选转为 sparse）。</para>
    /// Packs the source directory into an ext4 or erofs image (optionally converted to sparse)
    /// according to the current packing settings panel state.
    /// </summary>
    /// <param name="sourceDirectory">
    /// <para>要打包的源目录（通常是解包产物目录）。</para>
    /// The source directory to pack (usually an unpack output directory).
    /// </param>
    /// <param name="outputPath">
    /// <para>输出镜像路径。</para>
    /// The output image path.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// <para>打包结果。</para>
    /// The packing result.
    /// </returns>
    public static async Task<FirmwareOpResult> PackImageAsync(string sourceDirectory, string outputPath, CancellationToken cancellationToken = default)
    {
        var settings = ProjectSettingViewModel.Instance;
        if (settings is null)
        {
            return FirmwareOpResult.Fail("Packing settings panel is not available  打包设置面板不可用");
        }

        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return FirmwareOpResult.Fail($"Source directory not found: {sourceDirectory}  源目录不存在：{sourceDirectory}");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return FirmwareOpResult.Fail("Output path is empty  输出路径为空");
        }

        var formatName = settings.TypeEXT4 ? "EXT4" : "EROFS";
        try
        {
            // 镜像构建本身是 CPU/IO 密集操作，放后台线程避免阻塞 UI。
            // Image building is CPU/IO bound; run it on a background thread so the UI stays responsive.
            if (settings.TypeEXT4)
            {
                var options = new Ext4Options
                {
                    Ext4Compress = settings.EXT4Compress,
                };
                await Task.Run(() => Ext4Builder.Build(sourceDirectory, outputPath, options), cancellationToken);
            }
            else
            {
                var options = new ErofsOptions
                {
                    // lz4hc 映射到高压缩区间（5-9），普通 lz4 映射到快速区间（1-4）。
                    // lz4hc maps to the high-compression band (5-9), plain lz4 to the fast band (1-4).
                    Compress = settings.EROFSLZ4 || settings.EROFSLZ4HC,
                    CompressionLevel = settings.EROFSLZ4HC
                        ? Math.Max(5, settings.EROFSComGrade)
                        : Math.Clamp(settings.EROFSComGrade, 1, 4),
                };
                await Task.Run(() =>
                {
                    using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    new ErofsBuilder().Build(sourceDirectory, fs, options);
                }, cancellationToken);
            }

            // 面板选择 SPARSE 时把 raw 镜像转为 Android sparse 格式。
            // When the panel selects SPARSE, convert the raw image to the Android sparse format.
            if (settings.FormatSPARSE)
            {
                var sparsePath = outputPath + ".sparse";
                var converted = await FirmwareService.Instance.ConvertSparseAsync(outputPath, sparsePath, toSparse: true, cancellationToken);
                if (!converted.Success)
                {
                    return converted;
                }

                try { File.Delete(outputPath); } catch { /* 保留 raw 副本不影响结果 */ }
                return new FirmwareOpResult
                {
                    Success = true,
                    Summary = $"Packed '{sourceDirectory}' as {formatName} sparse image to '{sparsePath}'  已将 '{sourceDirectory}' 打包为 {formatName} sparse 镜像：{sparsePath}",
                };
            }

            return new FirmwareOpResult
            {
                Success = true,
                Summary = $"Packed '{sourceDirectory}' as {formatName} raw image to '{outputPath}'  已将 '{sourceDirectory}' 打包为 {formatName} raw 镜像：{outputPath}",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail($"Failed to pack {formatName} image: {ex.Message}  打包 {formatName} 镜像失败：{ex.Message}");
        }
    }
}
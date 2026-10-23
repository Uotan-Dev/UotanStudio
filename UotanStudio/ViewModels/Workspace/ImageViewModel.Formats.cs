using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Projects;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    private readonly ImageInputService _imageInput = new(
        ProjectContextService.Instance.ResolvePath,
        (category, message) => LogService.Instance.Write(LogLevel.Warn, category, message));

    internal static string GetEntryBaseName(ImageEntryViewModel image)
    {
        var name = Path.GetFileNameWithoutExtension(image.RelativePath);
        return string.IsNullOrWhiteSpace(name) ? image.DisplayName : name;
    }

    private IReadOnlyList<ImageEntryViewModel> GetSelectedImages()
    {
        return [.. Images.Where(image => image.IsSelected)];
    }

    private string? GetSelectedSourcePath(ImageEntryViewModel image, string operation)
        => _imageInput.ResolveSourcePath(image.RelativePath, image.IsDirectory, operation);

    private static async Task<FirmwareOperationResult> ConvertToRawAsync(string sourcePath, string outputDir, string name)
    {
        return await Task.Run(() =>
        {
            var outputPath = Path.Combine(outputDir, name + ".raw");
            using var sparseFile = SparseReader.ImportAuto(sourcePath);
            using var output = File.Create(outputPath);
            sparseFile.WriteRawToStream(output);
            return new FirmwareOperationResult { Success = true, Summary = $"Converted {sourcePath} to raw at {outputPath}  已转为 raw：{outputPath}" };
        });
    }

    /// <summary>
    /// Extracts a raw physical image: probes the magic for an ext4/erofs/f2fs filesystem image and
    /// cascades into its contents with the matching parser when matched; otherwise falls back to
    /// copying the raw byte stream. Header probing and unpack-directory semantics
    /// (<see cref="ImageOperationPaths"/>) are unchanged.
    /// <para>分解 raw 物理镜像：先按魔数探测是否为 ext4/erofs/f2fs 文件系统镜像，命中则用对应解析器
    /// 级联解出内部文件系统内容；未命中则回退为原 raw 字节流复制。头部探测与解包目录规则
    /// （<see cref="ImageOperationPaths"/>）语义不变。</para>
    /// </summary>
    private static async Task<FirmwareOperationResult> ExtractRawAsync(string sourcePath, string outputDir, string name, IProgress<(int processed, int total)>? progress = null, IProgress<Firmware.Core.FileSystemByteProgress>? byteProgress = null, IReadOnlyList<string>? erofsExtraDevicePaths = null, CancellationToken cancellationToken = default)
    {
        var layout = ImageInputService.ProbeFilesystemHeader(sourcePath);
        if (layout is RawImageLayout.Ext4 or RawImageLayout.Erofs or RawImageLayout.F2fs)
            return await FirmwareService.Instance.UnpackFileSystemImageAsync(sourcePath, outputDir, progress,
                cancellationToken, byteProgress, erofsExtraDevicePaths);
        return await ConvertToRawAsync(sourcePath, outputDir, name);
    }

    private static string GetUniqueFileName(string directory, string fileName)
        => ProjectFiles.GetUniqueFileName(directory, fileName);

    private static void DeleteDirectorySafe(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", $"Undo cleanup failed for {path}: {ex.Message}  撤销清理失败：{ex.Message}");
        }
    }

    private static void DeleteFileSafe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", $"Undo cleanup failed for {path}: {ex.Message}  撤销清理失败：{ex.Message}");
        }
    }
}
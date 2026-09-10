using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using FormatDetector = UotanStudio.Firmware.SharedKernel.FormatDetector;

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
        return Images.Where(image => image.IsSelected).ToArray();
    }

    private string? GetSelectedSourcePath(ImageEntryViewModel image, string operation)
        => _imageInput.ResolveSourcePath(image.RelativePath, image.IsDirectory, operation);

    private static async Task<FirmwareOpResult> ConvertToRawAsync(string sourcePath, string outputDir, string name)
    {
        return await Task.Run<FirmwareOpResult>(() =>
        {
            var outputPath = Path.Combine(outputDir, name + ".raw");
            using var sparseFile = SparseReader.ImportAuto(sourcePath);
            using var output = File.Create(outputPath);
            sparseFile.WriteRawToStream(output);
            return new FirmwareOpResult { Success = true, Summary = $"Converted {sourcePath} to raw at {outputPath}  已转为 raw：{outputPath}" };
        });
    }

    /// <summary>
    /// <para>分解 raw 物理镜像：先按魔数探测是否为 ext4/erofs/f2fs 文件系统镜像，命中则用对应解析器
    /// 级联解出内部文件系统内容；未命中则回退为原 raw 字节流复制。头部探测与解包目录规则
    /// （<see cref="ImageOperationPaths"/>）语义不变。</para>
    /// Extracts a raw physical image: probes the magic for an ext4/erofs/f2fs filesystem image and
    /// cascades into its contents with the matching parser when matched; otherwise falls back to
    /// copying the raw byte stream. Header probing and unpack-directory semantics
    /// (<see cref="ImageOperationPaths"/>) are unchanged.
    /// </summary>
    private static async Task<FirmwareOpResult> ExtractRawAsync(string sourcePath, string outputDir, string name, IProgress<(int processed, int total)>? progress = null)
    {
        // 探测 raw 镜像内文件系统布局：ext4 魔数 0xEF53 位于 1080、erofs/f2fs 魔数位于 1024。
        // Probe the filesystem layout inside the raw image: ext4 magic 0xEF53 at 1080, erofs/f2fs at 1024.
        var layout = ImageInputService.ProbeFilesystemHeader(sourcePath);
        switch (layout)
        {
            case RawImageLayout.Ext4:
            {
                var result = await Task.Run(() =>
                {
                    using var parser = new Ext4Parser(sourcePath);
                    parser.ExtractToDirectory(outputDir, progress: progress);
                    var entries = parser.ListFiles();
                    return new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Extracted {entries.Count} entries from '{sourcePath}' to '{outputDir}'  已从 '{sourcePath}' 解出 {entries.Count} 个条目到 '{outputDir}'"
                    };
                });
                if (result.Success) await WriteRepackMetadataSafeAsync(sourcePath, outputDir);
                return result;
            }
            case RawImageLayout.Erofs:
            {
                var result = await Task.Run(() =>
                {
                    using var parser = new ErofsParser(sourcePath);
                    parser.ExtractToDirectory(outputDir, progress: progress);
                    var files = parser.EnumerateFiles();
                    return new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Extracted {files.Count} files from '{sourcePath}' to '{outputDir}'  已从 '{sourcePath}' 解出 {files.Count} 个文件到 '{outputDir}'"
                    };
                });
                if (result.Success) await WriteRepackMetadataSafeAsync(sourcePath, outputDir);
                return result;
            }
            case RawImageLayout.F2fs:
            {
                var result = await Task.Run(() =>
                {
                    using var parser = new F2fsParser(sourcePath);
                    parser.ExtractToDirectory(outputDir, progress: progress);
                    var entries = parser.ListFiles();
                    return new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Extracted {entries.Count} entries from '{sourcePath}' to '{outputDir}'  已从 '{sourcePath}' 解出 {entries.Count} 个条目到 '{outputDir}'"
                    };
                });
                if (result.Success) await WriteRepackMetadataSafeAsync(sourcePath, outputDir);
                return result;
            }
        }

        // 未命中文件系统魔数 → 保留原 raw 复制行为（按字节流落盘 .raw）。
        // No filesystem magic matched → keep the original raw-copy behavior (write the byte stream as .raw).
        return await ConvertToRawAsync(sourcePath, outputDir, name);
    }

    /// <summary>
    /// 通过格式检测器获取处理器并写入 <c>.uotanstudio-repack.json</c> 元数据；失败仅记调试日志，不阻塞提取。
    /// Resolves the format handler via <see cref="FormatDetector"/> and writes
    /// <c>.uotanstudio-repack.json</c> metadata; failures are swallowed to avoid blocking extraction.
    /// </summary>
    private static async Task WriteRepackMetadataSafeAsync(string sourcePath, string outputDir)
    {
        try
        {
            UotanStudio.Firmware.SharedKernel.FirmwareFormatRegistry.RegisterAll();
            var handler = FormatDetector.Detect(sourcePath);
            if (handler is not null)
            {
                var metadataRoot = ProjectContextService.Instance.GetMetadataRoot();
                // Metadata parsing and inode enumeration perform synchronous disk I/O before yielding.
                // 元数据解析及 inode 遍历包含同步磁盘 I/O，整个写入过程都必须离开 UI 线程。
                await Task.Run(() => UotanStudio.Firmware.SharedKernel.RepackMetadata.WriteAsync(
                    handler, sourcePath, outputDir, metadataRoot: metadataRoot));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Repack metadata write failed: {ex.Message}");
        }
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

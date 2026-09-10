using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    /// <summary>
    /// <para>将若干外部文件复制进项目 <c>images/</c> 目录（重名自动加后缀），建立镜像条目并登记历史。</para>
    /// Copies the given external files into the project <c>images/</c> directory (auto-suffixing on name
    /// collision), creating image entries and recording history.
    /// </summary>
    /// <param name="paths">
    /// <para>要导入的源文件路径列表。</para>
    /// Source file paths to import.
    /// </param>
    public async Task ImportAsync(string[] paths)
    {
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || context.ActiveSubProject is null)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Import aborted: no project is open  导入中止：尚未打开工程");
            return;
        }

        try
        {
            var imagesDir = context.EnsureImagesDirectory();
            var imported = new List<ImageEntryViewModel>();
            foreach (var path in paths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var destination = Path.Combine(imagesDir, GetUniqueFileName(imagesDir, Path.GetFileName(path)));
                await ProjectFiles.CopyAsync(path, destination);
                var relative = context.RelativePath(destination);
                if (relative is null)
                {
                    File.Delete(destination);
                    continue;
                }

                var format = ImageInputService.DetectFormat(destination);
                if (format is "raw")
                {
                    File.Delete(destination);
                    continue;
                }

                imported.Add(new ImageEntryViewModel
                {
                    DisplayName = Path.GetFileName(destination),
                    RelativePath = relative,
                    SizeBytes = new FileInfo(destination).Length,
                    Format = format
                });
            }

            if (imported.Count == 0)
            {
                return;
            }

            foreach (var entry in imported)
            {
                Images.Add(entry);
            }

            // 自动推断打包类型：首个 ext4/erofs 镜像的格式继承到打包设置面板。
            // Auto-infer packing type: inherit the format of the first ext4/erofs image.
            var firstFs = imported.FirstOrDefault(e => e.Format is "ext4" or "erofs");
            if (firstFs is not null)
            {
                ProjectSettingViewModel.Instance?.ApplySourceFormat(firstFs.Format);
            }

            LogService.Instance.Write(LogLevel.Info, "Images", $"Imported {imported.Count} image(s)  已导入 {imported.Count} 个镜像");
            OperationHistoryService.Instance.Record(
                $"Import {imported.Count} image(s)",
                new HistoryOperationDto
                {
                    Kind = HistoryOperationKind.ImportImages,
                    Description = $"Import {imported.Count} image(s)",
                    Count = imported.Count,
                    RelativePaths = imported.Select(e => e.RelativePath).ToList()
                },
                () =>
                {
                    foreach (var entry in imported)
                    {
                        Images.Remove(entry);
                    }
                },
                () =>
                {
                    foreach (var entry in imported)
                    {
                        Images.Add(entry);
                    }
                });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Import failed: {ex.Message}  导入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// <para>把单个容器文件（super / payload.bin / zip）复制进项目 <c>images/</c> 目录并登记条目；
    /// 格式列使用调用方给定的标签（普通导入的格式判定不识别容器类型）。</para>
    /// Copies a single container file (super / payload.bin / zip) into the project <c>images/</c>
    /// directory and registers its entry; the format column uses the caller-supplied label since the
    /// plain import format detection does not recognize container types.
    /// </summary>
    /// <param name="sourcePath"><para>容器源文件路径。</para>Source container path.</param>
    /// <param name="formatLabel"><para>格式列显示文本（super / payload / zip）。</para>Format column text (super / payload / zip).</param>
    /// <returns><para>登记的镜像条目；未打开工程或源不存在时为 null。</para>The registered entry; null when no project is open or the source is missing.</returns>
    public async Task<ImageEntryViewModel?> ImportContainerAsync(string sourcePath, string formatLabel)
    {
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || context.ActiveSubProject is null || !File.Exists(sourcePath))
        {
            return null;
        }

        var imagesDir = context.EnsureImagesDirectory();
        var destination = Path.Combine(imagesDir, GetUniqueFileName(imagesDir, Path.GetFileName(sourcePath)));
        await ProjectFiles.CopyAsync(sourcePath, destination).ConfigureAwait(true);
        var relative = context.RelativePath(destination);
        if (relative is null)
        {
            File.Delete(destination);
            return null;
        }

        var entry = new ImageEntryViewModel
        {
            DisplayName = Path.GetFileName(destination),
            RelativePath = relative,
            SizeBytes = new FileInfo(destination).Length,
            Format = formatLabel
        };
        Images.Add(entry);
        return entry;
    }

    /// <summary>
    /// <para>把已在工程内（images/）的提取产物登记为镜像条目（不复制文件）。</para>
    /// Registers an extracted file already inside the project (images/) as an image entry (no copy).
    /// </summary>
    /// <param name="absolutePath"><para>提取产物的绝对路径。</para>Absolute path of the extracted file.</param>
    /// <returns><para>登记的镜像条目；路径不在工程内时为 null。</para>The registered entry; null when the path is outside the project.</returns>
    public ImageEntryViewModel? AddExtractedImage(string absolutePath)
    {
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || !File.Exists(absolutePath))
        {
            return null;
        }

        var relative = context.RelativePath(absolutePath);
        if (relative is null)
        {
            return null;
        }

        var entry = new ImageEntryViewModel
        {
            DisplayName = Path.GetFileName(absolutePath),
            RelativePath = relative,
            SizeBytes = new FileInfo(absolutePath).Length,
            Format = ImageInputService.DetectFormat(absolutePath)
        };
        Images.Add(entry);
        return entry;
    }

    /// <summary>
    /// <para>将当前选中的镜像从项目目录复制到目标路径。</para>
    /// Copies the currently selected image from the project directory to the target path.
    /// </summary>
    /// <param name="targetPath">
    /// <para>导出目标文件路径。</para>
    /// The target export file path.
    /// </param>
    public async Task ExportAsync(string targetPath)
    {
        var image = SelectedImage;
        if (image is null || string.IsNullOrEmpty(targetPath))
        {
            return;
        }

        var source = GetSelectedSourcePath(image, "Export");
        if (source is null)
        {
            return;
        }

        try
        {
            await ProjectFiles.CopyAsync(source, targetPath, overwrite: true);
            LogService.Instance.Write(LogLevel.Info, "Images", $"Exported {image.DisplayName} -> {targetPath}  已导出 {image.DisplayName}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Export failed: {ex.Message}  导出失败：{ex.Message}");
        }
    }
}

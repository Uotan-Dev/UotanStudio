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
    /// <param name="progress">
    /// <para>整体加权百分比（0–100）：按所有待复制文件总字节数折算，跨文件单调递增，不会回跳。</para>
    /// Overall weighted percentage (0–100) computed against the total bytes of all files; monotonic
    /// across files so the progress bar never jumps back.</param>
    /// <param name="byteProgress">
    /// <para>整体字节进度：<c>Processed</c> 为累计已完成字节数，<c>Total</c> 为所有待复制文件总字节数。</para>
    /// Overall byte progress: <c>Processed</c> is accumulated completed bytes, <c>Total</c> is the
    /// total bytes of all files to copy.</param>
    public async Task ImportAsync(string[] paths, IProgress<double>? progress = null,
        IProgress<(long Processed, long Total)>? byteProgress = null)
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

        if (IsOperating) return;
        IsOperating = true;
        StatusText = LanguageService.Instance.GetString("NewProject_Copying");
        var generation = _progressGeneration;
        try
        {
            var imagesDir = context.EnsureImagesDirectory();
            var imported = new List<ImageEntryViewModel>();

            // 预先汇总所有待复制文件的总字节数，用于跨文件加权进度；totalBytes=0 时不上报。
            // Pre-compute total bytes of all files to copy for weighted cross-file progress;
            // reporting is skipped when totalBytes is 0.
            var existingPaths = paths.Where(File.Exists).ToList();
            var totalBytes = existingPaths.Sum(p => new FileInfo(p).Length);
            var completedBytes = 0L;
            ReportByteStatistics(0, totalBytes);
            if (totalBytes > 0)
            {
                progress?.Report(0);
                byteProgress?.Report((0, totalBytes));
            }

            foreach (var path in existingPaths)
            {
                var fileSize = new FileInfo(path).Length;

                var destination = Path.Combine(imagesDir, GetUniqueFileName(imagesDir, Path.GetFileName(path)));
                var completedBeforeFile = completedBytes;
                await ProjectFiles.CopyAsync(path, destination, progress: new Progress<double>(fraction =>
                {
                    if (generation != _progressGeneration || !double.IsFinite(fraction)) return;
                    if (totalBytes > 0)
                    {
                        // 单文件百分比折算为整体加权百分比：completedBytes + fraction * fileSize。
                        // Convert per-file percentage into a weighted overall figure.
                        var overallBytes = completedBeforeFile + fraction / 100.0 * fileSize;
                        var reportedBytes = Math.Max(completedBytes, (long)overallBytes);
                        ReportByteStatistics(reportedBytes, totalBytes);
                        progress?.Report(reportedBytes * 100.0 / totalBytes);
                        byteProgress?.Report((reportedBytes, totalBytes));
                    }
                }));
                // 文件复制完成后累计已完成字节数，供后续文件加权计算。
                // Accumulate completed bytes once this file finishes copying.
                completedBytes += fileSize;
                ReportByteStatistics(completedBytes, totalBytes);
                progress?.Report(totalBytes > 0 ? completedBytes * 100.0 / totalBytes : 100);
                byteProgress?.Report((completedBytes, totalBytes));

                var relative = context.RelativePath(destination);
                if (relative is null)
                {
                    File.Delete(destination);
                    continue;
                }

                var format = await Task.Run(() => ImageInputService.DetectFormat(destination));
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
                StatusText = LanguageService.Instance.GetString("ImageView_NoSelection");
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
            StatusText = LanguageService.Instance.GetString("ImageView_OperationDone");
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
            StatusText = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Images", $"Import failed: {ex.Message}  导入失败：{ex.Message}");
            if (progress is not null || byteProgress is not null) throw;
        }
        finally { IsOperating = false; }
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
    public async Task<ImageEntryViewModel?> ImportContainerAsync(string sourcePath, string formatLabel, IProgress<double>? progress = null,
        IProgress<(long Processed, long Total)>? byteProgress = null)
    {
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || context.ActiveSubProject is null || !File.Exists(sourcePath))
        {
            return null;
        }

        if (IsOperating) return null;
        IsOperating = true;
        StatusText = LanguageService.Instance.GetString("NewProject_Copying");
        var generation = _progressGeneration;
        try
        {
            var imagesDir = context.EnsureImagesDirectory();
            var destination = Path.Combine(imagesDir, GetUniqueFileName(imagesDir, Path.GetFileName(sourcePath)));
            var totalBytes = new FileInfo(sourcePath).Length;
            ReportByteStatistics(0, totalBytes);
            byteProgress?.Report((0, totalBytes));
            await ProjectFiles.CopyAsync(sourcePath, destination, progress: new Progress<double>(percent =>
            {
                if (generation != _progressGeneration || !double.IsFinite(percent)) return;
                ReportByteStatistics((long)(totalBytes * Math.Clamp(percent, 0, 100) / 100), totalBytes);
                progress?.Report(percent);
                byteProgress?.Report(((long)(totalBytes * percent / 100), totalBytes));
            })).ConfigureAwait(true);
            ReportByteStatistics(totalBytes, totalBytes);
            progress?.Report(100);
            byteProgress?.Report((totalBytes, totalBytes));
            StatusText = LanguageService.Instance.GetString("ImageView_OperationDone");
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
        catch (Exception ex)
        {
            StatusText = ex.Message;
            throw;
        }
        finally { IsOperating = false; }
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

        if (IsOperating) return;
        IsOperating = true;
        StatusText = image.DisplayName;
        var generation = _progressGeneration;
        try
        {
            var total = new FileInfo(source).Length;
            ReportByteStatistics(0, total);
            await ProjectFiles.CopyAsync(source, targetPath, overwrite: true, progress: new Progress<double>(percent =>
            {
                if (generation == _progressGeneration && double.IsFinite(percent))
                    ReportByteStatistics((long)(total * Math.Clamp(percent, 0, 100) / 100), total);
            }));
            ReportByteStatistics(total, total);
            StatusText = LanguageService.Instance.GetString("ImageView_OperationDone");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Exported {image.DisplayName} -> {targetPath}  已导出 {image.DisplayName}");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Images", $"Export failed: {ex.Message}  导出失败：{ex.Message}");
        }
        finally { IsOperating = false; }
    }
}

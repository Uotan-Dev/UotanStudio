using UotanStudio.Projects;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    /// <summary>Copies the given external files into the project <c>images/</c> directory (auto-suffixing on name collision), creating image entries and recording history.
    /// <para>将若干外部文件复制进项目 <c>images/</c> 目录（重名自动加后缀），建立镜像条目并登记历史。</para></summary>
    /// <param name="paths">Source file paths to import.<para>要导入的源文件路径列表。</para></param>
    /// <param name="progress">Overall weighted percentage (0–100) computed against the total bytes of all files; monotonic
    /// <para>整体加权百分比（0–100）：按所有待复制文件总字节数折算，跨文件单调递增，不会回跳。</para>across files so the progress bar never jumps back.</param>
    /// <param name="byteProgress">Overall byte progress: <c>Processed</c> is accumulated completed bytes, <c>Total</c> is the
    /// <para>整体字节进度：<c>Processed</c> 为累计已完成字节数，<c>Total</c> 为所有待复制文件总字节数。</para>total bytes of all files to copy.</param>
    public async Task ImportAsync(string[] paths, IProgress<double>? progress = null,
        IProgress<(long Processed, long Total)>? byteProgress = null, CancellationToken cancellationToken = default)
    {
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || context.ActiveSubProject is null)
        {

            LogService.Instance.Write(LogLevel.Warn, "Images",
                $"Import aborted: no project is open  导入中止：尚未打开工程 "
                + $"[solutionOpen={context.IsProjectOpen}, solutionPath={context.CurrentSolutionPath ?? "<null>"}, activeSubProject={context.ActiveSubProject?.Name ?? "<null>"}]");
            return;
        }

        if (IsOperating) return;
        IsOperating = true;
        StatusText = LanguageService.Instance.GetString("NewProject_Copying");
        var generation = _progressGeneration;
        List<string> copiedPaths = [];
        List<ImageEntryViewModel> imported = [];
        var committed = false;
        try
        {
            var imagesDir = context.EnsureImagesDirectory();

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
                cancellationToken.ThrowIfCancellationRequested();
                var fileSize = new FileInfo(path).Length;

                var destination = Path.Combine(imagesDir, GetUniqueFileName(imagesDir, Path.GetFileName(path)));
                var completedBeforeFile = completedBytes;
                await ProjectFiles.CopyAsync(path, destination, cancellationToken: cancellationToken, progress: new Progress<double>(fraction =>
                {
                    if (generation != _progressGeneration || !double.IsFinite(fraction)) return;
                    if (totalBytes > 0)
                    {

                        var overallBytes = completedBeforeFile + fraction / 100.0 * fileSize;
                        var reportedBytes = Math.Max(completedBytes, (long)overallBytes);
                        ReportByteStatistics(reportedBytes, totalBytes);
                        progress?.Report(reportedBytes * 100.0 / totalBytes);
                        byteProgress?.Report((reportedBytes, totalBytes));
                    }
                }));

                copiedPaths.Add(destination);
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
                committed = true;
                return;
            }

            foreach (var entry in imported)
            {
                Images.Add(entry);
            }

            var firstFs = imported.FirstOrDefault(e => e.Format is "ext4" or "erofs");
            if (firstFs is not null)
            {
                ProjectSettingViewModel.Instance?.ApplySourceFormat(firstFs.Format);
            }

            LogService.Instance.Write(LogLevel.Info, "Images", $"Imported {imported.Count} image(s)  已导入 {imported.Count} 个镜像");
            StatusText = LanguageService.Instance.GetString("ImageView_OperationDone");
            var history = CreateOutputOperation(HistoryOperationKind.ImportImages,
                $"Import {imported.Count} image(s)", imported.Select(e => context.ResolvePath(e.RelativePath)!));
            if (history is not null)
            {
                history.Count = imported.Count;
                history.RelativePaths = [.. imported.Select(e => e.RelativePath)];
            }
            OperationHistoryService.Instance.Record(
                $"Import {imported.Count} image(s)", history,
                () =>
                {
                    if (history is not null) UndoOutput(history);
                    foreach (var entry in imported)
                    {
                        Images.Remove(entry);
                    }
                },
                () =>
                {
                    if (history is not null) RedoOutput(history);
                    foreach (var entry in imported)
                    {
                        Images.Add(entry);
                    }
                });
            committed = true;
        }
        catch (Exception ex)
        {
            StatusText = UserError.Describe(ex);
            LogService.Instance.Write(LogLevel.Error, "Images", $"Import failed: {ex.Message}  导入失败：{ex.Message}");
            if (progress is not null || byteProgress is not null) throw;
        }
        finally
        {
            if (!committed)
            {
                foreach (var entry in imported)
                {
                    Images.Remove(entry);
                }
                foreach (var path in copiedPaths)
                {
                    File.Delete(path);
                }
            }
            IsOperating = false;
        }
    }

    /// <summary>
    /// Imports a single container file into the project and registers its image entry.
    /// <para>把单个容器文件导入工程并登记对应的镜像条目。</para>
    /// </summary>
    /// <param name="sourcePath">Source container path. <para>容器源文件路径。</para></param>
    /// <param name="formatLabel">Format column text (super / payload / zip). <para>格式列显示文本（super / payload / zip）。</para></param>
    /// <returns>The registered entry; null when no project is open or the source is missing. <para>登记的镜像条目；未打开工程或源不存在时为 null。</para></returns>
    public async Task<ImageEntryViewModel?> ImportContainerAsync(string sourcePath, string formatLabel, IProgress<double>? progress = null,
        IProgress<(long Processed, long Total)>? byteProgress = null, CancellationToken cancellationToken = default)
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
        string? copiedPath = null;
        ImageEntryViewModel? addedEntry = null;
        var committed = false;
        try
        {
            var imagesDir = context.EnsureImagesDirectory();
            var destination = Path.Combine(imagesDir, GetUniqueFileName(imagesDir, Path.GetFileName(sourcePath)));
            var totalBytes = new FileInfo(sourcePath).Length;
            ReportByteStatistics(0, totalBytes);
            byteProgress?.Report((0, totalBytes));
            await ProjectFiles.CopyAsync(sourcePath, destination, cancellationToken: cancellationToken, progress: new Progress<double>(percent =>
            {
                if (generation != _progressGeneration || !double.IsFinite(percent)) return;
                ReportByteStatistics((long)(totalBytes * Math.Clamp(percent, 0, 100) / 100), totalBytes);
                progress?.Report(percent);
                byteProgress?.Report(((long)(totalBytes * percent / 100), totalBytes));
            })).ConfigureAwait(true);
            copiedPath = destination;
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
            addedEntry = entry;
            var history = CreateOutputOperation(HistoryOperationKind.ImportImages,
                $"Import {entry.DisplayName}", [destination]);
            if (history is not null)
            {
                history.Count = 1;
                history.RelativePaths = [relative];
            }
            OperationHistoryService.Instance.Record(
                history?.Description ?? $"Import {entry.DisplayName}", history,
                () =>
                {
                    if (history is not null) UndoOutput(history);
                    Images.Remove(entry);
                },
                () =>
                {
                    if (history is not null) RedoOutput(history);
                    if (!Images.Contains(entry)) Images.Add(entry);
                });
            committed = true;
            return entry;
        }
        catch (Exception ex)
        {
            StatusText = UserError.Describe(ex);
            throw;
        }
        finally
        {
            if (!committed)
            {
                if (addedEntry is not null)
                {
                    Images.Remove(addedEntry);
                }
                if (copiedPath is not null)
                {
                    File.Delete(copiedPath);
                }
            }
            IsOperating = false;
        }
    }

    /// <summary>
    /// Registers an extracted file already inside the project (images/) as an image entry (no copy).
    /// <para>把已在工程内（images/）的提取产物登记为镜像条目（不复制文件）。</para>
    /// </summary>
    /// <param name="absolutePath">Absolute path of the extracted file. <para>提取产物的绝对路径。</para></param>
    /// <returns>The registered entry; null when the path is outside the project. <para>登记的镜像条目；路径不在工程内时为 null。</para></returns>
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
    /// Copies the currently selected image from the project directory to the target path.
    /// <para>将当前选中的镜像从项目目录复制到目标路径。</para>
    /// </summary>
    /// <param name="targetPath">
    /// The target export file path.
    /// <para>导出目标文件路径。</para>
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
            StatusText = UserError.Describe(ex);
            LogService.Instance.Write(LogLevel.Error, "Images", $"Export failed: {ex.Message}  导出失败：{ex.Message}");
        }
        finally { IsOperating = false; }
    }
}
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using FormatDetector = UotanStudio.Firmware.SharedKernel.FormatDetector;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    /// <summary>Extracts the clicked entry or checked entries. 提取右键条目或勾选条目。</summary>
    [RelayCommand]
    private Task ExtractSelectedAsync(ImageEntryViewModel? image = null) => RunImagesAsync("extract", image);

    /// <summary>Builds images using directory metadata. 按目录元数据构建镜像。</summary>
    [RelayCommand]
    private Task SynthesizeImgAsync(ImageEntryViewModel? image = null) => RunImagesAsync("img", image);

    /// <summary>Builds Brotli-compressed DAT output. 构建 Brotli 压缩的 DAT 输出。</summary>
    [RelayCommand]
    private Task SynthesizeBrAsync(ImageEntryViewModel? image = null) => RunImagesAsync("br", image);

    internal async Task RunExplorerOperationAsync(string path, bool pack)
    {
        try
        {
            if (IsOperating) return;
            var root = ProjectContextService.Instance.ActiveSubProject?.RootPath;
            if (root is null || !Path.GetFullPath(path).StartsWith(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("Select the sub-project containing this entry before operating on it. 请先选择此条目所属的子项目。");
            var relative = ProjectContextService.Instance.RelativePath(path);
            if (relative is null) throw new IOException("The selected path is outside the project.");
            await RunImagesAsync(pack ? "img" : "extract", new ImageEntryViewModel
            {
                RelativePath = relative,
                DisplayName = Path.GetFileName(path),
                IsDirectory = pack,
                Format = pack ? "folder" : ImageInputService.DetectFormat(path)
            });
            if (TopLevelExtensions.GetTopLevel() is Avalonia.Controls.Window owner)
                await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Dock_ImagesView"), StatusText,
                    Ursa.Controls.MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Explorer", ex.Message);
            if (TopLevelExtensions.GetTopLevel() is Avalonia.Controls.Window owner)
                await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Dock_ImagesView"), ex.Message,
                    Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    private async Task RunImagesAsync(string operation, ImageEntryViewModel? clicked)
    {
        if (IsOperating) return;
        var entries = clicked is null ? GetSelectedImages() : new[] { clicked };
        if (entries.Count == 0)
        {
            StatusText = LanguageService.Instance.GetString("ImageView_NoSelection");
            return;
        }

        var context = ProjectContextService.Instance;
        var project = context.ActiveSubProject;
        if (project is null) return;
        var requests = entries.Select(entry => (Entry: entry, Paths: new ImageOperationPaths(
            context.ResolvePath(entry.RelativePath) ?? string.Empty, entry.IsDirectory, project.RootPath, project.OutputDir))).ToArray();
        var brotliLevel = ProjectSettingViewModel.Instance?.BRComGrade ?? 3;
        IsOperating = true;
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        ProgressPercent = 0;
        try
        {
            for (var index = 0; index < requests.Length; index++)
            {
                var (entry, paths) = requests[index];
                StatusText = $"[{index + 1}/{requests.Length}] {operation}: {entry.DisplayName}";
                // 条目内进度换算为总进度：当前条目占用 1/N 的进度条区间。
                // Scale in-item progress into overall progress: each item owns a 1/N slice of the bar.
                var itemProgress = CreateItemProgress(index, requests.Length);
                try
                {
                    _ = paths.UnpackedDirectory;
                    if (operation == "extract")
                    {
                        var (success, skippedEntry) = await ExtractEntryAsync(entry, paths, itemProgress);
                        if (skippedEntry) skipped++;
                        else if (success)
                        {
                            succeeded++;
                            entry.IsUnpacked = true;
                        }
                        else failed++;
                    }
                    else
                    {
                        Directory.CreateDirectory(paths.OutputRoot);
                        var result = await Task.Run(() => ImageOutputService.Build(paths, operation, brotliLevel));
                        if (result.Success) succeeded++;
                        else failed++;
                        LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "Images", result.Summary);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    LogService.Instance.Write(LogLevel.Error, "Images", $"{entry.DisplayName}: {ex.Message}");
                }
                finally { ProgressPercent = (double)(index + 1) / requests.Length * 100; }
            }
            StatusText = string.Format(LanguageService.Instance.GetString("Image_OperationSummary"), succeeded, failed, skipped);
        }
        finally { IsOperating = false; }
    }

    /// <summary>Maps item progress into a batch slice, preserving the dynamic queue's completion cap.</summary>
    private IProgress<double> CreateItemProgress(int completed, int total, bool growingQueue = false)
    {
        var denominator = growingQueue ? Math.Max(1, total) : total;
        var itemBase = (double)completed / denominator * 100;
        var itemSlice = 100.0 / denominator;
        return new Progress<double>(percent =>
        {
            var value = itemBase + Math.Clamp(percent, 0, 99.9) / 100 * itemSlice;
            if (growingQueue) value = Math.Min(99.9, value);
            ProgressPercent = Math.Max(ProgressPercent, value);
        });
    }

    /// <summary>
    /// <para>解包单个镜像条目：校验范围、检测格式、按需确认覆盖并按格式分派解包；
    /// 结果摘要写入统一日志。</para>
    /// Unpacks a single image entry: validates ranges, detects the format, confirms overwrite on
    /// demand and dispatches by format; the result summary goes to the unified log.
    /// </summary>
    /// <param name="entry"><para>镜像条目（用于按格式分派）。</para>The image entry (used for format dispatch).</param>
    /// <param name="paths"><para>操作路径（源文件与解包目录）。</para>Operation paths (source file and unpacked directory).</param>
    /// <param name="itemProgress"><para>条目内进度（0-100，可选），由调用方换算为总进度。</para>Optional in-item progress (0-100); the caller scales it into overall progress.</param>
    /// <returns>解包是否成功，以及是否被用户跳过。Whether extraction succeeded and whether the user skipped it.</returns>
    private async Task<(bool Success, bool Skipped)> ExtractEntryAsync(ImageEntryViewModel entry, ImageOperationPaths paths, IProgress<double>? itemProgress = null)
    {
        paths.ValidateExtraction();
        UotanStudio.Firmware.SharedKernel.FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(paths.Source)
            ?? throw new IOException("Unsupported firmware format.");
        if (Directory.Exists(paths.UnpackedDirectory)
            && !await RequestExtractOverwriteAsync(paths.UnpackedDirectory))
        {
            return (false, true);
        }

        // 文件系统解析器的 (已处理, 总数) 回调换算为条目内百分比。
        // Convert the filesystem parsers' (processed, total) callbacks into an in-item percentage.
        var fileProgress = itemProgress is null
            ? null
            : new Progress<(int processed, int total)>(t => itemProgress.Report(t.total > 0 ? t.processed * 100.0 / t.total : 0));

        var result = await ExtractionDirectory.ReplaceAsync(paths.UnpackedDirectory, async () => entry.Format switch
        {
            "boot" => await Task.Run(() => _firmware.UnpackBootAsync(paths.Source, paths.UnpackedDirectory)),
            "sparse" => await Task.Run(() => _firmware.UnpackSparseImageAsync(paths.Source, paths.UnpackedDirectory)),
            "ext4" or "erofs" or "f2fs" or "raw" => await ExtractRawAsync(paths.Source, paths.UnpackedDirectory, paths.Name, fileProgress),
            _ => await Task.Run(async () =>
            {
                var options = new UotanStudio.Firmware.SharedKernel.UnpackOptions();
                if (itemProgress is not null) options.Progress = new FirmwareItemProgress(itemProgress);
                var unpacked = await handler.UnpackAsync(paths.Source, paths.UnpackedDirectory, options, CancellationToken.None);
                return new FirmwareOpResult { Success = unpacked.Success, Summary = unpacked.Summary };
            })
        });
        LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "Images", result.Summary);
        return (result.Success, false);
    }

    /// <summary>
    /// <para>供新建项目流程调用的解包入口：把导入的镜像逐个解包到工程目录；开启完全解包时
    /// 继续扫描解包产物中的嵌套镜像（如 ROM 包内的 payload.bin、payload 产出的分区镜像）并
    /// 递归解包。进度与状态写入本视图模型，由镜像面板进度条显示。</para>
    /// Unpack entry for the new-project flow: unpacks imported images into the project directory one
    /// by one; with full unpack enabled, scans unpacked output for nested images (payload.bin inside a
    /// ROM package, partition images produced by payload) and unpacks them recursively. Progress and
    /// status are written to this view model for the image panel progress bar.
    /// </summary>
    /// <param name="entries"><para>要解包的镜像条目。</para>The image entries to unpack.</param>
    /// <param name="unpackFully"><para>是否递归解包嵌套镜像。</para>Whether to unpack nested images recursively.</param>
    public async Task UnpackFromProjectCreationAsync(IReadOnlyList<ImageEntryViewModel> entries, bool unpackFully)
    {
        if (IsOperating || entries.Count == 0) return;
        var context = ProjectContextService.Instance;
        var project = context.ActiveSubProject;
        if (project is null) return;

        IsOperating = true;
        var succeeded = 0;
        var failed = 0;
        ProgressPercent = 0;
        try
        {
            var queue = new Queue<ImageEntryViewModel>(entries);
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var done = 0;
            var total = queue.Count;
            while (queue.Count > 0)
            {
                var entry = queue.Dequeue();
                var source = context.ResolvePath(entry.RelativePath) ?? string.Empty;
                if (!processed.Add(source)) continue;
                StatusText = $"[{done + 1}/{total}] extract: {entry.DisplayName}";
                // 条目内进度换算为总进度：队列可能动态增长，按已完成数近似分片。
                // Scale in-item progress into overall progress; the queue may grow, so slices
                // are approximated by the completed count.
                var itemProgress = CreateItemProgress(done, total, growingQueue: true);
                try
                {
                    var paths = new ImageOperationPaths(source, entry.IsDirectory, project.RootPath, project.OutputDir);
                    var (success, skippedEntry) = await ExtractEntryAsync(entry, paths, itemProgress);
                    if (skippedEntry) continue;
                    if (success)
                    {
                        succeeded++;
                        entry.IsUnpacked = true;
                        if (unpackFully)
                        {
                            var nestedImages = await Task.Run(() => CollectNestedImages(paths.UnpackedDirectory));
                            foreach (var nested in nestedImages)
                            {
                                total++;
                                queue.Enqueue(nested);
                            }
                        }
                    }
                    else failed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    LogService.Instance.Write(LogLevel.Error, "Images", $"{entry.DisplayName}: {ex.Message}");
                }
                finally
                {
                    done++;
                    ProgressPercent = (double)done / total * 100;
                }
            }
            StatusText = string.Format(LanguageService.Instance.GetString("Image_OperationSummary"), succeeded, failed, 0);
        }
        finally { IsOperating = false; }
    }

    /// <summary>
    /// <para>扫描解包目录根层与一级子目录，收集可继续解包的嵌套镜像条目；
    /// <c>unknown</c>/<c>raw</c> 格式与工程外的文件被跳过。</para>
    /// Scans the unpacked directory root and first-level subdirectories for nested images that can be
    /// unpacked further; <c>unknown</c>/<c>raw</c> formats and files outside the project are skipped.
    /// </summary>
    private static List<ImageEntryViewModel> CollectNestedImages(string directory)
    {
        var nested = new List<ImageEntryViewModel>();
        if (!Directory.Exists(directory)) return nested;

        var candidates = new List<string>();
        try
        {
            candidates.AddRange(Directory.EnumerateFiles(directory));
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                candidates.AddRange(Directory.EnumerateFiles(sub));
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", $"Nested image scan failed for {directory}: {ex.Message}  嵌套镜像扫描失败：{ex.Message}");
            return nested;
        }

        var context = ProjectContextService.Instance;
        foreach (var file in candidates)
        {
            var format = ImageInputService.DetectFormat(file);
            if (format is "unknown" or "raw" or "folder") continue;
            var relative = context.RelativePath(file);
            if (relative is null) continue;
            nested.Add(new ImageEntryViewModel
            {
                DisplayName = Path.GetFileName(file),
                RelativePath = relative,
                SizeBytes = 0,
                Format = format
            });
        }
        return nested;
    }
}

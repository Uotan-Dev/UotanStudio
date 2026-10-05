using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using FormatDetector = UotanStudio.Firmware.Core.FormatDetector;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    private readonly ProgressMeter _meter = new();

    /// <summary><para>解包统计行：已处理/总量 · 速度 · 剩余时间。</para>
    /// Unpack stats line: processed/total · speed · eta.</summary>
    [ObservableProperty]
    private string _progressStats = string.Empty;

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
                await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Dock_ImagesView"), global::UotanStudio.Services.UserError.Describe(ex),
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
        var equivalenceBlocks = new List<string>();
        ProgressPercent = 0;
        ReportItemStatistics(0, requests.Length);
        try
        {
            for (var index = 0; index < requests.Length; index++)
            {
                var (entry, paths) = requests[index];
                StatusText = $"[{index + 1}/{requests.Length}] {operation}: {entry.DisplayName}";

                var itemProgress = CreateItemProgress(index, requests.Length);
                try
                {
                    _ = paths.UnpackedDirectory;
                    if (operation == "extract")
                    {
                        var hadExistingTarget = File.Exists(paths.UnpackedDirectory) || Directory.Exists(paths.UnpackedDirectory);
                        var (success, skippedEntry, targetDirectory) = await ExtractEntryAsync(entry, paths, itemProgress);
                        if (skippedEntry) skipped++;
                        else if (success)
                        {
                            succeeded++;
                            entry.IsUnpacked = true;
                            if (!hadExistingTarget && CreateOutputOperation(HistoryOperationKind.ExtractImage,
                                    $"Extract {entry.DisplayName}", [targetDirectory]) is { } history)
                            {
                                OperationHistoryService.Instance.Record(history.Description, history,
                                    () => UndoOutput(history), () => RedoOutput(history));
                            }
                        }
                        else failed++;
                    }
                    else
                    {
                        Directory.CreateDirectory(paths.OutputRoot);

                        var conflicts = ImageOutputService.GetPlannedOutputs(paths, operation).Where(File.Exists).ToArray();
                        if (!await ConfirmPackOverwriteAsync(conflicts))
                        {
                            skipped++;
                            continue;
                        }
                        var byteProgress = CreateFileSystemProgress(entry.DisplayName);
                        var result = await Task.Run(() => ImageOutputService.Build(paths, operation, brotliLevel, byteProgress));
                        if (result.Success) succeeded++;
                        else failed++;
                        LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "Images", result.Summary);
                        if (operation == "img" && result.Success)
                        {
                            var lines = FormatEquivalenceLines(entry.DisplayName, result).ToArray();
                            foreach (var line in lines)
                            {
                                LogService.Instance.Write(LogLevel.Info, "Images", line);
                            }
                            equivalenceBlocks.Add(string.Join(Environment.NewLine, lines));
                        }
                        if (result.Success && conflicts.Length == 0)
                        {
                            var kind = operation switch
                            {
                                "img" => HistoryOperationKind.SynthesizeImg,
                                "br" => HistoryOperationKind.SynthesizeBr,
                                _ => HistoryOperationKind.SynthesizeDat
                            };
                            if (CreateOutputOperation(kind, $"Synthesize {operation} for {entry.DisplayName}",
                                    ImageOutputService.GetPlannedOutputs(paths, operation)) is { } history)
                            {
                                OperationHistoryService.Instance.Record(history.Description, history,
                                    () => UndoOutput(history), () => RedoOutput(history));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    LogService.Instance.Write(LogLevel.Error, "Images", $"{entry.DisplayName}: {ex.Message}");
                }
                finally
                {
                    _progressGeneration++;
                    if (_fileSystemStage is null)
                    {
                        ProgressPercent = (double)(index + 1) / requests.Length * 100;
                        ReportItemStatistics(index + 1, requests.Length);
                    }
                }
            }
            StatusText = string.Format(LanguageService.Instance.GetString("Image_OperationSummary"), succeeded, failed, skipped);
            EquivalenceReport = FirmwareEquivalencePresenter.FormatVisibleReport(equivalenceBlocks, LanguageService.Instance.GetString);
        }
        finally { IsOperating = false; }
    }

    /// <summary>
    /// <para>按现有结果呈现方式，把一个构建结果的四维度等价报告展开为标题加四行本地化文本。只有文件系统镜像的
    /// 构建结果才会进入这里：dat/br 产出的是 DAT 与压缩流而非镜像，报告其等价性只会误述产物；未被库评估的维度
    /// 保持“未评估”。每个条目的全部维度行都写入统一日志，可见结果区只保留最后一项，批量构建不会拉长底部状态区。</para>
    /// Expands one build result's four-dimension equivalence report into the heading plus four localized lines used
    /// by the existing result surface. Only a filesystem image build reaches this helper, because DAT and Brotli
    /// outputs are streams rather than images and reporting equivalence for them would misdescribe the artifact, and
    /// a dimension the library never evaluated stays "not evaluated". Every entry's dimension lines are written to
    /// the unified log while the visible result area keeps only the last entry, so a batch build cannot stretch the
    /// footer without bound.
    /// </summary>
    private static IEnumerable<string> FormatEquivalenceLines(string entryName, FirmwareOpResult result)
    {
        var language = LanguageService.Instance;
        yield return FirmwareEquivalencePresenter.FormatHeader(language.GetString, entryName);
        foreach (var line in FirmwareEquivalencePresenter.FormatLines(result.Equivalence, language.GetString))
        {
            yield return line;
        }
    }

    /// <summary>Maps item progress into a batch slice, preserving the dynamic queue's completion cap.</summary>
    private IProgress<double> CreateItemProgress(int completed, int total, bool growingQueue = false)
    {
        var denominator = growingQueue ? Math.Max(1, total) : total;
        var itemBase = (double)completed / denominator * 100;
        var itemSlice = 100.0 / denominator;
        var generation = _progressGeneration;
        return new Progress<double>(percent =>
        {
            if (generation != _progressGeneration || !IsOperating || !double.IsFinite(percent)) return;
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
    /// <returns>解包是否成功、是否被用户跳过，以及实际使用的解包目录（重名时已加后缀）。
    /// Whether extraction succeeded, whether the user skipped it, and the directory actually used
    /// (suffixed when a duplicate name was found).</returns>
    private async Task<(bool Success, bool Skipped, string TargetDirectory)> ExtractEntryAsync(ImageEntryViewModel entry, ImageOperationPaths paths, IProgress<double>? itemProgress = null, CancellationToken cancellationToken = default)
    {
        paths.ValidateExtraction();
        var targetDirectory = ResolveUnpackTargetDirectory(paths);
        using var metadataScope = UotanStudio.Firmware.Core.Repacking.RepackMetadata.UseContext(
            ProjectContextService.Instance.CaptureMetadataContext(targetDirectory));
        UotanStudio.Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
        // 条目的格式标签来自解决方案快照，先还原为格式标识再分派。
        // Entry labels come from the solution snapshot, so they are parsed back into a format identity
        // before dispatch.
        var format = ImageInputService.ParseFormatLabel(entry.Format);
        var handler = ImageInputService.IsBootFamilyFormat(format)
            ? new UotanStudio.Firmware.BootSecurity.BootImage.BootImageFormatHandler()
            : FormatDetector.Detect(paths.Source) ?? throw new IOException("Unsupported firmware format.");
        if (Directory.Exists(targetDirectory)
            && !await RequestExtractOverwriteAsync(targetDirectory))
        {
            return (false, true, targetDirectory);
        }

        IReadOnlyList<string>? erofsDevices = null;
        if (format is ImageFormatKind.Ext4 or ImageFormatKind.Erofs or ImageFormatKind.F2fs or ImageFormatKind.Raw
            && ImageInputService.ProbeFilesystemHeader(paths.Source) == RawImageLayout.Erofs)
        {
            erofsDevices = await SelectErofsDevicesAsync(paths.Source, cancellationToken);
            if (erofsDevices is null) return (false, true, targetDirectory);
        }

        var sourceInfo = new FileInfo(paths.Source);
        LogService.Instance.Write(LogLevel.Info, "Images",
            $"Extracting {entry.DisplayName} | source={paths.Source} | size={ProgressMeter.FormatSize(sourceInfo.Length)} | format={handler.FormatName} | target={targetDirectory}");

        var byteProgress = CreateFileSystemProgress(entry.DisplayName);

        var result = await ExtractionDirectory.ReplaceAsync(targetDirectory, async () => format switch
        {
            ImageFormatKind.Boot or ImageFormatKind.InitBoot or ImageFormatKind.Recovery or ImageFormatKind.VendorBoot => await Task.Run(() => _firmware.UnpackBootAsync(paths.Source, targetDirectory), cancellationToken),
            ImageFormatKind.Sparse => await Task.Run(() => _firmware.UnpackSparseImageAsync(paths.Source, targetDirectory, byteProgress: byteProgress, cancellationToken: cancellationToken), cancellationToken),
            ImageFormatKind.Ext4 or ImageFormatKind.Erofs or ImageFormatKind.F2fs or ImageFormatKind.Raw => await ExtractRawAsync(paths.Source, targetDirectory, paths.Name,
                byteProgress: byteProgress, erofsExtraDevicePaths: erofsDevices, cancellationToken: cancellationToken),
            // boot 家族其余变体（MTK/Samsung/DHTB/ChromeOS/Tegra/zImage/RSCE）由 BootImageFormatHandler 分派解包。
            // Remaining boot-family variants (MTK/Samsung/DHTB/ChromeOS/Tegra/zImage/RSCE) dispatch through BootImageFormatHandler.
            _ => await Task.Run(async () =>
            {
                var options = new UotanStudio.Firmware.Core.UnpackOptions { FileSystemProgress = byteProgress };
                if (itemProgress is not null) options.Progress = new FirmwareItemProgress(itemProgress);
                var unpacked = await handler.UnpackAsync(paths.Source, targetDirectory, options, cancellationToken);
                return FirmwareOpResult.From(unpacked);
            })
        });
        LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "Images", result.Summary);
        if (result.Success) LogExtractionContents(targetDirectory);
        return (result.Success, false, targetDirectory);
    }

    private static async Task<IReadOnlyList<string>?> SelectErofsDevicesAsync(string imagePath, CancellationToken cancellationToken)
    {
        var count = ErofsFormatHandler.ReadExtraDeviceCount(imagePath);
        if (count == 0) return [];
        var topLevel = TopLevelExtensions.GetTopLevel()
            ?? throw new InvalidOperationException(LanguageService.Instance.GetString("Image_ErofsDevicePickerUnavailable"));
        var language = LanguageService.Instance;
        var paths = new List<string>(count);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            Path.GetFullPath(imagePath)
        };
        for (var id = 1; id <= count; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = string.Format(language.GetString("Image_ErofsSelectDevice"), id, count),
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.All]
            });
            if (files.Count == 0) return null;
            using var file = files[0];
            var path = file.TryGetLocalPath()
                ?? throw new IOException(language.GetString("Image_ErofsDeviceMustBeLocal"));
            if (!seen.Add(Path.GetFullPath(path)))
                throw new InvalidDataException(language.GetString("Image_ErofsDuplicateDevice"));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// <para>返回本次解包实际使用的目标目录：当标准解包目录已被同名文件占用（文件与目录冲突，
    /// 无法作为目录解包）时，自动在原名称后追加 <c>(1)</c>/<c>(2)</c> 形式的后缀以避开冲突；
    /// 否则原样返回标准解包目录。</para>
    /// Returns the directory actually used for this extraction: when the standard unpacked directory
    /// is already occupied by a same-named file (a file/directory conflict that cannot be unpacked
    /// into), the original name is automatically given a <c>(1)</c>/<c>(2)</c> style suffix to avoid
    /// the collision; otherwise the standard unpacked directory is returned unchanged.
    /// </summary>
    private static string ResolveUnpackTargetDirectory(ImageOperationPaths paths)
    {
        var basePath = paths.UnpackedDirectory;

        if (!File.Exists(basePath))
        {
            return basePath;
        }

        var parent = Path.GetDirectoryName(basePath)!;
        var name = Path.GetFileName(basePath);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(parent, $"{name}({i})");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
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
    public async Task UnpackFromProjectCreationAsync(IReadOnlyList<ImageEntryViewModel> entries, bool unpackFully, CancellationToken cancellationToken = default)
    {
        if (IsOperating || entries.Count == 0) return;
        var context = ProjectContextService.Instance;
        var project = context.ActiveSubProject;
        if (project is null) return;

        IsOperating = true;
        var succeeded = 0;
        var failed = 0;
        ProgressPercent = 0;
        _meter.Reset();
        ProgressStats = string.Empty;
        try
        {
            var queue = new Queue<ImageEntryViewModel>(entries);
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var seenNames = new HashSet<string>(entries.Select(entry => entry.DisplayName), StringComparer.OrdinalIgnoreCase);
            var done = 0;
            var total = queue.Count;
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = queue.Dequeue();
                var source = context.ResolvePath(entry.RelativePath) ?? string.Empty;
                if (!processed.Add(source)) continue;
                StatusText = string.Format(LanguageService.Instance.GetString("Image_FsStageFile"),
                    LanguageService.Instance.GetString("Image_FsExtracting"), entry.DisplayName);

                var itemProgress = CreateItemProgress(done, total, growingQueue: true);
                try
                {
                    var paths = new ImageOperationPaths(source, entry.IsDirectory, project.RootPath, project.OutputDir);
                    var (success, skippedEntry, targetDirectory) = await ExtractEntryAsync(entry, paths, itemProgress, cancellationToken);
                    if (skippedEntry) continue;
                    if (success)
                    {
                        succeeded++;
                        entry.IsUnpacked = true;
                        if (unpackFully)
                        {
                            var nestedImages = await Task.Run(() => CollectNestedImages(targetDirectory));
                            foreach (var nested in nestedImages)
                            {
                                if (!seenNames.Add(nested.DisplayName)) continue;
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
                    _progressGeneration++;
                    done++;
                    if (_fileSystemStage is null) ProgressPercent = (double)done / total * 100;
                }
            }
            StatusText = string.Format(LanguageService.Instance.GetString("Image_OperationSummary"), succeeded, failed, 0);
        }
        finally { IsOperating = false; }
        if (failed > 0) throw new IOException(StatusText);
    }

    /// <summary>
    /// <para>把解包目录根层的产物统计（目录与文件数量）写成一行统一日志，不逐条展开条目，
    /// 避免递归解包大量嵌套镜像时刷屏。</para>
    /// Writes a single unified-log line with the root-level extraction tally (directory and file
    /// counts) instead of listing entries one by one, so recursive unpacking of nested images
    /// does not flood the log.
    /// </summary>
    /// <param name="directory"><para>解包目标目录。</para>Extraction target directory.</param>
    private static void LogExtractionContents(string directory)
    {
        try
        {
            var directories = 0;
            var files = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(path)) directories++;
                else files++;
            }
            LogService.Instance.Write(LogLevel.Info, "Images",
                $"Extracted contents: {directories} dir(s), {files} file(s)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", $"Cannot list extraction contents of {directory}: {ex.Message}");
        }
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
            // 探测只返回文件内的格式标识；目录条目的 "folder" 标签由打包模式单独写入。
            // Probing only reports file formats; the "folder" label is written by pack mode separately.
            var format = ImageInputService.ProbeFormat(file);
            if (format is ImageFormatKind.Unknown or ImageFormatKind.Raw)
            {
                continue;
            }

            var relative = context.RelativePath(file);
            if (relative is null) continue;
            nested.Add(new ImageEntryViewModel
            {
                DisplayName = Path.GetFileName(file),
                RelativePath = relative,
                SizeBytes = 0,
                Format = ImageInputService.GetFormatLabel(format)
            });
        }
        return nested;
    }
}
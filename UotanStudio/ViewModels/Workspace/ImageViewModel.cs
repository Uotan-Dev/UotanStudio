using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Devices;
using FormatDetector = UotanStudio.Firmware.SharedKernel.FormatDetector;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>镜像面板视图模型（单例），承载项目内镜像清单并实现分解、合成、导入、导出等真实打包解包逻辑。
/// 与设备面板类似，工作区停靠面板与顶部文件菜单共享同一实例，避免双实例双订阅。</para>
/// Image panel view model (singleton), holding the project image list and implementing real unpack/pack,
/// import and export logic. Like the device panel, the workspace dock and the top file menu share the
/// same instance to avoid duplicate instances and duplicate subscriptions.
/// </summary>
internal partial class ImageViewModel : Tool
{
    private static readonly Lazy<ImageViewModel> _lazyInstance = new(() => new ImageViewModel());

    /// <summary>
    /// <para>获取当前镜像面板单例实例，供主窗口与工作区停靠面板共享。</para>
    /// Gets the active image panel singleton, shared by the workspace dock and the top file menu.
    /// </summary>
    internal static ImageViewModel Instance => _lazyInstance.Value;

    /// <summary>
    /// <para>确保镜像面板单例已创建并返回它。</para>
    /// Ensures the image panel singleton exists and returns it.
    /// </summary>
    internal static ImageViewModel EnsureInstance() => Instance;

    private readonly FirmwareService _firmware = FirmwareService.Instance;

    /// <summary>
    /// <para>解包时目标目录已存在，请求用户确认是否覆盖；视图层订阅此事件并弹出对话框，
    /// 将结果写入 <c>TaskCompletionSource&lt;bool&gt;</c>。</para>
    /// Raised when the extract target directory already exists; the view subscribes and shows a
    /// confirmation dialog, writing the result to the <c>TaskCompletionSource&lt;bool&gt;</c>.
    /// </summary>
    internal event EventHandler<(string DirectoryName, TaskCompletionSource<bool> Confirmation)>? ExtractOverwriteRequested;

    private async Task<bool> RequestExtractOverwriteAsync(string directoryName)
    {
        var tcs = new TaskCompletionSource<bool>();
        ExtractOverwriteRequested?.Invoke(this, (directoryName, tcs));
        return await tcs.Task;
    }

    /// <summary>
    /// <para>获取项目内镜像条目集合，条目路径为相对工程根目录。</para>
    /// Gets the collection of image entries in the project; entry paths are relative to the project root.
    /// </summary>
    public ObservableCollection<ImageEntryViewModel> Images { get; } = [];

    /// <summary>
    /// <para>获取或设置当前是否显示打包操作。</para>
    /// Gets or sets whether pack actions are currently shown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnpackMode))]
    private bool _isPackMode;

    partial void OnIsPackModeChanged(bool value)
    {
        _ = RefreshProjectImagesAsync();
    }

    /// <summary>
    /// <para>获取当前是否显示解包操作；设置为 true 时切换到解包模式。</para>
    /// Gets whether unpack actions are currently shown; setting this to true switches to unpack mode.
    /// </summary>
    public bool IsUnpackMode
    {
        get => !IsPackMode;
        set
        {
            if (value)
            {
                IsPackMode = false;
            }
        }
    }

    /// <summary>
    /// <para>获取子项目选择下拉框的可选项（子项目名称列表）。</para>
    /// Gets the available items in the sub-project selector combo box (sub-project name list).
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string>? _subProjectItems;

    /// <summary>
    /// <para>获取或设置是否显示子项目选择下拉框；当存在两个及以上子项目时显示。</para>
    /// Gets or sets whether the sub-project selector combo box is visible;
    /// shown when two or more sub-projects exist.
    /// </summary>
    [ObservableProperty]
    private bool _isSubProjectVisible;

    /// <summary>
    /// <para>获取或设置当前选中的子项目名称；变更时切换活动子项目并刷新条目列表。</para>
    /// Gets or sets the currently selected sub-project name; switching refreshes the entry list.
    /// </summary>
    [ObservableProperty]
    private string? _selectedSubProject;

    partial void OnSelectedSubProjectChanged(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var context = ProjectContextService.Instance;
        if (context.SetActiveSubProject(value))
        {
            _ = RefreshProjectImagesAsync();
        }
    }

    /// <summary>
    /// <para>获取或设置当前选中的镜像条目。</para>
    /// Gets or sets the currently selected image entry.
    /// </summary>
    [ObservableProperty]
    private ImageEntryViewModel? _selectedImage;

    partial void OnSelectedImageChanged(ImageEntryViewModel? value)
    {
        // 通知属性面板等订阅方刷新显示镜像详细参数。
        // Notify subscribers such as the properties panel to refresh with detailed image parameters.
        WeakReferenceMessenger.Default.Send(new ImageSelectionChangedMessage(value));
    }

    /// <summary>
    /// <para>获取或设置面板状态文本，回报批量刷入等操作的进度与结果。</para>
    /// Gets or sets the panel status text, reporting progress and results of operations
    /// such as batch flashing.
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// <para>获取或设置当前批量刷入操作的完成百分比。</para>
    /// Gets or sets the completion percentage of the current batch-flash operation.
    /// </summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>
    /// <para>创建镜像面板视图模型并注册单例实例。</para>
    /// Creates the image panel view model and registers the singleton instance.
    /// </summary>
    public ImageViewModel()
    {
        // 订阅工程上下文变更：打开工程时自动扫描 images/ 目录注册已知镜像格式，
        // 关闭工程时清空列表。
        // Subscribe to project-context changes: on open, auto-register known image formats from the
        // images/ directory; on close, clear the list.
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);

        // 注册跨会话操作历史重建工厂，使重启后可恢复撤销栈。
        // Register the cross-session history reconstructor so the undo stack can be restored after restart.
        OperationHistoryService.ReconstructOperation = ReconstructOperation;
    }

    /// <summary>
    /// <para>响应工程上下文变更消息：打开工程时异步扫描 <c>images/</c> 目录并注册已知镜像格式，
    /// 关闭工程时清空镜像列表。</para>
    /// Responds to project-context-changed messages: asynchronously scans the <c>images/</c> directory
    /// and registers known image formats on open, and clears the list on close.
    /// </summary>
    private async void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message)
    {
        await RefreshProjectImagesAsync();
    }

    /// <summary>
    /// <para>解包模式下排除的保留目录名集合（不区分大小写）。</para>
    /// Reserved directory names excluded in pack mode (case-insensitive).
    /// </summary>
    private static readonly HashSet<string> ReservedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "images", "output", ".deleted", ".uotan"
    };

    /// <summary>
    /// <para>根据当前模式扫描工程条目：解包模式扫描 <c>images/</c> 目录中的可解包文件，
    /// 打包模式扫描子项目根目录下的解包产物文件夹。未打开工程时清空列表。</para>
    /// Scans project entries based on the current mode: in unpack mode scans <c>images/</c> for
    /// unpackable files; in pack mode scans the sub-project root for unpacked result directories.
    /// Clears the list when no project is open.
    /// </summary>
    private async Task RefreshProjectImagesAsync()
    {
        var context = ProjectContextService.Instance;
        Images.Clear();

        if (!context.IsProjectOpen)
        {
            return;
        }

        RebuildSubProjectItems();

        if (IsPackMode)
        {
            await RefreshPackEntriesAsync(context);
        }
        else
        {
            await RefreshUnpackEntriesAsync(context);
        }
    }

    /// <summary>
    /// <para>解包模式：扫描 <c>images/</c> 目录，把已知镜像格式的文件注册到列表。</para>
    /// Unpack mode: scans the <c>images/</c> directory, registering files with known image formats.
    /// </summary>
    private async Task RefreshUnpackEntriesAsync(ProjectContextService context)
    {
        var imagesDir = context.ActiveSubProject?.ImagesDir;
        if (string.IsNullOrEmpty(imagesDir) || !Directory.Exists(imagesDir))
        {
            return;
        }

        // 目录枚举与格式探测在后台线程执行，避免大目录/大文件头读取阻塞 UI。
        // Directory enumeration and format probing run on a background thread so large folders or
        // header reads never block the UI.
        var discovered = await Task.Run(() =>
        {
            var list = new List<(string Relative, long Size, string Format)>();
            foreach (var file in Directory.EnumerateFiles(imagesDir))
            {
                var format = DetectFormat(file);
                if (format is "unknown" or "raw")
                {
                    continue;
                }

                var relative = context.RelativePath(file);
                if (relative is not null)
                {
                    list.Add((relative, new FileInfo(file).Length, format));
                }
            }
            return list;
        }).ConfigureAwait(true);

        foreach (var (relative, size, format) in discovered)
        {
            if (Images.Any(i => i.RelativePath == relative))
            {
                continue;
            }

            Images.Add(new ImageEntryViewModel
            {
                DisplayName = Path.GetFileName(relative),
                RelativePath = relative,
                SizeBytes = size,
                Format = format
            });
        }

        LogService.Instance.Write(LogLevel.Info, "Images", $"Auto-registered {discovered.Count} known image(s)  自动注册 {discovered.Count} 个已知镜像");
    }

    /// <summary>
    /// <para>打包模式：扫描子项目根目录下的一级子目录（排除保留目录），注册为可打包条目。</para>
    /// Pack mode: scans first-level sub-directories of the sub-project root (excluding reserved ones),
    /// registering them as packable entries.
    /// </summary>
    private async Task RefreshPackEntriesAsync(ProjectContextService context)
    {
        var subProjectRoot = context.ActiveSubProject?.RootPath;
        if (string.IsNullOrEmpty(subProjectRoot) || !Directory.Exists(subProjectRoot))
        {
            return;
        }

        // 目录枚举在后台线程执行。
        // Directory enumeration runs on a background thread.
        var discovered = await Task.Run(() =>
        {
            var list = new List<(string Name, string Relative)>();
            foreach (var dir in Directory.EnumerateDirectories(subProjectRoot))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || ReservedDirNames.Contains(name))
                {
                    continue;
                }

                var relative = context.RelativePath(dir);
                if (relative is not null)
                {
                    list.Add((name, relative));
                }
            }
            return list;
        }).ConfigureAwait(true);

        foreach (var (name, relative) in discovered)
        {
            if (Images.Any(i => i.RelativePath == relative))
            {
                continue;
            }

            Images.Add(new ImageEntryViewModel
            {
                DisplayName = name,
                RelativePath = relative,
                SizeBytes = 0,
                Format = "folder",
                IsDirectory = true
            });
        }

        LogService.Instance.Write(LogLevel.Info, "Images", $"Auto-registered {discovered.Count} unpacked folder(s)  自动注册 {discovered.Count} 个解包文件夹");
    }

    /// <summary>
    /// <para>根据当前解决方案的子项目列表重建下拉框选项；存在两个及以上子项目时显示下拉框。</para>
    /// Rebuilds the sub-project selector combo box from the current solution's sub-project list;
    /// the dropdown is shown when two or more sub-projects exist.
    /// </summary>
    private void RebuildSubProjectItems()
    {
        var context = ProjectContextService.Instance;
        var subProjects = context.SubProjects;

        if (subProjects.Count < 2)
        {
            SubProjectItems = null;
            IsSubProjectVisible = false;

            // 单子项目时确保选中项与活动子项目一致。
            // Ensure selection matches the active sub-project when only one exists.
            if (subProjects.Count == 1)
            {
                SelectedSubProject = subProjects[0].Name;
            }

            return;
        }

        var items = new ObservableCollection<string>(subProjects.Select(sp => sp.Name));
        SubProjectItems = items;
        IsSubProjectVisible = true;

        // 保持当前选中项有效；若无效则回退到活动子项目。
        // Keep the current selection valid; fall back to the active sub-project.
        var activeName = context.ActiveSubProject?.Name;
        if (SelectedSubProject is null || !items.Contains(SelectedSubProject))
        {
            SelectedSubProject = activeName ?? items[0];
        }
    }

    /// <summary>
    /// <para>从持久化操作描述符重建撤销/重做动作（供操作历史跨会话恢复）。</para>
    /// Reconstructs undo/redo actions from a persisted operation descriptor (for cross-session history restore).
    /// </summary>
    private static (Action Undo, Action Redo) ReconstructOperation(HistoryOperationDto operation)
    {
        switch (operation.Kind)
        {
            case HistoryOperationKind.ImportImages:
                return ReconstructImport(operation);
            case HistoryOperationKind.DeleteImage:
                return ReconstructDelete(operation);
            case HistoryOperationKind.ExtractImage:
                return (() => DeleteDirectorySafe(operation.OutputPaths.FirstOrDefault() ?? string.Empty), () => { });
            case HistoryOperationKind.SynthesizeImg:
            case HistoryOperationKind.SynthesizeBr:
            case HistoryOperationKind.SynthesizeSuper:
                return (() => DeleteFileSafe(operation.OutputPaths.FirstOrDefault() ?? string.Empty), () => { });
            case HistoryOperationKind.SynthesizeDat:
                return (() =>
                {
                    foreach (var path in operation.OutputPaths)
                    {
                        DeleteFileSafe(path);
                    }
                }, () => { }
                );
            default:
                LogService.Instance.Write(LogLevel.Warn, "History",
                    $"Unknown history operation kind: {operation.Kind}");
                return (
                    () => LogService.Instance.Write(LogLevel.Warn, "History", $"Undo: unknown operation '{operation.Kind}'"),
                    () => LogService.Instance.Write(LogLevel.Warn, "History", $"Redo: unknown operation '{operation.Kind}'")
                );
        }
    }

    private static (Action Undo, Action Redo) ReconstructImport(HistoryOperationDto operation)
    {
        var vm = EnsureInstance();
        return (
            () =>
            {
                foreach (var relative in operation.RelativePaths)
                {
                    var entry = vm.Images.FirstOrDefault(i => i.RelativePath == relative);
                    if (entry is not null)
                    {
                        vm.Images.Remove(entry);
                    }
                }
            },
            () =>
            {
                foreach (var relative in operation.RelativePaths)
                {
                    if (vm.Images.Any(i => i.RelativePath == relative))
                    {
                        continue;
                    }

                    var absolute = ProjectContextService.Instance.ResolvePath(relative);
                    if (absolute is null || !File.Exists(absolute))
                    {
                        continue;
                    }

                    vm.Images.Add(new ImageEntryViewModel
                    {
                        DisplayName = Path.GetFileName(relative),
                        RelativePath = relative,
                        SizeBytes = new FileInfo(absolute).Length,
                        Format = DetectFormat(absolute)
                    });
                }
            }
        );
    }

    private static (Action Undo, Action Redo) ReconstructDelete(HistoryOperationDto operation)
    {
        var vm = EnsureInstance();
        var source = ProjectContextService.Instance.ResolvePath(operation.RelativePath);
        return (
            () =>
            {
                if (source is null || !File.Exists(operation.DestinationPath))
                {
                    return;
                }

                File.Move(operation.DestinationPath, source);
                vm.Images.Add(new ImageEntryViewModel
                {
                    DisplayName = Path.GetFileName(operation.RelativePath),
                    RelativePath = operation.RelativePath,
                    SizeBytes = new FileInfo(source).Length,
                    Format = DetectFormat(source)
                });
            },
            () =>
            {
                if (source is null || !File.Exists(source))
                {
                    return;
                }

                File.Move(source, operation.DestinationPath);
                var entry = vm.Images.FirstOrDefault(i => i.RelativePath == operation.RelativePath);
                if (entry is not null)
                {
                    vm.Images.Remove(entry);
                }
            }
        );
    }

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
                await Task.Run(() => File.Copy(path, destination));
                var relative = context.RelativePath(destination);
                if (relative is null)
                {
                    File.Delete(destination);
                    continue;
                }

                var format = DetectFormat(destination);
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
            await Task.Run(() => File.Copy(source, targetPath, overwrite: true));
            LogService.Instance.Write(LogLevel.Info, "Images", $"Exported {image.DisplayName} -> {targetPath}  已导出 {image.DisplayName}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Export failed: {ex.Message}  导出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// <para>按选中镜像的格式分发到对应解包逻辑，输出到 <c>intermediate/&lt;名称&gt;/</c> 并登记历史（撤销删除输出目录）。</para>
    /// Dispatches the selected image by its format to the matching unpack logic, writing to
    /// <c>intermediate/&lt;name&gt;/</c> and recording history (undo deletes the output directory).
    /// </summary>
    [RelayCommand]
    private async Task ExtractSelectedAsync()
    {
        var selectedImages = GetSelectedImages();
        if (selectedImages.Count == 0)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Extract aborted: no project is open  分解中止：尚未打开工程");
            return;
        }

        ProgressPercent = 0;
        for (var i = 0; i < selectedImages.Count; i++)
        {
            var image = selectedImages[i];
            StatusText = $"[{i + 1}/{selectedImages.Count}] Extracting {image.DisplayName}  正在分解 {image.DisplayName}";
            await ExtractImageAsync(image, i, selectedImages.Count);
            ProgressPercent = (double)(i + 1) / selectedImages.Count * 100;
        }

        StatusText = $"Extracted {selectedImages.Count} image(s)  已分解 {selectedImages.Count} 个镜像";
        ProgressPercent = 100;
    }

    private async Task ExtractImageAsync(ImageEntryViewModel image, int fileIndex = 0, int fileCount = 1)
    {
        var source = GetSelectedSourcePath(image, "Extract");
        if (source is null)
        {
            return;
        }

        var context = ProjectContextService.Instance;
        var name = GetEntryBaseName(image);
        try
        {
            // 解包产物输出到子项目根目录（与 images/ 同级），而非 intermediate/ 目录。
            // Unpack output goes to the sub-project root (same level as images/), not intermediate/.
            var subProjectRoot = context.ActiveSubProject?.RootPath;
            if (subProjectRoot is null)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", "Extract aborted: no active sub-project  分解中止：无活动子项目");
                return;
            }

            var outputDir = Path.Combine(subProjectRoot, name);

            // 目标目录已存在时弹出确认对话框，用户选择覆盖则删除后重建，选择跳过则中止。
            // When the target directory exists, prompt the user: overwrite deletes and recreates, skip aborts.
            if (Directory.Exists(outputDir))
            {
                var overwrite = await RequestExtractOverwriteAsync(name);
                if (!overwrite)
                {
                    LogService.Instance.Write(LogLevel.Info, "Images", $"Extract skipped: directory \"{name}\" already exists  分解跳过：目录 \"{name}\" 已存在");
                    return;
                }

                DeleteDirectorySafe(outputDir);
            }

            Directory.CreateDirectory(outputDir);
            LogService.Instance.Write(LogLevel.Info, "Images", $"Extracting {image.Format}: {image.RelativePath} -> {outputDir}  正在分解 {image.Format}：{image.RelativePath} -> {outputDir}");
            var result = image.Format switch
            {
                "boot" => await Task.Run(() => _firmware.UnpackBootAsync(source, outputDir)),
                "payload" => await Task.Run(() => _firmware.ExtractPayloadAsync(source, outputDir)),
                "cpio" => await Task.Run(() => _firmware.UnpackCpioAsync(source, outputDir)),
                "oppo" => await Task.Run(() => _firmware.ExtractOppoAsync(source, outputDir)),
                // sparse 镜像级联解出内部文件系统内容（非文件系统内容时服务内回退 .raw）。
                // Sparse images cascade into the internal filesystem contents (falls back to .raw
                // inside the service when the content is not a filesystem).
                "sparse" => await Task.Run(() => _firmware.UnpackSparseImageAsync(source, outputDir)),
                // 文件系统镜像：用对应解析器级联解出内部文件系统内容。
                // Filesystem images: cascade into the internal filesystem contents with the matching parser.
                "ext4" or "erofs" or "f2fs" or "raw" => await ExtractRawAsync(source, outputDir, name,
                    new Progress<(int processed, int total)>(p =>
                    {
                        if (p.total > 0)
                        {
                            // 加权进度：当前文件内部进度按文件在总列表中的占比缩放。
                            // Weighted progress: per-file internal progress scaled by file's share of the total.
                            ProgressPercent = ((double)fileIndex + (double)p.processed / p.total) / fileCount * 100;
                        }
                    })),
                _ => FirmwareOpResult.Fail($"Unsupported image format: {image.Format}  不支持的镜像格式：{image.Format}")
            };

            if (!result.Success)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", result.Summary);
                return;
            }

            LogService.Instance.Write(LogLevel.Info, "Images", result.Summary);
            OperationHistoryService.Instance.Record(
                $"Extract {image.DisplayName}",
                new HistoryOperationDto
                {
                    Kind = HistoryOperationKind.ExtractImage,
                    Description = $"Extract {image.DisplayName}",
                    DisplayName = image.DisplayName,
                    OutputPaths = [outputDir]
                },
                () => DeleteDirectorySafe(outputDir),
                () => { });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Extract failed: {ex.Message}  分解失败：{ex.Message}");
        }
    }

    /// <summary>
    /// <para>从解包目录重新打包 boot 镜像到 <c>intermediate/img/</c> 并登记历史。</para>
    /// Repacks a boot image from the unpack directory to <c>intermediate/img/</c> and records history.
    /// </summary>
    [RelayCommand]
    private async Task SynthesizeImgAsync()
    {
        var selectedImages = GetSelectedImages();
        if (selectedImages.Count == 0)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize img aborted: no project is open  合成 img 中止：尚未打开工程");
            return;
        }

        ProgressPercent = 0;
        for (var i = 0; i < selectedImages.Count; i++)
        {
            var image = selectedImages[i];
            StatusText = $"[{i + 1}/{selectedImages.Count}] Synthesizing img: {image.DisplayName}  正在合成 img：{image.DisplayName}";
            await SynthesizeSingleImgAsync(image);
            ProgressPercent = (double)(i + 1) / selectedImages.Count * 100;
        }

        StatusText = $"Synthesized img: {selectedImages.Count} image(s)  已合成 img：{selectedImages.Count} 个镜像";
        ProgressPercent = 100;
    }

    private async Task SynthesizeSingleImgAsync(ImageEntryViewModel image)
    {
        var name = GetEntryBaseName(image);
        try
        {
            var subProjectRoot = ProjectContextService.Instance.ActiveSubProject?.RootPath;
            if (subProjectRoot is null)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", "Synthesize img aborted: no active sub-project  合成 img 中止：无活动子项目");
                return;
            }

            var unpackDir = Path.Combine(subProjectRoot, name);
            var outputDir = ProjectContextService.Instance.EnsureOutputDirectory();
            var outputPath = Path.Combine(outputDir, name + "-synthesized.img");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Synthesizing boot image from {unpackDir}  正在从解包目录合成 boot 镜像");
            var result = await Task.Run(() => _firmware.PackBootAsync(unpackDir, outputPath));
            if (!result.Success)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", result.Summary);
                StatusText = $"Synthesize img failed  合成 img 失败";
                ProgressPercent = 0;
                return;
            }

            LogService.Instance.Write(LogLevel.Info, "Images", result.Summary);
            OperationHistoryService.Instance.Record(
                $"Synthesize img {image.DisplayName}",
                new HistoryOperationDto
                {
                    Kind = HistoryOperationKind.SynthesizeImg,
                    Description = $"Synthesize img {image.DisplayName}",
                    DisplayName = image.DisplayName,
                    OutputPaths = [outputPath]
                },
                () => DeleteFileSafe(outputPath),
                () => { });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Synthesize img failed: {ex.Message}  合成 img 失败：{ex.Message}");
            StatusText = $"Synthesize img failed: {ex.Message}  合成 img 失败：{ex.Message}";
            ProgressPercent = 0;
        }
    }

    /// <summary>
    /// <para>从 ramdisk（或解包）目录打包 CPIO 到 <c>intermediate/br/</c> 并登记历史。</para>
    /// Packs a CPIO archive from the ramdisk (or unpack) directory to <c>intermediate/br/</c> and records history.
    /// </summary>
    [RelayCommand]
    private async Task SynthesizeBrAsync()
    {
        var selectedImages = GetSelectedImages();
        if (selectedImages.Count == 0)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize br aborted: no project is open  合成 br 中止：尚未打开工程");
            return;
        }

        ProgressPercent = 0;
        for (var i = 0; i < selectedImages.Count; i++)
        {
            var image = selectedImages[i];
            StatusText = $"[{i + 1}/{selectedImages.Count}] Synthesizing CPIO: {image.DisplayName}  正在打包 CPIO：{image.DisplayName}";
            await SynthesizeSingleBrAsync(image);
            ProgressPercent = (double)(i + 1) / selectedImages.Count * 100;
        }

        StatusText = $"Synthesized CPIO: {selectedImages.Count} image(s)  已打包 CPIO：{selectedImages.Count} 个镜像";
        ProgressPercent = 100;
    }

    private async Task SynthesizeSingleBrAsync(ImageEntryViewModel image)
    {
        var name = GetEntryBaseName(image);
        try
        {
            var subProjectRoot = ProjectContextService.Instance.ActiveSubProject?.RootPath;
            if (subProjectRoot is null)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", "Synthesize br aborted: no active sub-project  合成 br 中止：无活动子项目");
                return;
            }

            var unpackDir = Path.Combine(subProjectRoot, name);
            // 优先使用 boot 解包产物的 ramdisk 子目录，否则使用解包目录本身。
            // Prefer the ramdisk sub-directory produced by a boot unpack, otherwise the unpack directory.
            var ramdiskDir = Path.Combine(unpackDir, "ramdisk");
            var inputDir = Directory.Exists(ramdiskDir) ? ramdiskDir : unpackDir;
            var outputDir = ProjectContextService.Instance.EnsureOutputDirectory();
            var outputPath = Path.Combine(outputDir, name + "-ramdisk.cpio");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Synthesizing cpio from {inputDir}  正在从 {inputDir} 打包 CPIO");
            var result = await Task.Run(() => _firmware.PackCpioAsync(inputDir, outputPath));
            if (!result.Success)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", result.Summary);
                StatusText = $"Synthesize br failed  合成 br 失败";
                ProgressPercent = 0;
                return;
            }

            LogService.Instance.Write(LogLevel.Info, "Images", result.Summary);
            OperationHistoryService.Instance.Record(
                $"Synthesize br {image.DisplayName}",
                new HistoryOperationDto
                {
                    Kind = HistoryOperationKind.SynthesizeBr,
                    Description = $"Synthesize br {image.DisplayName}",
                    DisplayName = image.DisplayName,
                    OutputPaths = [outputPath]
                },
                () => DeleteFileSafe(outputPath),
                () => { });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Synthesize br failed: {ex.Message}  合成 br 失败：{ex.Message}");
            StatusText = $"Synthesize br failed: {ex.Message}  合成 br 失败：{ex.Message}";
            ProgressPercent = 0;
        }
    }

    /// <summary>
    /// <para>将选中镜像从列表移除并把项目内文件移动到 <c>intermediate/deleted/</c>，登记可撤销历史（撤销移回）。</para>
    /// Removes the selected image from the list and moves its project file to
    /// <c>intermediate/deleted/</c>, recording reversible history (undo moves it back).
    /// </summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selectedImages = GetSelectedImages();
        if (selectedImages.Count == 0)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Delete aborted: no project is open  删除中止：尚未打开工程");
            return;
        }

        ProgressPercent = 0;
        for (var i = 0; i < selectedImages.Count; i++)
        {
            var image = selectedImages[i];
            StatusText = $"[{i + 1}/{selectedImages.Count}] Deleting {image.DisplayName}  正在删除 {image.DisplayName}";
            await DeleteImageAsync(image);
            ProgressPercent = (double)(i + 1) / selectedImages.Count * 100;
        }

        if (SelectedImage is not null && !Images.Contains(SelectedImage))
        {
            SelectedImage = null;
        }
    }

    private async Task DeleteImageAsync(ImageEntryViewModel image)
    {
        var source = GetSelectedSourcePath(image, "Delete");
        if (source is null)
        {
            return;
        }

        try
        {
            var subProjectRoot = ProjectContextService.Instance.ActiveSubProject?.RootPath;
            if (subProjectRoot is null)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", "Delete aborted: no active sub-project  删除中止：无活动子项目");
                return;
            }

            var deletedDir = Path.Combine(subProjectRoot, ".deleted");
            Directory.CreateDirectory(deletedDir);
            var destination = GetUniqueFileName(deletedDir, Path.GetFileName(source));
            await Task.Run(() => File.Move(source, destination));
            Images.Remove(image);
            LogService.Instance.Write(LogLevel.Info, "Images", $"Deleted {image.DisplayName} -> intermediate/deleted  已删除 {image.DisplayName}");
            OperationHistoryService.Instance.Record(
                $"Delete {image.DisplayName}",
                new HistoryOperationDto
                {
                    Kind = HistoryOperationKind.DeleteImage,
                    Description = $"Delete {image.DisplayName}",
                    DisplayName = image.DisplayName,
                    RelativePath = image.RelativePath,
                    DestinationPath = destination
                },
                () =>
                {
                    File.Move(destination, source);
                    Images.Add(image);
                },
                () =>
                {
                    File.Move(source, destination);
                    Images.Remove(image);
                });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Delete failed: {ex.Message}  删除失败：{ex.Message}");
        }
    }

    /// <summary>
    /// <para>使用 LP 元数据构建器与 sparse 写入器将项目内 raw/sparse 镜像合成为 super 镜像，输出到
    /// <c>intermediate/super/</c> 并登记历史。</para>
    /// Builds a super image from the project's raw/sparse images using the LP metadata builder and the
    /// sparse writer, outputting to <c>intermediate/super/</c> and recording history.
    /// </summary>
    [RelayCommand]
    private async Task SynthesizeSuperAsync()
    {
        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize super aborted: no project is open  合成 super 中止：尚未打开工程");
            return;
        }

        var settings = ProjectSettingViewModel.EnsureInstance();
        var partitions = new List<SuperPartitionInput>();
        foreach (var image in Images.Where(image => image.IsSelected))
        {
            if (image.Format is not ("raw" or "sparse"))
            {
                continue;
            }

            var path = ProjectContextService.Instance.ResolvePath(image.RelativePath);
            if (path is not null && File.Exists(path))
            {
                partitions.Add(new SuperPartitionInput(Path.GetFileNameWithoutExtension(image.RelativePath), path));
            }
        }

        if (partitions.Count == 0)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize super aborted: no raw/sparse images in project  合成 super 中止：项目中无 raw/sparse 镜像");
            return;
        }

        StatusText = $"Synthesizing super image ({partitions.Count} partitions)  正在合成 super 镜像（{partitions.Count} 个分区）";
        ProgressPercent = -1;
        try
        {
            var outputDir = ProjectContextService.Instance.EnsureOutputDirectory();
            var outputPath = Path.Combine(outputDir, "super.img");
            var deviceSize = ulong.TryParse(settings.SUPERSize, out var parsed) && parsed > 0 ? parsed : 9_126_805_504UL;
            LogService.Instance.Write(LogLevel.Info, "Images", $"Synthesizing super image with {partitions.Count} partition(s)  正在合成 super 镜像（{partitions.Count} 个分区）");
            var result = await _firmware.SynthesizeSuperAsync(partitions, outputPath, deviceSize, settings.SUPERName);
            if (!result.Success)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", result.Summary);
                StatusText = $"Synthesize super failed  合成 super 失败";
                ProgressPercent = 0;
                return;
            }

            LogService.Instance.Write(LogLevel.Info, "Images", result.Summary);
            StatusText = $"Synthesized super image  已合成 super 镜像";
            ProgressPercent = 100;
            OperationHistoryService.Instance.Record(
                "Synthesize super image",
                new HistoryOperationDto
                {
                    Kind = HistoryOperationKind.SynthesizeSuper,
                    Description = "Synthesize super image",
                    OutputPaths = [outputPath]
                },
                () => DeleteFileSafe(outputPath),
                () => { });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Synthesize super failed: {ex.Message}  合成 super 失败：{ex.Message}");
            StatusText = $"Synthesize super failed: {ex.Message}  合成 super 失败：{ex.Message}";
            ProgressPercent = 0;
        }
    }

    /// <summary>
    /// <para>把用户勾选的可刷写镜像（boot/raw/sparse）按顺序批量刷入当前选中设备：分区名优先取
    /// <see cref="ImageEntryViewModel"/> 已有分区字段，缺省用文件名去扩展名；经
    /// <see cref="BatchFlashService.FlashSelectedAsync"/> 逐分区复用 <see cref="FlashService.FlashImageAsync"/>
    /// 执行，单分区失败会继续，进度与结果回报到状态文本。</para>
    /// Flashes the user-selected flashable images (boot/raw/sparse) to the currently selected device
    /// in order: the partition name prefers an existing partition field on
    /// <see cref="ImageEntryViewModel"/>, falling back to the file name without its extension; each
    /// partition is flashed sequentially via <see cref="BatchFlashService.FlashSelectedAsync"/> which reuses
    /// <see cref="FlashService.FlashImageAsync"/>, a single-partition failure does not stop the rest,
    /// and progress/results are reported to the status text.
    /// </summary>
    [RelayCommand]
    private async Task FlashSelectedAsync()
    {
        ProgressPercent = 0;

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Flash selected aborted: no project is open  选中文件刷入中止：尚未打开工程");
            return;
        }

        var device = DeviceViewModel.Instance?.SelectedDevice;
        if (device is null)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Flash selected aborted: no device selected  选中文件刷入中止：未选中设备");
            return;
        }

        var entries = new List<(string Partition, string ImagePath)>();
        foreach (var image in Images.Where(image => image.IsSelected))
        {
            if (image.Format is not ("boot" or "raw" or "sparse"))
            {
                continue;
            }

            var path = ProjectContextService.Instance.ResolvePath(image.RelativePath);
            if (path is null || !File.Exists(path))
            {
                LogService.Instance.Write(LogLevel.Warn, "Images", $"Flash selected skipped missing file: {image.RelativePath}  选中文件刷入跳过缺失文件：{image.RelativePath}");
                continue;
            }

            entries.Add((GetEntryBaseName(image), path));
        }

        if (entries.Count == 0)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Flash selected aborted: no flashable images selected  选中文件刷入中止：未勾选可刷写镜像");
            return;
        }

        StatusText = $"Flashing {entries.Count} image(s)  正在刷入 {entries.Count} 个镜像";
        try
        {
            var progress = new Progress<FlashProgress>(fp =>
            {
                var percent = Math.Clamp(fp.Percent, 0, 100);
                ProgressPercent = percent;
                StatusText = $"[{percent}%] {fp.Status}";
            });
            var result = await BatchFlashService.FlashSelectedAsync(device, entries, progress, CancellationToken.None).ConfigureAwait(true);
            if (result.Success)
            {
                ProgressPercent = 100;
            }
            StatusText = result.Summary;
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Warn, "Images", result.Summary);
        }
        catch (Exception ex)
        {
            ProgressPercent = 0;
            StatusText = $"Flash selected failed: {ex.Message}  选中文件刷入失败：{ex.Message}";
            LogService.Instance.Write(LogLevel.Error, "Images", $"Flash selected failed: {ex.Message}  选中文件刷入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// <para>使用 <see cref="DatWriter"/> 将选中镜像生成为 <c>.new.dat</c> 与 <c>.transfer.list</c>，
    /// 输出到 <c>intermediate/dat/</c> 并登记历史。</para>
    /// Uses <see cref="DatWriter"/> to generate a <c>.new.dat</c> and a <c>.transfer.list</c> from the
    /// selected image, outputting to <c>intermediate/dat/</c> and recording history.
    /// </summary>
    [RelayCommand]
    private async Task SynthesizeDatAsync()
    {
        var selectedImages = GetSelectedImages();
        if (selectedImages.Count == 0)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize dat aborted: no project is open  合成 dat 中止：尚未打开工程");
            return;
        }

        ProgressPercent = 0;
        for (var i = 0; i < selectedImages.Count; i++)
        {
            var image = selectedImages[i];
            StatusText = $"[{i + 1}/{selectedImages.Count}] Synthesizing DAT: {image.DisplayName}  正在生成 DAT：{image.DisplayName}";
            await SynthesizeSingleDatAsync(image);
            ProgressPercent = (double)(i + 1) / selectedImages.Count * 100;
        }

        StatusText = $"Synthesized DAT: {selectedImages.Count} image(s)  已生成 DAT：{selectedImages.Count} 个镜像";
        ProgressPercent = 100;
    }

    private async Task SynthesizeSingleDatAsync(ImageEntryViewModel image)
    {
        var source = GetSelectedSourcePath(image, "Synthesize dat");
        if (source is null)
        {
            return;
        }

        var name = GetEntryBaseName(image);
        try
        {
            var outputDir = ProjectContextService.Instance.EnsureOutputDirectory();
            var datPath = Path.Combine(outputDir, name + ".new.dat");
            var listPath = Path.Combine(outputDir, name + ".transfer.list");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Synthesizing DAT: {image.RelativePath}  正在生成 DAT：{image.RelativePath}");

            // sparse 输入先解为 raw 再生成 DAT。
            // Sparse inputs are first converted to raw before generating the DAT output.
            var rawSource = source;
            if (image.Format == "sparse")
            {
                var converted = await ConvertToRawAsync(source, outputDir, name);
                if (!converted.Success)
                {
                    LogService.Instance.Write(LogLevel.Error, "Images", converted.Summary);
                    return;
                }

                rawSource = Path.Combine(outputDir, name + ".raw");
            }

            await Task.Run(() => DatWriter.Write(rawSource, datPath, listPath));
            LogService.Instance.Write(LogLevel.Info, "Images", $"Generated {datPath} + {listPath}  已生成 DAT 输出");
            OperationHistoryService.Instance.Record(
                $"Synthesize dat {image.DisplayName}",
                new HistoryOperationDto
                {
                    Kind = HistoryOperationKind.SynthesizeDat,
                    Description = $"Synthesize dat {image.DisplayName}",
                    DisplayName = image.DisplayName,
                    OutputPaths = [datPath, listPath]
                },
                () =>
                {
                    DeleteFileSafe(datPath);
                    DeleteFileSafe(listPath);
                },
                () => { });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Synthesize dat failed: {ex.Message}  合成 dat 失败：{ex.Message}");
            StatusText = $"Synthesize dat failed: {ex.Message}  合成 dat 失败：{ex.Message}";
            ProgressPercent = 0;
        }
    }

    private static string? GetSelectedSourcePath(ImageEntryViewModel image, string operation)
    {
        if (image is null)
        {
            return null;
        }

        var source = ProjectContextService.Instance.ResolvePath(image.RelativePath);
        if (source is null)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", $"{operation} aborted: path not found: {image.RelativePath}  {operation}中止：找不到路径：{image.RelativePath}");
            return null;
        }

        // 目录条目（打包模式）检查 Directory.Exists，文件条目检查 File.Exists。
        // Directory entries (pack mode) check Directory.Exists; file entries check File.Exists.
        if (image.IsDirectory ? !Directory.Exists(source) : !File.Exists(source))
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", $"{operation} aborted: not found: {image.RelativePath}  {operation}中止：找不到：{image.RelativePath}");
            return null;
        }

        return source;
    }

    private IReadOnlyList<ImageEntryViewModel> GetSelectedImages()
    {
        return Images.Where(image => image.IsSelected).ToArray();
    }

    private static string GetEntryBaseName(ImageEntryViewModel image)
    {
        var name = Path.GetFileNameWithoutExtension(image.RelativePath);
        return string.IsNullOrWhiteSpace(name) ? image.DisplayName : name;
    }

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
    /// 级联解出内部文件系统内容；未命中则回退为原 raw 字节流复制。
    /// （魔数判定读 1082 字节头部，仿照 <c>FileManagerEditorViewModel.DetectFilesystem</c>：
    /// ext4 0xEF53@1080、erofs 0xE0F5E1E2@1024、f2fs 0xF2F52010@1024。）</para>
    /// Extracts a raw physical image: probes the magic for an ext4/erofs/f2fs filesystem image and
    /// cascades into its contents with the matching parser when matched; otherwise falls back to
    /// copying the raw byte stream. (Magic detection reads a 1082-byte header, mirroring
    /// FileManagerEditorViewModel.DetectFilesystem: ext4 0xEF53@1080, erofs 0xE0F5E1E2@1024,
    /// f2fs 0xF2F52010@1024.)
    /// </summary>
    private static async Task<FirmwareOpResult> ExtractRawAsync(string sourcePath, string outputDir, string name, IProgress<(int processed, int total)>? progress = null)
    {
        // 读取 1082 字节头部：ext4 魔数 0xEF53 位于 1080、erofs/f2fs 魔数位于 1024。
        // Read a 1082-byte header: ext4 magic 0xEF53 at 1080, erofs/f2fs magic at 1024.
        var header = FormatDetector.ReadHeader(sourcePath, 1082);

        // ext4 文件系统镜像 → 级联解出内部文件系统内容。
        // ext4 filesystem image → cascade into the filesystem contents.
        if (header.Length >= 1082
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1080, 2)) == 0xEF53)
        {
            return await Task.Run(() =>
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
        }

        // erofs 文件系统镜像 → 级联解出内部文件系统内容。
        // erofs filesystem image → cascade into the filesystem contents.
        if (header.Length >= 1028
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xE0F5E1E2)
        {
            return await Task.Run(() =>
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
        }

        // f2fs 文件系统镜像 → 级联解出内部文件系统内容。
        // f2fs filesystem image → cascade into the filesystem contents.
        if (header.Length >= 1028
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xF2F52010)
        {
            return await Task.Run(() =>
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
        }

        // 未命中文件系统魔数 → 保留原 raw 复制行为（按字节流落盘 .raw）。
        // No filesystem magic matched → keep the original raw-copy behavior (write the byte stream as .raw).
        return await ConvertToRawAsync(sourcePath, outputDir, name);
    }

    private static string GetUniqueFileName(string directory, string fileName)
    {
        var candidate = fileName;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 1;
        while (File.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{baseName}_{index}{extension}";
            index++;
        }

        return candidate;
    }

    private static string DetectFormat(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        switch (extension)
        {
            case ".cpio":
                return "cpio";
            case ".ofp":
            case ".ops":
            case ".ozip":
                return "oppo";
            case ".simg":
            case ".sparse":
                return "sparse";
        }

        try
        {
            // 读取 1082 字节头部：ext4 魔数 0xEF53 位于 1080、erofs/f2fs 魔数位于 1024。
            // Read a 1082-byte header: ext4 magic 0xEF53 at 1080, erofs/f2fs magic at 1024.
            var header = FormatDetector.ReadHeader(filePath, 1082);
            if (header.Length >= 4)
            {
                // Android sparse 魔数 0xED26FF3A（小端）。
                // Android sparse magic 0xED26FF3A (little-endian).
                if (header[0] == 0x3A && header[1] == 0xFF && header[2] == 0x26 && header[3] == 0xED)
                {
                    return "sparse";
                }

                if (Encoding.ASCII.GetString(header, 0, 4) == "CrAU")
                {
                    return "payload";
                }
            }

            if (header.Length >= 8)
            {
                var magic = Encoding.ASCII.GetString(header, 0, 8);
                if (magic.StartsWith("ANDROID!", StringComparison.Ordinal) || magic.StartsWith("VNDRBOOT", StringComparison.Ordinal))
                {
                    return "boot";
                }
            }

            // ext4 文件系统镜像：魔数 0xEF53 位于偏移 1080。
            // ext4 filesystem image: magic 0xEF53 at offset 1080.
            if (header.Length >= 1082
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1080, 2)) == 0xEF53)
            {
                return "ext4";
            }

            // erofs 文件系统镜像：魔数 0xE0F5E1E2 位于偏移 1024。
            // erofs filesystem image: magic 0xE0F5E1E2 at offset 1024.
            if (header.Length >= 1028
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xE0F5E1E2)
            {
                return "erofs";
            }

            // f2fs 文件系统镜像：魔数 0xF2F52010 位于偏移 1024。
            // f2fs filesystem image: magic 0xF2F52010 at offset 1024.
            if (header.Length >= 1028
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xF2F52010)
            {
                return "f2fs";
            }
        }
        catch
        {
            // 头部读取失败时回退到扩展名推断。
            // Falls back to extension-based inference when the header cannot be read.
        }

        if (extension == ".img")
        {
            return "raw";
        }

        return extension == ".bin" && Path.GetFileName(filePath).Contains("payload", StringComparison.OrdinalIgnoreCase) ? "payload" : "unknown";
    }

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

    /// <summary>
    /// <para>手动刷新镜像列表：重新扫描工程 images/ 目录并注册已知格式镜像。</para>
    /// Manually refreshes the image list: re-scans the project images/ directory and registers known-format images.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        StatusText = "Refreshing images  正在刷新镜像列表";
        ProgressPercent = -1;
        await RefreshProjectImagesAsync();
        StatusText = $"Refreshed: {Images.Count} image(s)  已刷新：{Images.Count} 个镜像";
        ProgressPercent = 0;
    }

}

/// <summary>
/// <para>镜像条目视图模型，承载镜像的显示名称、项目内相对路径、大小、格式与选中状态。</para>
/// Image entry view model, carrying the image's display name, project-relative path, size, format and selection state.
/// </summary>
public sealed partial class ImageEntryViewModel : ObservableObject
{
    /// <summary>
    /// <para>获取或设置显示名称。</para>
    /// Gets or sets the display name.
    /// </summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>
    /// <para>获取或设置相对工程根目录的镜像路径。</para>
    /// Gets or sets the image path relative to the project root directory.
    /// </summary>
    [ObservableProperty]
    private string _relativePath = string.Empty;

    /// <summary>
    /// <para>获取或设置镜像文件大小（字节）。</para>
    /// Gets or sets the image file size in bytes.
    /// </summary>
    [ObservableProperty]
    private long _sizeBytes;

    /// <summary>
    /// <para>获取自动换算到最大合适单位的文件大小。</para>
    /// Gets the file size converted to the largest suitable unit.
    /// </summary>
    public string DisplaySize => FormatSize(SizeBytes);

    partial void OnSizeBytesChanged(long value) => OnPropertyChanged(nameof(DisplaySize));

    /// <summary>
    /// <para>获取或设置镜像格式标识（boot/payload/cpio/oppo/sparse/raw/unknown）。</para>
    /// Gets or sets the image format identifier (boot/payload/cpio/oppo/sparse/raw/unknown).
    /// </summary>
    [ObservableProperty]
    private string _format = "unknown";

    /// <summary>
    /// <para>获取或设置是否加入批量操作选择集。</para>
    /// Gets or sets whether the entry is included in the batch-operation selection.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// <para>获取或设置此条目是否通过当前筛选条件（供 DataGrid 行可见性绑定）。</para>
    /// Gets or sets whether this entry passes the current filter (bound to DataGrid row visibility).
    /// </summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>
    /// <para>获取或设置此镜像是否已解包（子项目根目录下存在同名目录）。</para>
    /// Gets or sets whether this image has been unpacked (a same-name directory exists at the sub-project root).
    /// </summary>
    [ObservableProperty]
    private bool _isUnpacked;

    /// <summary>
    /// <para>获取或设置此条目是否为目录（打包模式下的解包产物文件夹）。</para>
    /// Gets or sets whether this entry is a directory (unpacked result folder in pack mode).
    /// </summary>
    [ObservableProperty]
    private bool _isDirectory;

    private static string FormatSize(long bytes)
    {
        ReadOnlySpan<string> units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var size = (double)Math.Max(0, bytes);
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024d;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}
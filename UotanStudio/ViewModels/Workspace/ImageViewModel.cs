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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    private bool _isOperating;

    internal bool CanOperate => !IsOperating;

    private int _refreshVersion;


    private async Task<bool> RequestExtractOverwriteAsync(string directoryName)
    {
        var owner = TopLevelExtensions.GetTopLevel() as Avalonia.Controls.Window;
        if (owner is null) return false;
        var language = LanguageService.Instance;
        return await DialogService.ConfirmAsync(owner, language.GetString("Image_ExtractOverwriteTitle"),
            string.Format(language.GetString("Image_ExtractOverwriteMessage"), directoryName),
            language.GetString("Image_ExtractOverwrite"), language.GetString("Image_ExtractSkip"));
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
    public bool IsPackMode
    {
        get => _mode == ImageMode.Pack;
        set { if (value) SetMode(ImageMode.Pack); }
    }

    /// <summary>
    /// <para>获取当前是否显示解包操作；设置为 true 时切换到解包模式。</para>
    /// Gets whether unpack actions are currently shown; setting this to true switches to unpack mode.
    /// </summary>
    public bool IsUnpackMode
    {
        get => _mode == ImageMode.Unpack;
        set { if (value) SetMode(ImageMode.Unpack); }
    }

    /// <summary>
    /// <para>获取当前是否显示输出镜像；设置为 true 时切换到输出模式。</para>
    /// Gets whether output images are shown; setting this to true switches to output mode.
    /// </summary>
    public bool IsOutputMode
    {
        get => _mode == ImageMode.Output;
        set { if (value) SetMode(ImageMode.Output); }
    }

    private enum ImageMode { Unpack, Pack, Output }

    private ImageMode _mode;

    private void SetMode(ImageMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        OnPropertyChanged(nameof(IsUnpackMode));
        OnPropertyChanged(nameof(IsPackMode));
        OnPropertyChanged(nameof(IsOutputMode));
        _ = RefreshProjectImagesAsync();
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
        if (IsOperating || string.IsNullOrEmpty(value))
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
    /// 打包模式扫描子项目根目录下的解包产物文件夹，输出模式扫描 <c>output/</c> 镜像。未打开工程时清空列表。</para>
    /// Scans project entries based on the current mode: in unpack mode scans <c>images/</c> for
    /// unpackable files; in pack mode scans the sub-project root for unpacked result directories;
    /// in output mode scans <c>output/</c> for images.
    /// Clears the list when no project is open.
    /// </summary>
    private async Task RefreshProjectImagesAsync()
    {
        var version = ++_refreshVersion;
        var context = ProjectContextService.Instance;
        Images.Clear();
        SelectedImage = null;

        if (!context.IsProjectOpen)
        {
            return;
        }

        RebuildSubProjectItems();

        try
        {
            if (IsPackMode) await RefreshPackEntriesAsync(context, version);
            else await RefreshFileEntriesAsync(context, version, IsOutputMode);
        }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Images", $"Refresh failed: {ex.Message}"); }
    }

    /// <summary>
    /// <para>扫描 <c>images/</c> 或 <c>output/</c> 目录；输出模式同时包含 raw 镜像。</para>
    /// Scans <c>images/</c> or <c>output/</c>; output mode also includes raw images.
    /// </summary>
    private async Task RefreshFileEntriesAsync(ProjectContextService context, int version, bool outputMode)
    {
        var root = context.RootDirectory;
        if (root is null) return;
        var imagesDir = outputMode ? context.ActiveSubProject?.OutputDir : context.ActiveSubProject?.ImagesDir;
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
                if (format == "unknown" || (!outputMode && format == "raw"))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative is not null)
                {
                    list.Add((relative, new FileInfo(file).Length, format));
                }
            }
            return list;
        }).ConfigureAwait(true);

        foreach (var (relative, size, format) in discovered)
        {
            if (version != _refreshVersion) return;
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
    private async Task RefreshPackEntriesAsync(ProjectContextService context, int version)
    {
        var root = context.RootDirectory;
        if (root is null) return;
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

                var relative = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
                if (relative is not null)
                {
                    list.Add((name, relative));
                }
            }
            return list;
        }).ConfigureAwait(true);

        foreach (var (name, relative) in discovered)
        {
            if (version != _refreshVersion) return;
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
        await Task.Run(() => File.Copy(sourcePath, destination)).ConfigureAwait(true);
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
            Format = DetectFormat(absolutePath)
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
            await Task.Run(() => File.Copy(source, targetPath, overwrite: true));
            LogService.Instance.Write(LogLevel.Info, "Images", $"Exported {image.DisplayName} -> {targetPath}  已导出 {image.DisplayName}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Export failed: {ex.Message}  导出失败：{ex.Message}");
        }
    }

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
                Format = pack ? "folder" : DetectFormat(path)
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
                var itemBase = (double)index / requests.Length * 100;
                var itemSlice = 100.0 / requests.Length;
                var itemProgress = new Progress<double>(p => ProgressPercent = Math.Max(ProgressPercent, itemBase + Math.Clamp(p, 0, 99.9) / 100 * itemSlice));
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
                var itemBase = (double)done / Math.Max(1, total) * 100;
                var itemSlice = 100.0 / Math.Max(1, total);
                var itemProgress = new Progress<double>(p => ProgressPercent = Math.Max(ProgressPercent, Math.Min(99.9, itemBase + Math.Clamp(p, 0, 99.9) / 100 * itemSlice)));
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
            var format = DetectFormat(file);
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


    /// <summary>
    /// <para>将选中镜像从列表移除并把项目内文件移动到 <c>intermediate/deleted/</c>，登记可撤销历史（撤销移回）。</para>
    /// Removes the selected image from the list and moves its project file to
    /// <c>intermediate/deleted/</c>, recording reversible history (undo moves it back).
    /// </summary>
    [RelayCommand]
    private Task DeleteSelectedAsync() => RunExclusiveAsync(DeleteSelectedCoreAsync);

    private async Task RunExclusiveAsync(Func<Task> operation)
    {
        if (IsOperating) return;
        IsOperating = true;
        try { await operation(); }
        catch (Exception ex) { LogService.Instance.Write(LogLevel.Error, "Images", ex.Message); }
        finally { IsOperating = false; }
    }

    private async Task DeleteSelectedCoreAsync()
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

    internal async Task DeleteImageAsync(ImageEntryViewModel image)
    {
        if (image.IsDirectory)
        {
            await DeleteUnpackedDirectoryAsync(image);
            return;
        }

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
            var destination = Path.Combine(deletedDir, GetUniqueFileName(deletedDir, Path.GetFileName(source)));
            await Task.Run(() => File.Move(source, destination));
            Images.Remove(image);
            if (ReferenceEquals(SelectedImage, image)) SelectedImage = null;
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

    internal async Task DeleteUnpackedDirectoryAsync(ImageEntryViewModel image)
    {
        var owner = TopLevelExtensions.GetTopLevel() as Avalonia.Controls.Window;
        var context = ProjectContextService.Instance;
        var root = context.ActiveSubProject?.RootPath;
        if (owner is null || root is null)
        {
            return;
        }

        var language = LanguageService.Instance;
        try
        {
            var source = context.ResolvePath(image.RelativePath) ?? throw new IOException("Invalid project path.");
            var directory = new ImageOperationPaths(source, image.IsDirectory, root, root).UnpackedDirectory;

            if (!Directory.Exists(directory))
            {
                await DialogService.ShowAsync(owner, language.GetString("Image_UnpackedDirectoryMissingTitle"),
                    string.Format(language.GetString("Image_UnpackedDirectoryMissingMessage"), directory), Ursa.Controls.MessageBoxIcon.Information);
                return;
            }

            if (!await DialogService.ConfirmAsync(owner, language.GetString("Dialog_ConfirmDelete"),
                    string.Format(language.GetString("Dialog_ConfirmDeleteMessage"), directory),
                    language.GetString("Menu_Delete")))
            {
                return;
            }

            await Task.Run(() => Directory.Delete(directory, recursive: true));
            image.IsUnpacked = false;
            if (image.IsDirectory)
            {
                Images.Remove(image);
                if (ReferenceEquals(SelectedImage, image)) SelectedImage = null;
            }
            StatusText = language.GetString("ImageView_OperationDone");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Deleted unpacked directory: {directory}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Delete unpacked directory failed: {ex.Message}");
            await DialogService.ShowAsync(owner, language.GetString("Dialog_DeleteFailed"), ex.Message, Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// <para>使用 LP 元数据构建器与 sparse 写入器将项目内 raw/sparse 镜像合成为 super 镜像，输出到
    /// <c>intermediate/super/</c> 并登记历史。</para>
    /// Builds a super image from the project's raw/sparse images using the LP metadata builder and the
    /// sparse writer, outputting to <c>intermediate/super/</c> and recording history.
    /// </summary>
    [RelayCommand]
    private Task SynthesizeSuperAsync() => RunExclusiveAsync(SynthesizeSuperCoreAsync);

    private async Task SynthesizeSuperCoreAsync()
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
    /// <para>把右键条目或用户勾选的可刷写镜像（boot/raw/sparse/ext4/erofs/f2fs）刷入当前选中设备：分区名优先取
    /// <see cref="ImageEntryViewModel"/> 已有分区字段，缺省用文件名去扩展名；经
    /// <see cref="BatchFlashService.FlashSelectedAsync"/> 逐分区复用 <see cref="FlashService.FlashImageAsync"/>
    /// 执行，单分区失败会继续，进度与结果回报到状态文本。</para>
    /// Flashes the clicked or checked flashable images (boot/raw/sparse/ext4/erofs/f2fs) to the currently selected device
    /// in order: the partition name prefers an existing partition field on
    /// <see cref="ImageEntryViewModel"/>, falling back to the file name without its extension; each
    /// partition is flashed sequentially via <see cref="BatchFlashService.FlashSelectedAsync"/> which reuses
    /// <see cref="FlashService.FlashImageAsync"/>, a single-partition failure does not stop the rest,
    /// and progress/results are reported to the status text.
    /// </summary>
    [RelayCommand]
    private Task FlashSelectedAsync(ImageEntryViewModel? image = null) => RunExclusiveAsync(() => FlashSelectedCoreAsync(image));

    private async Task FlashSelectedCoreAsync(ImageEntryViewModel? clicked)
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
        var selected = clicked is null ? GetSelectedImages() : [clicked];
        foreach (var image in selected)
        {
            if (image.IsDirectory || image.Format is not ("boot" or "raw" or "sparse" or "ext4" or "erofs" or "f2fs"))
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

    /// <summary>Builds DAT output for the clicked or checked entries. 为右键或勾选条目构建 DAT。</summary>
    [RelayCommand]
    private Task SynthesizeDatAsync(ImageEntryViewModel? image = null) => RunImagesAsync("dat", image);

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

    internal static string GetEntryBaseName(ImageEntryViewModel image)
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

        // erofs 文件系统镜像 → 级联解出内部文件系统内容。
        // erofs filesystem image → cascade into the filesystem contents.
        if (header.Length >= 1028
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xE0F5E1E2)
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

        // f2fs 文件系统镜像 → 级联解出内部文件系统内容。
        // f2fs filesystem image → cascade into the filesystem contents.
        if (header.Length >= 1028
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xF2F52010)
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

        if (extension is ".img" or ".raw")
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

/// <summary>
/// <para>把固件库 <see cref="UotanStudio.Firmware.SharedKernel.IFirmwareProgress"/> 的百分比回调
/// 适配为条目内 <see cref="IProgress{Double}"/>，供 <see cref="UnpackOptions.Progress"/> 使用。</para>
/// Adapts the firmware library's <see cref="UotanStudio.Firmware.SharedKernel.IFirmwareProgress"/>
/// percent callbacks to an in-item <see cref="IProgress{Double}"/> for <see cref="UnpackOptions.Progress"/>.
/// </summary>
internal sealed class FirmwareItemProgress(IProgress<double> itemProgress) : UotanStudio.Firmware.SharedKernel.IFirmwareProgress
{
    /// <inheritdoc />
    public void Report(double percent, string message) => itemProgress.Report(percent);
}

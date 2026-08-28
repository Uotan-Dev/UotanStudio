using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Dat;
using UotanStudio.Firmware.Sparse;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;

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
    /// <summary>
    /// <para>获取当前镜像面板单例实例，供主窗口与工作区停靠面板共享。</para>
    /// Gets the active image panel singleton, shared by the workspace dock and the top file menu.
    /// </summary>
    public static ImageViewModel? Instance { get; private set; }

    /// <summary>
    /// <para>确保镜像面板单例已创建并返回它；首次调用时构造并在构造函数中注册到 <see cref="Instance"/>，
    /// 后续调用复用同一实例。</para>
    /// Ensures the image panel singleton exists and returns it; the first call constructs the instance
    /// (registered on <see cref="Instance"/> in the constructor) and later calls reuse the same instance.
    /// </summary>
    internal static ImageViewModel EnsureInstance() => Instance ??= new();

    private readonly FirmwareService _firmware = FirmwareService.Instance;

    /// <summary>
    /// <para>获取项目内镜像条目集合，条目路径为相对工程根目录。</para>
    /// Gets the collection of image entries in the project; entry paths are relative to the project root.
    /// </summary>
    public ObservableCollection<ImageEntryViewModel> Images { get; } = [];

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
    /// <para>创建镜像面板视图模型并注册单例实例。</para>
    /// Creates the image panel view model and registers the singleton instance.
    /// </summary>
    public ImageViewModel()
    {
        Instance = this;
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
    /// <para>扫描当前工程 <c>images/</c> 目录，把已知镜像格式的文件自动注册到镜像列表；
    /// 未打开工程或目录不存在时清空列表。已存在条目跳过，避免重复。</para>
    /// Scans the current project's <c>images/</c> directory, auto-registering files with known image
    /// formats; clears the list when no project is open or the directory is missing. Existing entries
    /// are skipped to avoid duplicates.
    /// </summary>
    private async Task RefreshProjectImagesAsync()
    {
        var context = ProjectContextService.Instance;
        Images.Clear();

        if (!context.IsProjectOpen || string.IsNullOrEmpty(context.ImagesDirectory) || !Directory.Exists(context.ImagesDirectory))
        {
            return;
        }

        // 目录枚举与格式探测在后台线程执行，避免大目录/大文件头读取阻塞 UI。
        // Directory enumeration and format probing run on a background thread so large folders or
        // header reads never block the UI.
        var discovered = await Task.Run(() =>
        {
            var list = new List<(string Relative, long Size, string Format)>();
            foreach (var file in Directory.EnumerateFiles(context.ImagesDirectory))
            {
                var format = DetectFormat(file);
                if (format == "unknown")
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
                return (() => { }, () => { });
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
        if (!context.IsProjectOpen)
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

                imported.Add(new ImageEntryViewModel
                {
                    DisplayName = Path.GetFileName(destination),
                    RelativePath = relative,
                    SizeBytes = new FileInfo(destination).Length,
                    Format = DetectFormat(destination)
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
        var image = SelectedImage;
        if (image is null)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Extract aborted: no project is open  分解中止：尚未打开工程");
            return;
        }

        var source = GetSelectedSourcePath(image, "Extract");
        if (source is null)
        {
            return;
        }

        var name = GetEntryBaseName(image);
        try
        {
            var outputDir = ProjectContextService.Instance.EnsureIntermediateDirectory(name);
            LogService.Instance.Write(LogLevel.Info, "Images", $"Extracting {image.Format}: {image.RelativePath}  正在分解 {image.Format}：{image.RelativePath}");
            var result = image.Format switch
            {
                "boot" => await Task.Run(() => _firmware.UnpackBootAsync(source, outputDir)),
                "payload" => await Task.Run(() => _firmware.ExtractPayloadAsync(source, outputDir)),
                "cpio" => await Task.Run(() => _firmware.UnpackCpioAsync(source, outputDir)),
                "oppo" => await Task.Run(() => _firmware.ExtractOppoAsync(source, outputDir)),
                "sparse" or "raw" => await ConvertToRawAsync(source, outputDir, name),
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
        var image = SelectedImage;
        if (image is null)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize img aborted: no project is open  合成 img 中止：尚未打开工程");
            return;
        }

        var name = GetEntryBaseName(image);
        try
        {
            var unpackDir = ProjectContextService.Instance.EnsureIntermediateDirectory(name);
            var outputDir = ProjectContextService.Instance.EnsureIntermediateDirectory("img");
            var outputPath = Path.Combine(outputDir, name + "-synthesized.img");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Synthesizing boot image from {unpackDir}  正在从解包目录合成 boot 镜像");
            var result = await Task.Run(() => _firmware.PackBootAsync(unpackDir, outputPath));
            if (!result.Success)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", result.Summary);
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
        }
    }

    /// <summary>
    /// <para>按打包设置面板的当前设置，从选中镜像的解包目录重新打包系统分区镜像
    /// （ext4/erofs，可选 sparse）到 <c>intermediate/img/</c> 并登记历史。</para>
    /// Repacks the selected image's unpack directory into a system-partition image (ext4/erofs,
    /// optionally sparse) according to the current packing settings panel, outputting to
    /// <c>intermediate/img/</c> and recording history.
    /// </summary>
    [RelayCommand]
    private async Task PackImageAsync()
    {
        var image = SelectedImage;
        if (image is null)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Pack image aborted: no project is open  打包镜像中止：尚未打开工程");
            return;
        }

        var name = GetEntryBaseName(image);
        try
        {
            var unpackDir = ProjectContextService.Instance.EnsureIntermediateDirectory(name);
            if (!Directory.Exists(unpackDir) || !Directory.EnumerateFileSystemEntries(unpackDir).Any())
            {
                LogService.Instance.Write(LogLevel.Warn, "Images", $"Pack image aborted: unpack directory is empty ({unpackDir})  打包镜像中止：解包目录为空（{unpackDir}）");
                return;
            }

            var outputDir = ProjectContextService.Instance.EnsureIntermediateDirectory("img");
            var outputPath = Path.Combine(outputDir, name + "-packed.img");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Packing system image from {unpackDir}  正在打包系统镜像（源：{unpackDir}）");
            var result = await PackingService.PackImageAsync(unpackDir, outputPath);
            if (!result.Success)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", result.Summary);
                return;
            }

            LogService.Instance.Write(LogLevel.Info, "Images", result.Summary);
            OperationHistoryService.Instance.Record(
                $"Pack image {image.DisplayName}",
                new HistoryOperationDto
                {
                    Kind = HistoryOperationKind.SynthesizeImg,
                    Description = $"Pack image {image.DisplayName}",
                    DisplayName = image.DisplayName,
                    OutputPaths = [outputPath]
                },
                () => DeleteFileSafe(outputPath),
                () => { });
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Images", $"Pack image failed: {ex.Message}  打包镜像失败：{ex.Message}");
        }
    }

    /// <summary>
    /// <para>从 ramdisk（或解包）目录打包 CPIO 到 <c>intermediate/br/</c> 并登记历史。</para>
    /// Packs a CPIO archive from the ramdisk (or unpack) directory to <c>intermediate/br/</c> and records history.
    /// </summary>
    [RelayCommand]
    private async Task SynthesizeBrAsync()
    {
        var image = SelectedImage;
        if (image is null)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize br aborted: no project is open  合成 br 中止：尚未打开工程");
            return;
        }

        var name = GetEntryBaseName(image);
        try
        {
            var unpackDir = ProjectContextService.Instance.EnsureIntermediateDirectory(name);
            // 优先使用 boot 解包产物的 ramdisk 子目录，否则使用解包目录本身。
            // Prefer the ramdisk sub-directory produced by a boot unpack, otherwise the unpack directory.
            var ramdiskDir = Path.Combine(unpackDir, "ramdisk");
            var inputDir = Directory.Exists(ramdiskDir) ? ramdiskDir : unpackDir;
            var outputDir = ProjectContextService.Instance.EnsureIntermediateDirectory("br");
            var outputPath = Path.Combine(outputDir, name + "-ramdisk.cpio");
            LogService.Instance.Write(LogLevel.Info, "Images", $"Synthesizing cpio from {inputDir}  正在从 {inputDir} 打包 CPIO");
            var result = await Task.Run(() => _firmware.PackCpioAsync(inputDir, outputPath));
            if (!result.Success)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", result.Summary);
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
        var image = SelectedImage;
        if (image is null)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Delete aborted: no project is open  删除中止：尚未打开工程");
            return;
        }

        var source = GetSelectedSourcePath(image, "Delete");
        if (source is null)
        {
            return;
        }

        try
        {
            var deletedDir = ProjectContextService.Instance.EnsureIntermediateDirectory("deleted");
            var destination = GetUniqueFileName(deletedDir, Path.GetFileName(source));
            await Task.Run(() => File.Move(source, destination));
            Images.Remove(image);
            SelectedImage = null;
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
        foreach (var image in Images)
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

        try
        {
            var outputDir = ProjectContextService.Instance.EnsureIntermediateDirectory("super");
            var outputPath = Path.Combine(outputDir, "super.img");
            var deviceSize = ulong.TryParse(settings.SUPERSize, out var parsed) && parsed > 0 ? parsed : 9_126_805_504UL;
            LogService.Instance.Write(LogLevel.Info, "Images", $"Synthesizing super image with {partitions.Count} partition(s)  正在合成 super 镜像（{partitions.Count} 个分区）");
            var result = await _firmware.SynthesizeSuperAsync(partitions, outputPath, deviceSize, settings.SUPERName);
            if (!result.Success)
            {
                LogService.Instance.Write(LogLevel.Error, "Images", result.Summary);
                return;
            }

            LogService.Instance.Write(LogLevel.Info, "Images", result.Summary);
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
        var image = SelectedImage;
        if (image is null)
        {
            return;
        }

        if (!ProjectContextService.Instance.IsProjectOpen)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", "Synthesize dat aborted: no project is open  合成 dat 中止：尚未打开工程");
            return;
        }

        var source = GetSelectedSourcePath(image, "Synthesize dat");
        if (source is null)
        {
            return;
        }

        var name = GetEntryBaseName(image);
        try
        {
            var outputDir = ProjectContextService.Instance.EnsureIntermediateDirectory("dat");
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
        }
    }

    private static string? GetSelectedSourcePath(ImageEntryViewModel image, string operation)
    {
        if (image is null)
        {
            return null;
        }

        var source = ProjectContextService.Instance.ResolvePath(image.RelativePath);
        if (source is null || !File.Exists(source))
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", $"{operation} aborted: file not found: {image.RelativePath}  {operation}中止：找不到文件：{image.RelativePath}");
            return null;
        }

        return source;
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
            var header = new byte[8];
            using var fs = File.OpenRead(filePath);
            var read = fs.Read(header, 0, header.Length);
            if (read >= 4)
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

            if (read >= 8)
            {
                var magic = Encoding.ASCII.GetString(header, 0, 8);
                if (magic.StartsWith("ANDROID!", StringComparison.Ordinal) || magic.StartsWith("VNDRBOOT", StringComparison.Ordinal))
                {
                    return "boot";
                }
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
    /// <para>获取或设置镜像格式标识（boot/payload/cpio/oppo/sparse/raw/unknown）。</para>
    /// Gets or sets the image format identifier (boot/payload/cpio/oppo/sparse/raw/unknown).
    /// </summary>
    [ObservableProperty]
    private string _format = "unknown";

    /// <summary>
    /// <para>获取或设置是否选中。</para>
    /// Gets or sets whether the entry is selected.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;
}
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Ui;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// Image panel view model (singleton), holding the project image list and implementing real unpack/pack,
/// import and export logic. Like the device panel, the workspace dock and the top file menu share the
/// same instance to avoid duplicate instances and duplicate subscriptions.
/// <para>镜像面板视图模型（单例），承载项目内镜像清单并实现分解、合成、导入、导出等真实打包解包逻辑。
/// 与设备面板类似，工作区停靠面板与顶部文件菜单共享同一实例，避免双实例双订阅。</para>
/// </summary>
internal partial class ImageViewModel : Tool
{
    private static readonly Lazy<ImageViewModel> _lazyInstance = new(() => new ImageViewModel());

    /// <summary>
    /// Gets the active image panel singleton, shared by the workspace dock and the top file menu.
    /// <para>获取当前镜像面板单例实例，供主窗口与工作区停靠面板共享。</para>
    /// </summary>
    internal static ImageViewModel Instance => _lazyInstance.Value;

    /// <summary>
    /// Ensures the image panel singleton exists and returns it.
    /// <para>确保镜像面板单例已创建并返回它。</para>
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
    /// Before a pack writes, checks for same-named output files and asks whether to overwrite; declining
    /// skips the entry. Returns <c>true</c> immediately when there is no conflict.
    /// <para>打包写入前检查同名输出文件：存在时询问是否覆盖，取消则跳过该条目。无冲突直接返回 <c>true</c>。</para>
    /// </summary>
    /// <param name="existing">Absolute paths of the targets that already exist. <para>已存在的目标文件绝对路径。</para></param>
    /// <returns><para>可以继续写入时为 <c>true</c>；用户取消或无可询问窗口时为 <c>false</c>。</para>
    /// <c>true</c> when the write may proceed; <c>false</c> when the user declines or no owner window is available.</returns>
    private static async Task<bool> ConfirmPackOverwriteAsync(IReadOnlyList<string> existing)
    {
        if (existing.Count == 0) return true;
        if (TopLevelExtensions.GetTopLevel() is not Avalonia.Controls.Window owner) return false;
        var language = LanguageService.Instance;
        var names = string.Join("\n", existing.Select(Path.GetFileName));
        return await DialogService.ConfirmAsync(owner, language.GetString("Image_PackOverwriteTitle"),
            language.GetString("Image_PackOverwriteMessage") + "\n" + names,
            language.GetString("Image_ExtractOverwrite"), language.GetString("Image_ExtractSkip"));
    }

    /// <summary>
    /// Gets the collection of image entries in the project; entry paths are relative to the project root.
    /// <para>获取项目内镜像条目集合，条目路径为相对工程根目录。</para>
    /// </summary>
    public ObservableCollection<ImageEntryViewModel> Images { get; } = [];

    /// <summary>
    /// Gets or sets whether pack actions are currently shown.
    /// <para>获取或设置当前是否显示打包操作。</para>
    /// </summary>
    public bool IsPackMode
    {
        get => _mode == ImageMode.Pack;
        set { if (value) SetMode(ImageMode.Pack); }
    }

    /// <summary>
    /// Gets whether unpack actions are currently shown; setting this to true switches to unpack mode.
    /// <para>获取当前是否显示解包操作；设置为 true 时切换到解包模式。</para>
    /// </summary>
    public bool IsUnpackMode
    {
        get => _mode == ImageMode.Unpack;
        set { if (value) SetMode(ImageMode.Unpack); }
    }

    /// <summary>
    /// Gets whether output images are shown; setting this to true switches to output mode.
    /// <para>获取当前是否显示输出镜像；设置为 true 时切换到输出模式。</para>
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
    /// Gets the available items in the sub-project selector combo box (sub-project name list).
    /// <para>获取子项目选择下拉框的可选项（子项目名称列表）。</para>
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string>? _subProjectItems;

    /// <summary>
    /// Gets or sets whether the sub-project selector combo box is visible;
    /// shown when two or more sub-projects exist.
    /// <para>获取或设置是否显示子项目选择下拉框；当存在两个及以上子项目时显示。</para>
    /// </summary>
    [ObservableProperty]
    private bool _isSubProjectVisible;

    /// <summary>
    /// Gets or sets the currently selected sub-project name; switching refreshes the entry list.
    /// <para>获取或设置当前选中的子项目名称；变更时切换活动子项目并刷新条目列表。</para>
    /// </summary>
    [ObservableProperty]
    private string? _selectedSubProject;

    partial void OnSelectedSubProjectChanged(string? value)
    {
        if (_synchronizingSubProject || string.IsNullOrEmpty(value))
        {
            return;
        }
        if (value == ProjectContextService.Instance.ActiveSubProject?.Name) return;
        _ = SwitchSelectedSubProjectAsync(value);
    }

    private bool _synchronizingSubProject;

    private async Task SwitchSelectedSubProjectAsync(string name)
    {
        try
        {
            if (!IsOperating) await ProjectOperationsService.Instance.SwitchSubProjectAsync(name);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Projects", $"Switch sub-project failed: {ex.Message}");
        }
        finally
        {
            _synchronizingSubProject = true;
            try { SelectedSubProject = ProjectContextService.Instance.ActiveSubProject?.Name; }
            finally { _synchronizingSubProject = false; }
        }
    }

    /// <summary>
    /// Gets or sets the currently selected image entry.
    /// <para>获取或设置当前选中的镜像条目。</para>
    /// </summary>
    [ObservableProperty]
    private ImageEntryViewModel? _selectedImage;

    partial void OnSelectedImageChanged(ImageEntryViewModel? value)
    {

        WeakReferenceMessenger.Default.Send(new ImageSelectionChangedMessage(value));
    }

    /// <summary>
    /// Gets or sets the panel status text, reporting progress and results of operations
    /// such as batch flashing.
    /// <para>获取或设置面板状态文本，回报批量刷入等操作的进度与结果。</para>
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// Gets or sets the completion percentage of the current batch-flash operation.
    /// <para>获取或设置当前批量刷入操作的完成百分比。</para>
    /// </summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>
    /// Creates the image panel view model and registers the singleton instance.
    /// <para>创建镜像面板视图模型并注册单例实例。</para>
    /// </summary>
    public ImageViewModel()
    {

        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);

        OperationHistoryService.ReconstructOperation = ReconstructOperation;

        var deviceVm = DeviceViewModel.EnsureInstance();
        ((INotifyPropertyChanged)deviceVm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceViewModel.SelectedDevice))
            {
                RefreshFlashSelectedEnablement();
            }
        };

        App.DeviceMonitor.DevicesChanged += (_, _) => RefreshFlashSelectedEnablement();
    }

    /// <summary>
    /// Refreshes the "flash selected" command's enabled state on the UI thread to avoid mutating
    /// control state from a background thread.
    /// <para>在 UI 线程上刷新「刷入选中文件」命令的可用状态，避免后台线程直接修改控件状态触发异常。</para>
    /// </summary>
    private void RefreshFlashSelectedEnablement()
        => Dispatcher.UIThread.Post(FlashSelectedCommand.NotifyCanExecuteChanged);

    /// <summary>
    /// Responds to project-context-changed messages: asynchronously scans the <c>images/</c> directory
    /// and registers known image formats on open, and clears the list on close.
    /// <para>响应工程上下文变更消息：打开工程时异步扫描 <c>images/</c> 目录并注册已知镜像格式，
    /// 关闭工程时清空镜像列表。</para>
    /// </summary>
    private async void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message)
    {
        await RefreshProjectImagesAsync();
    }

    /// <summary>
    /// Reserved directory names excluded in pack mode (case-insensitive): <c>delivery/</c> is a delivery
    /// output directory and, like the other reserved names, is not a packable extraction result.
    /// <para>打包模式下排除的保留目录名集合（不区分大小写）：<c>delivery/</c> 是交付输出目录，
    /// 与其他保留目录一样不是可打包的解包产物。</para>
    /// </summary>
    private static readonly FrozenSet<string> ReservedDirNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "images", "output", "delivery", ".deleted", ".uotan"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns whether a first-level project folder is reserved, so the pack view never registers it as a
    /// packable extraction result.
    /// <para>返回一级子目录是否属于保留目录：打包视图中不应把这些目录登记为可打包的解包产物。</para>
    /// </summary>
    internal static bool IsReservedPackDirectory(string name) => ReservedDirNames.Contains(name);

    /// <summary>
    /// Scans project entries based on the current mode: in unpack mode scans <c>images/</c> for
    /// unpackable files; in pack mode scans the sub-project root for unpacked result directories;
    /// in output mode scans <c>output/</c> for images.
    /// Clears the list when no project is open.
    /// <para>根据当前模式扫描工程条目：解包模式扫描 <c>images/</c> 目录中的可解包文件，
    /// 打包模式扫描子项目根目录下的解包产物文件夹，输出模式扫描 <c>output/</c> 镜像。未打开工程时清空列表。</para>
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
    /// Scans <c>images/</c> or <c>output/</c>; output mode also includes raw images, while unpack mode
    /// registers images that carry a filesystem internally, boot-family images and HarmonyOS
    /// <c>update.bin</c> and Coolpad <c>cpb</c> containers (content probing independent of the extension, see
    /// <see cref="ImageInputService.ContainsFileSystem"/> and <see cref="ImageInputService.IsBootFamilyFormat"/>).
    /// <para>扫描 <c>images/</c> 或 <c>output/</c> 目录；输出模式同时包含 raw 镜像，解包模式登记
    /// 内部包含文件系统的镜像、boot 系列镜像与 HarmonyOS <c>update.bin</c>、酷派 <c>cpb</c> 容器（与后缀无关的内容探测，见
    /// <see cref="ImageInputService.ContainsFileSystem"/> 与 <see cref="ImageInputService.IsBootFamilyFormat"/>）。</para>
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

        var discovered = await Task.Run(() =>
        {
            List<(string Relative, long Size, string Format)> list = [];
            foreach (var file in Directory.EnumerateFiles(imagesDir))
            {
                var kind = ImageInputService.ProbeFormat(file);
                if (kind == ImageFormatKind.Unknown)
                {
                    continue;
                }

                if (!outputMode && !(ImageInputService.ContainsFileSystem(file)
                    || ImageInputService.IsBootFamilyFormat(kind)
                    || kind is ImageFormatKind.UpdateBin or ImageFormatKind.Cpb))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative is null)
                {
                    continue;
                }

                var size = new FileInfo(file).Length;

                if (size == 0)
                {
                    continue;
                }

                list.Add((relative, size, ImageInputService.GetFormatLabel(kind)));
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
    /// Pack mode: scans first-level sub-directories of the sub-project root (excluding reserved ones),
    /// registering them as packable entries.
    /// <para>打包模式：扫描子项目根目录下的一级子目录（排除保留目录），注册为可打包条目。</para>
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

        var discovered = await Task.Run(() =>
        {
            List<(string Name, string Relative)> list = [];
            foreach (var dir in Directory.EnumerateDirectories(subProjectRoot))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || IsReservedPackDirectory(name))
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
    /// Rebuilds the sub-project selector combo box from the current solution's sub-project list;
    /// the dropdown is shown when two or more sub-projects exist.
    /// <para>根据当前解决方案的子项目列表重建下拉框选项；存在两个及以上子项目时显示下拉框。</para>
    /// </summary>
    private void RebuildSubProjectItems()
    {
        _synchronizingSubProject = true;
        try { RebuildSubProjectItemsCore(); }
        finally { _synchronizingSubProject = false; }
    }

    private void RebuildSubProjectItemsCore()
    {
        var context = ProjectContextService.Instance;
        var subProjects = context.SubProjects;

        if (subProjects.Count < 2)
        {
            SubProjectItems = null;
            IsSubProjectVisible = false;

            if (subProjects.Count == 1)
            {
                SelectedSubProject = subProjects[0].Name;
            }

            return;
        }

        var items = new ObservableCollection<string>(subProjects.Select(sp => sp.Name));
        if (SubProjectItems is null || !SubProjectItems.SequenceEqual(items)) SubProjectItems = items;
        IsSubProjectVisible = true;

        var activeName = context.ActiveSubProject?.Name;
        if (SelectedSubProject != activeName)
        {
            SelectedSubProject = activeName ?? items[0];
        }
    }

    /// <summary>
    /// Selects all visible image entries in the current list, or clears the selection when all visible entries are already selected.
    /// <para>全选当前列表中的可见镜像条目；若全部可见条目已选中则取消全选。</para>
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        var visible = Images.Where(image => image.IsVisible).ToList();
        if (visible.Count == 0)
        {
            return;
        }

        var shouldSelect = !visible.All(image => image.IsSelected);
        foreach (var image in visible)
        {
            image.IsSelected = shouldSelect;
        }
    }

    /// <summary>
    /// Manually refreshes the image list: re-scans the project images/ directory and registers known-format images.
    /// <para>手动刷新镜像列表：重新扫描工程 images/ 目录并注册已知格式镜像。</para>
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        StatusText = LanguageService.Instance.GetString("ImageView_Refreshing");
        ProgressPercent = -1;
        await RefreshProjectImagesAsync();
        StatusText = string.Format(LanguageService.Instance.GetString("ImageView_Refreshed"), Images.Count);
        ProgressPercent = 0;
    }

    /// <summary>
    /// Image-list refresh before the project-creation flow saves: re-scans images/ so manually
    /// registered container entries (zip and other unknown formats) are filtered and extracted
    /// artifacts match the disk, keeping container entries out of the persisted project snapshot.
    /// <para>工程创建流程保存前的镜像列表刷新：重新扫描 images/ 目录，使手动登记的容器条目
    /// （zip 等未知格式）被过滤，提取产物与磁盘实际内容一致，避免把容器条目持久化进工程快照。</para>
    /// </summary>
    internal async Task RefreshAfterProjectCreationAsync()
    {
        await RefreshProjectImagesAsync();
    }

}
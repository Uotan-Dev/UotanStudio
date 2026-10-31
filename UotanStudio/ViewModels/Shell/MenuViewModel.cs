using System.Collections.ObjectModel;
using System.Collections.Specialized;

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Build;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.VendorFirmware.Workflow;
using UotanStudio.Messages;
using UotanStudio.Projects;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Network;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Ui;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.Services.Workspace;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Top menu bar view model, providing command implementations for File, Edit, View, Tools, and Help menus.
/// <para>顶部菜单栏视图模型，提供文件、编辑、视图、工具与帮助菜单的所有命令实现。</para>
/// </summary>
public partial class MenuViewModel : ViewModelBase, IDisposable
{
    private static TopLevel? TopLevel => TopLevelExtensions.GetTopLevel();

    /// <summary>
    /// Currently active bottom main panel; gates availability of node-editor-related View menu commands (minimap/zoom).
    /// <para>当前激活的底栏主面板；影响视图菜单中节点编辑器相关命令（小地图/缩放）的可用性。</para>
    /// </summary>
    /// <remarks>
    /// Updated by <c>MainView</c> via <see cref="ActiveTabChangedMessage"/> on tab switch; defaults to <see cref="ActiveTab.Workspace"/> (matching the workspace page MainView selects on startup).
    /// <para>由 <c>MainView</c> 通过 <see cref="ActiveTabChangedMessage"/> 在面板切换时更新；初始为 <see cref="ActiveTab.Workspace"/>（与 MainView 启动时选中的工作区页对应）。</para>
    /// </remarks>
    private ActiveTab _currentTab = ActiveTab.Workspace;

    private readonly ProjectOperationsService _projectOps;
    private readonly IWindowService _windows;
    private readonly ILocalizer _localizer;
    private readonly IUnifiedLog _log;

    /// <summary>
    /// Cached collection-changed handler so it can be unsubscribed in <see cref="Dispose"/>.
    /// <para>缓存的集合变更事件处理器，用于在 <see cref="Dispose"/> 中取消订阅。</para>
    /// </summary>
    private readonly NotifyCollectionChangedEventHandler _recentProjectsChangedHandler;

    /// <summary>
    /// Creates the menu view model, subscribing to active-tab-changed messages to maintain <see cref="_currentTab"/>
    /// and loading the recent projects list.
    /// <para>创建菜单视图模型，订阅激活面板切换消息以维护 <see cref="_currentTab"/>，并加载最近打开项目列表。</para>
    /// </summary>
    public MenuViewModel() : this(ProjectOperationsService.Instance) { }

    internal MenuViewModel(ProjectOperationsService projectOperations, IWindowService? windows = null,
        ILocalizer? localizer = null, IUnifiedLog? log = null)
    {
        _projectOps = projectOperations;
        _windows = windows ?? NullWindowService.Instance;
        _localizer = localizer ?? LanguageService.Instance;
        _log = log ?? LogService.Instance;
        WeakReferenceMessenger.Default.Register<ActiveTabChangedMessage>(this, OnActiveTabChanged);
        _recentProjectsChangedHandler = (_, _) => OnPropertyChanged(nameof(HasRecentProjects));
        RecentProjects.CollectionChanged += _recentProjectsChangedHandler;
        LoadRecentProjects();
    }

    /// <summary>
    /// Recently opened solutions (most recent first), backing the "Open Recent" submenu.
    /// <para>最近打开的解决方案列表（最新在前），用于「最近打开」子菜单。</para>
    /// </summary>
    public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = [];

    /// <summary>
    /// Whether any recent projects exist, gating the "Open Recent" menu item's availability.
    /// <para>是否存在最近打开记录，驱动「最近打开」菜单项是否可用。</para>
    /// </summary>
    public bool HasRecentProjects => RecentProjects.Count > 0;

    /// <summary>
    /// Maximum number of recent projects kept.
    /// <para>最多保留的最近打开项目数。</para>
    /// </summary>
    private const int MaxRecentProjects = 10;

    /// <summary>
    /// Loads the recent projects list from application settings, keeping only entries whose files still exist.
    /// <para>从应用设置加载最近打开项目列表（仅保留实际存在的文件）。</para>
    /// </summary>
    private void LoadRecentProjects()
    {
        RecentProjects.Clear();
        var saved = SettingsPersistenceService.Instance.LoadSettings().RecentProjects;
        if (saved is null)
        {
            return;
        }

        foreach (var path in saved)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            RecentProjects.Add(CreateRecentEntry(path));
        }
    }

    /// <summary>
    /// Records a recently opened solution: deduplicates to the top, trims the list and persists it.
    /// <para>记录一个最近打开的解决方案：去重置顶、裁剪数量并持久化。</para>
    /// </summary>
    /// <param name="path">The full solution file path. <para>解决方案文件完整路径。</para></param>
    private void RecordRecentProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var existing = RecentProjects.FirstOrDefault(e => string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentProjects.Remove(existing);
        }

        RecentProjects.Insert(0, CreateRecentEntry(fullPath));
        while (RecentProjects.Count > MaxRecentProjects)
        {
            RecentProjects.RemoveAt(RecentProjects.Count - 1);
        }

        PersistRecentProjects();
    }

    /// <summary>
    /// Persists the recent projects list to application settings (merged write, preserving other settings).
    /// <para>将最近打开项目列表持久化到应用设置（合并写入，不覆盖其他设置项）。</para>
    /// </summary>
    private void PersistRecentProjects()
    {
        SettingsPersistenceService.Instance.UpdateRecentProjects([.. RecentProjects.Select(e => e.Path)]);
    }

    /// <summary>
    /// Creates a recent project entry whose open command invokes <see cref="OpenProjectFromPathAsync"/>;
    /// on failure (e.g. the file was deleted) the entry is removed and persisted automatically.
    /// <para>创建一个最近项目条目，其打开命令直接调用 <see cref="OpenProjectFromPathAsync"/>；
    /// 打开失败（如文件已被删除）时自动移除该条目并持久化。</para>
    /// </summary>
    /// <param name="path">The full solution file path. <para>解决方案文件完整路径。</para></param>
    private RecentProjectEntry CreateRecentEntry(string path) =>
        new(path, new AsyncRelayCommand(async () =>
        {
            if (!await OpenProjectFromPathAsync(path))
            {
                var entry = RecentProjects.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    RecentProjects.Remove(entry);
                    PersistRecentProjects();
                }
            }
        }));

    /// <summary>
    /// Responds to active-tab changes: updates <see cref="_currentTab"/> and notifies node-editor-related commands to re-evaluate their availability.
    /// <para>响应激活面板切换：更新 <see cref="_currentTab"/> 并通知节点编辑器相关命令重新计算可用性。</para>
    /// </summary>
    /// <param name="recipient">The message recipient. <para>消息接收者。</para></param>
    /// <param name="message">The message carrying the newly active tab. <para>包含新激活面板的消息。</para></param>
    private void OnActiveTabChanged(object recipient, ActiveTabChangedMessage message)
    {
        if (_currentTab == message.Tab) return;
        _currentTab = message.Tab;

        ToggleMinimapCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ResetZoomCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Whether the Node Editor panel is currently active; used solely by <c>CanExecute</c> helpers.
    /// <para>当前是否在节点编辑器面板；仅供 <c>CanExecute</c> 私有方法使用。</para>
    /// </summary>
    private bool IsNodeEditorActive => _currentTab == ActiveTab.NodeEditor;

    /// <summary>
    /// Opens the new-project form (new-solution semantics); on create, builds the solution from the
    /// combined path, imports selected images, extracts selected entries from a single super /
    /// payload.bin / update.bin / zip container in place inside images/, and, with full unpack checked, recursively
    /// unpacks the extracted supported imgs into same-name folders.
    /// <para>打开新建项目配置窗口（新建解决方案语义）；点击创建后按组合路径创建解决方案、
    /// 导入选中镜像；单文件容器（super / payload.bin / update.bin / zip）按勾选条目在 images/ 内原地进行向提取，
    /// 勾选完全解压时对提取出的受支持 img 继续递归解包为同名文件夹。</para>
    /// </summary>
    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (!_windows.HasOwnerWindow) return;
        var window = _windows.CreateNewProjectDialog(NewProjectWindowMode.Solution, null, null);
        window.CreateWithCancellationAsync = async (form, cancellationToken) =>
        {

            GuardProjectLocationNotNested(form.Location, requireDirectChild: false);
            var solutionPath = Path.Combine(form.Location, form.ProjectName + ".uotan");
            await _projectOps.CreateNewSolutionAsync(solutionPath, cancellationToken);
            if (!string.Equals(_projectOps.CurrentProjectPath, solutionPath, StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException("Project creation was cancelled.");
            RecordRecentProject(solutionPath);

            var images = ImageViewModel.EnsureInstance();
            if (!string.IsNullOrWhiteSpace(form.RemoteUrl))
            {
                await ImportRemoteRomAsync(form, images, window, cancellationToken);
            }
            else if (form.ContainerPath is not null)
            {
                await ImportContainerWithSelectionAsync(form, images, window, cancellationToken);
            }
            else if (form.SelectedImagePaths.Length > 0)
            {
                await images.ImportAsync(form.SelectedImagePaths,
                    byteProgress: window.BeginByteProgress(_localizer.GetString("NewProject_Copying")), cancellationToken: cancellationToken);
            }

            WorkspacePanelService.Instance.Show(WorkspacePanelService.ImagesView);
            GoToWorkspace();

            if (form.ContainerPath is null)
            {
                await UnpackSelectedPlainImagesAsync(form, images, window, cancellationToken);
            }

            if (form.MergeMyPartitions)
            {
                await MergeMyPartitionsIntoSystemAsync(window, cancellationToken);
            }

            await images.RefreshAfterProjectCreationAsync();
            if (!await _projectOps.SaveAsync(saveActiveDocument: false))
                throw new IOException("Could not save the imported project.");
        };
        await window.ShowDialogAsync();
    }

    private async Task ImportRemoteRomAsync(NewProjectFormResult form, ImageViewModel images,
        INewProjectDialog window, CancellationToken cancellationToken)
    {
        var uri = new Uri(form.RemoteUrl!, UriKind.Absolute);
        using var client = SharedHttpClient.Create(TimeSpan.FromHours(2));
        var service = new RemoteRomService(client);
        var outputDirectory = ProjectContextService.Instance.EnsureImagesDirectory();
        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "remote-rom.bin";
        fileName = string.Concat(fileName.Select(ch => PathSearchValues.IsInvalidFileNameChar(ch) ? '_' : ch));

        var temp = Path.Combine(Path.GetTempPath(), $"uotan-remote-{Guid.NewGuid():N}-{fileName}");
        try
        {
            window.ResetProgressStats();
            var progress = window.BeginByteProgress(_localizer.GetString("NewProject_Downloading"));
            if (form.ContainerKind == ContainerKind.Payload && form.SelectedPartitions.Length > 0 && !form.CopyOriginal)
            {
                try
                {
                    await service.ExtractPayloadPartitionsAsync(uri, form.SelectedPartitions, outputDirectory, progress: progress, cancellationToken: cancellationToken);
                    return;
                }
                catch (InvalidOperationException)
                {

                }
            }
            if (form.ContainerKind == ContainerKind.Zip && form.SelectedZipMembers.Length > 0 && !form.CopyOriginal)
            {
                await service.DownloadZipEntriesAsync(uri, form.SelectedZipMembers, outputDirectory, progress, cancellationToken);
                return;
            }
            if (form.ContainerKind == ContainerKind.Zip && form.SelectedPartitions.Length > 0 && !form.CopyOriginal)
            {
                try
                {
                    await service.ExtractZipPayloadPartitionsAsync(uri, form.SelectedPartitions, outputDirectory, progress, cancellationToken);
                    return;
                }
                catch (InvalidOperationException)
                {

                }
            }
            await service.DownloadAsync(uri, temp, progress, cancellationToken);
            if (form.ContainerKind != ContainerKind.None && form.SelectedPartitions.Length + form.SelectedZipMembers.Length > 0)
            {
                await ImportContainerWithSelectionAsync(form with { ContainerPath = temp, CopyOriginal = form.CopyOriginal }, images, window, cancellationToken);
            }
            else
            {
                var destination = Path.Combine(outputDirectory, fileName);
                File.Move(temp, destination, overwrite: true);
                images.AddExtractedImage(destination);
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Unpacking for plain multi-selected images (shared by new solution and new sub-project): only
    /// imported entries are unpacked; with full unpack checked nested images are unpacked recursively.
    /// <para>普通多选镜像的解包（新建解决方案与新增子项目共用）：只解包导入成功的镜像条目，
    /// 勾选完全解压时递归解包嵌套镜像。</para>
    /// </summary>
    private async Task UnpackSelectedPlainImagesAsync(NewProjectFormResult form, ImageViewModel images, INewProjectDialog window, CancellationToken cancellationToken = default)
    {
        if (!form.UnpackImages || form.SelectedImagePaths.Length == 0) return;

        var entries = images.Images
            .Where(entry => !entry.IsDirectory && form.SelectedImagePaths
                .Any(path => string.Equals(Path.GetFileName(path), entry.DisplayName,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            .ToList();
        if (entries.Count == 0) return;
        await UnpackWithCreationProgressAsync(images, entries, form.UnpackFully, window, cancellationToken);
    }

    /// <summary>
    /// Container import plus targeted extraction (shared by new solution and new sub-project): the
    /// container is copied into images/ and the selected entries are extracted in place; with full
    /// unpack checked the extracted images are recursively unpacked. Extraction runs on a background
    /// thread and writes to the log.
    /// <para>容器导入与定向提取（新建解决方案与新增子项目共用）：复制容器到 images/ 后按勾选
    /// 原地提取；勾选完全解压时对提取产物执行递归解包。提取在后台线程执行并写入日志。</para>
    /// </summary>
    private static async Task UnpackWithCreationProgressAsync(ImageViewModel images,
        IReadOnlyList<ImageEntryViewModel> entries, bool fully, INewProjectDialog window, CancellationToken cancellationToken = default)
    {
        void Update(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(images.StatusText) or nameof(images.ProgressPercent) or nameof(images.ProgressStats))
                window.ReportProgress(images.StatusText, images.ProgressPercent, statistics: images.ProgressStats);
        }
        window.ResetProgressStats();
        images.PropertyChanged += Update;
        try { await images.UnpackFromProjectCreationAsync(entries, fully, cancellationToken); }
        finally { images.PropertyChanged -= Update; }
    }

    private async Task ImportContainerWithSelectionAsync(NewProjectFormResult form, ImageViewModel images, INewProjectDialog window, CancellationToken cancellationToken = default)
    {
        var source = form.ContainerPath;
        if (source is null) return;
        var outputDirectory = ProjectContextService.Instance.EnsureImagesDirectory();
        await SourceContainerMetadataService.SaveAsync(source, form.ContainerKind);
        var containerPath = source;
        if (form.CopyOriginal)
        {
            var container = await images.ImportContainerAsync(source, form.ContainerKind switch
            {
                ContainerKind.Super => "super",
                ContainerKind.Payload => "payload",
                ContainerKind.Zip => "zip",
                ContainerKind.UpdateBin => "update.bin",
                _ => "raw"
            }, byteProgress: window.BeginByteProgress(_localizer.GetString("NewProject_Copying")), cancellationToken: cancellationToken);
            if (container is null) throw new IOException("Container import failed.");
            containerPath = ProjectContextService.Instance.ResolvePath(container.RelativePath) ?? source;
        }
        window.ResetProgressStats();
        window.ReportProgress(_localizer.GetString("NewProject_Extracting"));
        if (!form.UnpackImages
            || (form.SelectedZipMembers.Length == 0 && form.SelectedPartitions.Length == 0))
        {
            return;
        }

        List<string> extractedPaths = [];
        try
        {
            if (form.ContainerKind == ContainerKind.Zip && form.SelectedZipMembers.Length > 0)
            {
                foreach (var member in form.SelectedZipMembers)
                {
                    var byteProgress = window.BeginByteProgress(_localizer.GetString("NewProject_Extracting"));
                    var result = await Task.Run(() => ContainerEntryExtractor.ExtractAsync(
                        containerPath, [member], fromPayloadOnly: false, outputDirectory: outputDirectory, byteProgress: byteProgress, cancellationToken: cancellationToken)).ConfigureAwait(true);
                    extractedPaths.AddRange(result.OutputPaths);
                }
            }

            if (form.SelectedPartitions.Length > 0)
            {

                var byteProgress = window.BeginByteProgress(_localizer.GetString("NewProject_Extracting"));
                var result = await Task.Run(() => ContainerEntryExtractor.ExtractAsync(
                    containerPath, form.SelectedPartitions,
                    fromPayloadOnly: form.ContainerKind == ContainerKind.Zip, outputDirectory: outputDirectory, byteProgress: byteProgress, cancellationToken: cancellationToken)).ConfigureAwait(true);
                extractedPaths.AddRange(result.OutputPaths);
            }

            _log.Write(LogLevel.Info, "Images",
                $"Extracted {extractedPaths.Count} image(s) from {Path.GetFileName(containerPath)}");
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "Images",
                $"Container extraction failed: {ex.Message}");
            throw;
        }

        if (extractedPaths.Count == 0) return;

        var extractedEntries = extractedPaths
            .Select(images.AddExtractedImage)
            .OfType<ImageEntryViewModel>()
            .Where(entry => entry.SizeBytes > 0 && entry.Format is not ("raw" or "unknown"))
            .ToList();
        if (form.UnpackFully && extractedEntries.Count > 0)
        {
            await UnpackWithCreationProgressAsync(images, extractedEntries, true, window);
        }
    }

    /// <summary>
    /// Merge step of the creation flow: discovers the unpacked system and my_* trees under the
    /// sub-project root, merges each my_* tree into the system tree with its own file_contexts/fs_config
    /// rebased, and after all merges succeed removes the merged trees, their images/ files and the
    /// .uotan/metadata entries. A missing system or my_* tree is logged and skipped, and only my_* trees
    /// of the system tree's own slot are merged — a tree of another slot is left in place.
    /// <para>创建流程的合并 my_ 分区步骤：发现子项目根下解包出的 system 与 my_* 目录树，逐个把
    /// my_* 树并入 system 树并对分区自带的 file_contexts/fs_config 改基，全部成功后移除已合并的
    /// my_* 树、images/ 内同名镜像与 .uotan/metadata 元数据。system 或 my_* 树缺失时记录日志并跳过；
    /// 只并入与 system 树同槽位的 my_* 树，其它槽位的目录树保持原样。</para>
    /// </summary>
    /// <param name="window">The new-project window showing progress. <para>显示进度的新建项目窗口。</para></param>
    /// <param name="cancellationToken">Cancels the merge; no files are removed on cancellation. <para>取消合并；取消时不会删除任何文件。</para></param>
    /// <exception cref="IOException">The sub-project root is missing or a partition merge failed. <para>子项目根目录缺失或某个分区合并失败。</para></exception>
    private async Task MergeMyPartitionsIntoSystemAsync(INewProjectDialog window, CancellationToken cancellationToken)
    {
        var project = ProjectContextService.Instance.ActiveSubProject;
        var root = project?.RootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new IOException("The active sub-project root is missing; my-partition merge aborted.");

        var (systemTree, myTrees) = MyPartitionMergePlanner.DiscoverTrees(root);
        if (systemTree is null || myTrees.Count == 0)
        {
            _log.Write(LogLevel.Warn, "Images",
                "My-partition merge skipped: no unpacked system or my_* tree was found in the sub-project.");
            return;
        }

        var service = new MyPartitionMergeService();
        var merged = new List<string>();
        var imagesRoot = Path.Combine(root, "images");
        var systemPartition = Path.GetFileName(Path.TrimEndingDirectorySeparator(systemTree));
        try
        {
            var done = 0;
            foreach (var tree in myTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partition = Path.GetFileName(Path.TrimEndingDirectorySeparator(tree));
                window.ResetProgressStats();
                window.ReportProgress(
                    string.Format(_localizer.GetString("NewProject_MergingMyPartitions"), partition),
                    done * 100.0 / myTrees.Count);
                var result = await service.MergeAsync(tree, systemTree, configDirectory: null,
                    new MyPartitionMergeOptions
                    {
                        SkipMissingSidecars = true,
                        MyMetadataDirectory = RepackMetadata.ResolveMetadataDirectory(imagesRoot, root, partition),
                        SystemMetadataDirectory = RepackMetadata.ResolveMetadataDirectory(imagesRoot, root, systemPartition),
                        SystemPartitionName = systemPartition,
                    }, cancellationToken: cancellationToken);
                merged.Add(partition);
                done++;
                _log.Write(LogLevel.Info, "Images",
                    $"Merged {partition} into system: {result.MovedEntries} entries, file_contexts {(result.FileContextsRebased ? "rebased" : "skipped")}, fs_config {(result.FsConfigRebased ? "rebased" : "skipped")}, preservation {(result.PreservationFolded ? $"folded ({result.FoldedInodeEntries} inodes)" : "manifest not found")}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Write(LogLevel.Error, "Images",
                $"My-partition merge failed after {merged.Count} partition(s): {ex.Message}");
            throw;
        }

        window.ResetProgressStats();
        window.ReportProgress(_localizer.GetString("NewProject_CleaningMergedPartitions"), 100);
        foreach (var tree in myTrees)
        {
            var partition = Path.GetFileName(Path.TrimEndingDirectorySeparator(tree));
            Directory.Delete(tree, recursive: true);
            foreach (var image in MyPartitionMergePlanner.FindPartitionImageFiles(imagesRoot, partition))
                File.Delete(image);
            var metadata = RepackMetadata.ResolveMetadataDirectory(imagesRoot, root, partition);
            if (Directory.Exists(metadata))
                Directory.Delete(metadata, recursive: true);
        }
        _log.Write(LogLevel.Info, "Images",
            $"Removed {myTrees.Count} merged my-partition tree(s) with their images and metadata.");
    }

    /// <summary>Creates a folder in the explorer directory. <para>在资源管理器选定目录中新建文件夹。</para></summary>
    [RelayCommand]
    private Task NewFolderAsync() => CreateExplorerItemAsync(isDirectory: true);

    /// <summary>Creates a file in the explorer directory. <para>在资源管理器选定目录中新建文件。</para></summary>
    [RelayCommand]
    private Task NewFileAsync() => CreateExplorerItemAsync(isDirectory: false);

    private async Task CreateExplorerItemAsync(bool isDirectory)
    {
        var owner = DialogService.GetOwnerWindow();
        if (owner is null || WorkspaceVM?.ProjectExplorer is not { } explorer) return;
        try
        {
            var directory = explorer.CreationDirectory;
            var pickedDirectory = string.IsNullOrEmpty(directory) || !Directory.Exists(directory);
            if (pickedDirectory) directory = await PickFolderAsync(owner, "Menu_SelectCreationDirectory");
            if (directory is null) return;
            var path = await ExplorerItemCreationService.CreateAsync(owner, directory, isDirectory);
            if (path is null) return;
            if (pickedDirectory) explorer.OpenFolder(directory);
            explorer.Refresh();
            WorkspacePanelService.Instance.Show(WorkspacePanelService.ProjectExplorer);
            GoToWorkspace();
            if (!isDirectory) await WorkspaceVM.OpenDocumentAsync(path, EditorOpenMode.Text);
        }
        catch (Exception ex)
        {
            await DialogService.ShowAsync(owner, _localizer.GetString(
                isDirectory ? "Dialog_NewFolderFailed" : "Dialog_NewFileFailed"), UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    /// <summary>Browses a selected folder in the explorer. <para>选择普通文件夹并在资源管理器中浏览。</para></summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var owner = DialogService.GetOwnerWindow();
        if (owner is null || WorkspaceVM?.ProjectExplorer is not { } explorer) return;
        try
        {
            var directory = await PickFolderAsync(owner, "Menu_OpenFolder");
            if (directory is null) return;
            explorer.OpenFolder(directory);
            WorkspacePanelService.Instance.Show(WorkspacePanelService.ProjectExplorer);
            GoToWorkspace();
        }
        catch (Exception ex)
        {
            await DialogService.ShowAsync(owner, _localizer.GetString("Menu_OpenFailed"),
                UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    /// <summary>Opens selected files in the existing editors. <para>选择文件并使用现有编辑器打开。</para></summary>
    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var owner = DialogService.GetOwnerWindow();
        if (owner is null || WorkspaceVM is null || !owner.StorageProvider.CanOpen) return;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = _localizer.GetString("Menu_OpenFile"),
                AllowMultiple = true,
                FileTypeFilter = [FilePickerFileTypes.All]
            });
            foreach (var file in files)
            {
                if (file.TryGetLocalPath() is { } path)
                {
                    GoToWorkspace();
                    await WorkspaceVM.OpenDocumentAsync(path);
                }
            }
        }
        catch (Exception ex)
        {
            await DialogService.ShowAsync(owner, _localizer.GetString("Menu_OpenFailed"),
                UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    private static async Task<string?> PickFolderAsync(Window owner, string titleKey)
    {
        if (!owner.StorageProvider.CanPickFolder) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString(titleKey),
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>
    /// Opens an existing solution by showing a file-open dialog to pick a <c>.uotan</c> file, restoring the
    /// image list, packing settings, canvas and operation history.
    /// <para>打开现有解决方案，弹出文件打开对话框选择 <c>.uotan</c> 文件，恢复镜像清单、打包设置、画布与操作历史。</para>
    /// </summary>
    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        try
        {
            var path = await PickProjectFileAsync(isSave: false);
            if (path == null)
            {
                return;
            }

            await OpenProjectFromPathAsync(path);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "Project", $"Open project failed: {ex.Message}");
            var owner = DialogService.GetOwnerWindow();
            if (owner is not null)
            {
                await DialogService.ShowAsync(owner, _localizer.GetString("Menu_OpenFailed"),
                    _localizer.GetString("Menu_OpenProjectFolderFailed"), Ursa.Controls.MessageBoxIcon.Error);
            }
        }
    }

    /// <summary>
    /// Opens the solution at the specified path (shared by the menu and file associations): loads the firmware
    /// and node projects, rebuilding the image list, packing settings, canvas and operation history. Returns
    /// whether the open succeeded.
    /// <para>从指定路径打开解决方案（供菜单与命令行共用）：加载固件项目与节点项目，重建镜像清单、打包设置、
    /// 画布与操作历史。返回是否成功。</para>
    /// </summary>
    public async Task<bool> OpenProjectFromPathAsync(string path)
    {
        var result = await _projectOps.OpenProjectFromPathAsync(path);
        if (result)
        {
            RecordRecentProject(path);
            return true;
        }
        await ReportOpenFailureAsync();
        return false;
    }

    /// <summary>
    /// Turns the session's open-failure reason into an explicit error dialog: a missing file, an unreadable
    /// solution and a corrupt node project each get their own wording, and a version mismatch reports the stored
    /// format version so the user can tell whether an older build is needed for migration. Hosts without a
    /// window (tests) only log.
    /// <para>把打开失败的会话原因转成显式错误弹窗：文件缺失、解决方案版本不可读、节点项目损坏各有独立文案，
    /// 版本不匹配时附上磁盘上的格式版本以便判断是否需要迁移。无窗口宿主（测试）只写日志。</para>
    /// </summary>
    private async Task ReportOpenFailureAsync()
    {
        var language = _localizer;
        var failure = _projectOps.OpenFailure;
        var target = _projectOps.OpenFailurePath;
        var version = _projectOps.OpenFailureStoredVersion;
        var message = (failure, version) switch
        {
            (ProjectOpenFailure.MissingFile, _) => string.Format(language.GetString("Menu_OpenProjectMissing"), target),
            (ProjectOpenFailure.InvalidSolution, { } stored) => string.Format(
                language.GetString("Menu_OpenSolutionUnsupported"), target, stored),
            (ProjectOpenFailure.InvalidSolution, _) => string.Format(language.GetString("Menu_OpenSolutionUnreadable"), target),
            (ProjectOpenFailure.InvalidNodeProject, { } stored) => string.Format(
                language.GetString("Menu_OpenNodeProjectUnsupported"), target, stored),
            (ProjectOpenFailure.InvalidNodeProject, _) => string.Format(
                language.GetString("Menu_OpenNodeProjectUnreadable"), target),
            _ => string.Format(language.GetString("Menu_OpenSolutionUnreadable"), target)
        };
        _log.Write(LogLevel.Error, "Project", $"Open failed: {target} ({message})");
        var owner = DialogService.GetOwnerWindow();
        if (owner is not null)
            await DialogService.ShowAsync(owner, language.GetString("Menu_OpenFailed"), message,
                Ursa.Controls.MessageBoxIcon.Error);
    }

    /// <summary>
    /// Loads a registered node project into the canvas and switches to the Node Editor tab (shared by the
    /// explorer double-click and the canvas tab strip). Returns whether the open succeeded.
    /// <para>把解决方案登记的节点项目载入画布并切到节点编辑器选项卡（供资源管理器双击与标签条共用）。
    /// 返回是否成功。</para>
    /// </summary>
    public async Task<bool> OpenNodeProjectAsync(string name)
    {
        if (!await _projectOps.OpenNodeProjectAsync(name)) return false;
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.NodeEditor));
        return true;
    }

    /// <summary>
    /// Switches the active node project canvas.
    /// <para>切换活动节点项目画布。</para>
    /// </summary>
    public Task<bool> SetActiveNodeProjectAsync(string name) => _projectOps.SetActiveNodeProjectAsync(name);

    /// <summary>
    /// Closes the canvas document of a node project, keeping its solution registration and file on disk.
    /// <para>关闭活动节点项目的画布文档，保留解决方案登记与磁盘文件。</para>
    /// </summary>
    public Task<bool> CloseNodeProjectAsync(string name) => _projectOps.CloseNodeProjectAsync(name);

    /// <summary>
    /// Creates an empty node project in the open solution: the name is the next unused <c>NodeN</c>, the file
    /// is written to <c>nodes/&lt;name&gt;.uotannode</c>, and it becomes the active canvas. Without an open
    /// solution it reports and skips.
    /// <para>在当前解决方案中新建一个空白节点项目：名称自动取下一个未占用的 <c>NodeN</c>，写入
    /// <c>nodes/&lt;name&gt;.uotannode</c> 并成为活动画布。未打开解决方案时给出提示并跳过。</para>
    /// </summary>
    [RelayCommand]
    private async Task CreateNodeProjectAsync()
    {
        var context = ProjectContextService.Instance;
        var owner = DialogService.GetOwnerWindow();
        if (!context.IsProjectOpen)
        {
            if (owner is not null)
                await DialogService.ShowAsync(owner, _localizer.GetString("Explorer_NodeProject"),
                    _localizer.GetString("Menu_CreateNodeProjectNoProject"),
                    Ursa.Controls.MessageBoxIcon.Information);
            return;
        }

        try
        {
            if (!await _projectOps.CreateNodeProjectAsync(NextNodeProjectName(context))) return;
            WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.NodeEditor));
        }
        catch (Exception ex)
        {
            if (owner is not null)
                await DialogService.ShowAsync(owner, _localizer.GetString("Explorer_NodeProject"),
                    UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    private static string NextNodeProjectName(ProjectContextService context)
    {
        var index = 1;
        while (context.NodeProjects.Any(node => string.Equals(node.Name, $"Node{index}", StringComparison.OrdinalIgnoreCase)))
            index++;
        return $"Node{index}";
    }

    /// <summary>
    /// Imports the given files into the current solution's image list (shared by file associations and the menu).
    /// <para>将若干文件导入当前解决方案的镜像清单（供命令行与菜单共用）。</para>
    /// </summary>
    public async Task ImportFilesAsync(string[] paths)
    {
        await _projectOps.ImportFilesAsync(paths);
    }

    /// <summary>
    /// Adds a new sub-project to the current solution: opens the new-project form (sub-project mode;
    /// the location defaults to the solution root and the name to the first NewProjectN that skips
    /// existing sub-projects); on create, writes the <c>.uotanfw</c> and solution files, then imports
    /// images or extracts from a container per the selection (shared with the new-solution flow).
    /// <para>向当前解决方案添加新子项目：打开新建表单（子项目模式，位置默认当前解决方案根目录、
    /// 名称默认跳过现有子项目的 NewProjectN）；点击创建后写入 <c>.uotanfw</c> 与解决方案文件，
    /// 随后按勾选导入镜像或从容器定向提取（与新建解决方案共用同一流程）。</para>
    /// </summary>
    [RelayCommand]
    private async Task AddSubProjectAsync()
    {
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || context.RootDirectory is null)
        {
            _log.Write(LogLevel.Warn, "Menu", "Add sub-project aborted: no solution is open");
            return;
        }

        if (!_windows.HasOwnerWindow) return;
        var window = _windows.CreateNewProjectDialog(
            NewProjectWindowMode.SubProject,
            context.RootDirectory,
            context.SubProjects.Select(sp => sp.Name));
        window.CreateWithCancellationAsync = async (form, cancellationToken) =>
        {

            GuardProjectLocationNotNested(form.Location, requireDirectChild: true);
            var fwPath = Path.Combine(form.Location, form.ProjectName + ".uotanfw");
            await _projectOps.AddSubProjectAsync(fwPath);

            if (!await _projectOps.SwitchSubProjectAsync(form.ProjectName)) return;

            var images = ImageViewModel.EnsureInstance();
            if (form.ContainerPath is not null)
            {
                await ImportContainerWithSelectionAsync(form, images, window, cancellationToken);
            }
            else if (form.SelectedImagePaths.Length > 0)
            {
                await images.ImportAsync(form.SelectedImagePaths,
                    byteProgress: window.BeginByteProgress(_localizer.GetString("NewProject_Copying")), cancellationToken: cancellationToken);
                await UnpackSelectedPlainImagesAsync(form, images, window, cancellationToken);
            }

            if (form.MergeMyPartitions)
            {
                await MergeMyPartitionsIntoSystemAsync(window, cancellationToken);
            }

            await images.RefreshAfterProjectCreationAsync();
            if (!await _projectOps.SaveAsync(saveActiveDocument: false))
                throw new IOException("Could not save the imported project.");
        };
        await window.ShowDialogAsync();
    }

    /// <summary>
    /// Rejects scaffolding a new project or sub-project inside the currently open solution tree: a new
    /// solution must be an external, standalone directory; a new sub-project must be a direct child of
    /// the solution root (an independent flat folder, never nested).
    /// <para>拒绝把项目/子项目脚手架落到当前打开的解决方案树内部。新解决方案必须是外部独立目录；
    /// 新增子项目必须是解决方案根的直接子级（独立平铺文件夹，禁止嵌套）。</para>
    /// </summary>
    /// <param name="location">The combined directory chosen in the form. <para>对话框选定的组合目录。</para></param>
    /// <param name="requireDirectChild">
    /// <c>true</c> for a new sub-project (must be a direct child of the solution root); <c>false</c> for a new
    /// solution (must not sit inside an existing project tree).
    /// <para><c>true</c> 表示新增子项目（要求是解决方案根的直接子级）；<c>false</c> 表示新建解决方案（要求不位于现有项目树内）。</para>
    /// </param>
    /// <exception cref="IOException">Thrown when the location would nest. <para>位置会形成嵌套布局时。</para></exception>
    private static void GuardProjectLocationNotNested(string location, bool requireDirectChild)
    {
        var root = ProjectContextService.Instance.RootDirectory;
        if (root is null) return;
        var rootPath = Path.GetFullPath(root);
        var loc = Path.GetFullPath(location);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(rootPath, loc, comparison))
            throw new IOException("A project folder must be an independent directory.");
        if (requireDirectChild)
        {
            if (!ProjectValidation.IsDirectSubProjectDirectory(rootPath, loc))
                throw new IOException("A sub-project must be an independent folder directly under the solution root.");
            return;
        }
        var prefix = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        if (loc.StartsWith(prefix, comparison))
            throw new IOException("A project folder must not be nested inside an existing project.");
    }

    /// <summary>
    /// Current project file path, populated by save/open operations.
    /// <para>当前工程文件路径，由保存/打开操作填充。</para>
    /// </summary>
    private string? _currentProjectPath
    {
        get => _projectOps.CurrentProjectPath;
        set => _projectOps.CurrentProjectPath = value;
    }

    /// <summary>
    /// Current node editor view model reference, set externally so persistence can access the canvas.
    /// <para>当前节点编辑器视图模型引用，由外部设置以便持久化访问画布。</para>
    /// </summary>
    public NodeEditorViewModel? NodeEditorVM
    {
        get => _projectOps.NodeEditorVM;
        set => _projectOps.NodeEditorVM = value;
    }

    /// <summary>
    /// Current workspace view model reference, set externally so Ctrl+S can save the active document.
    /// <para>当前工作区视图模型引用，由外部设置以便 Ctrl+S 保存活动文档。</para>
    /// </summary>
    public WorkspaceViewModel? WorkspaceVM
    {
        get => _projectOps.WorkspaceVM;
        set => _projectOps.WorkspaceVM = value;
    }

    /// <summary>
    /// Saves the current project (and the active document editor first, if applicable),
    /// triggering Save As if there is no project path yet.
    /// <para>保存当前项目，若无路径则触发另存流程。先尝试保存当前活动的文档编辑器。</para>
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_currentTab == ActiveTab.Workspace && WorkspaceVM?.FindDocumentsPanePublic()?.ActiveDockable is { } document
            && DocumentSaveService.CanSave(document))
        {
            await DocumentSaveService.SaveAsync(document);
            return;
        }
        if (!await _projectOps.SaveAsync(saveActiveDocument: _currentTab == ActiveTab.Workspace))
        {
            await SaveAsAsync();
        }
    }

    /// <summary>
    /// If the active document has a SaveCommand and IsDirty, executes it.
    /// <para>若当前活动文档具有 SaveCommand 且 IsDirty，则执行保存。</para>
    /// </summary>
    private async Task SaveActiveDocumentAsync() => await _projectOps.SaveActiveDocumentAsync();

    /// <summary>
    /// Saves the solution under a new path, showing a file-save dialog, copying the image directory to the
    /// new root, then persisting.
    /// <para>另存为，弹出文件保存对话框选择新解决方案路径，复制镜像目录到新根目录后持久化。</para>
    /// </summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var path = await PickProjectFileAsync(isSave: true);
        if (path == null) return;
        await _projectOps.SaveAsAsync(path);
        RecordRecentProject(path);
    }

    /// <summary>
    /// Writes the complete project snapshot to the specified path.
    /// <para>将当前工程完整快照写入指定路径。</para>
    /// </summary>
    private async Task PersistToAsync(string path) => await _projectOps.PersistToAsync(path);

    /// <summary>
    /// Exports the current workspace contents, showing a file-save dialog to pick the export path.
    /// <para>导出当前工作区内容，弹出文件保存对话框选择导出路径。</para>
    /// </summary>
    /// <summary>
    /// Opens a file comparison: picks two files and sends the file-compare request message.
    /// <para>打开文件比较：先后选择两个文件并发送文件比较请求消息。</para>
    /// </summary>
    [RelayCommand]
    private async Task CompareFilesAsync()
    {
        var topLevel = TopLevel;
        if (topLevel == null) return;

        var left = await PickCompareFileAsync(topLevel);
        if (left == null) return;

        var right = await PickCompareFileAsync(topLevel);
        if (right == null) return;

        WeakReferenceMessenger.Default.Send(new OpenFileCompareRequestMessage { LeftPath = left, RightPath = right });
    }

    /// <summary>
    /// Opens a folder comparison: picks two folders and sends the folder-compare request message.
    /// <para>打开文件夹比较：先后选择两个文件夹并发送文件夹比较请求消息。</para>
    /// </summary>
    [RelayCommand]
    private async Task CompareFoldersAsync()
    {
        var topLevel = TopLevel;
        if (topLevel == null) return;

        var left = await PickCompareFolderAsync(topLevel);
        if (left == null) return;

        var right = await PickCompareFolderAsync(topLevel);
        if (right == null) return;

        WeakReferenceMessenger.Default.Send(new OpenFolderCompareRequestMessage { LeftDirectory = left, RightDirectory = right });
    }

    private static async Task<string?> PickCompareFileAsync(TopLevel topLevel)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Menu_CompareFiles"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private static async Task<string?> PickCompareFolderAsync(TopLevel topLevel)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("Menu_CompareFolders"),
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>
    /// Exports the current solution as a ZIP archive.
    /// <para>导出当前解决方案为 ZIP 存档。</para>
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var topLevel = TopLevel;
        if (topLevel is not Window owner) return;
        var root = ProjectContextService.Instance.RootDirectory;
        if (root is null)
        {
            await DialogService.ShowAsync(owner, _localizer.GetString("Menu_Export"),
                _localizer.GetString("FindReplace_NoProject"), Ursa.Controls.MessageBoxIcon.Information);
            return;
        }

        var files = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _localizer.GetString("Menu_Export"),
            DefaultExtension = "zip",
            FileTypeChoices =
            [
                new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });

        if (files?.TryGetLocalPath() is not { } output) return;
        try
        {
            if (root != ProjectContextService.Instance.RootDirectory) throw new IOException("The active project changed.");
            foreach (var document in WorkspaceSessionCapture.Enumerate(WorkspaceVM?.Layout).Where(DocumentSaveService.IsDirty).ToArray())
            {
                var path = document switch
                {
                    FileViewModel text => text.FilePath,
                    HexEditorViewModel hex => hex.FilePath,
                    DtbEditorViewModel dtb => dtb.FilePath,
                    ManifestEditorViewModel manifest => manifest.FilePath,
                    PartitionEditorViewModel partition => partition.FilePath,
                    _ => null
                };
                if (path is null || !Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
                        + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (!await DocumentSaveService.SaveAsync(document, owner)) return;
            }
            if (root != ProjectContextService.Instance.RootDirectory) throw new IOException("The active project changed.");
            if (!await _projectOps.SaveAsync(saveActiveDocument: false)) return;
            await _projectOps.ExportAsync(output);
            await DialogService.ShowAsync(owner, _localizer.GetString("Menu_Export"), output, Ursa.Controls.MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "Export", ex.Message);
            await DialogService.ShowAsync(owner, _localizer.GetString("Menu_Export"), UserError.Describe(ex), Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Imports images: shows a multi-select file dialog, then copies the chosen files into the project
    /// <c>images/</c> directory and adds them to the image list.
    /// <para>导入镜像，弹出文件多选对话框后复制所选文件到项目 <c>images/</c> 目录并加入镜像清单。</para>
    /// </summary>
    [RelayCommand]
    private async Task ImportImageAsync()
    {
        var topLevel = TopLevel;
        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _localizer.GetString("Menu_ImportImage"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.img", "*.bin", "*.cpio", "*.ofp", "*.ops", "*.ozip", "*.cpb", "*.simg", "*.sparse"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });
        if (files.Count == 0)
        {
            return;
        }

        await ImageViewModel.EnsureInstance().ImportAsync([.. files.Select(f => f.Path.LocalPath)]);
    }

    /// <summary>
    /// Exports the selected image: shows a save-file dialog to pick the target path, then copies the
    /// image out of the project directory.
    /// <para>导出选中镜像，弹出文件保存对话框选择目标路径后从项目目录复制出去。</para>
    /// </summary>
    [RelayCommand]
    private async Task ExportImageAsync()
    {
        var selected = ImageViewModel.EnsureInstance().SelectedImage;
        if (selected is null)
        {
            _log.Write(LogLevel.Warn, "Menu", "Export image aborted: no image selected");
            return;
        }

        var topLevel = TopLevel;
        if (topLevel == null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _localizer.GetString("Menu_ExportImage"),
            DefaultExtension = "img",
            FileTypeChoices =
            [
                new FilePickerFileType("Image") { Patterns = ["*.img", "*.bin"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });
        if (file == null)
        {
            return;
        }

        await ImageViewModel.EnsureInstance().ExportAsync(file.Path.LocalPath);
    }

    /// <summary>
    /// Exits the application by sending an exit message handled by App.
    /// <para>退出应用程序，发送退出消息由 App 处理。</para>
    /// </summary>
    [RelayCommand]
    private void Exit()
    {
        WeakReferenceMessenger.Default.Send(new ExitApplicationMessage());
    }

    /// <summary>Undoes edits in the current page. <para>撤销当前页面内的编辑。</para></summary>
    [RelayCommand]
    private async Task Undo()
    {
        if (_currentTab == ActiveTab.NodeEditor)
        {
            SendNodeEditorAction(NodeEditorAction.Undo);
            return;
        }

        if (CurrentTextEditor() is { } editor)
        {
            editor.Undo();
            return;
        }

        if (_projectOps.CanUndo)
            await _projectOps.UndoAsync();
    }

    /// <summary>Redoes edits in the current page. <para>重做当前页面内的编辑。</para></summary>
    [RelayCommand]
    private async Task Redo()
    {
        if (_currentTab == ActiveTab.NodeEditor)
        {
            SendNodeEditorAction(NodeEditorAction.Redo);
            return;
        }

        if (CurrentTextEditor() is { } editor)
        {
            editor.Redo();
            return;
        }

        if (_projectOps.CanRedo)
            await _projectOps.RedoAsync();
    }

    /// <summary>Cut selection: delegates to the active document first, falling back to the node editor. <para>剪切选中内容：优先委托给活动文档，否则转发到节点编辑器。</para></summary>
    [RelayCommand]
    private void Cut() => ExecuteEditAction(NodeEditorAction.Cut, static editable => editable.Cut());

    /// <summary>Copy selection: delegates to the active document first, falling back to the node editor. <para>复制选中内容：优先委托给活动文档，否则转发到节点编辑器。</para></summary>
    [RelayCommand]
    private void Copy() => ExecuteEditAction(NodeEditorAction.Copy, static editable => editable.Copy());

    /// <summary>Paste clipboard contents: delegates to the active document first, falling back to the node editor. <para>粘贴剪贴板内容：优先委托给活动文档，否则转发到节点编辑器。</para></summary>
    [RelayCommand]
    private void Paste() => ExecuteEditAction(NodeEditorAction.Paste, static editable => editable.Paste());

    /// <summary>Delete selection: delegates to the active document first, falling back to the node editor. <para>删除选中内容：优先委托给活动文档，否则转发到节点编辑器。</para></summary>
    [RelayCommand]
    private void Delete() => ExecuteEditAction(NodeEditorAction.Delete, static editable => editable.Delete());

    /// <summary>Select all: delegates to the active document first, falling back to the node editor. <para>全选：优先委托给活动文档，否则转发到节点编辑器。</para></summary>
    [RelayCommand]
    private void SelectAll() => ExecuteEditAction(NodeEditorAction.SelectAll, static editable => editable.SelectAll());

    private void ExecuteEditAction(NodeEditorAction action, Action<IEditable> edit)
    {
        if (TryGetEditable() is { } editable) edit(editable);
        else if (_currentTab == ActiveTab.NodeEditor)
            SendNodeEditorAction(action);
    }

    private void SendNodeEditorAction(NodeEditorAction action)
    {
        if (_projectOps.NodeEditorVM is { } editor)
        {
            WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(action, editor.EditorId));
        }
    }

    /// <summary>
    /// Attempts to get the editable view model of the currently active document.
    /// <para>尝试获取当前活动文档的可编辑视图模型。</para>
    /// </summary>
    private IEditable? TryGetEditable() => _currentTab == ActiveTab.Workspace
        ? WorkspaceVM?.FindDocumentsPanePublic()?.ActiveDockable as IEditable : null;

    private AvaloniaEdit.TextEditor? CurrentTextEditor() => _currentTab == ActiveTab.Workspace
        ? WorkspaceVM?.FindDocumentsPanePublic()?.ActiveDockable switch
        {
            FileViewModel { IsBinary: false } text => text.Editor,
            DtbEditorViewModel dtb => dtb.Editor,
            _ => null
        } : null;

    [RelayCommand]
    private void Find() => WeakReferenceMessenger.Default.Send(new OpenDocumentSearchMessage(ShowReplace: false));

    [RelayCommand]
    private void Replace() => WeakReferenceMessenger.Default.Send(new OpenDocumentSearchMessage(ShowReplace: true));

    /// <summary>Switch to the Workspace tab. <para>切换到工作区选项卡。</para></summary>
    [RelayCommand]
    private void GoToWorkspace()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
    }

    /// <summary>Switch to the Node Editor tab. <para>切换到节点编辑器选项卡。</para></summary>
    [RelayCommand]
    private void GoToNodeEditor()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.NodeEditor));
    }

    /// <summary>Switch to the Settings tab. <para>切换到设置选项卡。</para></summary>
    [RelayCommand]
    private void GoToSettings()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Settings));
    }

    /// <summary>Open or focus the workspace compare document. <para>打开或聚焦工作区对比文档。</para></summary>
    [RelayCommand]
    private void GoToCompare()
    {

        WeakReferenceMessenger.Default.Send(new OpenCompareRequestMessage { OpenInNewTab = true });
    }

    /// <summary>
    /// Workspace dock panel service; the Window menu items bind IsChecked to its visibility
    /// properties and invoke its show/hide commands.
    /// <para>工作区停靠面板服务；「窗口」菜单项通过其可见性属性绑定 IsChecked，并调用显示/隐藏命令。</para>
    /// </summary>
    public WorkspacePanelService WorkspacePanels => WorkspacePanelService.Instance;

    /// <summary>Show or hide the Project Explorer panel. <para>显示或隐藏项目资源管理器面板。</para></summary>
    [RelayCommand]
    private void ToggleProjectExplorer() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.ProjectExplorer);

    /// <summary>Show or hide the packing settings panel. <para>显示或隐藏打包设置面板。</para></summary>
    [RelayCommand]
    private void ToggleProjectSetting() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.ProjectSetting);

    /// <summary>Show or hide the workspace plugins panel.</summary>
    [RelayCommand]
    private void TogglePluginTool() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.PluginTool);

    /// <summary>Show or hide the image manager panel. <para>显示或隐藏镜像管理面板。</para></summary>
    [RelayCommand]
    private void ToggleImagesView() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.ImagesView);

    /// <summary>显示或隐藏 AI 助手。 Show or hide the AI assistant.</summary>
    [RelayCommand]
    private void ToggleAiAssistant() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.AiAssistant);

    /// <summary>Show or hide the properties panel. <para>显示或隐藏属性面板。</para></summary>
    [RelayCommand]
    private void ToggleProperties() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.Properties);

    /// <summary>Show or hide the workspace log panel. <para>显示或隐藏工作区日志面板。</para></summary>
    [RelayCommand]
    private void ToggleWorkspaceLog() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.WorkspaceLog);

    /// <summary>Show or hide the ADB log panel. <para>显示或隐藏 ADB 日志面板。</para></summary>
    [RelayCommand]
    private void ToggleAdbLog() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.AdbLog);

    /// <summary>显示或隐藏终端面板。 Show or hide the terminal panel.</summary>
    [RelayCommand]
    private void ToggleTerminal() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.Terminal);

    /// <summary>
    /// Opens the device manager window, bringing it to the foreground if already open.
    /// <para>打开设备管理器窗口；窗口已打开时前置聚焦。</para>
    /// </summary>
    [RelayCommand]
    private void OpenDevice()
    {
        _windows.ShowHostWindow(HostWindowKind.Device);
    }

    /// <summary>
    /// Opens the global log window, bringing it to the foreground if already open.
    /// <para>打开全局日志窗口；窗口已打开时前置聚焦。</para>
    /// </summary>
    [RelayCommand]
    private void OpenLogWindow()
    {
        _windows.ShowHostWindow(HostWindowKind.Log);
    }

    /// <summary>
    /// Opens the USB tree-view window, bringing it to the foreground if already open.
    /// <para>打开 USB 树视图窗口；窗口已打开时前置聚焦。</para>
    /// </summary>
    [RelayCommand]
    private void OpenUsbTree()
    {
        _windows.ShowHostWindow(HostWindowKind.UsbTree);
    }

    /// <summary>Opens or activates the independent key library.</summary>
    [RelayCommand]
    private void OpenKeyManager() => _windows.ShowHostWindow(HostWindowKind.KeyManager);

    /// <summary>Opens or activates the independent visual-plugin manager.</summary>
    [RelayCommand]
    private void OpenVisualPluginManager() => _windows.ShowHostWindow(HostWindowKind.VisualPluginManager);

    [RelayCommand]
    private void OpenApkSigner() => _windows.ShowHostWindow(HostWindowKind.ApkSigning);

    /// <summary>打开 Boot 修补窗口；窗口已打开时前置聚焦。</summary>
    [RelayCommand]
    private void OpenBootPatch() => _windows.ShowHostWindow(HostWindowKind.BootPatch);

    /// <summary>Opens or activates the AVB footer and A/B OTA tool.
    /// <para>打开或聚焦 AVB 尾部与 A/B OTA 工具。</para></summary>
    [RelayCommand]
    private void OpenAvbTool() => _windows.ShowHostWindow(HostWindowKind.AvbTool);

    /// <summary>打开文件校验器窗口；窗口已打开时前置聚焦。</summary>
    [RelayCommand]
    private void OpenFileChecksum() => _windows.ShowChecksumWindow();

    /// <summary>
    /// Toggles the minimap display state of the node editor. Only available when the Node Editor panel is active.
    /// <para>切换节点编辑器的小地图显示状态。仅在节点编辑器面板激活时可用。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ToggleMinimap()
    {
        SendNodeEditorAction(NodeEditorAction.ToggleMinimap);
    }

    /// <summary>Zoom in the node canvas. Only available when the Node Editor panel is active. <para>放大节点画布。仅在节点编辑器面板激活时可用。</para></summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ZoomIn()
    {
        SendNodeEditorAction(NodeEditorAction.ZoomIn);
    }

    /// <summary>Zoom out the node canvas. Only available when the Node Editor panel is active. <para>缩小节点画布。仅在节点编辑器面板激活时可用。</para></summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ZoomOut()
    {
        SendNodeEditorAction(NodeEditorAction.ZoomOut);
    }

    /// <summary>Reset the node canvas zoom. Only available when the Node Editor panel is active. <para>重置节点画布缩放。仅在节点编辑器面板激活时可用。</para></summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ResetZoom()
    {
        SendNodeEditorAction(NodeEditorAction.ResetZoom);
    }

    /// <summary>Run the current node workflow. Only available when the Node Editor panel is active. <para>运行当前节点工作流。仅在节点编辑器面板激活时可用。</para></summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void RunWorkflow()
    {
        SendNodeEditorAction(NodeEditorAction.Run);
    }

    /// <summary>Stop the running node workflow. Only available when the Node Editor panel is active. <para>停止正在执行的节点工作流。仅在节点编辑器面板激活时可用。</para></summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void StopWorkflow()
    {
        SendNodeEditorAction(NodeEditorAction.Stop);
    }

    /// <summary>Open online documentation. <para>打开在线文档。</para></summary>
    [RelayCommand]
    private void Documentation()
    {
        ExternalLinkService.TryOpen(string.Concat(BuildInfo.RepositoryUrl, "/wiki"));
    }

    /// <summary>Open the issue reporting page. <para>打开问题反馈页面。</para></summary>
    [RelayCommand]
    private void ReportIssue()
    {
        ExternalLinkService.TryOpen(string.Concat(BuildInfo.RepositoryUrl, "/issues"));
    }

    /// <summary>
    /// Shows the About dialog with version and description info.
    /// <para>显示关于对话框，包含版本与简介信息。</para>
    /// </summary>
    [RelayCommand]
    private void About() => _windows.ShowAboutDialog();

    /// <summary>
    /// Shows a file open/save dialog to pick a project file path, returning the chosen path or <c>null</c>.
    /// <para>弹出文件选择/保存对话框选取项目文件路径，返回所选路径或 <c>null</c>。</para>
    /// </summary>
    /// <param name="isSave">
    /// Whether this is a save dialog; false means open dialog.
    /// <para>是否为保存对话框；false 则为打开对话框。</para>
    /// </param>
    /// <returns>
    /// The chosen local file path; returns <c>null</c> when cancelled.
    /// <para>所选文件本地路径；取消时返回 <c>null</c>。</para>
    /// </returns>
    private static async Task<string?> PickProjectFileAsync(bool isSave)
    {
        var topLevel = TopLevel;
        if (topLevel == null) return null;

        if (isSave)
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LanguageService.Instance.GetString("Menu_NewProject"),
                DefaultExtension = "uotan",
                FileTypeChoices =
                [
                    new FilePickerFileType("UotanStudio Solution") { Patterns = ["*.uotan"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });
            return file?.Path.LocalPath;
        }
        else
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Menu_OpenProject"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("UotanStudio Solution") { Patterns = ["*.uotan"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }
    }

    /// <summary>
    /// Releases resources: unsubscribes from collection-changed events and unregisters message handlers.
    /// <para>释放资源：取消订阅集合变更事件，注销消息处理器。</para>
    /// </summary>
    public void Dispose()
    {
        RecentProjects.CollectionChanged -= _recentProjectsChangedHandler;
        WeakReferenceMessenger.Default.Unregister<ActiveTabChangedMessage>(this);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Main tab navigation target enum, triggered by the top menu.
/// <para>主选项卡导航目标枚举，由顶部菜单触发。</para>
/// </summary>
public enum NavigationTarget
{
    /// <summary>Device manager. <para>设备管理器。</para></summary>
    Device,
    /// <summary>Workspace. <para>工作区。</para></summary>
    Workspace,
    /// <summary>Node editor. <para>节点编辑器。</para></summary>
    NodeEditor,
    /// <summary>Global log panel. <para>全局日志面板。</para></summary>
    Log,
    /// <summary>Settings. <para>设置。</para></summary>
    Settings,
    /// <summary>ROM delivery configuration. <para>ROM 交付配置。</para></summary>
    Delivery
}

/// <summary>
/// Navigate-to-tab message, sent by the top menu to the MainView.
/// <para>导航到主选项卡的消息，由顶部菜单发送给 MainView。</para>
/// </summary>
public class NavigateToTabMessage
{
    /// <summary>Gets the navigation target. <para>获取要导航到的目标。</para></summary>
    public NavigationTarget Target { get; }

    /// <summary>
    /// Creates a navigation message.
    /// <para>创建导航消息。</para>
    /// </summary>
    /// <param name="target">The navigation target. <para>导航目标。</para></param>
    public NavigateToTabMessage(NavigationTarget target) => Target = target;
}

/// <summary>
/// Exit-application message, sent by the top menu to the App.
/// <para>退出应用程序消息，由顶部菜单发送给 App。</para>
/// </summary>
public class ExitApplicationMessage;

/// <summary>
/// Currently active bottom main panel enum, sent by MainView when the TabbedPage switches.
/// <para>当前激活的底栏主面板枚举，由 MainView 在 TabbedPage 切换时发送。</para>
/// </summary>
public enum ActiveTab
{
    /// <summary>Device manager panel. <para>设备管理器面板。</para></summary>
    Device,
    /// <summary>Workspace panel. <para>工作区面板。</para></summary>
    Workspace,
    /// <summary>Node editor panel. <para>节点编辑器面板。</para></summary>
    NodeEditor,
    /// <summary>Global log panel. <para>全局日志面板。</para></summary>
    Log,
    /// <summary>Settings panel. <para>设置面板。</para></summary>
    Settings,
    /// <summary>ROM delivery configuration panel. <para>ROM 交付配置面板。</para></summary>
    Delivery,
    /// <summary>Flash panel. <para>烧录面板。</para></summary>
    Flash
}

/// <summary>
/// Active-tab-changed message, sent by MainView to subscribers (e.g. MenuViewModel) to inform them of the currently active bottom main panel.
/// <para>激活面板切换消息，由 MainView 发送给订阅者（如 MenuViewModel）以告知当前激活的底栏主面板。</para>
/// </summary>
public class ActiveTabChangedMessage
{
    /// <summary>Gets the newly active tab. <para>获取新激活的面板。</para></summary>
    public ActiveTab Tab { get; }

    /// <summary>
    /// Creates an active-tab-changed message.
    /// <para>创建激活面板切换消息。</para>
    /// </summary>
    /// <param name="tab">The newly active tab. <para>新激活的面板。</para></param>
    public ActiveTabChangedMessage(ActiveTab tab) => Tab = tab;
}

/// <summary>
/// Node editor action enum, triggered by the top View menu.
/// <para>节点编辑器动作枚举，由顶部视图菜单触发。</para>
/// </summary>
public enum NodeEditorAction
{
    /// <summary>Toggle the minimap. <para>切换小地图。</para></summary>
    ToggleMinimap,
    /// <summary>Zoom in. <para>放大。</para></summary>
    ZoomIn,
    /// <summary>Zoom out. <para>缩小。</para></summary>
    ZoomOut,
    /// <summary>Reset zoom. <para>重置缩放。</para></summary>
    ResetZoom,
    /// <summary>Undo. <para>撤销。</para></summary>
    Undo,
    /// <summary>Redo. <para>重做。</para></summary>
    Redo,
    /// <summary>Cut selection. <para>剪切选中。</para></summary>
    Cut,
    /// <summary>Copy selection. <para>复制选中。</para></summary>
    Copy,
    /// <summary>Paste. <para>粘贴。</para></summary>
    Paste,
    /// <summary>Delete selection. <para>删除选中。</para></summary>
    Delete,
    /// <summary>Select all. <para>全选。</para></summary>
    SelectAll,
    /// <summary>Run the workflow. <para>运行工作流。</para></summary>
    Run,
    /// <summary>Stop the workflow. <para>停止工作流。</para></summary>
    Stop
}

/// <summary>
/// Node editor action message, sent by the top View menu to the NodeEditorViewModel.
/// <para>节点编辑器动作消息，由顶部视图菜单发送给 NodeEditorViewModel。</para>
/// </summary>
public class NodeEditorActionMessage
{
    /// <summary>Gets the node editor action. <para>获取节点编辑器动作。</para></summary>
    public NodeEditorAction Action { get; }
    public Guid EditorId { get; }

    /// <summary>
    /// Creates a node editor action message.
    /// <para>创建节点编辑器动作消息。</para>
    /// </summary>
    /// <param name="action">The action type. <para>动作类型。</para></param>
    public NodeEditorActionMessage(NodeEditorAction action, Guid editorId)
    {
        Action = action;
        EditorId = editorId;
    }
}
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Shell;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// <para>顶部菜单栏视图模型，提供文件、编辑、视图、工具与帮助菜单的所有命令实现。</para>
/// Top menu bar view model, providing command implementations for File, Edit, View, Tools, and Help menus.
/// </summary>
public partial class MenuViewModel : ViewModelBase, IDisposable
{
    private static TopLevel? TopLevel => TopLevelExtensions.GetTopLevel();

    /// <summary>
    /// <para>当前激活的底栏主面板；影响视图菜单中节点编辑器相关命令（小地图/缩放）的可用性。</para>
    /// Currently active bottom main panel; gates availability of node-editor-related View menu commands (minimap/zoom).
    /// </summary>
    /// <remarks>
    /// <para>由 <c>MainView</c> 通过 <see cref="ActiveTabChangedMessage"/> 在面板切换时更新；初始为 <see cref="ActiveTab.Workspace"/>（与 MainView 默认 SelectedIndex=3 对应）。</para>
    /// Updated by <c>MainView</c> via <see cref="ActiveTabChangedMessage"/> on tab switch; defaults to <see cref="ActiveTab.Workspace"/> (matching MainView default SelectedIndex=3).
    /// </remarks>
    private ActiveTab _currentTab = ActiveTab.Workspace;

    private readonly ProjectOperationsService _projectOps = ProjectOperationsService.Instance;

    /// <summary>
    /// <para>缓存的集合变更事件处理器，用于在 <see cref="Dispose"/> 中取消订阅。</para>
    /// Cached collection-changed handler so it can be unsubscribed in <see cref="Dispose"/>.
    /// </summary>
    private readonly NotifyCollectionChangedEventHandler _recentProjectsChangedHandler;

    /// <summary>
    /// <para>创建菜单视图模型，订阅激活面板切换消息以维护 <see cref="_currentTab"/>，并加载最近打开项目列表。</para>
    /// Creates the menu view model, subscribing to active-tab-changed messages to maintain <see cref="_currentTab"/>
    /// and loading the recent projects list.
    /// </summary>
    public MenuViewModel()
    {
        WeakReferenceMessenger.Default.Register<ActiveTabChangedMessage>(this, OnActiveTabChanged);
        _recentProjectsChangedHandler = (_, _) => OnPropertyChanged(nameof(HasRecentProjects));
        RecentProjects.CollectionChanged += _recentProjectsChangedHandler;
        LoadRecentProjects();
    }

    /// <summary>
    /// <para>最近打开的解决方案列表（最新在前），用于「最近打开」子菜单。</para>
    /// Recently opened solutions (most recent first), backing the "Open Recent" submenu.
    /// </summary>
    public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = [];

    /// <summary>
    /// <para>是否存在最近打开记录，驱动「最近打开」菜单项是否可用。</para>
    /// Whether any recent projects exist, gating the "Open Recent" menu item's availability.
    /// </summary>
    public bool HasRecentProjects => RecentProjects.Count > 0;

    /// <summary>
    /// <para>最多保留的最近打开项目数。</para>
    /// Maximum number of recent projects kept.
    /// </summary>
    private const int MaxRecentProjects = 10;

    /// <summary>
    /// <para>从应用设置加载最近打开项目列表（仅保留实际存在的文件）。</para>
    /// Loads the recent projects list from application settings, keeping only entries whose files still exist.
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
    /// <para>记录一个最近打开的解决方案：去重置顶、裁剪数量并持久化。</para>
    /// Records a recently opened solution: deduplicates to the top, trims the list and persists it.
    /// </summary>
    /// <param name="path"><para>解决方案文件完整路径。</para>The full solution file path.</param>
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
    /// <para>将最近打开项目列表持久化到应用设置（合并写入，不覆盖其他设置项）。</para>
    /// Persists the recent projects list to application settings (merged write, preserving other settings).
    /// </summary>
    private void PersistRecentProjects()
    {
        SettingsPersistenceService.Instance.UpdateRecentProjects(RecentProjects.Select(e => e.Path).ToList());
    }

    /// <summary>
    /// <para>创建一个最近项目条目，其打开命令直接调用 <see cref="OpenProjectFromPathAsync"/>；
    /// 打开失败（如文件已被删除）时自动移除该条目并持久化。</para>
    /// Creates a recent project entry whose open command invokes <see cref="OpenProjectFromPathAsync"/>;
    /// on failure (e.g. the file was deleted) the entry is removed and persisted automatically.
    /// </summary>
    /// <param name="path"><para>解决方案文件完整路径。</para>The full solution file path.</param>
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
    /// <para>响应激活面板切换：更新 <see cref="_currentTab"/> 并通知节点编辑器相关命令重新计算可用性。</para>
    /// Responds to active-tab changes: updates <see cref="_currentTab"/> and notifies node-editor-related commands to re-evaluate their availability.
    /// </summary>
    /// <param name="recipient"><para>消息接收者。</para>The message recipient.</param>
    /// <param name="message"><para>包含新激活面板的消息。</para>The message carrying the newly active tab.</param>
    private void OnActiveTabChanged(object recipient, ActiveTabChangedMessage message)
    {
        if (_currentTab == message.Tab) return;
        _currentTab = message.Tab;
        // 通知节点编辑器相关命令重新评估 CanExecute
        ToggleMinimapCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ResetZoomCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// <para>当前是否在节点编辑器面板；仅供 <c>CanExecute</c> 私有方法使用。</para>
    /// Whether the Node Editor panel is currently active; used solely by <c>CanExecute</c> helpers.
    /// </summary>
    private bool IsNodeEditorActive => _currentTab == ActiveTab.NodeEditor;

    // ===== 文件 / File =====

    /// <summary>
    /// <para>创建一个新解决方案，弹出文件保存对话框选择 <c>.uotan</c> 路径，打开解决方案上下文后清空画布、
    /// 镜像清单与历史，并写入空的固件项目、节点项目与解决方案文件。</para>
    /// Creates a new solution by showing a file-save dialog to pick the <c>.uotan</c> path, opening the
    /// solution context, clearing the canvas, image list and history, then writing empty firmware project,
    /// node project and solution files.
    /// </summary>
    [RelayCommand]
    private async Task NewProjectAsync()
    {
        var path = await PickProjectFileAsync(isSave: true);
        if (path == null)
        {
            return;
        }

        await CreateNewSolutionAsync(path);
    }

    /// <summary>
    /// <para>在指定路径创建并持久化一个空解决方案（固件项目 + 节点项目 + 状态目录）。</para>
    /// Creates and persists an empty solution (firmware project + node project + state directory) at the
    /// specified path.
    /// </summary>
    private async Task CreateNewSolutionAsync(string path)
    {
        await _projectOps.CreateNewSolutionAsync(path);
        RecordRecentProject(path);
    }

    /// <summary>
    /// <para>打开现有解决方案，弹出文件打开对话框选择 <c>.uotan</c> 文件，恢复镜像清单、打包设置、画布与操作历史。</para>
    /// Opens an existing solution by showing a file-open dialog to pick a <c>.uotan</c> file, restoring the
    /// image list, packing settings, canvas and operation history.
    /// </summary>
    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var path = await PickProjectFileAsync(isSave: false);
        if (path == null)
        {
            return;
        }

        await OpenProjectFromPathAsync(path);
    }

    /// <summary>
    /// <para>从指定路径打开解决方案（供菜单与命令行共用）：加载固件项目与节点项目，重建镜像清单、打包设置、
    /// 画布与操作历史。返回是否成功。</para>
    /// Opens the solution at the specified path (shared by the menu and the command line): loads the firmware
    /// and node projects, rebuilding the image list, packing settings, canvas and operation history. Returns
    /// whether the open succeeded.
    /// </summary>
    public async Task<bool> OpenProjectFromPathAsync(string path)
    {
        var result = await _projectOps.OpenProjectFromPathAsync(path);
        if (result) RecordRecentProject(path);
        return result;
    }

    /// <summary>
    /// <para>将若干文件导入当前解决方案的镜像清单（供命令行与菜单共用）。</para>
    /// Imports the given files into the current solution's image list (shared by the command line and the menu).
    /// </summary>
    public async Task ImportFilesAsync(string[] paths)
    {
        await _projectOps.ImportFilesAsync(paths);
    }

    /// <summary>
    /// <para>向当前解决方案添加新子项目：创建子项目目录结构（<c>images/</c>、<c>output/</c>、<c>.uotanfw</c>），
    /// 重名时拒绝操作并记录全局日志。</para>
    /// Adds a new sub-project to the current solution: creates the sub-project directory structure
    /// (<c>images/</c>, <c>output/</c>, <c>.uotanfw</c>); refuses and logs on name collision.
    /// </summary>
    [RelayCommand]
    private async Task AddSubProjectAsync()
    {
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || context.RootDirectory is null)
        {
            Services.LogService.Instance.Write(LogLevel.Warn, "Menu", "Add sub-project aborted: no solution is open  添加子项目中止：尚未打开解决方案");
            return;
        }

        var topLevel = TopLevel;
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Menu_AddSubProject"),
            SuggestedFileName = "NewDevice.uotanfw",
            FileTypeChoices =
            [
                new FilePickerFileType("UotanStudio Firmware Project") { Patterns = ["*.uotanfw"] }
            ]
        });

        if (result is null) return;

        var fwPath = result.TryGetLocalPath();
        if (string.IsNullOrEmpty(fwPath)) return;

        await _projectOps.AddSubProjectAsync(fwPath);
    }

    /// <summary>
    /// <para>当前工程文件路径，由保存/打开操作填充。</para>
    /// Current project file path, populated by save/open operations.
    /// </summary>
    private string? _currentProjectPath
    {
        get => _projectOps.CurrentProjectPath;
        set => _projectOps.CurrentProjectPath = value;
    }

    /// <summary>
    /// <para>当前节点编辑器视图模型引用，由外部设置以便持久化访问画布。</para>
    /// Current node editor view model reference, set externally so persistence can access the canvas.
    /// </summary>
    public NodeEditorViewModel? NodeEditorVM
    {
        get => _projectOps.NodeEditorVM;
        set => _projectOps.NodeEditorVM = value;
    }

    /// <summary>
    /// <para>当前工作区视图模型引用，由外部设置以便 Ctrl+S 保存活动文档。</para>
    /// Current workspace view model reference, set externally so Ctrl+S can save the active document.
    /// </summary>
    public WorkspaceViewModel? WorkspaceVM
    {
        get => _projectOps.WorkspaceVM;
        set => _projectOps.WorkspaceVM = value;
    }

    /// <summary>
    /// <para>保存当前项目，若无路径则触发另存流程。先尝试保存当前活动的文档编辑器。</para>
    /// Saves the current project (and the active document editor first, if applicable),
    /// triggering Save As if there is no project path yet.
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
    /// <para>若当前活动文档具有 SaveCommand 且 IsDirty，则执行保存。</para>
    /// If the active document has a SaveCommand and IsDirty, executes it.
    /// </summary>
    private async Task SaveActiveDocumentAsync() => await _projectOps.SaveActiveDocumentAsync();

    /// <summary>
    /// <para>另存为，弹出文件保存对话框选择新解决方案路径，复制镜像目录到新根目录后持久化。</para>
    /// Saves the solution under a new path, showing a file-save dialog, copying the image directory to the
    /// new root, then persisting.
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
    /// <para>将当前工程完整快照写入指定路径。</para>
    /// Writes the complete project snapshot to the specified path.
    /// </summary>
    private async Task PersistToAsync(string path) => await _projectOps.PersistToAsync(path);

    /// <summary>
    /// <para>导出当前工作区内容，弹出文件保存对话框选择导出路径。</para>
    /// Exports the current workspace contents, showing a file-save dialog to pick the export path.
    /// </summary>
    /// <summary>
    /// <para>打开文件比较：先后选择两个文件并发送文件比较请求消息。</para>
    /// Opens a file comparison: picks two files and sends the file-compare request message.
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
    /// <para>打开文件夹比较：先后选择两个文件夹并发送文件夹比较请求消息。</para>
    /// Opens a folder comparison: picks two folders and sends the folder-compare request message.
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
    /// <para>导出当前解决方案为 ZIP 存档。</para>
    /// Exports the current solution as a ZIP archive.
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var topLevel = TopLevel;
        if (topLevel is not Window owner) return;
        var root = ProjectContextService.Instance.RootDirectory;
        if (root is null)
        {
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Menu_Export"),
                LanguageService.Instance.GetString("FindReplace_NoProject"), Ursa.Controls.MessageBoxIcon.Information);
            return;
        }

        var files = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Menu_Export"),
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
            await Task.Run(() => ProjectArchiveService.ExportAsync(root, output));
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Menu_Export"), output, Ursa.Controls.MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Export", ex.Message);
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Menu_Export"), ex.Message, Ursa.Controls.MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// <para>导入镜像，弹出文件多选对话框后复制所选文件到项目 <c>images/</c> 目录并加入镜像清单。</para>
    /// Imports images: shows a multi-select file dialog, then copies the chosen files into the project
    /// <c>images/</c> directory and adds them to the image list.
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
            Title = LanguageService.Instance.GetString("Menu_ImportImage"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.img", "*.bin", "*.cpio", "*.ofp", "*.ops", "*.ozip", "*.simg", "*.sparse"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });
        if (files.Count == 0)
        {
            return;
        }

        await ImageViewModel.EnsureInstance().ImportAsync(files.Select(f => f.Path.LocalPath).ToArray());
    }

    /// <summary>
    /// <para>导出选中镜像，弹出文件保存对话框选择目标路径后从项目目录复制出去。</para>
    /// Exports the selected image: shows a save-file dialog to pick the target path, then copies the
    /// image out of the project directory.
    /// </summary>
    [RelayCommand]
    private async Task ExportImageAsync()
    {
        var selected = ImageViewModel.EnsureInstance().SelectedImage;
        if (selected is null)
        {
            Services.LogService.Instance.Write(LogLevel.Warn, "Menu", "Export image aborted: no image selected  导出镜像中止：未选中镜像");
            return;
        }

        var topLevel = TopLevel;
        if (topLevel == null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageService.Instance.GetString("Menu_ExportImage"),
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
    /// <para>退出应用程序，发送退出消息由 App 处理。</para>
    /// Exits the application by sending an exit message handled by App.
    /// </summary>
    [RelayCommand]
    private void Exit()
    {
        WeakReferenceMessenger.Default.Send(new ExitApplicationMessage());
    }

    // ===== 编辑 / Edit =====

    /// <summary><para>撤销当前页面内的编辑。</para>Undoes edits in the current page.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (_currentTab == ActiveTab.NodeEditor)
            WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Undo));
        else CurrentTextEditor()?.Undo();
    }

    /// <summary><para>重做当前页面内的编辑。</para>Redoes edits in the current page.</summary>
    [RelayCommand]
    private void Redo()
    {
        if (_currentTab == ActiveTab.NodeEditor)
            WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Redo));
        else CurrentTextEditor()?.Redo();
    }

    /// <summary><para>剪切选中内容：优先委托给活动文档，否则转发到节点编辑器。</para>Cut selection: delegates to the active document first, falling back to the node editor.</summary>
    [RelayCommand]
    private void Cut()
    {
        if (TryGetEditable() is { } e) e.Cut();
        else if (_currentTab == ActiveTab.NodeEditor) WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Cut));
    }

    /// <summary><para>复制选中内容：优先委托给活动文档，否则转发到节点编辑器。</para>Copy selection: delegates to the active document first, falling back to the node editor.</summary>
    [RelayCommand]
    private void Copy()
    {
        if (TryGetEditable() is { } e) e.Copy();
        else if (_currentTab == ActiveTab.NodeEditor) WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Copy));
    }

    /// <summary><para>粘贴剪贴板内容：优先委托给活动文档，否则转发到节点编辑器。</para>Paste clipboard contents: delegates to the active document first, falling back to the node editor.</summary>
    [RelayCommand]
    private void Paste()
    {
        if (TryGetEditable() is { } e) e.Paste();
        else if (_currentTab == ActiveTab.NodeEditor) WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Paste));
    }

    /// <summary><para>删除选中内容：优先委托给活动文档，否则转发到节点编辑器。</para>Delete selection: delegates to the active document first, falling back to the node editor.</summary>
    [RelayCommand]
    private void Delete()
    {
        if (TryGetEditable() is { } e) e.Delete();
        else if (_currentTab == ActiveTab.NodeEditor) WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.Delete));
    }

    /// <summary><para>全选：优先委托给活动文档，否则转发到节点编辑器。</para>Select all: delegates to the active document first, falling back to the node editor.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        if (TryGetEditable() is { } e) e.SelectAll();
        else if (_currentTab == ActiveTab.NodeEditor) WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.SelectAll));
    }

    /// <summary>
    /// <para>尝试获取当前活动文档的可编辑视图模型。</para>
    /// Attempts to get the editable view model of the currently active document.
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

    // ===== 视图 / View =====

    /// <summary><para>切换到工作区选项卡。</para>Switch to the Workspace tab.</summary>
    [RelayCommand]
    private void GoToWorkspace()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Workspace));
    }

    /// <summary><para>切换到节点编辑器选项卡。</para>Switch to the Node Editor tab.</summary>
    [RelayCommand]
    private void GoToNodeEditor()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.NodeEditor));
    }

    /// <summary><para>切换到设置选项卡。</para>Switch to the Settings tab.</summary>
    [RelayCommand]
    private void GoToSettings()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTabMessage(NavigationTarget.Settings));
    }

    /// <summary><para>打开或聚焦工作区对比文档。</para>Open or focus the workspace compare document.</summary>
    [RelayCommand]
    private void GoToCompare()
    {
        WeakReferenceMessenger.Default.Send(new OpenCompareRequestMessage());
    }

    // ===== 窗口 / Window =====

    /// <summary>
    /// <para>工作区停靠面板服务；「窗口」菜单项通过其可见性属性绑定 IsChecked，并调用显示/隐藏命令。</para>
    /// Workspace dock panel service; the Window menu items bind IsChecked to its visibility
    /// properties and invoke its show/hide commands.
    /// </summary>
    public WorkspacePanelService WorkspacePanels => WorkspacePanelService.Instance;

    /// <summary><para>显示或隐藏项目资源管理器面板。</para>Show or hide the Project Explorer panel.</summary>
    [RelayCommand]
    private void ToggleProjectExplorer() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.ProjectExplorer);

    /// <summary><para>显示或隐藏打包设置面板。</para>Show or hide the packing settings panel.</summary>
    [RelayCommand]
    private void ToggleProjectSetting() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.ProjectSetting);

    /// <summary><para>显示或隐藏设备面板。</para>Show or hide the device panel.</summary>
    [RelayCommand]
    private void ToggleDevicePanel() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.Device);

    /// <summary><para>显示或隐藏镜像管理面板。</para>Show or hide the image manager panel.</summary>
    [RelayCommand]
    private void ToggleImagesView() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.ImagesView);

    /// <summary>显示或隐藏 AI 助手。 Show or hide the AI assistant.</summary>
    [RelayCommand]
    private void ToggleAiAssistant() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.AiAssistant);

    /// <summary><para>显示或隐藏属性面板。</para>Show or hide the properties panel.</summary>
    [RelayCommand]
    private void ToggleProperties() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.Properties);

    /// <summary><para>显示或隐藏工作区日志面板。</para>Show or hide the workspace log panel.</summary>
    [RelayCommand]
    private void ToggleWorkspaceLog() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.WorkspaceLog);

    /// <summary><para>显示或隐藏 ADB 日志面板。</para>Show or hide the ADB log panel.</summary>
    [RelayCommand]
    private void ToggleAdbLog() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.AdbLog);

    /// <summary>显示或隐藏终端面板。 Show or hide the terminal panel.</summary>
    [RelayCommand]
    private void ToggleTerminal() => WorkspacePanelService.Instance.Toggle(WorkspacePanelService.Terminal);

    // ===== 工具 / Tools =====

    /// <summary>
    /// <para>打开设备管理器窗口；窗口已打开时前置聚焦。</para>
    /// Opens the device manager window, bringing it to the foreground if already open.
    /// </summary>
    [RelayCommand]
    private void OpenDevice()
    {
        ShowWindow(ref _deviceWindow, () => new DeviceWindow());
    }

    /// <summary>
    /// <para>打开全局日志窗口；窗口已打开时前置聚焦。</para>
    /// Opens the global log window, bringing it to the foreground if already open.
    /// </summary>
    [RelayCommand]
    private void OpenLogWindow()
    {
        ShowWindow(ref _logWindow, () => new LogWindow());
    }

    /// <summary>
    /// <para>当前已打开的设备管理器窗口引用，用于单例前置与生命周期释放。</para>
    /// Reference to the currently open device manager window, used for singleton focusing and lifecycle release.
    /// </summary>
    private static Window? _deviceWindow;

    /// <summary>
    /// <para>当前已打开的全局日志窗口引用，用于单例前置与生命周期释放。</para>
    /// Reference to the currently open global log window, used for singleton focusing and lifecycle release.
    /// </summary>
    private static Window? _logWindow;

    /// <summary>
    /// <para>显示辅助工具窗口：若尚未打开则创建并显示，否则前置并恢复正常窗口状态。</para>
    /// Shows a helper tool window: creates and shows it if not open, otherwise brings it to the foreground and restores the normal window state.
    /// </summary>
    /// <param name="holder"><para>窗口引用字段。</para>The window reference field.</param>
    /// <param name="factory"><para>窗口工厂，用于创建新窗口实例。</para>The factory creating a new window instance.</param>
    private static void ShowWindow(ref Window? holder, Func<Window> factory)
    {
        if (holder == null || !holder.IsVisible)
        {
            // 释放对已关闭窗口的旧引用，允许其被回收后再创建新窗口
            holder = null;
            holder = factory();
            holder.Show();
        }
        else
        {
            holder.WindowState = WindowState.Normal;
            holder.Activate();
        }
    }

    /// <summary>
    /// <para>切换节点编辑器的小地图显示状态。仅在节点编辑器面板激活时可用。</para>
    /// Toggles the minimap display state of the node editor. Only available when the Node Editor panel is active.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ToggleMinimap()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ToggleMinimap));
    }

    /// <summary><para>放大节点画布。仅在节点编辑器面板激活时可用。</para>Zoom in the node canvas. Only available when the Node Editor panel is active.</summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ZoomIn()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ZoomIn));
    }

    /// <summary><para>缩小节点画布。仅在节点编辑器面板激活时可用。</para>Zoom out the node canvas. Only available when the Node Editor panel is active.</summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ZoomOut()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ZoomOut));
    }

    /// <summary><para>重置节点画布缩放。仅在节点编辑器面板激活时可用。</para>Reset the node canvas zoom. Only available when the Node Editor panel is active.</summary>
    [RelayCommand(CanExecute = nameof(IsNodeEditorActive))]
    private void ResetZoom()
    {
        WeakReferenceMessenger.Default.Send(new NodeEditorActionMessage(NodeEditorAction.ResetZoom));
    }

    // ===== 帮助 / Help =====

    /// <summary><para>打开在线文档。</para>Open online documentation.</summary>
    [RelayCommand]
    private async Task Documentation()
    {
        await OpenUrlAsync("https://github.com/UotanStudio/UotanStudio/wiki");
    }

    /// <summary><para>打开问题反馈页面。</para>Open the issue reporting page.</summary>
    [RelayCommand]
    private async Task ReportIssue()
    {
        await OpenUrlAsync("https://github.com/UotanStudio/UotanStudio/issues");
    }

    /// <summary>
    /// <para>使用宿主系统浏览器打开指定 URL，失败时写入统一日志。</para>
    /// Opens the specified URL using the host system browser, logging on failure.
    /// </summary>
    private static Task OpenUrlAsync(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (System.Exception ex)
        {
            Services.LogService.Instance.Write(LogLevel.Error, "Menu", $"Failed to open URL '{url}': {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// <para>显示关于对话框，包含版本与简介信息。</para>
    /// Shows the About dialog with version and description info.
    /// </summary>
    [RelayCommand]
    private void About()
    {
        var dialog = new AboutDialog();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window)
        {
            dialog.ShowDialog(window);
        }
        else if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime)
        {
            dialog.Show();
        }
    }

    // ===== 辅助方法 =====

    /// <summary>
    /// <para>弹出文件选择/保存对话框选取项目文件路径，返回所选路径或 <c>null</c>。</para>
    /// Shows a file open/save dialog to pick a project file path, returning the chosen path or <c>null</c>.
    /// </summary>
    /// <param name="isSave">
    /// <para>是否为保存对话框；false 则为打开对话框。</para>
    /// Whether this is a save dialog; false means open dialog.
    /// </param>
    /// <returns>
    /// <para>所选文件本地路径；取消时返回 <c>null</c>。</para>
    /// The chosen local file path; returns <c>null</c> when cancelled.
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
    /// <para>释放资源：取消订阅集合变更事件，注销消息处理器。</para>
    /// Releases resources: unsubscribes from collection-changed events and unregisters message handlers.
    /// </summary>
    public void Dispose()
    {
        RecentProjects.CollectionChanged -= _recentProjectsChangedHandler;
        WeakReferenceMessenger.Default.Unregister<ActiveTabChangedMessage>(this);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// <para>主选项卡导航目标枚举，由顶部菜单触发。</para>
/// Main tab navigation target enum, triggered by the top menu.
/// </summary>
public enum NavigationTarget
{
    /// <summary><para>设备管理器。</para>Device manager.</summary>
    Device,
    /// <summary><para>工作区。</para>Workspace.</summary>
    Workspace,
    /// <summary><para>节点编辑器。</para>Node editor.</summary>
    NodeEditor,
    /// <summary><para>全局日志面板。</para>Global log panel.</summary>
    Log,
    /// <summary><para>设置。</para>Settings.</summary>
    Settings
}

/// <summary>
/// <para>导航到主选项卡的消息，由顶部菜单发送给 MainView。</para>
/// Navigate-to-tab message, sent by the top menu to the MainView.
/// </summary>
public class NavigateToTabMessage
{
    /// <summary><para>获取要导航到的目标。</para>Gets the navigation target.</summary>
    public NavigationTarget Target { get; }

    /// <summary>
    /// <para>创建导航消息。</para>
    /// Creates a navigation message.
    /// </summary>
    /// <param name="target"><para>导航目标。</para>The navigation target.</param>
    public NavigateToTabMessage(NavigationTarget target) => Target = target;
}

/// <summary>
/// <para>退出应用程序消息，由顶部菜单发送给 App。</para>
/// Exit-application message, sent by the top menu to the App.
/// </summary>
public class ExitApplicationMessage;

/// <summary>
/// <para>当前激活的底栏主面板枚举，由 MainView 在 TabbedPage 切换时发送。</para>
/// Currently active bottom main panel enum, sent by MainView when the TabbedPage switches.
/// </summary>
public enum ActiveTab
{
    /// <summary><para>设备管理器面板。</para>Device manager panel.</summary>
    Device,
    /// <summary><para>工作区面板。</para>Workspace panel.</summary>
    Workspace,
    /// <summary><para>节点编辑器面板。</para>Node editor panel.</summary>
    NodeEditor,
    /// <summary><para>全局日志面板。</para>Global log panel.</summary>
    Log,
    /// <summary><para>设置面板。</para>Settings panel.</summary>
    Settings
}

/// <summary>
/// <para>激活面板切换消息，由 MainView 发送给订阅者（如 MenuViewModel）以告知当前激活的底栏主面板。</para>
/// Active-tab-changed message, sent by MainView to subscribers (e.g. MenuViewModel) to inform them of the currently active bottom main panel.
/// </summary>
public class ActiveTabChangedMessage
{
    /// <summary><para>获取新激活的面板。</para>Gets the newly active tab.</summary>
    public ActiveTab Tab { get; }

    /// <summary>
    /// <para>创建激活面板切换消息。</para>
    /// Creates an active-tab-changed message.
    /// </summary>
    /// <param name="tab"><para>新激活的面板。</para>The newly active tab.</param>
    public ActiveTabChangedMessage(ActiveTab tab) => Tab = tab;
}

/// <summary>
/// <para>节点编辑器动作枚举，由顶部视图菜单触发。</para>
/// Node editor action enum, triggered by the top View menu.
/// </summary>
public enum NodeEditorAction
{
    /// <summary><para>切换小地图。</para>Toggle the minimap.</summary>
    ToggleMinimap,
    /// <summary><para>放大。</para>Zoom in.</summary>
    ZoomIn,
    /// <summary><para>缩小。</para>Zoom out.</summary>
    ZoomOut,
    /// <summary><para>重置缩放。</para>Reset zoom.</summary>
    ResetZoom,
    /// <summary><para>撤销。</para>Undo.</summary>
    Undo,
    /// <summary><para>重做。</para>Redo.</summary>
    Redo,
    /// <summary><para>剪切选中。</para>Cut selection.</summary>
    Cut,
    /// <summary><para>复制选中。</para>Copy selection.</summary>
    Copy,
    /// <summary><para>粘贴。</para>Paste.</summary>
    Paste,
    /// <summary><para>删除选中。</para>Delete selection.</summary>
    Delete,
    /// <summary><para>全选。</para>Select all.</summary>
    SelectAll
}

/// <summary>
/// <para>节点编辑器动作消息，由顶部视图菜单发送给 NodeEditorViewModel。</para>
/// Node editor action message, sent by the top View menu to the NodeEditorViewModel.
/// </summary>
public class NodeEditorActionMessage
{
    /// <summary><para>获取节点编辑器动作。</para>Gets the node editor action.</summary>
    public NodeEditorAction Action { get; }

    /// <summary>
    /// <para>创建节点编辑器动作消息。</para>
    /// Creates a node editor action message.
    /// </summary>
    /// <param name="action"><para>动作类型。</para>The action type.</param>
    public NodeEditorActionMessage(NodeEditorAction action) => Action = action;
}
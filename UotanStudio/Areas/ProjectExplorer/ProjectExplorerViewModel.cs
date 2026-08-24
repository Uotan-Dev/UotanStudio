using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Core.Inspector;
using UotanStudio.Core.Projects;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.ProjectExplorer;

/// <summary>
/// <para>项目资源管理器视图模型：展示当前工程的目录/部件树与最近工程列表，
/// 提供新建与打开工程动作。</para>
/// Project explorer view model: shows the current project's directory/component
/// tree and the recent projects list, with create/open actions.
/// </summary>
public partial class ProjectExplorerViewModel : ViewModelBase
{
    /// <summary>
    /// <para>获取当前激活的资源管理器实例，供主菜单新建/打开/保存工程复用其上下文。</para>
    /// Gets the active explorer instance, reused by the main menu for create/open/save.
    /// </summary>
    public static ProjectExplorerViewModel? Instance { get; private set; }

    /// <summary>
    /// <para>获取工程树的根节点集合。</para>
    /// Gets the root nodes of the project tree.
    /// </summary>
    public ObservableCollection<ProjectTreeItem> Tree { get; } = [];

    /// <summary>
    /// <para>获取最近工程路径集合。</para>
    /// Gets the recent project paths.
    /// </summary>
    public ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>
    /// <para>获取或设置是否处于忙碌（异步操作期间）。</para>
    /// Gets or sets whether the explorer is busy (during async operations).
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// <para>获取或设置状态文本。</para>
    /// Gets or sets the status text.
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// <para>获取或设置当前选中的工程树条目；变化时广播 <see cref="SelectionChangedMessage"/>。</para>
    /// Gets or sets the currently selected project tree item; broadcasts a
    /// <see cref="SelectionChangedMessage"/> when it changes.
    /// </summary>
    [ObservableProperty]
    private ProjectTreeItem? _selectedItem;

    /// <summary>
    /// <para>获取当前工程名称；未打开时为空。</para>
    /// Gets the current project name; empty when no project is open.
    /// </summary>
    public string? ProjectName => _openedName;

    private string? _openedName;

    /// <summary>
    /// <para>初始化项目资源管理器并刷新最近列表。</para>
    /// Initializes the project explorer and refreshes the recent list.
    /// </summary>
    public ProjectExplorerViewModel()
    {
        Instance = this;
        _ = RefreshRecent();
    }

    partial void OnSelectedItemChanged(ProjectTreeItem? value) =>
        WeakReferenceMessenger.Default.Send(new SelectionChangedMessage(value));

    /// <summary>
    /// <para>刷新最近工程项目列表。</para>
    /// Refreshes the recent projects list.
    /// </summary>
    [RelayCommand]
    private async Task<bool> RefreshRecent()
    {
        RecentProjects.Clear();
        foreach (var path in FirmwareProjectService.RecentProjects())
        {
            RecentProjects.Add(path);
        }

        // 有打开工程时顺带重建工程树，使外部文件变更与导入结果即时反映到树上。
        // When a project is open, also rebuild the tree so external file changes and
        // import results are reflected immediately.
        var openPath = FirmwareProjectService.OpenPath;
        if (!string.IsNullOrWhiteSpace(openPath))
        {
            var project = await Task.Run(() => FirmwareProjectService.LoadProject(openPath));
            if (project is not null)
            {
                BuildTree(project);
            }
        }

        OnPropertyChanged(nameof(ProjectName));
        return true;
    }

    /// <summary>
    /// <para>打开一个工程文件并重建工程树。</para>
    /// Opens a project file and rebuilds the project tree.
    /// </summary>
    /// <param name="path"><para>工程文件路径。</para>The project file path.</param>
    public async Task OpenProjectAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var project = await Task.Run(() => FirmwareProjectService.LoadProject(path));
            if (project is null)
            {
                StatusText = LanguageService.Instance.GetString("Project_OpenFailed");
                return;
            }

            BuildTree(project);
            await RefreshRecent();
            StatusText = LanguageService.Instance.GetString("Project_Opened");
            LogService.Instance.Write(LogLevel.Info, "ProjectExplorer", $"Opened project: {project.Name}");
            WeakReferenceMessenger.Default.Send(new ProjectOpenedMessage());
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>新建并保存一个工程，随后重建工程树。</para>
    /// Creates and saves a new project, then rebuilds the tree.
    /// </summary>
    /// <param name="directory"><para>工程目录。</para>Project directory.</param>
    /// <param name="name"><para>工程名称。</para>Project name.</param>
    public async Task CreateProjectAsync(string? directory, string? name)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var project = await Task.Run(() =>
            {
                var p = FirmwareProjectService.CreateProject(directory, name);
                return FirmwareProjectService.SaveProject(p) ? p : null;
            });
            if (project is null)
            {
                StatusText = LanguageService.Instance.GetString("Project_SaveFailed");
                return;
            }

            BuildTree(project);
            await RefreshRecent();
            StatusText = string.Format(LanguageService.Instance.GetString("Project_Created"), project.Name);
            LogService.Instance.Write(LogLevel.Info, "ProjectExplorer", $"Created project: {project.Name}");
            WeakReferenceMessenger.Default.Send(new ProjectOpenedMessage());
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>保存当前打开的工程到磁盘；无打开工程时返回 false。</para>
    /// Saves the currently open project to disk; returns false when no project is open.
    /// </summary>
    /// <returns>保存是否成功。<para>Whether the save succeeded.</para></returns>
    public async Task<bool> SaveAsync()
    {
        if (FirmwareProjectService.OpenPath is null)
        {
            StatusText = LanguageService.Instance.GetString("Project_SaveNoProject");
            return false;
        }

        IsBusy = true;
        try
        {
            var ok = await Task.Run(() =>
            {
                var project = FirmwareProjectService.LoadProject(FirmwareProjectService.OpenPath);
                return project is not null && FirmwareProjectService.SaveProject(project);
            });
            if (ok)
            {
                await RefreshRecent();
                StatusText = LanguageService.Instance.GetString("Project_Saved");
                var savedName = FirmwareProjectService.CurrentProject?.Name;
                LogService.Instance.Write(LogLevel.Info, "ProjectExplorer",
                    $"Saved project: {savedName}");
            }
            else
            {
                StatusText = LanguageService.Instance.GetString("Project_SaveFailed");
            }

            return ok;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>懒加载指定文件树条目的结构子节点（payload 分区、cpio 条目等），结果按路径缓存。</para>
    /// Lazy-loads the structure children of the given file tree item (payload partitions,
    /// cpio entries, etc.); results are cached by path.
    /// </summary>
    /// <param name="item"><para>要解析的树条目。</para>The tree item to resolve.</param>
    public Task LoadStructureAsync(ProjectTreeItem item) => item.LoadStructureAsync();

    /// <summary>
    /// <para>打开选中文件（发送 <see cref="OpenDocumentMessage"/>）。</para>
    /// Opens the selected file (sends an <see cref="OpenDocumentMessage"/>).
    /// </summary>
    /// <param name="item"><para>要打开的文件条目。</para>The file item to open.</param>
    public void OpenItem(ProjectTreeItem item)
    {
        if (item.IsDirectory)
        {
            return;
        }

        WeakReferenceMessenger.Default.Send(new OpenDocumentMessage(item.FullPath));
    }

    /// <summary>
    /// <para>将部件文件解包到工程根目录（发送 <see cref="UnpackRequestMessage"/>）。</para>
    /// Requests unpacking a component file into the project root directory (sends an
    /// <see cref="UnpackRequestMessage"/>).
    /// </summary>
    /// <param name="item"><para>要解包的部件文件条目。</para>The component file item to unpack.</param>
    public void UnpackItem(ProjectTreeItem item)
    {
        if (item.IsDirectory)
        {
            return;
        }

        var outputDirectory = ProjectRootDirectory();
        WeakReferenceMessenger.Default.Send(new UnpackRequestMessage(item.FullPath, outputDirectory));
        LogService.Instance.Write(LogLevel.Info, "ProjectExplorer", $"Unpack requested: {item.FullPath} -> {outputDirectory}");
    }

    /// <summary>
    /// <para>将文件添加到当前工程的部件清单并保存。</para>
    /// Adds a file to the current project's component list and saves it.
    /// </summary>
    /// <param name="item"><para>要添加的文件条目。</para>The file item to add.</param>
    public async Task AddToComponentsAsync(ProjectTreeItem item)
    {
        if (item.IsDirectory || FirmwareProjectService.OpenPath is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await Task.Run(() =>
            {
                var project = FirmwareProjectService.LoadProject(FirmwareProjectService.OpenPath);
                if (project is null)
                {
                    return false;
                }

                var relative = Path.GetRelativePath(project.RootDirectory, item.FullPath);
                if (!project.Components.Contains(relative))
                {
                    project.Components.Add(relative);
                }

                return FirmwareProjectService.SaveProject(project);
            });
            StatusText = ok
                ? LanguageService.Instance.GetString("Explorer_AddedComponent")
                : LanguageService.Instance.GetString("Explorer_AddComponentFailed");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "ProjectExplorer", $"Add to components failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>重命名文件条目，成功后重建工程树。</para>
    /// Renames a file item and rebuilds the project tree on success.
    /// </summary>
    /// <param name="item"><para>要重命名的条目。</para>The item to rename.</param>
    /// <param name="newName"><para>新文件名。</para>The new file name.</param>
    public async Task RenameItemAsync(ProjectTreeItem item, string newName)
    {
        if (item.IsDirectory || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var directory = Path.GetDirectoryName(item.FullPath);
            if (directory is null)
            {
                return;
            }

            var newPath = Path.Combine(directory, newName);
            var ok = await Task.Run(() =>
            {
                File.Move(item.FullPath, newPath);
                return true;
            });
            if (!ok)
            {
                return;
            }

            if (FirmwareProjectService.OpenPath is not null)
            {
                await OpenProjectAsync(FirmwareProjectService.OpenPath);
            }

            StatusText = LanguageService.Instance.GetString("Explorer_Renamed");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "ProjectExplorer", $"Rename failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>将外部选择的固件镜像文件导入当前工程：复制到工程的 firmware/ 子目录，
    /// 登记进部件清单并保存，随后重建工程树。</para>
    /// Imports the selected external firmware image files into the current project: copies
    /// them into the project's firmware/ subdirectory, registers them in the component list,
    /// saves the project, then rebuilds the tree.
    /// </summary>
    /// <param name="sourceFiles"><para>要导入的源文件完整路径列表。</para>
    /// The full paths of the source files to import.</param>
    public async Task ImportImagesAsync(IReadOnlyList<string> sourceFiles)
    {
        if (sourceFiles.Count == 0)
        {
            return;
        }

        var openPath = FirmwareProjectService.OpenPath;
        if (string.IsNullOrWhiteSpace(openPath))
        {
            StatusText = LanguageService.Instance.GetString("Project_ImportNoProject");
            return;
        }

        IsBusy = true;
        try
        {
            var imported = await Task.Run(() =>
            {
                var project = FirmwareProjectService.LoadProject(openPath);
                if (project is null)
                {
                    return 0;
                }

                var targetDir = Path.Combine(project.RootDirectory, "firmware");
                Directory.CreateDirectory(targetDir);

                // 已占用名称集合，避免同批次内与应用目录已有文件发生重名覆盖。
                // Track used names to avoid overwriting collisions within a batch or
                // against files already present in the target directory.
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var existing in Directory.EnumerateFiles(targetDir))
                {
                    used.Add(Path.GetFileName(existing));
                }

                var count = 0;
                foreach (var source in sourceFiles)
                {
                    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                    {
                        continue;
                    }

                    var destName = UniqueName(Path.GetFileName(source), used);
                    var dest = Path.Combine(targetDir, destName);
                    File.Copy(source, dest, overwrite: false);
                    used.Add(destName);

                    var relative = Path.GetRelativePath(project.RootDirectory, dest);
                    if (!project.Components.Contains(relative))
                    {
                        project.Components.Add(relative);
                    }

                    count++;
                }

                return FirmwareProjectService.SaveProject(project) ? count : 0;
            });

            if (imported > 0)
            {
                if (FirmwareProjectService.CurrentProject is { } project)
                {
                    BuildTree(project);
                }

                var target = FirmwareProjectService.CurrentProject?.RootDirectory ?? openPath;
                StatusText = string.Format(
                    LanguageService.Instance.GetString("Project_Imported"), imported);
                LogService.Instance.Write(LogLevel.Info, "ProjectExplorer",
                    $"Imported {imported} image(s) into {target}");

                // 切回工作区标签页，让侧边资源管理器立即可见刷新后的工程树（含新导入镜像）。
                // Switch back to the workspace tab so the side explorer immediately shows the
                // refreshed project tree (including the newly imported images).
                WeakReferenceMessenger.Default.Send(new NavigateTabMessage(1));
            }
            else
            {
                StatusText = LanguageService.Instance.GetString("Project_ImportFailed");
            }
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "ProjectExplorer",
                $"Import images failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>将当前工程部件清单中的镜像文件导出到目标目录（保留相对路径结构）。</para>
    /// Exports the current project's component image files into a target directory,
    /// preserving the relative directory structure.
    /// </summary>
    /// <param name="targetDirectory"><para>目标目录完整路径。</para>
    /// The full path of the target directory.</param>
    public async Task ExportImagesAsync(string? targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return;
        }

        var openPath = FirmwareProjectService.OpenPath;
        if (string.IsNullOrWhiteSpace(openPath))
        {
            StatusText = LanguageService.Instance.GetString("Project_ExportNoProject");
            return;
        }

        IsBusy = true;
        try
        {
            var exported = await Task.Run(() =>
            {
                var project = FirmwareProjectService.LoadProject(openPath);
                if (project is null)
                {
                    return 0;
                }

                Directory.CreateDirectory(targetDirectory);
                var count = 0;
                foreach (var relative in project.Components)
                {
                    var source = Path.GetFullPath(Path.Combine(project.RootDirectory, relative));
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    var dest = Path.GetFullPath(Path.Combine(targetDirectory, relative));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(source, dest, overwrite: true);
                    count++;
                }

                return count;
            });

            StatusText = exported > 0
                ? string.Format(LanguageService.Instance.GetString("Project_Exported"), exported)
                : LanguageService.Instance.GetString("Project_ExportFailed");
            LogService.Instance.Write(LogLevel.Info, "ProjectExplorer",
                $"Exported {exported} image(s) to {targetDirectory}");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "ProjectExplorer",
                $"Export images failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>生成不与给定集合冲突的目录内唯一文件名（重名时追加数字序号）。</para>
    /// Produces a file name unique against the given set within a directory, appending a
    /// numeric suffix on collision.
    /// </summary>
    /// <param name="fileName"><para>候选文件名。</para>The candidate file name.</param>
    /// <param name="used"><para>已占用名称集合。</para>The set of names already in use.</param>
    /// <returns>唯一文件名。</returns>
    private static string UniqueName(string fileName, ISet<string> used)
    {
        if (!used.Contains(fileName))
        {
            return fileName;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            var candidate = $"{baseName}({i}){extension}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string ProjectRootDirectory()
    {
        var openPath = FirmwareProjectService.OpenPath;
        if (string.IsNullOrWhiteSpace(openPath))
        {
            return AppContext.BaseDirectory;
        }

        var directory = Path.GetDirectoryName(openPath);
        return string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
    }

    /// <summary>
    /// <para>清理并重建工程树。</para>
    /// Clears and rebuilds the project tree.
    /// </summary>
    /// <param name="project"><para>当前工程。</para>The current project.</param>
    private void BuildTree(FirmwareProject project)
    {
        _openedName = project.Name;
        Tree.Clear();
        var root = new ProjectTreeItem(_openedName ?? project.RootDirectory, project.RootDirectory, true);
        Tree.Add(root);
        PopulateDirectory(root, project.RootDirectory);
        OnPropertyChanged(nameof(ProjectName));
    }

    /// <summary>
    /// <para>递归填充目录节点；固件部件文件标记为可展开以支持懒加载。</para>
    /// Recursively populates a directory node; firmware component files are marked expandable
    /// for lazy loading.
    /// </summary>
    /// <param name="parent"><para>父节点。</para>The parent node.</param>
    /// <param name="fullPath"><para>目录完整路径。</para>The directory full path.</param>
    private void PopulateDirectory(ProjectTreeItem parent, string fullPath)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fullPath))
            {
                var node = new ProjectTreeItem(Path.GetFileName(dir), dir, true);
                PopulateDirectory(node, dir);
                parent.Children.Add(node);
            }

            foreach (var file in Directory.EnumerateFiles(fullPath))
            {
                var node = new ProjectTreeItem(Path.GetFileName(file), file, false);
                if (FirmwareInspector.CanInspect(file))
                {
                    node.IsExpandable = true;
                    // 占位子节点：让 TreeView 显示展开箭头，展开时懒加载真实结构子节点。
                    // Placeholder child so the TreeView shows an expander arrow; the real
                    // structure children are lazy-loaded on expansion.
                    node.Children.Add(new ProjectTreeItem(string.Empty, file, false));
                }

                parent.Children.Add(node);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "ProjectExplorer",
                $"Failed to read directory {fullPath}: {ex.Message}");
        }
    }
}

/// <summary>
/// <para>工程树条目模型。</para>
/// Project tree item model.
/// </summary>
public partial class ProjectTreeItem(string name, string fullPath, bool isDirectory) : ViewModelBase
{
    /// <summary>
    /// <para>结构解析结果缓存：完整路径 → 结构树。</para>
    /// Structure parse cache: full path → structure tree.
    /// </summary>
    private static readonly Dictionary<string, InspectorNode?> StructureCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <para>获取显示名称。</para>
    /// Gets the display name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// <para>获取完整路径。</para>
    /// Gets the full path.
    /// </summary>
    public string FullPath { get; } = fullPath;

    /// <summary>
    /// <para>获取是否为目录。</para>
    /// Gets whether this is a directory.
    /// </summary>
    public bool IsDirectory { get; } = isDirectory;

    /// <summary>
    /// <para>获取或设置是否可展开（固件部件文件懒加载结构子节点）。</para>
    /// Gets or sets whether the item is expandable (firmware component files lazy-load
    /// structure children).
    /// </summary>
    public bool IsExpandable { get; set; }

    /// <summary>
    /// <para>获取或设置是否已展开（目录默认展开）。</para>
    /// Gets or sets whether the item is expanded (directories expanded by default).
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded = isDirectory;

    /// <summary>
    /// <para>获取或设置附加详情（结构解析摘要）。</para>
    /// Gets or sets additional detail (structure parse summary).
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// <para>获取子节点集合。</para>
    /// Gets the child nodes.
    /// </summary>
    public ObservableCollection<ProjectTreeItem> Children { get; } = [];

    /// <summary>
    /// <para>懒加载结构子节点：展开/点击时调用 <see cref="FirmwareInspector.InspectAsync"/>，
    /// 结果按完整路径缓存，避免重复解析。</para>
    /// Lazy-loads structure children: calls <see cref="FirmwareInspector.InspectAsync"/> when
    /// expanded/clicked; results are cached by full path to avoid re-parsing.
    /// </summary>
    /// <returns>是否成功解析出结构。<para>Whether a structure was resolved.</para></returns>
    public async Task<bool> LoadStructureAsync()
    {
        if (IsDirectory || !IsExpandable || IsStructureLoaded)
        {
            return IsStructureLoaded;
        }

        IsStructureLoaded = true;
        if (!StructureCache.TryGetValue(FullPath, out var node))
        {
            node = await FirmwareInspector.InspectAsync(FullPath);
            StructureCache[FullPath] = node;
        }

        Children.Clear();
        if (node is null)
        {
            IsExpandable = false;
            return false;
        }

        Detail = node.Detail;
        foreach (var child in node.Children)
        {
            Children.Add(FromInspector(child));
        }

        return true;
    }

    /// <summary>
    /// <para>获取是否已尝试加载结构子节点。</para>
    /// Gets whether structure children have been attempted to be loaded.
    /// </summary>
    public bool IsStructureLoaded { get; private set; }

    internal void MarkStructureLoaded() => IsStructureLoaded = true;

    /// <summary>
    /// <para>递归将结构节点转换为树条目。</para>
    /// Recursively converts an inspector node into a tree item.
    /// </summary>
    /// <param name="node"><para>结构节点。</para>The inspector node.</param>
    private static ProjectTreeItem FromInspector(InspectorNode node)
    {
        var item = new ProjectTreeItem(node.Name, node.Name, false)
        {
            IsExpandable = node.Children.Count > 0,
            Detail = node.Detail
        };
        // 结构子节点已完整解析，标记为已加载，避免展开时再次按路径解析。
        // Structure children are already fully parsed; mark as loaded so expansion does not
        // attempt to re-parse by path.
        item.MarkStructureLoaded();
        foreach (var child in node.Children)
        {
            item.Children.Add(FromInspector(child));
        }

        return item;
    }
}
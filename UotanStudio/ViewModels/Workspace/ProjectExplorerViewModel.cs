using System.Collections.ObjectModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>项目资源管理器视图模型，以当前工程的 <c>images/</c> 与 <c>output/</c> 目录为根展示真实目录树，
/// 订阅工程上下文变更消息并在打开/关闭工程时重建树。</para>
/// Project explorer view model, showing the real directory tree rooted at the current project's
/// <c>images/</c> and <c>output/</c> folders, rebuilding on project open/close via the
/// project-context-changed message.
/// </summary>
internal partial class ProjectExplorerViewModel : Tool, IDisposable
{
    /// <summary>
    /// <para>树根节点集合，包含工程根和通过文件菜单打开的普通文件夹。</para>
    /// Root nodes for the project and a folder opened from the File menu.
    /// </summary>
    public AvaloniaList<ExplorerNode> TreeViewContent { get; } = [];

    [ObservableProperty] private ExplorerNode? _selectedNode;
    private ExplorerNode? _openedFolder;
    private FileSystemWatcher? _folderWatcher;
    private bool _disposed;

    internal string? CreationDirectory => SelectedNode is { IsPlaceholder: false } node
        ? node.IsDirectory ? node.Path : Path.GetDirectoryName(node.Path)
        : _openedFolder?.Path ?? ProjectContextService.Instance.RootDirectory;

    internal void OpenFolder(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var existing = TreeViewContent.FirstOrDefault(node => string.Equals(node.Path, directory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.EnsureChildrenLoaded();
            existing.IsExpanded = true;
            SelectedNode = existing;
            return;
        }

        _folderWatcher?.Dispose();
        _folderWatcher = null;
        if (_openedFolder is not null) TreeViewContent.Remove(_openedFolder);
        _openedFolder = new ExplorerNode(directory, isDirectory: true);
        _openedFolder.EnsureChildrenLoaded();
        _openedFolder.IsExpanded = true;
        TreeViewContent.Add(_openedFolder);
        SelectedNode = _openedFolder;
        try
        {
            _folderWatcher = new FileSystemWatcher(directory);
            ConfigureWatcher(_folderWatcher, includeChanged: false);
        }
        catch (Exception)
        {
            _folderWatcher?.Dispose();
            _folderWatcher = null;
        }
    }

    /// <summary>
    /// <para>监听工程 <c>images/</c> 与 <c>output/</c> 目录的文件系统变更，
    /// 在外部增删改后自动同步树结构。</para>
    /// Watches the project <c>images/</c> and <c>output/</c> directories so the tree
    /// auto-syncs after external file changes.
    /// </summary>
    private FileSystemWatcher? _watcher;

    /// <summary>
    /// <para>文件系统变更事件的防抖计时器：合并高频事件（批量复制等）后统一刷新。</para>
    /// Debounce timer for file-system events: coalesces bursts (bulk copies etc.) into one refresh.
    /// </summary>
    private readonly System.Threading.Timer _fsDebounceTimer;

    /// <summary>
    /// <para>创建项目资源管理器视图模型，订阅工程上下文变更消息并构建初始目录树。</para>
    /// Creates the project explorer view model, subscribing to project-context-changed messages and building the initial directory tree.
    /// </summary>
    public ProjectExplorerViewModel()
    {
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);

        // 防抖 400ms：连续文件系统事件合并为一次刷新，避免复制大文件时的刷新风暴。
        // Debounce 400ms: coalesce bursts of file-system events into a single refresh to avoid a
        // refresh storm when copying large files.
        _fsDebounceTimer = new System.Threading.Timer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyFileSystemChanges),
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        Refresh();
    }

    /// <summary>
    /// <para>响应工程上下文变更消息，重建目录树。</para>
    /// Responds to project-context-changed messages by rebuilding the directory tree.
    /// </summary>
    /// <param name="recipient"><para>消息接收者。</para>The message recipient.</param>
    /// <param name="message"><para>工程上下文变更消息。</para>The project-context-changed message.</param>
    private void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message) => Refresh();

    /// <summary>
    /// <para>重建目录树：以当前解决方案根目录为根节点；未打开工程或目录不存在时显示空树，不抛出异常。
    /// 重建同时重启文件系统监听。</para>
    /// Rebuilds the directory tree rooted at the current solution root; shows an empty tree without
    /// throwing when no project is open or the directory does not exist. Also restarts the file-system watcher.
    /// </summary>
    public void Refresh()
    {
        var svc = ProjectContextService.Instance;
        if (svc.RootDirectory is not null && Directory.Exists(svc.RootDirectory))
        {
            // 填充项目文件夹路径集合，供懒加载子节点自动标记。
            // Populate project folder paths for auto-marking lazily-loaded children.
            ExplorerNode.ProjectFolderPaths.Clear();
            foreach (var sp in svc.SubProjects)
            {
                ExplorerNode.ProjectFolderPaths.Add(sp.RootPath);
            }

            if (TreeViewContent.FirstOrDefault() is { } existing
                && string.Equals(existing.Path, svc.RootDirectory, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && TreeViewContent.LastOrDefault(node => !ReferenceEquals(node, _openedFolder))?.AssociatedProjectPath == svc.NodeProjectPath)
            {
                ApplyFileSystemChanges();
                return;
            }

            TreeViewContent.Clear();

            AddRoot(svc.RootDirectory, isProjectFolder: true);
            // 立即加载根节点子目录，使 FileSystemWatcher 触发的后续刷新能生效。
            // Eagerly load root children so subsequent FileSystemWatcher-triggered refreshes work.
            if (TreeViewContent.FirstOrDefault() is { IsDirectory: true } root)
                root.EnsureChildrenLoaded();

            // 节点项目顶层文件夹：与解决方案根目录同级，双击打开 .uotannode。
            // Node project top-level folder: sibling of the solution root, double-click opens .uotannode.
            // 清空 Children 使其不可展开（纯点击项，非目录浏览）。
            // Clear Children to make it non-expandable (a clickable item, not a directory browser).
            if (svc.NodeProjectPath is not null)
            {
                var nodeProject = new ExplorerNode(
                    svc.RootDirectory,
                    isDirectory: true,
                    isProjectFolder: true,
                    associatedProjectPath: svc.NodeProjectPath)
                {
                    Name = LanguageService.Instance.GetString("Explorer_NodeProject")
                };
                nodeProject.Children.Clear();
                TreeViewContent.Add(nodeProject);
            }
        }
        else
        {
            if (_openedFolder is not null && TreeViewContent.Count == 1 && ReferenceEquals(TreeViewContent[0], _openedFolder))
            {
                ApplyFileSystemChanges();
                RestartWatcher(svc);
                return;
            }
            TreeViewContent.Clear();
        }
        if (_openedFolder is not null && !TreeViewContent.Any(node => string.Equals(node.Path, _openedFolder.Path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            TreeViewContent.Add(_openedFolder);
        if (SelectedNode is not null && !TreeViewContent.Any(root => IsUnderRoot(SelectedNode.Path, root.Path)))
            SelectedNode = null;
        RestartWatcher(svc);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, root, comparison)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison);
    }

    private void AddRoot(string? directory, bool isProjectFolder = false)
    {
        if (directory is not null && Directory.Exists(directory))
        {
            TreeViewContent.Add(new ExplorerNode(directory, isDirectory: true, isProjectFolder: isProjectFolder));
        }
    }

    /// <summary>
    /// <para>重启文件系统监听：监听工程根目录下所有变更（<c>images/</c>、<c>output/</c> 等），
    /// 递归包含子目录；未打开工程时停止监听。</para>
    /// Restarts the file-system watcher on the project root (covering <c>images/</c>,
    /// <c>output/</c> etc.) with recursive subdirectories; stops watching when no project is open.
    /// </summary>
    private void RestartWatcher(ProjectContextService svc)
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnFileSystemChanged;
            _watcher.Created -= OnFileSystemChanged;
            _watcher.Deleted -= OnFileSystemChanged;
            _watcher.Renamed -= OnFileSystemChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        if (!svc.IsProjectOpen || string.IsNullOrEmpty(svc.RootDirectory))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(svc.RootDirectory);
            ConfigureWatcher(_watcher, includeChanged: true);
        }
        catch (Exception)
        {
            // 监听启动失败（路径不可达等）不影响资源管理器基本功能。
            // A failed watcher start (unreachable path etc.) must not break the explorer.
        }
    }

    /// <summary>Shares watcher setup while retaining the two roots and their existing event subscriptions.</summary>
    private void ConfigureWatcher(FileSystemWatcher watcher, bool includeChanged)
    {
        watcher.IncludeSubdirectories = true;
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
        if (includeChanged) watcher.Changed += OnFileSystemChanged;
        watcher.Created += OnFileSystemChanged;
        watcher.Deleted += OnFileSystemChanged;
        watcher.Renamed += OnFileSystemChanged;
        watcher.EnableRaisingEvents = true;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        // 防抖：重置计时器，批量事件合并为一次刷新。
        // Debounce: reset the timer so a burst of events becomes a single refresh.
        if (_disposed) return;
        try { _fsDebounceTimer.Change(400, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// <para>在 UI 线程应用文件系统变更：对已加载的目录节点重新扫描，保持展开状态。</para>
    /// Applies file-system changes on the UI thread: re-scans loaded directory nodes while
    /// preserving their expansion state.
    /// </summary>
    private void ApplyFileSystemChanges()
    {
        if (_disposed) return;
        foreach (var root in TreeViewContent)
        {
            root.RefreshDescendants();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _folderWatcher?.Dispose();
        _fsDebounceTimer.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// <para>资源管理器树节点，表示一个文件或目录；目录节点的子节点在首次展开时按需扫描加载。</para>
/// Explorer tree node representing a file or directory; directory children are loaded lazily on the first expansion.
/// </summary>
public sealed partial class ExplorerNode : ObservableObject
{
    /// <summary>目录展开状态。 Whether the directory is expanded.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// <para>资源管理器右键「以文本打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as text" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsTextHeader => LanguageService.Instance.GetString("Explorer_OpenAsText");

    /// <summary>
    /// <para>资源管理器右键「以十六进制打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as hex" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsHexHeader => LanguageService.Instance.GetString("Explorer_OpenAsHex");

    /// <summary>
    /// <para>资源管理器右键「以设备树打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as device tree" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsDtbHeader => LanguageService.Instance.GetString("Explorer_OpenAsDtb");

    /// <summary>
    /// <para>资源管理器右键「以 DEX 打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as DEX" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsDexHeader => LanguageService.Instance.GetString("Explorer_OpenAsDex");

    /// <summary>
    /// <para>资源管理器右键「以 Manifest 打开（AXML）」菜单项的本地化标题。</para>
    /// Localized title of the "open as Manifest (AXML)" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsAxmlHeader => LanguageService.Instance.GetString("Explorer_OpenAsAxml");

    /// <summary>
    /// <para>资源管理器右键「以 ARSC 打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as ARSC" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsArscHeader => LanguageService.Instance.GetString("Explorer_OpenAsArsc");

    /// <summary>
    /// <para>资源管理器右键「以 APK 打开」菜单项的本地化标题。</para>
    /// Localized title of the "open as APK" context-menu item in the explorer.
    /// </summary>
    public static string OpenAsApkHeader => LanguageService.Instance.GetString("Explorer_OpenAsApk");

    /// <summary>
    /// <para>资源管理器右键「打开方式」子菜单的本地化标题。</para>
    /// Localized title of the "open with" submenu in the explorer.
    /// </summary>
    public static string OpenAsMenuHeader => LanguageService.Instance.GetString("Explorer_OpenAsMenu");

    private bool _childrenLoaded;

    /// <summary>
    /// <para>项目文件夹路径集合（由 ProjectExplorerViewModel 在刷新时填充），用于懒加载子节点时自动标记。</para>
    /// Set of project folder paths (populated by ProjectExplorerViewModel on refresh), used to auto-mark lazily-loaded children.
    /// </summary>
    internal static HashSet<string> ProjectFolderPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <para>获取是否为项目文件夹（双击时打开关联的项目配置文件而非展开目录）。</para>
    /// Gets whether this node is a project folder (double-clicking opens the associated project config file instead of expanding).
    /// </summary>
    public bool IsProjectFolder { get; }

    /// <summary>
    /// <para>获取关联的项目配置文件路径（如 .uotannode），双击项目文件夹时优先打开此路径。</para>
    /// Gets the associated project config file path (e.g. .uotannode), opened directly on double-click.
    /// </summary>
    public string? AssociatedProjectPath { get; }

    /// <summary>
    /// <para>获取或设置节点显示名称。</para>
    /// Gets or sets the display name of the node.
    /// </summary>
    [ObservableProperty] private string _name;

    /// <summary>
    /// <para>获取节点的完整文件系统路径。</para>
    /// Gets the full file-system path of the node.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// <para>获取是否为目录节点。</para>
    /// Gets whether this node is a directory.
    /// </summary>
    public bool IsDirectory { get; }

    /// <summary>Gets the existing unpack configuration without parsing editable text. Uses the single-location rule: the consolidated metadata location when a project is open, the local file otherwise.<para>获取已有的解包配置路径（按单一位置规则：有项目时使用统一元数据位置，无项目时使用本地文件），不解析可编辑的文本。</para></summary>
    public string? RepackConfigurationPath
    {
        get
        {
            if (!IsDirectory || IsPlaceholder) return null;
            try
            {
                // ResolveActualMetadataPath applies the single-location rule: the consolidated
                // .uotan/metadata/<partition>/ when a project is open, the local file otherwise.
                var resolved = UotanStudio.Firmware.SharedKernel.RepackMetadata.ResolveActualMetadataPath(Path,
                    UotanStudio.Services.Projects.ProjectContextService.Instance.CaptureMetadataContext(Path));
                if (resolved is not null && File.Exists(resolved)) return resolved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // An unavailable owner must not redirect the editor into another metadata location.
            }

            return null;
        }
    }

    /// <summary>
    /// <para>获取是否为占位子节点（用于让尚未展开的目录显示展开箭头）。</para>
    /// Gets whether this node is a placeholder child (keeps the expander arrow visible for not-yet-expanded directories).
    /// </summary>
    public bool IsPlaceholder { get; }

    /// <summary>
    /// <para>获取子节点集合，在目录首次展开时填充。</para>
    /// Gets the collection of child nodes, populated on the directory's first expansion.
    /// </summary>
    public ObservableCollection<ExplorerNode> Children { get; } = [];

    /// <summary>
    /// <para>创建资源管理器节点；目录节点会预置一个占位子节点以显示展开箭头。</para>
    /// Creates an explorer node; directory nodes pre-insert a placeholder child so the expander arrow stays visible.
    /// </summary>
    /// <param name="path"><para>节点路径。</para>The node path.</param>
    /// <param name="isDirectory"><para>是否为目录。</para>Whether the node is a directory.</param>
    /// <param name="isPlaceholder"><para>是否为占位节点。</para>Whether the node is a placeholder.</param>
    /// <param name="isProjectFolder"><para>是否为项目文件夹。</para>Whether the node is a project folder.</param>
    /// <param name="associatedProjectPath"><para>关联的项目配置文件路径。</para>The associated project config file path.</param>
    public ExplorerNode(string path, bool isDirectory, bool isPlaceholder = false, bool isProjectFolder = false, string? associatedProjectPath = null)
    {
        Path = path;
        IsDirectory = isDirectory;
        IsPlaceholder = isPlaceholder;
        IsProjectFolder = isProjectFolder;
        AssociatedProjectPath = associatedProjectPath;
        _name = isPlaceholder ? string.Empty : GetDisplayName(path);
        if (isDirectory && !isPlaceholder)
        {
            Children.Add(new ExplorerNode(string.Empty, isDirectory: false, isPlaceholder: true));
        }
    }

    /// <summary>
    /// <para>首次展开目录时扫描并填充子节点；仅在首次调用时执行一次。</para>
    /// Scans and populates child nodes on the directory's first expansion; runs only once.
    /// </summary>
    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded || !IsDirectory)
        {
            return;
        }

        RefreshChildren();
    }

    internal void RefreshChildren()
    {
        try
        {
            var discovered = new List<ExplorerNode>();
            foreach (var dir in Directory.EnumerateDirectories(Path))
            {
                // 排除会话状态目录 .uotan/（类似 VS 的 .vs/），不在资源管理器中显示。
                // Exclude the session state directory .uotan/ (like VS's .vs/) from the explorer.
                if (string.Equals(System.IO.Path.GetFileName(dir), ".uotan", StringComparison.Ordinal))
                {
                    continue;
                }

                discovered.Add(new ExplorerNode(dir, isDirectory: true, isProjectFolder: ProjectFolderPaths.Contains(dir)));
            }

            foreach (var file in Directory.EnumerateFiles(Path))
            {
                // 隐藏项目配置文件（.uotan/.uotanfw/.uotannode），仿 VS 解决方案资源管理器行为。
                // Hide project config files (.uotan/.uotanfw/.uotannode), mimicking VS Solution Explorer behavior.
                var ext = System.IO.Path.GetExtension(file);
                if (string.Equals(ext, ".uotan", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".uotanfw", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".uotannode", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                discovered.Add(new ExplorerNode(file, isDirectory: false));
            }

            // Keep surviving node instances and their TreeViewItem containers intact.
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var previous = Children.Where(c => !c.IsPlaceholder).ToDictionary(c => c.Path, comparer);
            var desired = discovered.Select(c => previous.TryGetValue(c.Path, out var old)
                && old.IsDirectory == c.IsDirectory && old.IsProjectFolder == c.IsProjectFolder ? old : c).ToList();
            var retained = desired.ToHashSet();
            for (var i = Children.Count - 1; i >= 0; i--)
                if (!retained.Contains(Children[i])) Children.RemoveAt(i);
            for (var i = 0; i < desired.Count; i++)
            {
                if (i < Children.Count && ReferenceEquals(Children[i], desired[i])) continue;
                var index = Children.IndexOf(desired[i]);
                if (index >= 0) Children.Move(index, i);
                else Children.Insert(i, desired[i]);
            }
            _childrenLoaded = true;
        }
        catch (Exception)
        {
            // Preserve the existing tree when a directory is temporarily unreadable.
        }
    }

    /// <summary>
    /// <para>对已加载的目录节点重新扫描子节点（外部文件变更后同步）；未展开的目录保持惰性。
    /// 递归刷新已展开的后代目录，使树结构跟上磁盘内容。</para>
    /// Re-scans children of directories that have already been loaded (syncing after external file
    /// changes); not-yet-expanded directories stay lazy. Recursively refreshes expanded descendants
    /// so the tree mirrors the disk content.
    /// </summary>
    public void RefreshDescendants()
    {
        if (!IsDirectory || !_childrenLoaded)
        {
            return;
        }

        RefreshChildren();

        foreach (var child in Children)
        {
            child.RefreshDescendants();
        }
    }

    private static string GetDisplayName(string path)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}

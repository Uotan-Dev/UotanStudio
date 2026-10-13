using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.FileManagement;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// File-manager document showing the internal directory tree of raw filesystem images
/// (ext4/EROFS/F2FS), with directory navigation, file lists and selected-entry extraction.
/// <para>文件管理器文档，展示 raw 镜像（ext4/EROFS/F2FS）内部的目录树，
/// 提供目录导航、文件列表与选中条目提取。</para>
/// </summary>
public partial class FileManagerEditorViewModel : Document
{
    /// <summary>
    /// Cancellation source of the current load; cancelled when a new load starts or the document closes.
    /// <para>当前加载任务的取消源；触发新的加载或关闭文档时取消旧任务。</para>
    /// </summary>
    private CancellationTokenSource? _loadCts;

    private ImageBrowserSession? _session;

    /// <summary>
    /// The image file path of the document.
    /// <para>文档对应的镜像文件路径。</para>
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// Whether the image is being parsed in the background (drives the loading indicator).
    /// <para>是否正在后台解析镜像（驱动加载进度指示）。</para>
    /// </summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>
    /// The detected filesystem format name (EXT4 / EROFS / F2FS); empty when not loaded.
    /// <para>识别出的文件系统格式名称（EXT4 / EROFS / F2FS）；未加载时为空。</para>
    /// </summary>
    [ObservableProperty] private string _formatName = string.Empty;

    /// <summary>
    /// The image file size in bytes.
    /// <para>镜像文件大小（字节）。</para>
    /// </summary>
    [ObservableProperty] private long _imageSize;

    /// <summary>
    /// Total number of files (including symlinks) inside the image.
    /// <para>镜像内文件（含符号链接）总数。</para>
    /// </summary>
    [ObservableProperty] private int _fileCount;

    /// <summary>
    /// Total number of directories inside the image.
    /// <para>镜像内目录总数。</para>
    /// </summary>
    [ObservableProperty] private int _directoryCount;

    /// <summary>
    /// The selected file-list entry, used for path display and extraction.
    /// <para>当前选中的文件列表条目，用于路径显示和提取。</para>
    /// </summary>
    [ObservableProperty] private FileManagerEntryViewModel? _selectedEntry;

    /// <summary>
    /// Multi-select entry collection kept in sync with the current selection.
    /// <para>多选条目集合，与当前选中状态保持同步。</para>
    /// </summary>
    /// <value>The entries currently multi-selected.<para>当前处于多选状态的条目。</para></value>
    public ObservableCollection<FileManagerEntryViewModel> SelectedEntries { get; } = [];

    /// <summary>
    /// The current directory, also bound to the navigation tree selection.
    /// <para>当前浏览目录，同时绑定左侧目录树选中项。</para>
    /// </summary>
    [ObservableProperty] private FileManagerEntryViewModel? _currentDirectory;

    /// <summary>
    /// The immediate entries of the current directory.
    /// <para>当前目录的直接子条目。</para>
    /// </summary>
    [ObservableProperty] private IReadOnlyList<FileManagerEntryViewModel> _currentEntries = [];

    /// <summary>
    /// Whether to use square icon tiles; changing layouts preserves the directory and selection.
    /// <para>是否使用方形图标网格；切换布局保留目录和选中项。</para>
    /// </summary>
    [ObservableProperty] private bool _isGridView = true;

    /// <summary>
    /// Navigation tree roots, including the image root directory.
    /// <para>目录树根节点，包含镜像根目录。</para>
    /// </summary>
    public ObservableCollection<FileManagerEntryViewModel> DirectoryRoots { get; } = [];

    /// <summary>
    /// The current directory's absolute path inside the image.
    /// <para>当前目录的镜像内绝对路径。</para>
    /// </summary>
    public string CurrentPath => "/" + CurrentDirectory?.Path;

    /// <summary>
    /// Whether the loaded current directory is empty.
    /// <para>已加载的当前目录是否为空。</para>
    /// </summary>
    public bool IsDirectoryEmpty => !IsLoading && !HasError && CurrentDirectory is not null && CurrentEntries.Count == 0;

    /// <summary>
    /// The last error text; empty means no error.
    /// <para>最后一次错误文本；为空表示无错误。</para>
    /// </summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>
    /// Whether an error is present (drives the error-section visibility).
    /// <para>是否存在错误（驱动错误区显示）。</para>
    /// </summary>
    [ObservableProperty] private bool _hasError;

    /// <summary>
    /// The image file path (display binding for the toolbar).
    /// <para>镜像文件路径（详情区绑定显示）。</para>
    /// </summary>
    public string ImagePath => FilePath ?? string.Empty;

    /// <summary>
    /// The localized file-count statistics text.
    /// <para>文件总数的本地化统计文本。</para>
    /// </summary>
    public string FileCountText => string.Format(LanguageService.Instance.GetString("FileManager_FileCount"), FileCount);

    /// <summary>
    /// The localized directory-count statistics text.
    /// <para>目录总数的本地化统计文本。</para>
    /// </summary>
    public string DirectoryCountText => string.Format(LanguageService.Instance.GetString("FileManager_DirectoryCount"), DirectoryCount);

    /// <summary>
    /// The localized image-size statistics text.
    /// <para>镜像大小的本地化统计文本。</para>
    /// </summary>
    public string ImageSizeText => string.Format(LanguageService.Instance.GetString("FileManager_ImageSize"), FormatSize(ImageSize));

    /// <summary>
    /// The root-level entry collection (tree-view binding).
    /// <para>根层条目集合（TreeView 绑定）。</para>
    /// </summary>
    public ObservableCollection<FileManagerEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Reload command: rebuilds the file tree from the current image path.
    /// <para>重新加载命令：按当前镜像路径重建文件树。</para>
    /// </summary>
    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>
    /// Extract-selected command: extracts the selected file/directory to a user-chosen directory.
    /// <para>提取选中条目命令：将选中的文件/目录提取到用户选择的目录。</para>
    /// </summary>
    public IAsyncRelayCommand ExtractSelectedCommand { get; }

    /// <summary>
    /// Navigates to the parent directory.
    /// <para>返回上级目录。</para>
    /// </summary>
    public IRelayCommand NavigateUpCommand { get; }

    /// <summary>
    /// Creates the file-manager editor view model and initializes its commands.
    /// <para>创建文件管理器编辑器视图模型并初始化命令。</para>
    /// </summary>
    public FileManagerEditorViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading && FilePath is not null);
        ExtractSelectedCommand = new AsyncRelayCommand(ExtractSelectedAsync,
            () => !IsLoading && _session is not null && ExtractionEntry is not null);
        NavigateUpCommand = new RelayCommand(NavigateUp, () => !IsLoading && CurrentDirectory?.Parent is not null);
    }

    partial void OnFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(ImagePath));
        RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnImageSizeChanged(long value) => OnPropertyChanged(nameof(ImageSizeText));

    partial void OnFileCountChanged(int value) => OnPropertyChanged(nameof(FileCountText));

    partial void OnDirectoryCountChanged(int value) => OnPropertyChanged(nameof(DirectoryCountText));

    private FileManagerEntryViewModel? ExtractionEntry => SelectedEntry ?? (CurrentDirectory?.Parent is not null ? CurrentDirectory : null);

    partial void OnSelectedEntryChanged(FileManagerEntryViewModel? value)
    {
        ExtractSelectedCommand.NotifyCanExecuteChanged();
        WeakReferenceMessenger.Default.Send(new FileManagerSelectionChangedMessage(value, this));
    }

    partial void OnCurrentDirectoryChanged(FileManagerEntryViewModel? value)
    {
        SelectedEntry = null;
        CurrentEntries = value?.Children ?? (IReadOnlyList<FileManagerEntryViewModel>)[];
        for (var parent = value?.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
        }

        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(IsDirectoryEmpty));
        NavigateUpCommand.NotifyCanExecuteChanged();
        ExtractSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDirectoryEmpty));
        RefreshCommand.NotifyCanExecuteChanged();
        ExtractSelectedCommand.NotifyCanExecuteChanged();
        NavigateUpCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasErrorChanged(bool value) => OnPropertyChanged(nameof(IsDirectoryEmpty));

    /// <summary>
    /// Enters a directory; files and symbolic links stay selected and are not followed as directories.
    /// <para>进入目录；文件和符号链接保持选中，不作为目录跟随。</para>
    /// </summary>
    public void OpenEntry(FileManagerEntryViewModel entry)
    {
        if (IsLoading)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            CurrentDirectory = entry;
        }
        else
        {
            SelectedEntry = entry;
        }
    }

    private void NavigateUp()
    {
        if (CurrentDirectory?.Parent is not { } parent)
        {
            return;
        }

        var previous = CurrentDirectory;
        CurrentDirectory = parent;
        SelectedEntry = previous;
    }

    /// <summary>
    /// Asynchronously loads an image: detect the filesystem format, stream-parse the entries, and
    /// build the file tree and statistics.
    /// <para>异步加载镜像：识别文件系统格式 → 流式解析条目 → 构建文件树与统计。</para>
    /// </summary>
    /// <param name="path">
    /// The image file path.
    /// <para>镜像文件路径。</para>
    /// </param>
    public async Task LoadFromImageAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FilePath = path;
        Title = Path.GetFileName(path);

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        ResetState();

        try
        {
            var result = await Task.Run(() =>
            {
                var session = ImageBrowserSession.Open(path, ct,
                    LanguageService.Instance.GetString("FileManager_Unrecognized"));
                try
                {
                    var tree = FileManagerTreeBuilder.Build(session.Entries);
                    ct.ThrowIfCancellationRequested();
                    return (Session: session, Tree: tree);
                }
                catch { session.Dispose(); throw; }
            }, ct).ConfigureAwait(true);
            try
            {
                ct.ThrowIfCancellationRequested();
                ApplyLoadResult(result.Session, result.Tree);
            }
            catch { result.Session.Dispose(); throw; }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            ErrorMessage = global::UotanStudio.Services.UserError.Describe(ex);
            HasError = true;
            LogService.Instance.Write(LogLevel.Error, "FileManager",
                $"Failed to load filesystem image '{path}': {ex.Message}");
        }
        finally
        {
            if (_loadCts?.Token == ct) IsLoading = false;
        }
    }

    /// <summary>
    /// Releases the parser and tree data held by this document (called when the document is closed) so
    /// the GC can reclaim the no-longer-referenced large objects.
    /// <para>释放文档持有的解析器与树数据（文档关闭时调用），让 GC 回收不再引用的大对象。</para>
    /// </summary>
    public void Cleanup()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _session?.Dispose();
        _session = null;
        CurrentDirectory = null;
        DirectoryRoots.Clear();
        Entries.Clear();
        SelectedEntry = null;
        FileCount = 0;
        DirectoryCount = 0;
        ImageSize = 0;
        FormatName = string.Empty;
        ErrorMessage = string.Empty;
        HasError = false;
        IsLoading = false;
        FilePath = null;
    }

    /// <summary>
    /// Reloads the current image.
    /// <para>重新加载当前镜像。</para>
    /// </summary>
    private async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        var directoryPath = CurrentDirectory?.Path;
        var selectionPath = SelectedEntry?.Path;
        await LoadFromImageAsync(FilePath);
        if (DirectoryRoots.FirstOrDefault() is not { } root)
        {
            return;
        }

        var directory = root;
        foreach (var part in (directoryPath ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = directory.Directories.FirstOrDefault(entry => entry.Name == part);
            if (child is null)
            {
                break;
            }

            directory = child;
        }

        CurrentDirectory = directory;
        SelectedEntry = CurrentEntries.FirstOrDefault(entry => entry.Path == selectionPath);
    }

    /// <summary>
    /// Extracts the selected entry to a user-chosen directory; the directory is picked via the system
    /// folder picker.
    /// <para>将选中的条目提取到用户选择的目录；目录选择经系统目录选择器完成。</para>
    /// </summary>
    private async Task ExtractSelectedAsync()
    {
        var entries = SelectedEntries.Count > 0
            ? SelectedEntries.ToList()
            : SelectedEntry is not null ? [SelectedEntry] : [];

        if (entries.Count == 0 || _session is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        var session = _session;
        var sourcePath = FilePath;

        var topLevel = TopLevelExtensions.GetTopLevel();
        if (topLevel is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString("FileManager_Extract"),
        });
        if (folders.Count == 0)
        {
            return;
        }

        var target = folders[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        if (!ReferenceEquals(session, _session)) return;

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            var paths = entries.Select(e => e.Path).ToList();
            await Task.Run(() => session.Extract(paths, target)).ConfigureAwait(true);

            var summary = entries.Count == 1
                ? $"Extracted '{paths[0]}' from '{sourcePath}' to '{target}'."
                : $"Extracted {entries.Count} entries from '{sourcePath}' to '{target}'.";
            LogService.Instance.Write(LogLevel.Info, "FileManager", summary);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(session, _session)) return;
            ErrorMessage = global::UotanStudio.Services.UserError.Describe(ex);
            HasError = true;
            LogService.Instance.Write(LogLevel.Error, "FileManager",
                $"Failed to extract from '{sourcePath}': {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(session, _session)) IsLoading = false;
        }
    }

    /// <summary>
    /// Applies the background load result to the UI-visible properties.
    /// <para>应用后台加载结果到 UI 线程可见属性。</para>
    /// </summary>
    private void ApplyLoadResult(ImageBrowserSession session, IReadOnlyList<FileManagerEntryViewModel> tree)
    {
        _session?.Dispose();
        FormatName = session.FormatName;
        ImageSize = session.ImageSize;

        Entries.Clear();
        foreach (var node in tree)
        {
            Entries.Add(node);
        }

        var root = new FileManagerEntryViewModel("/", string.Empty, 0, FileEntryKind.Directory) { IsExpanded = true };
        foreach (var node in tree)
        {
            root.Children.Add(node);
        }

        var directories = new Stack<FileManagerEntryViewModel>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            foreach (var child in directory.Children)
            {
                child.Parent = directory;
                if (child.IsDirectory)
                {
                    directories.Push(child);
                }
            }
        }

        DirectoryRoots.Add(root);
        CurrentDirectory = root;

        (FileCount, DirectoryCount) = CountEntries(tree);
        _session = session;
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// Walks the tree to count files (including symlinks) and directories.
    /// <para>遍历树统计文件（含符号链接）与目录总数。</para>
    /// </summary>
    private static (int Files, int Directories) CountEntries(IReadOnlyList<FileManagerEntryViewModel> roots)
    {
        var files = 0;
        var directories = 0;
        var stack = new Stack<FileManagerEntryViewModel>(roots.Count);
        foreach (var root in roots)
        {
            stack.Push(root);
        }

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.IsDirectory)
            {
                directories++;
            }
            else
            {
                files++;
            }

            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        return (files, directories);
    }

    /// <summary>
    /// Resets the document display state (clears the previous result when a load starts).
    /// <para>重置文档展示状态（加载开始时清空上一次结果）。</para>
    /// </summary>
    private void ResetState()
    {
        _session?.Dispose();
        _session = null;
        CurrentDirectory = null;
        DirectoryRoots.Clear();
        Entries.Clear();
        SelectedEntry = null;
        SelectedEntries.Clear();
        FileCount = 0;
        DirectoryCount = 0;
        ImageSize = 0;
        FormatName = string.Empty;
        ErrorMessage = string.Empty;
        HasError = false;
    }

    /// <summary>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 MB"); 0 bytes shows "0 B".
    /// <para>将字节数格式化为可读大小文本（如 "1.5 MB"）；0 字节显示 "0 B"。</para>
    /// </summary>
    private static string FormatSize(long bytes) => SizeFormatter.Format(bytes);

}
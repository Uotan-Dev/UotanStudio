using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.F2fs;
using UotanStudio.Firmware.Sparse;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Views.Controls;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// <para>文件管理器文档编辑器视图模型：以 Dock 文档展示支持文件系统格式的 raw img
/// （ext4/EROFS/F2FS）内部的目录树，提供目录导航、文件列表与选中条目提取。</para>
/// File-manager document editor view model: a Dock document showing the internal directory tree of
/// raw filesystem images (ext4/EROFS/F2FS), with directory navigation, file lists and selected-entry
/// extraction.
/// </summary>
public partial class FileManagerEditorViewModel : Document
{
    /// <summary>
    /// <para>镜像内文件系统魔数判定所需的头部读取长度：
    /// ext4 魔数位于 1080（2 字节）→ 需 1082；erofs/f2fs 魔数位于 1024（4 字节）→ 需 1028。</para>
    /// Header length needed for filesystem magic detection: ext4 magic sits at offset 1080 (2 bytes),
    /// needing 1082; erofs/f2fs magic sits at 1024 (4 bytes), needing 1028.
    /// </summary>
    private const int FilesystemHeaderLength = 1082;

    /// <summary>
    /// <para>已打开的镜像解析器（ext4/EROFS/F2FS）；文档存活期间持有，供刷新与提取复用。</para>
    /// The opened image parser (ext4/EROFS/F2FS); held for the document lifetime, reused by refresh and extract.
    /// </summary>
    private IDisposable? _parser;

    /// <summary>
    /// <para>解析器不负责释放的额外资源（sparse 场景的 <see cref="SparseReadStream"/>，拥有并释放
    /// SparseFile 及其源文件句柄）；文档关闭或重置时释放。</para>
    /// Additional resource not released by the parser (the <see cref="SparseReadStream"/> in the sparse
    /// case, owning and releasing the SparseFile and its source-file handle); released on document close
    /// or reset.
    /// </summary>
    private IDisposable? _ownedSource;

    /// <summary>
    /// <para>最近一次解析得到的扁平条目列表（用于提取路径过滤与统计）。</para>
    /// The flat entry list from the latest parse (used for extraction filtering and statistics).
    /// </summary>
    private IReadOnlyList<FlatEntry> _flatEntries = [];

    /// <summary>
    /// <para>当前加载任务的取消源；触发新的加载或关闭文档时取消旧任务。</para>
    /// Cancellation source of the current load; cancelled when a new load starts or the document closes.
    /// </summary>
    private CancellationTokenSource? _loadCts;

    /// <summary>
    /// <para>文档对应的镜像文件路径。</para>
    /// The image file path of the document.
    /// </summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>
    /// <para>是否正在后台解析镜像（驱动加载进度指示）。</para>
    /// Whether the image is being parsed in the background (drives the loading indicator).
    /// </summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>
    /// <para>识别出的文件系统格式名称（EXT4 / EROFS / F2FS）；未加载时为空。</para>
    /// The detected filesystem format name (EXT4 / EROFS / F2FS); empty when not loaded.
    /// </summary>
    [ObservableProperty] private string _formatName = string.Empty;

    /// <summary>
    /// <para>镜像文件大小（字节）。</para>
    /// The image file size in bytes.
    /// </summary>
    [ObservableProperty] private long _imageSize;

    /// <summary>
    /// <para>镜像内文件（含符号链接）总数。</para>
    /// Total number of files (including symlinks) inside the image.
    /// </summary>
    [ObservableProperty] private int _fileCount;

    /// <summary>
    /// <para>镜像内目录总数。</para>
    /// Total number of directories inside the image.
    /// </summary>
    [ObservableProperty] private int _directoryCount;

    /// <summary>
    /// <para>当前选中的文件列表条目，用于路径显示和提取。</para>
    /// The selected file-list entry, used for path display and extraction.
    /// </summary>
    [ObservableProperty] private FileManagerEntryViewModel? _selectedEntry;

    /// <summary>
    /// <para>当前浏览目录，同时绑定左侧目录树选中项。</para>
    /// The current directory, also bound to the navigation tree selection.
    /// </summary>
    [ObservableProperty] private FileManagerEntryViewModel? _currentDirectory;

    /// <summary>
    /// <para>当前目录的直接子条目。</para>
    /// The immediate entries of the current directory.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<FileManagerEntryViewModel> _currentEntries = [];

    /// <summary>
    /// <para>是否使用方形图标网格；切换布局保留目录和选中项。</para>
    /// Whether to use square icon tiles; changing layouts preserves the directory and selection.
    /// </summary>
    [ObservableProperty] private bool _isGridView = true;

    /// <summary>
    /// <para>目录树根节点，包含镜像根目录。</para>
    /// Navigation tree roots, including the image root directory.
    /// </summary>
    public ObservableCollection<FileManagerEntryViewModel> DirectoryRoots { get; } = [];

    /// <summary>
    /// <para>当前目录的镜像内绝对路径。</para>
    /// The current directory's absolute path inside the image.
    /// </summary>
    public string CurrentPath => "/" + CurrentDirectory?.Path;

    /// <summary>
    /// <para>已加载的当前目录是否为空。</para>
    /// Whether the loaded current directory is empty.
    /// </summary>
    public bool IsDirectoryEmpty => !IsLoading && !HasError && CurrentDirectory is not null && CurrentEntries.Count == 0;

    /// <summary>
    /// <para>最后一次错误文本；为空表示无错误。</para>
    /// The last error text; empty means no error.
    /// </summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>
    /// <para>是否存在错误（驱动错误区显示）。</para>
    /// Whether an error is present (drives the error-section visibility).
    /// </summary>
    [ObservableProperty] private bool _hasError;

    /// <summary>
    /// <para>镜像文件路径（详情区绑定显示）。</para>
    /// The image file path (display binding for the toolbar).
    /// </summary>
    public string ImagePath => FilePath ?? string.Empty;

    /// <summary>
    /// <para>文件总数的本地化统计文本。</para>
    /// The localized file-count statistics text.
    /// </summary>
    public string FileCountText => string.Format(LanguageService.Instance.GetString("FileManager_FileCount"), FileCount);

    /// <summary>
    /// <para>目录总数的本地化统计文本。</para>
    /// The localized directory-count statistics text.
    /// </summary>
    public string DirectoryCountText => string.Format(LanguageService.Instance.GetString("FileManager_DirectoryCount"), DirectoryCount);

    /// <summary>
    /// <para>镜像大小的本地化统计文本。</para>
    /// The localized image-size statistics text.
    /// </summary>
    public string ImageSizeText => string.Format(LanguageService.Instance.GetString("FileManager_ImageSize"), FormatSize(ImageSize));

    /// <summary>
    /// <para>根层条目集合（TreeView 绑定）。</para>
    /// The root-level entry collection (tree-view binding).
    /// </summary>
    public ObservableCollection<FileManagerEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// <para>重新加载命令：按当前镜像路径重建文件树。</para>
    /// Reload command: rebuilds the file tree from the current image path.
    /// </summary>
    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>
    /// <para>提取选中条目命令：将选中的文件/目录提取到用户选择的目录。</para>
    /// Extract-selected command: extracts the selected file/directory to a user-chosen directory.
    /// </summary>
    public IAsyncRelayCommand ExtractSelectedCommand { get; }

    /// <summary>
    /// <para>返回上级目录。</para>
    /// Navigates to the parent directory.
    /// </summary>
    public IRelayCommand NavigateUpCommand { get; }

    /// <summary>
    /// <para>创建文件管理器编辑器视图模型并初始化命令。</para>
    /// Creates the file-manager editor view model and initializes its commands.
    /// </summary>
    public FileManagerEditorViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading && FilePath is not null);
        ExtractSelectedCommand = new AsyncRelayCommand(ExtractSelectedAsync,
            () => !IsLoading && _parser is not null && ExtractionEntry is not null);
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
    /// <para>进入目录；文件和符号链接保持选中，不作为目录跟随。</para>
    /// Enters a directory; files and symbolic links stay selected and are not followed as directories.
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
    /// <para>异步加载镜像：识别文件系统格式 → 流式解析条目 → 构建文件树与统计。</para>
    /// Asynchronously loads an image: detect the filesystem format, stream-parse the entries, and
    /// build the file tree and statistics.
    /// </summary>
    /// <param name="path">
    /// <para>镜像文件路径。</para>
    /// The image file path.
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
            // 解析与建树为 CPU/IO 密集操作，放到线程池执行避免阻塞 UI。
            // Parse and tree building are CPU/IO-bound; run them on the thread pool to keep the UI responsive.
            var result = await Task.Run(() => LoadImageCore(path, ct), ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            ApplyLoadResult(result);
        }
        catch (OperationCanceledException)
        {
            // 取消：保持文档当前状态，下次加载会重新覆盖。
            // Cancelled: keep the current document state; the next load overwrites it.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
            LogService.Instance.Write(LogLevel.Error, "FileManager",
                $"Failed to load filesystem image '{path}': {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// <para>释放文档持有的解析器与树数据（文档关闭时调用），让 GC 回收不再引用的大对象。</para>
    /// Releases the parser and tree data held by this document (called when the document is closed) so
    /// the GC can reclaim the no-longer-referenced large objects.
    /// </summary>
    public void Cleanup()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _parser?.Dispose();
        _ownedSource?.Dispose();
        _parser = null;
        _ownedSource = null;
        _flatEntries = [];
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
    /// <para>镜像内文件系统格式。</para>
    /// The filesystem format of the image.
    /// </summary>
    private enum FilesystemKind
    {
        /// <summary>
        /// <para>未知格式。</para>
        /// Unknown format.
        /// </summary>
        Unknown,

        /// <summary>
        /// <para>ext4。</para>
        /// ext4.
        /// </summary>
        Ext4,

        /// <summary>
        /// <para>EROFS。</para>
        /// EROFS.
        /// </summary>
        Erofs,

        /// <summary>
        /// <para>F2FS。</para>
        /// F2FS.
        /// </summary>
        F2fs,
    }

    /// <summary>
    /// <para>扁平文件系统条目（路径 + 大小 + 类型），作为树构建与提取的中间表示。</para>
    /// A flat filesystem entry (path + size + kind), the intermediate representation for tree building
    /// and extraction.
    /// </summary>
    /// <param name="Path">镜像内相对路径。<para>Relative path inside the image.</para></param>
    /// <param name="Size">条目大小（字节）。<para>The entry size in bytes.</para></param>
    /// <param name="Kind">条目类型。<para>The entry kind.</para></param>
    private sealed record FlatEntry(string Path, long Size, FileEntryKind Kind);

    /// <summary>
    /// <para>后台加载的一次性结果数据。</para>
    /// One-shot result data of a background load.
    /// </summary>
    /// <param name="FormatName">文件系统格式名。<para>The filesystem format name.</para></param>
    /// <param name="ImageSize">镜像文件大小（字节）。<para>The image file size in bytes.</para></param>
    /// <param name="Parser">打开的解析器。<para>The opened parser.</para></param>
    /// <param name="FlatEntries">扁平条目列表。<para>The flat entry list.</para></param>
    /// <param name="Tree">构建好的树（根层）。<para>The built tree (root level).</para></param>
    /// <param name="OwnedSource">解析器不负责释放的额外资源（如 sparse 逻辑流 <see cref="SparseReadStream"/>）；可为 null。
    /// <para>Additional resource not released by the parser (e.g. the sparse logical stream
    /// <see cref="SparseReadStream"/>); may be null.</para></param>
    private sealed record LoadResult(
        string FormatName,
        long ImageSize,
        IDisposable Parser,
        IReadOnlyList<FlatEntry> FlatEntries,
        IReadOnlyList<FileManagerEntryViewModel> Tree,
        IDisposable? OwnedSource = null);

    /// <summary>
    /// <para>重新加载当前镜像。</para>
    /// Reloads the current image.
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
    /// <para>将选中的条目提取到用户选择的目录；目录选择经系统目录选择器完成。</para>
    /// Extracts the selected entry to a user-chosen directory; the directory is picked via the system
    /// folder picker.
    /// </summary>
    private async Task ExtractSelectedAsync()
    {
        var entry = ExtractionEntry;
        if (entry is null || _parser is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

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

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            // 提取为流式 IO（逐块复制），后台执行避免阻塞 UI。
            // Extraction is streaming I/O (copied block by block); run it in the background.
            await Task.Run(() => ExtractEntry(entry.Path, target)).ConfigureAwait(true);
            LogService.Instance.Write(LogLevel.Info, "FileManager",
                $"Extracted '{entry.Path}' from '{FilePath}' to '{target}'.");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
            LogService.Instance.Write(LogLevel.Error, "FileManager",
                $"Failed to extract '{entry.Path}' from '{FilePath}': {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// <para>按魔数识别镜像的文件系统格式（ext4 0xEF53@1080、erofs 0xE0F5E1E2@1024、f2fs 0xF2F52010@1024）。
    /// 物理偏移未命中时，sparse 包装镜像（如 product.img）按逻辑偏移重新探测内部文件系统。</para>
    /// Detects the filesystem format of the image by magic (ext4 0xEF53@1080, erofs 0xE0F5E1E2@1024, f2fs 0xF2F52010@1024).
    /// When the physical offset misses, sparse-wrapped images (e.g. product.img) are re-probed at the logical offset.
    /// </summary>
    /// <param name="path">
    /// <para>镜像文件路径。</para>
    /// The image file path.
    /// </param>
    /// <returns>
    /// <para>识别出的文件系统格式；无法读取或未命中魔数时为 <see cref="FilesystemKind.Unknown"/>。</para>
    /// The detected filesystem kind; <see cref="FilesystemKind.Unknown"/> when unreadable or unmatched.
    /// </returns>
    private static FilesystemKind DetectFilesystem(string path)
    {
        try
        {
            // 先按物理偏移探测（raw 镜像直接命中）。
            // Probe the physical header first (raw images match directly).
            var kind = DetectKindFromHeader(FormatDetector.ReadHeader(path, FilesystemHeaderLength));
            if (kind != FilesystemKind.Unknown)
            {
                return kind;
            }

            // 物理头部未命中：sparse 包装的镜像按逻辑偏移重新探测内部文件系统。
            // Physical header missed: re-probe the logical header for sparse-wrapped images.
            if (FileOpenMode.IsSparseImage(path))
            {
                var logical = SparseReader.PeekLogicalRange(path, 0, FilesystemHeaderLength);
                kind = DetectKindFromHeader(logical);
                if (kind != FilesystemKind.Unknown)
                {
                    return kind;
                }
            }
        }
        catch
        {
            // 读取失败按未知格式处理。
            // A read failure is treated as an unknown format.
        }

        return FilesystemKind.Unknown;
    }

    /// <summary>
    /// <para>按头部字节中的文件系统魔数识别格式（ext4 0xEF53@1080、erofs 0xE0F5E1E2@1024、f2fs 0xF2F52010@1024）。
    /// 长度不足或未命中时返回 <see cref="FilesystemKind.Unknown"/>。</para>
    /// Detects the filesystem kind from header bytes by magic (ext4 0xEF53@1080, erofs 0xE0F5E1E2@1024,
    /// f2fs 0xF2F52010@1024). Returns <see cref="FilesystemKind.Unknown"/> when too short or unmatched.
    /// </summary>
    private static FilesystemKind DetectKindFromHeader(byte[] header)
    {
        if (header.Length >= 1082
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1080, 2)) == 0xEF53)
        {
            return FilesystemKind.Ext4;
        }

        if (header.Length >= 1028
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xE0F5E1E2)
        {
            return FilesystemKind.Erofs;
        }

        if (header.Length >= 1028
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xF2F52010)
        {
            return FilesystemKind.F2fs;
        }

        return FilesystemKind.Unknown;
    }

    /// <summary>
    /// <para>后台加载镜像：识别格式 → 流式解析 → 扁平列表 → 构建树。</para>
    /// Loads the image in the background: detect the format, stream-parse the entries, flatten the list
    /// and build the tree.
    /// </summary>
    private static LoadResult LoadImageCore(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 幂等注册全部格式处理器（应用启动时通常已注册），与固件面板共用同一注册表语义。
        // Idempotently register every format handler (the app usually does at startup), sharing the
        // registry semantics with the firmware panel.
        FirmwareFormatRegistry.RegisterAll();

        var kind = DetectFilesystem(path);
        // 输入是否为 sparse 包装镜像（如 product.img）：sparse 以逻辑流视图解析。
        // Whether the input is sparse-wrapped (e.g. product.img): sparse images are parsed via a
        // logical stream view.
        var isSparse = FileOpenMode.IsSparseImage(path);
        switch (kind)
        {
            case FilesystemKind.Ext4:
                if (isSparse)
                {
                    var (stream, imageSize, owned) = OpenSparseStream(path);
                    return LoadExt4(stream, imageSize, owned, ct);
                }

                return LoadExt4(path, ct);
            case FilesystemKind.Erofs:
                if (isSparse)
                {
                    var (stream, imageSize, owned) = OpenSparseStream(path);
                    return LoadErofs(stream, imageSize, owned, ct);
                }

                return LoadErofs(path, ct);
            case FilesystemKind.F2fs:
                if (isSparse)
                {
                    var (stream, imageSize, owned) = OpenSparseStream(path);
                    return LoadF2fs(stream, imageSize, owned, ct);
                }

                return LoadF2fs(path, ct);
            default:
                throw new InvalidDataException(
                    LanguageService.Instance.GetString("FileManager_Unrecognized"));
        }
    }

    /// <summary>
    /// <para>为 sparse 镜像打开逻辑流视图：<see cref="SparseReader.FromImageFile(string)"/> 构建
    /// <see cref="SparseFile"/>，再以 <see cref="SparseReadStream"/> 包装。该流拥有并释放 SparseFile
    /// （含源文件句柄），并作为 <see cref="LoadResult.OwnedSource"/> 返回以管理其生命周期。</para>
    /// Opens a logical stream view for a sparse image: builds a <see cref="SparseFile"/> via
    /// <see cref="SparseReader.FromImageFile(string)"/> and wraps it in a <see cref="SparseReadStream"/>.
    /// The stream owns and releases the SparseFile (including the source-file handle) and is returned as
    /// <see cref="LoadResult.OwnedSource"/> to manage its lifetime.
    /// </summary>
    private static (Stream Stream, long ImageSize, IDisposable OwnedSource) OpenSparseStream(string path)
    {
        var sparseFile = SparseReader.FromImageFile(path);
        var stream = new SparseReadStream(sparseFile);
        return (stream, stream.Length, stream);
    }

    /// <summary>
    /// <para>用 <see cref="Ext4Parser"/> 流式解析 ext4 镜像并构建树（路径版本：解析器自持并释放 FileStream）。</para>
    /// Stream-parses an ext4 image with <see cref="Ext4Parser"/> and builds the tree (path version: the
    /// parser owns and disposes its FileStream).
    /// </summary>
    private static LoadResult LoadExt4(string path, CancellationToken ct)
    {
        var parser = new Ext4Parser(path);
        return BuildExt4Result(parser, new FileInfo(path).Length, null, ct);
    }

    /// <summary>
    /// <para>用 <see cref="Ext4Parser"/> 流式解析 ext4 镜像并构建树（流版本，供 sparse 镜像使用）。
    /// <see cref="Ext4Parser"/> 持有并释放传入流；<see cref="SparseReadStream.Dispose()"/> 幂等，
    /// 双释放安全，仍放入 <paramref name="ownedSource"/> 兜底。</para>
    /// Stream-parses an ext4 image with <see cref="Ext4Parser"/> and builds the tree (stream version,
    /// used for sparse images). <see cref="Ext4Parser"/> takes ownership of the stream;
    /// <see cref="SparseReadStream.Dispose()"/> is idempotent, so a double dispose is safe, and the
    /// stream is still tracked in <paramref name="ownedSource"/> as a fallback.
    /// </summary>
    private static LoadResult LoadExt4(Stream stream, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        return BuildExt4Result(new Ext4Parser(stream), imageSize, ownedSource, ct);
    }

    /// <summary>
    /// <para>列出 ext4 条目、构建扁平列表与树，并组装 <see cref="LoadResult"/>。</para>
    /// Lists the ext4 entries, builds the flat list and tree, and assembles the <see cref="LoadResult"/>.
    /// </summary>
    private static LoadResult BuildExt4Result(Ext4Parser parser, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        var entries = parser.ListFiles();
        ct.ThrowIfCancellationRequested();

        var flat = new List<FlatEntry>(entries.Count);
        foreach (var entry in entries)
        {
            flat.Add(new FlatEntry(entry.Path, entry.Size, ToKind(entry.Kind)));
        }

        var tree = FileManagerTreeBuilder.Build(flat);
        return new LoadResult("EXT4", imageSize, parser, flat, tree, ownedSource);
    }

    /// <summary>
    /// <para>用 <see cref="ErofsParser"/> 流式解析 EROFS 镜像并构建树（路径版本：解析器自持并释放 FileStream）。
    /// 目录按路径合成。</para>
    /// Stream-parses an EROFS image with <see cref="ErofsParser"/> and builds the tree (path version:
    /// the parser owns and disposes its FileStream). Directories are synthesized from paths.
    /// </summary>
    private static LoadResult LoadErofs(string path, CancellationToken ct)
    {
        var parser = new ErofsParser(path);
        return BuildErofsResult(parser, new FileInfo(path).Length, null, ct);
    }

    /// <summary>
    /// <para>用 <see cref="ErofsParser"/> 流式解析 EROFS 镜像并构建树（流版本，供 sparse 镜像使用）。
    /// <see cref="ErofsParser"/> 不释放传入流，必须由 <paramref name="ownedSource"/>
    /// （即 <see cref="SparseReadStream"/>）负责释放。目录按路径合成。</para>
    /// Stream-parses an EROFS image with <see cref="ErofsParser"/> and builds the tree (stream version,
    /// used for sparse images). <see cref="ErofsParser"/> does not dispose the given stream;
    /// <paramref name="ownedSource"/> (the <see cref="SparseReadStream"/>) must. Directories are
    /// synthesized from paths.
    /// </summary>
    private static LoadResult LoadErofs(Stream stream, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        return BuildErofsResult(new ErofsParser(stream), imageSize, ownedSource, ct);
    }

    /// <summary>
    /// <para>列出 EROFS 条目、构建扁平列表与树，并组装 <see cref="LoadResult"/>。</para>
    /// Lists the EROFS entries, builds the flat list and tree, and assembles the <see cref="LoadResult"/>.
    /// </summary>
    private static LoadResult BuildErofsResult(ErofsParser parser, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        var entries = parser.EnumerateFiles();
        ct.ThrowIfCancellationRequested();

        var flat = new List<FlatEntry>(entries.Count);
        foreach (var entry in entries)
        {
            flat.Add(new FlatEntry(entry.Path, entry.Size, FileEntryKind.File));
        }

        var tree = FileManagerTreeBuilder.Build(flat);
        return new LoadResult("EROFS", imageSize, parser, flat, tree, ownedSource);
    }

    /// <summary>
    /// <para>用 <see cref="F2fsParser"/> 流式解析 F2FS 镜像并构建树（路径版本：解析器自持并释放 FileStream）。</para>
    /// Stream-parses an F2FS image with <see cref="F2fsParser"/> and builds the tree (path version: the
    /// parser owns and disposes its FileStream).
    /// </summary>
    private static LoadResult LoadF2fs(string path, CancellationToken ct)
    {
        var parser = new F2fsParser(path);
        return BuildF2fsResult(parser, new FileInfo(path).Length, null, ct);
    }

    /// <summary>
    /// <para>用 <see cref="F2fsParser"/> 流式解析 F2FS 镜像并构建树（流版本，供 sparse 镜像使用）。
    /// <see cref="F2fsParser"/> 持有并释放传入流；<see cref="SparseReadStream.Dispose()"/> 幂等，
    /// 双释放安全，仍放入 <paramref name="ownedSource"/> 兜底。</para>
    /// Stream-parses an F2FS image with <see cref="F2fsParser"/> and builds the tree (stream version,
    /// used for sparse images). <see cref="F2fsParser"/> takes ownership of the stream;
    /// <see cref="SparseReadStream.Dispose()"/> is idempotent, so a double dispose is safe, and the
    /// stream is still tracked in <paramref name="ownedSource"/> as a fallback.
    /// </summary>
    private static LoadResult LoadF2fs(Stream stream, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        return BuildF2fsResult(new F2fsParser(stream), imageSize, ownedSource, ct);
    }

    /// <summary>
    /// <para>列出 F2FS 条目、构建扁平列表与树，并组装 <see cref="LoadResult"/>。</para>
    /// Lists the F2FS entries, builds the flat list and tree, and assembles the <see cref="LoadResult"/>.
    /// </summary>
    private static LoadResult BuildF2fsResult(F2fsParser parser, long imageSize, IDisposable? ownedSource, CancellationToken ct)
    {
        var entries = parser.ListFiles();
        ct.ThrowIfCancellationRequested();

        var flat = new List<FlatEntry>(entries.Count);
        foreach (var entry in entries)
        {
            flat.Add(new FlatEntry(entry.Path, entry.Size, ToKind(entry.Kind)));
        }

        var tree = FileManagerTreeBuilder.Build(flat);
        return new LoadResult("F2FS", imageSize, parser, flat, tree, ownedSource);
    }

    /// <summary>
    /// <para>应用后台加载结果到 UI 线程可见属性。</para>
    /// Applies the background load result to the UI-visible properties.
    /// </summary>
    private void ApplyLoadResult(LoadResult result)
    {
        _parser?.Dispose();
        _ownedSource?.Dispose();
        _parser = result.Parser;
        _ownedSource = result.OwnedSource;
        _flatEntries = result.FlatEntries;
        FormatName = result.FormatName;
        ImageSize = result.ImageSize;

        Entries.Clear();
        foreach (var node in result.Tree)
        {
            Entries.Add(node);
        }

        var root = new FileManagerEntryViewModel("/", string.Empty, 0, FileEntryKind.Directory) { IsExpanded = true };
        foreach (var node in result.Tree)
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

        (FileCount, DirectoryCount) = CountEntries(result.Tree);
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// <para>遍历树统计文件（含符号链接）与目录总数。</para>
    /// Walks the tree to count files (including symlinks) and directories.
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
    /// <para>将 ext4 条目类型映射为统一的 <see cref="FileEntryKind"/>。</para>
    /// Maps an ext4 entry kind to the unified <see cref="FileEntryKind"/>.
    /// </summary>
    private static FileEntryKind ToKind(Ext4Parser.Ext4EntryKind kind) => kind switch
    {
        Ext4Parser.Ext4EntryKind.Directory => FileEntryKind.Directory,
        Ext4Parser.Ext4EntryKind.Symlink => FileEntryKind.Symlink,
        _ => FileEntryKind.File,
    };

    /// <summary>
    /// <para>将 F2FS 条目类型映射为统一的 <see cref="FileEntryKind"/>。</para>
    /// Maps an F2FS entry kind to the unified <see cref="FileEntryKind"/>.
    /// </summary>
    private static FileEntryKind ToKind(F2fsParser.F2fsEntryKind kind) => kind switch
    {
        F2fsParser.F2fsEntryKind.Directory => FileEntryKind.Directory,
        F2fsParser.F2fsEntryKind.Symlink => FileEntryKind.Symlink,
        _ => FileEntryKind.File,
    };

    /// <summary>
    /// <para>重置文档展示状态（加载开始时清空上一次结果）。</para>
    /// Resets the document display state (clears the previous result when a load starts).
    /// </summary>
    private void ResetState()
    {
        _parser?.Dispose();
        _ownedSource?.Dispose();
        _parser = null;
        _ownedSource = null;
        _flatEntries = [];
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
    }

    /// <summary>
    /// <para>将选中的条目（文件/符号链接/目录子树）提取到目标目录。</para>
    /// Extracts the selected entry (file/symlink/directory subtree) into the target directory.
    /// </summary>
    /// <param name="relativePath">
    /// <para>选中条目在镜像内的相对路径。</para>
    /// The relative path of the selected entry inside the image.
    /// </param>
    /// <param name="targetDirectory">
    /// <para>目标目录。</para>
    /// The target directory.
    /// </param>
    private void ExtractEntry(string relativePath, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        switch (_parser)
        {
            case Ext4Parser ext4:
                ExtractExt4(ext4, relativePath, targetDirectory);
                break;
            case ErofsParser erofs:
                ExtractErofs(erofs, relativePath, targetDirectory);
                break;
            case F2fsParser f2fs:
                ExtractF2fs(f2fs, relativePath, targetDirectory);
                break;
        }
    }

    /// <summary>
    /// <para>从 ext4 解析器提取选中条目：文件/符号链接直接写出，目录提取整棵子树。</para>
    /// Extracts the selected entry from an ext4 parser: files/symlinks are written directly, and a
    /// directory extracts its whole subtree.
    /// </summary>
    private void ExtractExt4(Ext4Parser parser, string relativePath, string targetDirectory)
    {
        var prefix = relativePath + "/";
        var isDirectory = _flatEntries.Any(e => e.Path == relativePath && e.Kind == FileEntryKind.Directory);

        foreach (var flat in _flatEntries)
        {
            if (flat.Path == relativePath)
            {
                if (!isDirectory)
                {
                    ExtractExt4Entry(parser, flat, targetDirectory);
                }

                continue;
            }

            if (flat.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                ExtractExt4Entry(parser, flat, targetDirectory);
            }
        }

        if (isDirectory)
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>
    /// <para>写出单个 ext4 条目（目录建目录；符号链接建原生链接并回退为目标文本文件）。</para>
    /// Writes a single ext4 entry (directories are created; symlinks become native links falling back to
    /// a target-text file).
    /// </summary>
    private static void ExtractExt4Entry(Ext4Parser parser, FlatEntry flat, string targetDirectory)
    {
        var target = Path.Combine(targetDirectory, flat.Path.Replace('/', Path.DirectorySeparatorChar));
        if (flat.Kind == FileEntryKind.Directory)
        {
            Directory.CreateDirectory(target);
            return;
        }

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (flat.Kind == FileEntryKind.Symlink)
        {
            // 符号链接目标为极短文本（快速符号链接 ≤ 60 字节），读入内存写出。
            // A symlink target is a short text (fast symlinks are ≤ 60 bytes); read it into memory.
            using var buffer = new MemoryStream();
            parser.ReadFile(flat.Path, buffer);
            var targetText = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            try
            {
                File.CreateSymbolicLink(target, targetText);
            }
            catch
            {
                File.WriteAllText(target, targetText);
            }

            return;
        }

        using (var output = File.Create(target))
        {
            parser.ReadFile(flat.Path, output);
        }
    }

    /// <summary>
    /// <para>从 F2FS 解析器提取选中条目：文件/符号链接直接写出，目录提取整棵子树。</para>
    /// Extracts the selected entry from an F2FS parser: files/symlinks are written directly, and a
    /// directory extracts its whole subtree.
    /// </summary>
    private void ExtractF2fs(F2fsParser parser, string relativePath, string targetDirectory)
    {
        var prefix = relativePath + "/";
        var isDirectory = _flatEntries.Any(e => e.Path == relativePath && e.Kind == FileEntryKind.Directory);

        foreach (var flat in _flatEntries)
        {
            if (flat.Path == relativePath)
            {
                if (!isDirectory)
                {
                    ExtractF2fsEntry(parser, flat, targetDirectory);
                }

                continue;
            }

            if (flat.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                ExtractF2fsEntry(parser, flat, targetDirectory);
            }
        }

        if (isDirectory)
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>
    /// <para>写出单个 F2FS 条目（目录建目录；符号链接建原生链接并回退为目标文本文件）。</para>
    /// Writes a single F2FS entry (directories are created; symlinks become native links falling back
    /// to a target-text file).
    /// </summary>
    private static void ExtractF2fsEntry(F2fsParser parser, FlatEntry flat, string targetDirectory)
    {
        var target = Path.Combine(targetDirectory, flat.Path.Replace('/', Path.DirectorySeparatorChar));
        if (flat.Kind == FileEntryKind.Directory)
        {
            Directory.CreateDirectory(target);
            return;
        }

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (flat.Kind == FileEntryKind.Symlink)
        {
            // 符号链接目标为极短文本，读入内存写出。
            // A symlink target is a short text; read it into memory.
            using var buffer = new MemoryStream();
            parser.ReadFile(flat.Path, buffer);
            var targetText = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            try
            {
                File.CreateSymbolicLink(target, targetText);
            }
            catch
            {
                File.WriteAllText(target, targetText);
            }

            return;
        }

        using (var output = File.Create(target))
        {
            parser.ReadFile(flat.Path, output);
        }
    }

    /// <summary>
    /// <para>从 EROFS 解析器提取选中条目：文件写出到相对路径，目录提取整棵子树。</para>
    /// Extracts the selected entry from an EROFS parser: files are written to their relative path and a
    /// directory extracts its whole subtree.
    /// </summary>
    private void ExtractErofs(ErofsParser parser, string relativePath, string targetDirectory)
    {
        var prefix = relativePath + "/";
        var matched = false;
        foreach (var flat in _flatEntries)
        {
            if (flat.Path != relativePath && !flat.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            matched = true;
            var target = Path.Combine(targetDirectory, flat.Path.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using var output = File.Create(target);
            parser.ReadFile(flat.Path, output);
        }

        // 目录本身也要创建（含空目录情形）。
        // The directory itself must be created too (including empty directories).
        if (!matched || !_flatEntries.Any(e => e.Path == relativePath))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>
    /// <para>将字节数格式化为可读大小文本（如 "1.5 MB"）；0 字节显示 "0 B"。</para>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 MB"); 0 bytes shows "0 B".
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var abs = Math.Abs(bytes);
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        var value = (double)abs;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : $"{value:0.##} {units[unit]}";
    }

    /// <summary>
    /// <para>扁平路径列表 → 目录树构建器：按 '/' 分组成树，缺失的中间目录自动合成，
    /// 每个文件夹内目录优先、按名称排序。</para>
    /// Flat path-list → tree builder: groups by '/', synthesizes missing intermediate directories, and
    /// sorts each folder with directories first and names in ordinal order.
    /// </summary>
    private static class FileManagerTreeBuilder
    {
        /// <summary>
        /// <para>从扁平条目列表构建目录树，返回根层条目。</para>
        /// Builds the directory tree from the flat entry list, returning the root-level entries.
        /// </summary>
        public static List<FileManagerEntryViewModel> Build(IReadOnlyList<FlatEntry> entries)
        {
            var root = new List<FileManagerEntryViewModel>();
            var byPath = new Dictionary<string, FileManagerEntryViewModel>(StringComparer.Ordinal);

            // 第一遍：为所有真实条目创建节点，确保真实目录优先于合成目录。
            // First pass: create nodes for all real entries so real directories win over synthesized ones.
            foreach (var entry in entries)
            {
                byPath[entry.Path] = new FileManagerEntryViewModel(GetName(entry.Path), entry.Path, entry.Size, entry.Kind);
            }

            // 第二遍：挂载到父节点，缺失的中间目录按需合成。
            // Second pass: attach to parents, synthesizing missing intermediate directories on demand.
            foreach (var entry in entries)
            {
                var node = byPath[entry.Path];
                var parent = EnsureParent(byPath, root, entry.Path);
                if (parent is null)
                {
                    root.Add(node);
                }
                else
                {
                    parent.Children.Add(node);
                }
            }

            foreach (var node in byPath.Values)
            {
                SortChildren(node.Children);
            }

            SortList(root);
            return root;
        }

        /// <summary>
        /// <para>确保条目父链存在并返回直接父节点；缺失的祖先目录按需合成到树中，根级父返回 <c>null</c>。</para>
        /// Ensures the entry's parent chain exists and returns the immediate parent; missing ancestor
        /// directories are synthesized into the tree, and a root-level parent returns <c>null</c>.
        /// </summary>
        private static FileManagerEntryViewModel? EnsureParent(
            Dictionary<string, FileManagerEntryViewModel> byPath,
            List<FileManagerEntryViewModel> root,
            string path)
        {
            var slash = path.LastIndexOf('/');
            if (slash < 0)
            {
                return null;
            }

            var parentPath = path[..slash];

            // 自直接父级向上收集缺失的祖先路径。
            // Collect missing ancestor paths upward from the immediate parent.
            var missing = new Stack<string>();
            var cursor = parentPath;
            while (cursor.Length > 0 && !byPath.ContainsKey(cursor))
            {
                missing.Push(cursor);
                var idx = cursor.LastIndexOf('/');
                cursor = idx < 0 ? string.Empty : cursor[..idx];
            }

            var parent = cursor.Length == 0 ? null : byPath[cursor];
            while (missing.Count > 0)
            {
                var dirPath = missing.Pop();
                var directory = new FileManagerEntryViewModel(GetName(dirPath), dirPath, 0, FileEntryKind.Directory);
                byPath[dirPath] = directory;
                if (parent is null)
                {
                    root.Add(directory);
                }
                else
                {
                    parent.Children.Add(directory);
                }

                parent = directory;
            }

            return parent;
        }

        /// <summary>
        /// <para>对子集合排序：目录优先，其后按名称序。</para>
        /// Sorts a child collection: directories first, then by name.
        /// </summary>
        private static void SortChildren(ObservableCollection<FileManagerEntryViewModel> children)
        {
            var sorted = children
                .OrderByDescending(static c => c.IsDirectory)
                .ThenBy(static c => c.Name, StringComparer.Ordinal)
                .ToList();
            children.Clear();
            foreach (var item in sorted)
            {
                children.Add(item);
            }
        }

        /// <summary>
        /// <para>对根层列表排序：目录优先，其后按名称序。</para>
        /// Sorts the root list: directories first, then by name.
        /// </summary>
        private static void SortList(List<FileManagerEntryViewModel> list)
        {
            list.Sort(static (a, b) =>
            {
                var byKind = b.IsDirectory.CompareTo(a.IsDirectory);
                return byKind != 0 ? byKind : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// <para>取路径最后一段作为名称。</para>
        /// Gets the last path segment as the name.
        /// </summary>
        private static string GetName(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? path : path[(slash + 1)..];
        }
    }
}
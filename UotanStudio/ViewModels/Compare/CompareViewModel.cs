using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>双栏比较文档，管理展示状态、取消、缓存和命令协调。
/// 文件读取与比较交给注入的后端，展示行由独立构建器生成；二进制差异仍按需扫描。</para>
/// Owns display state, cancellation, caches and commands for a two-column comparison.
/// An injected backend reads and compares files; a separate builder projects display rows.
/// Binary difference scans remain lazy.
/// </summary>
public sealed partial class CompareViewModel : Document
{
    private readonly IComparisonService _comparison;

    /// <summary>Creates a compare document with the default file-comparison services.</summary>
    public CompareViewModel() : this(ComparisonService.CreateDefault()) { }

    /// <summary>Creates a document with an explicitly supplied comparison backend.</summary>
    internal CompareViewModel(IComparisonService comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        _comparison = comparison;
    }

    private CancellationTokenSource _loadCts = new();
    private FolderCompareResult? _lastFolderResult;
    private List<FolderCompareItem> _folderItems = [];
    private ComparisonMetadata? _leftMetadata;
    private ComparisonMetadata? _rightMetadata;
    private CompareHexRowCollection? _currentHexRows;
    private string _statsText = string.Empty;

    /// <summary>
    /// <para>左侧路径：文件夹比较时为目录，文本/二进制比较时为文件。</para>
    /// The left path: a directory in folder mode, a file in text/binary mode.
    /// </summary>
    [ObservableProperty] private string _leftPath = string.Empty;

    /// <summary>
    /// <para>右侧路径：文件夹比较时为目录，文本/二进制比较时为文件；单侧比较时为空。</para>
    /// The right path: a directory in folder mode, a file in text/binary mode; empty for single-side
    /// comparisons.
    /// </summary>
    [ObservableProperty] private string _rightPath = string.Empty;

    /// <summary>
    /// <para>当前选中行在 <see cref="Rows"/> 中的索引；无选中时重置为 -1。</para>
    /// The index of the selected row within <see cref="Rows"/>; -1 when nothing is selected.
    /// </summary>
    [ObservableProperty] private int _selectedIndex = -1;

    /// <summary>
    /// <para>差异导航滚动请求：由上一处/下一处差异命令写入目标行索引，视图据此把两栏
    /// 顶部对齐滚动到该行；普通点选不写此属性（不触发滚动）。先写 -1 再写目标值以保证
    /// 同一索引重复导航也能再次触发属性通知。</para>
    /// Diff-navigation scroll request: written by the prev/next-diff commands with the target row
    /// index; the view top-aligns both panes to that row. Plain clicks never write it (no scroll
    /// happens). Written as -1 first, then the target, so re-navigating to the same index still
    /// raises a property-change notification.
    /// </summary>
    [ObservableProperty] private int _scrollToIndex = -1;

    /// <summary>
    /// <para>是否正在后台加载/比对（驱动忙碌指示）。</para>
    /// Whether a background load/comparison is running (drives the busy indicator).
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// <para>是否存在错误（驱动错误区显示）。</para>
    /// Whether an error is present (drives the error-section visibility).
    /// </summary>
    [ObservableProperty] private bool _hasError;

    /// <summary>
    /// <para>最后一次操作错误文本；为空表示无错误。</para>
    /// The last operation error text; empty means no error.
    /// </summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>
    /// <para>相同条目数（文件夹模式）。</para>
    /// Count of equal items (folder mode).
    /// </summary>
    [ObservableProperty] private int _equalCount;

    /// <summary>
    /// <para>不同条目数（文件夹模式）。</para>
    /// Count of different items (folder mode).
    /// </summary>
    [ObservableProperty] private int _differentCount;

    /// <summary>
    /// <para>仅左侧条目数（文件夹模式）。</para>
    /// Count of left-only items (folder mode).
    /// </summary>
    [ObservableProperty] private int _leftOnlyCount;

    /// <summary>
    /// <para>仅右侧条目数（文件夹模式）。</para>
    /// Count of right-only items (folder mode).
    /// </summary>
    [ObservableProperty] private int _rightOnlyCount;

    /// <summary>
    /// <para>是否显示相同条目（文件夹过滤开关）。</para>
    /// Whether equal items are shown (folder filter toggle).
    /// </summary>
    [ObservableProperty] private bool _showEqual = true;

    /// <summary>
    /// <para>是否显示不同条目（文件夹过滤开关）。</para>
    /// Whether different items are shown (folder filter toggle).
    /// </summary>
    [ObservableProperty] private bool _showDifferent = true;

    /// <summary>
    /// <para>是否显示仅左侧条目（文件夹过滤开关）。</para>
    /// Whether left-only items are shown (folder filter toggle).
    /// </summary>
    [ObservableProperty] private bool _showLeftOnly = true;

    /// <summary>
    /// <para>是否显示仅右侧条目（文件夹过滤开关）。</para>
    /// Whether right-only items are shown (folder filter toggle).
    /// </summary>
    [ObservableProperty] private bool _showRightOnly = true;

    /// <summary>
    /// <para>当前展示模式。</para>
    /// The active display mode.
    /// </summary>
    public CompareMode Mode
    {
        get => _mode;
        private set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasComparison));
            OnPropertyChanged(nameof(IsFolderMode));
            OnPropertyChanged(nameof(IsTextMode));
            OnPropertyChanged(nameof(IsBinaryMode));
            RebuildSideRows();
        }
    }

    private CompareMode _mode;

    /// <summary>
    /// <para>二进制模式的当前视图（元数据或十六进制）。</para>
    /// The binary mode's active view (metadata or hex).
    /// </summary>
    public BinaryViewKind BinaryViewKind
    {
        get => _binaryViewKind;
        private set
        {
            if (_binaryViewKind == value)
            {
                return;
            }

            _binaryViewKind = value;
            OnPropertyChanged();
        }
    }

    private BinaryViewKind _binaryViewKind = BinaryViewKind.Metadata;

    /// <summary>
    /// <para>当前展示的对比行列表；尚无比较内容时为 <c>null</c>。</para>
    /// The currently displayed compare rows; <c>null</c> when there is no comparison.
    /// </summary>
    public IReadOnlyList<CompareRowViewModel>? Rows
    {
        get => _rows;
        private set
        {
            if (ReferenceEquals(_rows, value))
            {
                return;
            }

            _rows = value;
            OnPropertyChanged();
            RebuildSideRows();
        }
    }

    private IReadOnlyList<CompareRowViewModel>? _rows;

    /// <summary>
    /// <para>左侧栏绑定的行列表：文件夹模式时排除仅右侧条目（仅右侧条目不在左侧显示），
    /// 文本/十六进制模式时与 <see cref="Rows"/> 相同。</para>
    /// The left-pane row list: in folder mode excludes right-only items (they are not shown on the
    /// left), and is identical to <see cref="Rows"/> in text/hex mode.
    /// </summary>
    public IReadOnlyList<CompareRowViewModel>? LeftRows
    {
        get => _leftRows;
        private set
        {
            if (ReferenceEquals(_leftRows, value)) return;
            _leftRows = value;
            OnPropertyChanged();
        }
    }
    private IReadOnlyList<CompareRowViewModel>? _leftRows;

    /// <summary>
    /// <para>右侧栏绑定的行列表：文件夹模式时排除仅左侧条目，
    /// 文本/十六进制模式时与 <see cref="Rows"/> 相同。</para>
    /// The right-pane row list: in folder mode excludes left-only items,
    /// and is identical to <see cref="Rows"/> in text/hex mode.
    /// </summary>
    public IReadOnlyList<CompareRowViewModel>? RightRows
    {
        get => _rightRows;
        private set
        {
            if (ReferenceEquals(_rightRows, value)) return;
            _rightRows = value;
            OnPropertyChanged();
        }
    }
    private IReadOnlyList<CompareRowViewModel>? _rightRows;

    /// <summary>
    /// <para>是否已有比较内容（驱动空态提示显示）。</para>
    /// Whether a comparison is loaded (drives the empty-state hint).
    /// </summary>
    public bool HasComparison => Mode != CompareMode.None;

    /// <summary>
    /// <para>左侧路径是否非空。</para>
    /// Whether the left path is non-empty.
    /// </summary>
    public bool HasLeft => !string.IsNullOrWhiteSpace(LeftPath);

    /// <summary>
    /// <para>右侧路径是否非空。</para>
    /// Whether the right path is non-empty.
    /// </summary>
    public bool HasRight => !string.IsNullOrWhiteSpace(RightPath);

    /// <summary>
    /// <para>是否处于文件夹比较模式。</para>
    /// Whether folder-compare mode is active.
    /// </summary>
    public bool IsFolderMode => Mode == CompareMode.Folder;

    /// <summary>
    /// <para>是否处于文本比较模式。</para>
    /// Whether text-compare mode is active.
    /// </summary>
    public bool IsTextMode => Mode == CompareMode.Text;

    /// <summary>
    /// <para>是否处于二进制比较模式。</para>
    /// Whether binary-compare mode is active.
    /// </summary>
    public bool IsBinaryMode => Mode == CompareMode.Binary;

    /// <summary>
    /// <para>状态栏汇总文本（差异汇总或"等待拖入右侧"提示）。</para>
    /// The status-bar summary text (diff summary or a "drop the right side" hint).
    /// </summary>
    public string StatsText
    {
        get => _statsText;
        private set
        {
            if (_statsText == value)
            {
                return;
            }

            _statsText = value;
            OnPropertyChanged();
        }
    }

    partial void OnLeftPathChanged(string value) => OnPropertyChanged(nameof(HasLeft));

    partial void OnRightPathChanged(string value) => OnPropertyChanged(nameof(HasRight));

    partial void OnSelectedIndexChanged(int value)
    {
        var max = Rows is null || Rows.Count == 0 ? -1 : Rows.Count - 1;
        var clamped = value < -1 ? -1 : value > max ? max : value;
        if (clamped != value)
        {
            SelectedIndex = clamped;
        }
    }

    partial void OnShowEqualChanged(bool value)
    {
        if (Mode == CompareMode.Folder)
        {
            ApplyFolderFilters();
        }
    }

    partial void OnShowDifferentChanged(bool value)
    {
        if (Mode == CompareMode.Folder)
        {
            ApplyFolderFilters();
        }
    }

    partial void OnShowLeftOnlyChanged(bool value)
    {
        if (Mode == CompareMode.Folder)
        {
            ApplyFolderFilters();
        }
    }

    partial void OnShowRightOnlyChanged(bool value)
    {
        if (Mode == CompareMode.Folder)
        {
            ApplyFolderFilters();
        }
    }

    /// <summary>
    /// <para>载入右键选中的路径：首次或文件/目录类型变化时填充左侧，否则填充或替换右侧。</para>
    /// Loads an explorer selection on the left initially or when switching between files and folders;
    /// otherwise fills or replaces the right side.
    /// </summary>
    /// <param name="path"><para>选中的文件或目录路径。</para>The selected file or directory path.</param>
    public Task AddComparisonPathAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var isDirectory = Directory.Exists(fullPath);
        if (!isDirectory && !File.Exists(fullPath))
        {
            throw new FileNotFoundException("Comparison path does not exist.", fullPath);
        }

        // 首次加载尚未完成时 Mode 可能仍为空态，按已设置的左侧路径决定配对。
        // The first load may not have set Mode yet; use the assigned left path to pair selections.
        var canPair = HasLeft && (isDirectory ? Directory.Exists(LeftPath) : File.Exists(LeftPath));
        var left = canPair ? LeftPath : fullPath;
        var right = canPair ? fullPath : string.Empty;
        return isDirectory
            ? OpenCompareFoldersAsync(left, right)
            : OpenCompareFilesAsync(left, right);
    }

    /// <summary>
    /// <para>打开文件比较：嗅探左右文件（NUL 文本探测且非已知固件扩展名则为文本模式，
    /// 否则二进制模式）并异步加载；右侧可留空作为单侧比较等待第二次拖入。</para>
    /// Opens a file comparison: sniffs both files (text mode when they pass the NUL probe and have no
    /// known firmware extension, otherwise binary mode) and loads asynchronously; the right side may be
    /// empty to await a second drop as a single-side comparison.
    /// </summary>
    /// <param name="leftPath">左文件路径。<para>Left file path.</para></param>
    /// <param name="rightPath">右文件路径；可留空。<para>Right file path; may be empty.</para></param>
    public Task OpenCompareFilesAsync(string leftPath, string rightPath)
    {
        if (string.IsNullOrWhiteSpace(leftPath))
        {
            return Task.CompletedTask;
        }

        LeftPath = leftPath;
        RightPath = rightPath ?? string.Empty;
        return LoadFilesAsync();
    }

    /// <summary>
    /// <para>打开文件夹比较：切到文件夹模式并触发首次异步比对；右侧可留空作为单侧比较
    /// （自行递归枚举左侧目录生成仅左侧行）等待第二次拖入。</para>
    /// Opens a folder comparison: switches to folder mode and kicks off the first asynchronous
    /// comparison; the right side may be empty as a single-side comparison (recursively enumerating the
    /// left directory into left-only rows) awaiting a second drop.
    /// </summary>
    /// <param name="leftDirectory">左目录路径。<para>Left directory path.</para></param>
    /// <param name="rightDirectory">右目录路径；可留空。<para>Right directory path; may be empty.</para></param>
    public Task OpenCompareFoldersAsync(string leftDirectory, string rightDirectory)
    {
        if (string.IsNullOrWhiteSpace(leftDirectory))
        {
            return Task.CompletedTask;
        }

        _currentHexRows?.Dispose();
        _currentHexRows = null;
        LeftPath = leftDirectory;
        RightPath = rightDirectory ?? string.Empty;
        Mode = CompareMode.Folder;
        return RefreshAsync();
    }

    /// <summary>
    /// <para>按拖拽路径打开比较：两个文件 → 文件比较；两个目录 → 文件夹比较；
    /// 单个文件/文件夹在空态时作为左侧打开对应比较等待再次拖入；已有文件夹比较时单个路径
    /// 替换对应侧（右侧留空时优先填充右侧，否则替换左侧）；已有文件比较时单个文件替换对应侧，
    /// 单个目录重新开始文件夹比较。</para>
    /// Opens a comparison from dropped paths: two files → file compare; two directories → folder
    /// compare; a single file/folder opens the matching comparison with it as the left side from the
    /// empty state, awaiting a second drop; with an existing folder comparison a single path replaces
    /// the matching side (filling the right side first when it is still empty, otherwise the left); with
    /// an existing file comparison a single file replaces the matching side while a single directory
    /// starts a fresh folder comparison.
    /// </summary>
    /// <param name="paths"><para>拖入的路径列表。</para>The dropped path list.</param>
    /// <returns><para>是否消费了全部路径（两个文件或两个目录）。</para>
    /// Whether all paths were consumed (two files or two directories).</returns>
    public async Task<bool> TryOpenDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
        {
            // 单个路径：文件在文件夹比较中重启文件比较、在文件比较中填充/替换对应侧；
            // 目录在文件夹比较中填充/替换对应侧、在文件比较中重启文件夹比较。
            // A single path: in folder mode a file restarts a file comparison while a directory
            // fills/replaces a side; in file mode a file fills/replaces a side while a directory
            // restarts a folder comparison.
            var single = paths[0];
            if (File.Exists(single))
            {
                if (Mode == CompareMode.Folder)
                {
                    await OpenCompareFilesAsync(single, string.Empty);
                }
                else if (HasComparison)
                {
                    if (!HasRight)
                    {
                        await ReplaceRightAsync(single);
                    }
                    else
                    {
                        await ReplaceLeftAsync(single);
                    }
                }
                else
                {
                    await OpenCompareFilesAsync(single, string.Empty);
                }

                return true;
            }

            if (Directory.Exists(single))
            {
                if (Mode == CompareMode.Folder)
                {
                    if (!HasRight)
                    {
                        await ReplaceRightAsync(single);
                    }
                    else
                    {
                        await ReplaceLeftAsync(single);
                    }
                }
                else
                {
                    await OpenCompareFoldersAsync(single, string.Empty);
                }

                return true;
            }

            return false;
        }

        if (paths.Count < 2)
        {
            return false;
        }

        var files = new List<string>();
        var directories = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                directories.Add(path);
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        if (files.Count >= 2)
        {
            await OpenCompareFilesAsync(files[0], files[1]);
            return true;
        }

        if (directories.Count >= 2)
        {
            await OpenCompareFoldersAsync(directories[0], directories[1]);
            return true;
        }

        return false;
    }

    /// <summary>
    /// <para>释放文档持有的比较状态（文档关闭时调用），取消进行中的加载并回收懒加载的
    /// 十六进制集合。</para>
    /// Releases the comparison state held by this document (called when the document is closed),
    /// cancels any in-flight load and disposes the lazily-loaded hex collection.
    /// </summary>
    public void Cleanup()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        _currentHexRows?.Dispose();
        _currentHexRows = null;
        _leftMetadata = null;
        _rightMetadata = null;
        _lastFolderResult = null;
        _folderItems = [];
        Rows = null;
        Mode = CompareMode.None;
        BinaryViewKind = BinaryViewKind.Metadata;
        SelectedIndex = -1;
        LeftPath = string.Empty;
        RightPath = string.Empty;
        IsBusy = false;
        HasError = false;
        ErrorMessage = string.Empty;
        EqualCount = 0;
        DifferentCount = 0;
        LeftOnlyCount = 0;
        RightOnlyCount = 0;
        StatsText = string.Empty;
    }

    /// <summary>
    /// <para>下一差异命令：在文本/十六进制模式下从选中行之后查找差异行并选中，绕回循环。</para>
    /// Next-diff command: in text/hex mode finds the next diff row after the selected one and selects
    /// it, wrapping around.
    /// </summary>
    [RelayCommand]
    private void NextDiff()
    {
        if (Mode is not (CompareMode.Text or CompareMode.Binary) || Rows is null || Rows.Count == 0)
        {
            return;
        }

        for (var i = 1; i <= Rows.Count; i++)
        {
            var idx = (SelectedIndex + i) % Rows.Count;
            if (Rows[idx].IsLeftDiff || Rows[idx].IsRightDiff)
            {
                SelectedIndex = idx;
                ScrollToIndex = -1;
                ScrollToIndex = idx;
                return;
            }
        }
    }

    /// <summary>
    /// <para>上一差异命令：在文本/十六进制模式下从选中行之前查找差异行并选中，绕回循环。</para>
    /// Previous-diff command: in text/hex mode finds the previous diff row before the selected one and
    /// selects it, wrapping around.
    /// </summary>
    [RelayCommand]
    private void PrevDiff()
    {
        if (Mode is not (CompareMode.Text or CompareMode.Binary) || Rows is null || Rows.Count == 0)
        {
            return;
        }

        for (var i = 1; i <= Rows.Count; i++)
        {
            var idx = ((SelectedIndex - i) % Rows.Count + Rows.Count) % Rows.Count;
            if (Rows[idx].IsLeftDiff || Rows[idx].IsRightDiff)
            {
                SelectedIndex = idx;
                ScrollToIndex = -1;
                ScrollToIndex = idx;
                return;
            }
        }
    }

    /// <summary>
    /// <para>切换二进制视图命令：在元数据与十六进制差异视图间切换并重建行；
    /// 切换前释放旧十六进制集合，切回元数据时使用缓存结果避免重复读取。</para>
    /// Toggle-binary-view command: switches between the metadata and hex-diff views and rebuilds the
    /// rows; the old hex collection is disposed first and switching back to metadata reuses the cached
    /// metadata to avoid re-reading.
    /// </summary>
    [RelayCommand]
    private async Task ToggleHexView()
    {
        if (Mode != CompareMode.Binary)
        {
            return;
        }

        if (BinaryViewKind == BinaryViewKind.Hex)
        {
            _currentHexRows?.Dispose();
            _currentHexRows = null;
            BinaryViewKind = BinaryViewKind.Metadata;
            Rows = ComparisonRowBuilder.ForMetadata(_leftMetadata, _rightMetadata, LanguageService.Instance.GetString("Compare_Unrecognized"));
            SelectedIndex = -1;
            return;
        }

        // 单侧（右空）没有可比较的右侧文件，无法产出差异区间，保持在元数据视图。
        // A single side (empty right) has no right file to diff against, so hex stays unavailable.
        if (string.IsNullOrWhiteSpace(RightPath))
        {
            return;
        }

        var ct = BeginLoad();
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            var maxLength = Math.Max(new FileInfo(LeftPath).Length, new FileInfo(RightPath).Length);
            var rowDiffBitmap = new byte[BinaryDiffService.RowBitmapLength(maxLength)];
            var stats = await _comparison.CompareBinaryAsync(LeftPath, RightPath, rowDiffBitmap, ct);
            if (ct.IsCancellationRequested)
            {
                // 协作式取消：位图/统计基于部分扫描结果，直接丢弃并保持当前视图。
                // Cooperative cancellation: the bitmap/stats came from a partial scan, discard them
                // and keep the current view.
                return;
            }

            _currentHexRows?.Dispose();
            var hex = new CompareHexRowCollection(LeftPath, RightPath, rowDiffBitmap);
            _currentHexRows = hex;
            Rows = hex;
            BinaryViewKind = BinaryViewKind.Hex;
            SelectedIndex = -1;
            StatsText = string.Format(
                LanguageService.Instance.GetString("Compare_Summary"),
                stats.TotalDifferentBytes,
                stats.RangeCount);
            HasError = false;
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 取消：保持当前状态。
            // Cancelled: keep the current state.
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
        finally
        {
            // 仅当仍是当前加载（未被更新的加载取代）时清除 busy：被取消的旧加载
            // 不得把新加载的 busy 状态置 false。
            // Clear busy only while this load is still the current one: a cancelled older load
            // must not clear the newer load's busy state.
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// <para>用拖入路径替换左侧（目录替换后重扫文件夹；文件重载对应模式），供单路径拖入使用。</para>
    /// Replaces the left side with a dropped path (directories re-scan the folder; files reload the
    /// matching mode); used by single-path drops.
    /// </summary>
    /// <param name="path">新左侧路径。<para>The new left path.</para></param>
    private async Task ReplaceLeftAsync(string path)
    {
        LeftPath = path;
        if (Directory.Exists(path))
        {
            Mode = CompareMode.Folder;
            await RefreshAsync();
        }
        else if (File.Exists(path))
        {
            await LoadFilesAsync();
        }
    }

    /// <summary>
    /// <para>用拖入路径替换右侧（目录替换后重扫文件夹；文件重载对应模式），供单路径拖入使用。</para>
    /// Replaces the right side with a dropped path (directories re-scan the folder; files reload the
    /// matching mode); used by single-path drops.
    /// </summary>
    /// <param name="path">新右侧路径。<para>The new right path.</para></param>
    private async Task ReplaceRightAsync(string path)
    {
        RightPath = path;
        if (Directory.Exists(path))
        {
            Mode = CompareMode.Folder;
            await RefreshAsync();
        }
        else if (File.Exists(path))
        {
            await LoadFilesAsync();
        }
    }

    /// <summary>
    /// <para>加载文件比较：嗅探左右文件选择文本/二进制模式并构建对应行与状态。</para>
    /// Loads a file comparison: sniffs both files to pick text/binary mode and builds the matching
    /// rows and status.
    /// </summary>
    private async Task LoadFilesAsync()
    {
        if (string.IsNullOrWhiteSpace(LeftPath))
        {
            return;
        }

        var ct = BeginLoad();
        _currentHexRows?.Dispose();
        _currentHexRows = null;
        _leftMetadata = null;
        _rightMetadata = null;
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        ResetFolderState();
        try
        {
            var leftIsText = await _comparison.IsTextLikeFileAsync(LeftPath, ct);
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(RightPath))
            {
                Mode = leftIsText ? CompareMode.Text : CompareMode.Binary;
                if (leftIsText)
                {
                    await LoadTextSingleSideAsync(ct);
                }
                else
                {
                    await LoadBinarySingleSideAsync(ct);
                }

                return;
            }

            var rightIsText = await _comparison.IsTextLikeFileAsync(RightPath, ct);
            ct.ThrowIfCancellationRequested();

            if (leftIsText && rightIsText)
            {
                Mode = CompareMode.Text;
                await LoadTextBothSidesAsync(ct);
            }
            else
            {
                Mode = CompareMode.Binary;
                await LoadBinaryBothSidesAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消：保持当前状态。
            // Cancelled: keep the current state.
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
        finally
        {
            // 仅当仍是当前加载（未被更新的加载取代）时清除 busy：被取消的旧加载
            // 不得把新加载的 busy 状态置 false。
            // Clear busy only while this load is still the current one: a cancelled older load
            // must not clear the newer load's busy state.
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// <para>加载双侧文本比较：流式 DiffPlex 行差异并生成对比行与差异汇总。</para>
    /// Loads a two-sided text comparison: a streamed DiffPlex line diff producing rows and a diff
    /// summary.
    /// </summary>
    private async Task LoadTextBothSidesAsync(CancellationToken ct)
    {
        var lines = await _comparison.CompareTextAsync(LeftPath, RightPath, ct);
        ct.ThrowIfCancellationRequested();

        var rows = new List<CompareRowViewModel>(lines.Count);
        var diffCount = 0;
        foreach (var line in lines)
        {
            var row = CompareRowViewModel.ForText(line);
            if (row.IsLeftDiff || row.IsRightDiff)
            {
                diffCount++;
            }

            rows.Add(row);
        }

        Rows = rows;
        SelectedIndex = -1;
        StatsText = string.Format(
            LanguageService.Instance.GetString("Compare_Summary"),
            diffCount,
            rows.Count);
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// <para>加载单侧文本比较：自行流式读取左侧文件行（UTF-8，上限 200 万行/256 MB）生成仅左侧行。</para>
    /// Loads a single-sided text comparison: streams the left file's lines itself (UTF-8, bounded to
    /// 2,000,000 lines / 256 MB) into left-only rows.
    /// </summary>
    private async Task LoadTextSingleSideAsync(CancellationToken ct)
    {
        var lines = await _comparison.ReadTextAsync(LeftPath, ct);
        ct.ThrowIfCancellationRequested();
        var rows = await Task.Run(() => ComparisonRowBuilder.ForText(lines), ct);
        ct.ThrowIfCancellationRequested();

        Rows = rows;
        SelectedIndex = -1;
        StatsText = LanguageService.Instance.GetString("Compare_DropRightFile");
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// <para>加载双侧二进制比较：识别并读取两侧元数据生成元数据行（差异统计仅在切换十六进制
    /// 原始视图时计算）。</para>
    /// Loads a two-sided binary comparison: detects and reads both sides' metadata into metadata
    /// rows (diff stats are computed only when switching to the hex-raw view).
    /// </summary>
    private async Task LoadBinaryBothSidesAsync(CancellationToken ct)
    {
        try
        {
            var leftMeta = await _comparison.ReadMetadataAsync(LeftPath, ct);
            ct.ThrowIfCancellationRequested();
            var rightMeta = await _comparison.ReadMetadataAsync(RightPath, ct);
            ct.ThrowIfCancellationRequested();

            _leftMetadata = leftMeta;
            _rightMetadata = rightMeta;
            BinaryViewKind = BinaryViewKind.Metadata;
            Rows = ComparisonRowBuilder.ForMetadata(leftMeta, rightMeta, LanguageService.Instance.GetString("Compare_Unrecognized"));
            SelectedIndex = -1;

            // 不在此跑全文件差异扫描：二进制模式下 StatsText 不显示，且多 GB 固件的全量扫描
            // 耗时数十秒，会让 busy 指示一直转；差异统计在切换十六进制原始视图时才需要
            // （CompareStats 位图重载），由 ToggleHexView 计算。
            // No full-file diff scan here: StatsText is not shown in binary mode, and a full scan
            // of multi-GB firmware takes tens of seconds, keeping the busy indicator spinning; diff
            // stats are only needed when switching to the hex-raw view (the CompareStats bitmap
            // overload), computed by ToggleHexView.
            HasError = false;
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 取消：保持当前状态，不向上传播（切换十六进制视图/重新加载会取消进行中的加载，
            // 若此处不捕获，后台 Task 抛出的 OCE 可能成为未处理异常导致崩溃）。
            // Cancelled: keep the current state and do not propagate — toggling the hex view or
            // reloading cancels the in-flight load, and without this catch the OCE thrown inside
            // the background Task could surface as an unhandled exception and crash the app.
        }
    }

    /// <summary>
    /// <para>加载单侧二进制比较：识别并读取左侧元数据生成元数据行，右侧留空并提示拖入。</para>
    /// Loads a single-sided binary comparison: detects and reads the left metadata into rows, leaving
    /// the right side empty with a drop hint.
    /// </summary>
    private async Task LoadBinarySingleSideAsync(CancellationToken ct)
    {
        var leftMeta = await _comparison.ReadMetadataAsync(LeftPath, ct);
        ct.ThrowIfCancellationRequested();

        _leftMetadata = leftMeta;
        _rightMetadata = null;
        BinaryViewKind = BinaryViewKind.Metadata;
        Rows = ComparisonRowBuilder.ForMetadata(leftMeta, null, LanguageService.Instance.GetString("Compare_Unrecognized"));
        SelectedIndex = -1;
        StatsText = LanguageService.Instance.GetString("Compare_DropRightFile");
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// <para>取消并重建加载令牌，返回新令牌。</para>
    /// Cancels and rebuilds the load cancellation-token source, returning the new token.
    /// </summary>
    private CancellationToken BeginLoad()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    /// <summary>
    /// <para>按当前模式与 <see cref="Rows"/> 重建左右两侧的行列表：所有模式两侧均共享
    /// <see cref="Rows"/>（行按相对路径/行号一一对应，同名/同行条目自然落在同一水平行）；
    /// 仅单侧条目由 <see cref="CompareRowViewModel"/> 的缺失侧单元格置空驱动，在另一侧渲染
    /// 空白占位行（不显示名称）以保持行对齐。</para>
    /// Rebuilds the left/right row lists by the current mode and <see cref="Rows"/>: both sides
    /// share <see cref="Rows"/> in every mode so rows are aligned one-to-one by relative path /
    /// line number (same-name items land on the same horizontal row). Left-only/right-only items
    /// rely on the empty missing-side cell of <see cref="CompareRowViewModel"/> and render as a
    /// blank placeholder row on the other side to keep alignment.
    /// </summary>
    private void RebuildSideRows()
    {
        LeftRows = Rows;
        RightRows = Rows;
    }


}
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Common;
using UotanStudio.Services;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>对比面板的展示模式。</para>
/// The compare panel's display mode.
/// </summary>
public enum CompareMode
{
    /// <summary>
    /// <para>空态：尚无比较内容。</para>
    /// Empty state: no comparison yet.
    /// </summary>
    None,

    /// <summary>
    /// <para>文件夹比较。</para>
    /// Folder comparison.
    /// </summary>
    Folder,

    /// <summary>
    /// <para>文本比较。</para>
    /// Text comparison.
    /// </summary>
    Text,

    /// <summary>
    /// <para>二进制比较（元数据或十六进制差异视图）。</para>
    /// Binary comparison (metadata or hex-diff view).
    /// </summary>
    Binary,
}

/// <summary>
/// <para>二进制比较的展示视图。</para>
/// The binary comparison's display view.
/// </summary>
public enum BinaryViewKind
{
    /// <summary>
    /// <para>元数据视图（格式名/摘要/键值对/条目/组件）。</para>
    /// Metadata view (format name / summary / key-values / entries / components).
    /// </summary>
    Metadata,

    /// <summary>
    /// <para>十六进制差异视图。</para>
    /// Hex-diff view.
    /// </summary>
    Hex,
}

/// <summary>
/// <para>对比页面视图模型：统一双栏对比面板的唯一宿主，按 <see cref="CompareMode"/> 承载
/// 文件夹 / 文本 / 二进制三种比较。文件夹比较走 <see cref="FolderCompareService"/>，
/// 文本走 <see cref="TextDiffService"/>（流式、有界），二进制先以 <see cref="FormatDetector"/> +
/// <see cref="IFormatHandler.ReadMetadataAsync"/> 展示元数据，再以 <see cref="BinaryDiffService"/>
/// 提供十六进制差异视图（懒加载）。</para>
/// Compare-page view model: the single host of the unified two-column compare panel, carrying
/// folder / text / binary comparisons per <see cref="CompareMode"/>. Folder comparisons use
/// <see cref="FolderCompareService"/>, text uses <see cref="TextDiffService"/> (streamed and bounded),
/// and binary first shows metadata via <see cref="FormatDetector"/> +
/// <see cref="IFormatHandler.ReadMetadataAsync"/>, then offers a lazily-loaded hex-diff view via
/// <see cref="BinaryDiffService"/>.
/// </summary>
public sealed partial class CompareViewModel : ViewModelBase
{
    private CancellationTokenSource _loadCts = new();
    private FolderCompareResult? _lastFolderResult;
    private List<FolderCompareItem> _folderItems = [];
    private SideBinaryMetadata? _leftMetadata;
    private SideBinaryMetadata? _rightMetadata;
    private CompareHexRowCollection? _currentHexRows;
    private string _statsText = string.Empty;

    /// <summary>
    /// <para>单侧固件元数据缓存（避免在元数据/十六进制视图间切换时重复读取）。</para>
    /// Per-side firmware metadata cache (avoids re-reading when toggling between the metadata and hex
    /// views).
    /// </summary>
    private sealed record SideBinaryMetadata(
        string FormatName,
        string Summary,
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<string> Entries,
        IReadOnlyList<FirmwareComponent> Components)
    {
        /// <summary>
        /// <para>未识别侧的占位元数据。</para>
        /// The placeholder metadata for an unrecognized side.
        /// </summary>
        public static SideBinaryMetadata Unrecognized { get; } =
            new(string.Empty, string.Empty, new Dictionary<string, string>(), [], []);
    }

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
    /// <para>刷新命令：在文件夹模式下重新比对并重建行与计数。</para>
    /// Refresh command: re-compares in folder mode and rebuilds the rows and counts.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Mode != CompareMode.Folder)
        {
            return;
        }

        var ct = BeginLoad();
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            // 右侧留空（单路径拖入等待第二次拖入）：不比对，自行递归枚举左侧目录生成仅左侧行。
            // With the right side empty (single-path drop awaiting a second drop) no comparison runs;
            // the left directory is recursively enumerated into left-only rows.
            if (string.IsNullOrWhiteSpace(RightPath))
            {
                var rows = await Task.Run(() => BuildLeftOnlyFolderRows(LeftPath, ct), ct);
                ct.ThrowIfCancellationRequested();
                _lastFolderResult = null;
                _folderItems = [];
                Rows = rows;
                EqualCount = 0;
                DifferentCount = 0;
                LeftOnlyCount = rows.Count;
                RightOnlyCount = 0;
                StatsText = LanguageService.Instance.GetString("Compare_DropRightFolder");
                SelectedIndex = -1;
                return;
            }

            var result = await FolderCompareService.Instance.CompareAsync(LeftPath, RightPath, ct);
            ct.ThrowIfCancellationRequested();

            EqualCount = result.EqualCount;
            DifferentCount = result.DifferentCount;
            LeftOnlyCount = result.LeftOnlyCount;
            RightOnlyCount = result.RightOnlyCount;
            _lastFolderResult = result;
            StatsText = string.Empty;
            ApplyFolderFilters();
        }
        catch (OperationCanceledException)
        {
            // 取消：保持当前状态，下次刷新覆盖。
            // Cancelled: keep the current state; the next refresh overwrites it.
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>把选中条目从左复制到右命令（复制成功后刷新比对）。</para>
    /// Copy-selected-left-to-right command (refreshes the comparison after the copy succeeds).
    /// </summary>
    [RelayCommand]
    private async Task CopyLeftToRightAsync() => await CopyAsync(fromLeftToRight: true);

    /// <summary>
    /// <para>把选中条目从右复制到左命令（复制成功后刷新比对）。</para>
    /// Copy-selected-right-to-left command (refreshes the comparison after the copy succeeds).
    /// </summary>
    [RelayCommand]
    private async Task CopyRightToLeftAsync() => await CopyAsync(fromLeftToRight: false);

    /// <summary>
    /// <para>删除左目录中选中文件命令（删除成功后刷新比对）。</para>
    /// Delete-selected-in-left command (refreshes the comparison after the deletion succeeds).
    /// </summary>
    [RelayCommand]
    private async Task DeleteLeftAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _folderItems.Count || _folderItems[SelectedIndex].Status == FolderItemStatus.RightOnly)
        {
            return;
        }

        await DeleteAsync(_folderItems[SelectedIndex], LeftPath);
    }

    /// <summary>
    /// <para>删除右目录中选中文件命令（删除成功后刷新比对）。</para>
    /// Delete-selected-in-right command (refreshes the comparison after the deletion succeeds).
    /// </summary>
    [RelayCommand]
    private async Task DeleteRightAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _folderItems.Count || _folderItems[SelectedIndex].Status == FolderItemStatus.LeftOnly)
        {
            return;
        }

        await DeleteAsync(_folderItems[SelectedIndex], RightPath);
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
            Rows = BuildBinaryMetadataRows(_leftMetadata, _rightMetadata);
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
            var ranges = await Task.Run(() => BinaryDiffService.Instance.CompareRanges(LeftPath, RightPath, ct), ct);
            ct.ThrowIfCancellationRequested();

            _currentHexRows?.Dispose();
            var hex = new CompareHexRowCollection(LeftPath, RightPath, ranges);
            _currentHexRows = hex;
            Rows = hex;
            BinaryViewKind = BinaryViewKind.Hex;
            SelectedIndex = -1;
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
            IsBusy = false;
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

        // 幂等注册全部格式处理器（应用启动时通常已注册），确保扩展名与检测集合完整。
        // Idempotently register every format handler (the app usually does at startup) so the
        // extension and detection sets are complete.
        FirmwareFormatRegistry.RegisterAll();

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
            var leftIsText = await Task.Run(() => IsTextLikeFile(LeftPath), ct);
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

            var rightIsText = await Task.Run(() => IsTextLikeFile(RightPath), ct);
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
            IsBusy = false;
        }
    }

    /// <summary>
    /// <para>加载双侧文本比较：流式 DiffPlex 行差异并生成对比行与差异汇总。</para>
    /// Loads a two-sided text comparison: a streamed DiffPlex line diff producing rows and a diff
    /// summary.
    /// </summary>
    private async Task LoadTextBothSidesAsync(CancellationToken ct)
    {
        await using var left = new FileStream(
            LeftPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var right = new FileStream(
            RightPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var lines = await Task.Run(() => TextDiffService.Instance.DiffLines(left, right, ct), ct);
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
        var rows = await Task.Run(() => ReadTextRowsLeftOnly(LeftPath, ct), ct);
        ct.ThrowIfCancellationRequested();

        Rows = rows;
        SelectedIndex = -1;
        StatsText = LanguageService.Instance.GetString("Compare_DropRightFile");
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// <para>加载双侧二进制比较：识别并读取两侧元数据生成元数据行，再流式汇总差异统计。</para>
    /// Loads a two-sided binary comparison: detects and reads both sides' metadata into metadata rows,
    /// then aggregates the diff statistics in a stream.
    /// </summary>
    private async Task LoadBinaryBothSidesAsync(CancellationToken ct)
    {
        var leftMeta = await Task.Run(() => ReadBinaryMetadata(LeftPath, ct), ct);
        ct.ThrowIfCancellationRequested();
        var rightMeta = await Task.Run(() => ReadBinaryMetadata(RightPath, ct), ct);
        ct.ThrowIfCancellationRequested();

        _leftMetadata = leftMeta;
        _rightMetadata = rightMeta;
        BinaryViewKind = BinaryViewKind.Metadata;
        Rows = BuildBinaryMetadataRows(leftMeta, rightMeta);
        SelectedIndex = -1;

        var stats = await Task.Run(() => BinaryDiffService.Instance.CompareStats(LeftPath, RightPath, ct), ct);
        ct.ThrowIfCancellationRequested();
        StatsText = string.Format(
            LanguageService.Instance.GetString("Compare_Summary"),
            stats.TotalDifferentBytes,
            stats.RangeCount);
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// <para>加载单侧二进制比较：识别并读取左侧元数据生成元数据行，右侧留空并提示拖入。</para>
    /// Loads a single-sided binary comparison: detects and reads the left metadata into rows, leaving
    /// the right side empty with a drop hint.
    /// </summary>
    private async Task LoadBinarySingleSideAsync(CancellationToken ct)
    {
        var leftMeta = await Task.Run(() => ReadBinaryMetadata(LeftPath, ct), ct);
        ct.ThrowIfCancellationRequested();

        _leftMetadata = leftMeta;
        _rightMetadata = null;
        BinaryViewKind = BinaryViewKind.Metadata;
        Rows = BuildBinaryMetadataRows(leftMeta, null);
        SelectedIndex = -1;
        StatsText = LanguageService.Instance.GetString("Compare_DropRightFile");
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// <para>按过滤开关重建文件夹行，并维护与行平行的底层条目列表（供复制/删除定位）。</para>
    /// Rebuilds the folder rows by the active filter toggles and keeps a parallel underlying-item list
    /// (for copy/delete targeting).
    /// </summary>
    private void ApplyFolderFilters()
    {
        _folderItems = [];
        if (_lastFolderResult is null)
        {
            Rows = null;
            return;
        }

        var rows = new List<CompareRowViewModel>();
        var items = new List<FolderCompareItem>();
        foreach (var item in _lastFolderResult.Items)
        {
            if (ShouldShow(item))
            {
                rows.Add(CompareRowViewModel.ForFolder(item));
                items.Add(item);
            }
        }

        _folderItems = items;
        Rows = rows;
        SelectedIndex = -1;
    }

    /// <summary>
    /// <para>判断文件夹条目是否按当前过滤开关显示。</para>
    /// Determines whether a folder item is shown under the active filter toggles.
    /// </summary>
    private bool ShouldShow(FolderCompareItem item) => item.Status switch
    {
        FolderItemStatus.Equal => ShowEqual,
        FolderItemStatus.Different => ShowDifferent,
        FolderItemStatus.LeftOnly => ShowLeftOnly,
        FolderItemStatus.RightOnly => ShowRightOnly,
        _ => true,
    };

    /// <summary>
    /// <para>把选中文件从一侧复制到另一侧，成功后刷新比对。</para>
    /// Copies the selected file from one side to the other, refreshing the comparison afterwards.
    /// </summary>
    /// <param name="fromLeftToRight">从左复制到右为 true，反之为 false。
    /// <para>true when copying left to right; false otherwise.</para></param>
    private async Task CopyAsync(bool fromLeftToRight)
    {
        if (Mode != CompareMode.Folder || SelectedIndex < 0 || SelectedIndex >= _folderItems.Count)
        {
            return;
        }

        var item = _folderItems[SelectedIndex];
        if (item.IsDirectory || (fromLeftToRight && item.Status == FolderItemStatus.RightOnly)
            || (!fromLeftToRight && item.Status == FolderItemStatus.LeftOnly))
        {
            return;
        }

        try
        {
            if (fromLeftToRight)
            {
                await FolderCompareService.Instance.CopyAsync(LeftPath, RightPath, item.RelativePath);
            }
            else
            {
                await FolderCompareService.Instance.CopyAsync(RightPath, LeftPath, item.RelativePath);
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    /// <summary>
    /// <para>删除指定目录下的选中文件，成功后刷新比对。</para>
    /// Deletes the selected file under the given directory, refreshing the comparison afterwards.
    /// </summary>
    /// <param name="item">被删除的文件夹条目。<para>The folder item to delete.</para></param>
    /// <param name="directory">所属目录路径。<para>The owning directory path.</para></param>
    private async Task DeleteAsync(FolderCompareItem item, string directory)
    {
        if (item.IsDirectory)
        {
            return;
        }

        try
        {
            await FolderCompareService.Instance.DeleteAsync(directory, item.RelativePath);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
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
    /// <para>清理文件夹模式的缓存状态（计数与条目映射），用于切换到文件模式前。</para>
    /// Clears the folder-mode cached state (counts and the item map) before switching to file modes.
    /// </summary>
    private void ResetFolderState()
    {
        _lastFolderResult = null;
        _folderItems = [];
        Rows = null;
        EqualCount = 0;
        DifferentCount = 0;
        LeftOnlyCount = 0;
        RightOnlyCount = 0;
    }

    /// <summary>
    /// <para>判断文件是否应按文本模式处理：通过 NUL 文本探测且无已知固件扩展名。</para>
    /// Determines whether a file should be treated as text: passes the NUL probe and has no known
    /// firmware extension.
    /// </summary>
    private static bool IsTextLikeFile(string path) => BinaryDiffService.Instance.IsTextFile(path) && !IsFirmwareExtension(path);

    /// <summary>
    /// <para>判断文件扩展名是否为已注册格式处理器的典型扩展名（视为固件 → 二进制模式）。</para>
    /// Determines whether a file's extension is a registered format handler's typical extension
    /// (treated as firmware → binary mode).
    /// </summary>
    private static bool IsFirmwareExtension(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0)
        {
            return false;
        }

        foreach (var handler in FormatDetector.RegisteredHandlers)
        {
            foreach (var handlerExtension in handler.FileExtensions)
            {
                if (string.Equals(handlerExtension, ext, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// <para>递归枚举左侧目录生成仅左侧对比行（名称 + 大小，深度按路径段），并按键排序。</para>
    /// Recursively enumerates the left directory into left-only compare rows (name + size, depth by
    /// path segment), sorted by key.
    /// </summary>
    private static List<CompareRowViewModel> BuildLeftOnlyFolderRows(string directory, CancellationToken ct)
    {
        var entries = new List<(string Relative, bool IsDirectory, long Size)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var isDirectory = Directory.Exists(entry);
            var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
            entries.Add((relative, isDirectory, isDirectory ? -1 : new FileInfo(entry).Length));
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Relative, b.Relative));

        var rows = new List<CompareRowViewModel>(entries.Count);
        foreach (var (relative, isDirectory, size) in entries)
        {
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.LeftOnly,
                isDirectory,
                relative.Count(static c => c == '/'),
                string.Empty,
                Path.GetFileName(relative),
                isDirectory ? "-" : FormatSize(size),
                true,
                string.Empty,
                string.Empty,
                string.Empty,
                false));
        }

        return rows;
    }

    /// <summary>
    /// <para>自行流式读取左侧文本文件行（UTF-8，上限 200 万行/256 MB，超出抛 <see cref="FormatException"/>）
    /// 生成仅左侧对比行。</para>
    /// Streams the left text file's lines itself (UTF-8, bounded to 2,000,000 lines / 256 MB, throwing
    /// <see cref="FormatException"/> beyond) into left-only compare rows.
    /// </summary>
    private static List<CompareRowViewModel> ReadTextRowsLeftOnly(string path, CancellationToken ct)
    {
        const int maxLines = 2_000_000;
        const long maxBytes = 256L * 1024 * 1024;

        var rows = new List<CompareRowViewModel>();
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 81920, leaveOpen: false);

        long totalBytes = 0;
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (rows.Count >= maxLines)
            {
                throw new FormatException($"Text file exceeds the {maxLines} line limit.");
            }

            totalBytes += Encoding.UTF8.GetByteCount(line);
            if (totalBytes > maxBytes)
            {
                throw new FormatException($"Text file exceeds the {maxBytes} byte limit.");
            }

            lineNumber++;
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.LeftOnly,
                false,
                0,
                lineNumber.ToString(),
                line,
                string.Empty,
                true,
                string.Empty,
                string.Empty,
                string.Empty,
                false));
        }

        return rows;
    }

    /// <summary>
    /// <para>识别单个文件的固件格式并读取元数据；未识别或读取失败时返回未识别占位。</para>
    /// Detects a file's firmware format and reads its metadata; returns the unrecognized placeholder
    /// when detection or the read fails.
    /// </summary>
    private static SideBinaryMetadata ReadBinaryMetadata(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var handler = FormatDetector.Detect(path);
        if (handler is null)
        {
            return SideBinaryMetadata.Unrecognized;
        }

        var result = handler.ReadMetadataAsync(path, ct).GetAwaiter().GetResult();
        ct.ThrowIfCancellationRequested();
        if (!result.Success)
        {
            LogService.Instance.Write(LogLevel.Warn, "Compare",
                $"Metadata read failed for '{path}': {result.Summary}");
            return SideBinaryMetadata.Unrecognized;
        }

        return new SideBinaryMetadata(
            handler.FormatName,
            result.Summary,
            result.Metadata,
            result.Entries,
            result.Components);
    }

    /// <summary>
    /// <para>由两侧元数据构建元数据视图行：格式名行、摘要行、键值对行（键并集）、条目行与组件行；
    /// 缺失侧取值 "-"，两侧都无法识别时给出单条"无法识别"提示行。</para>
    /// Builds the metadata-view rows from both sides' metadata: a format-name row, summary rows,
    /// key-value rows (key union), entry rows and component rows; the missing side shows "-", and a
    /// single "unrecognized" hint row is produced when neither side is recognized.
    /// </summary>
    private static List<CompareRowViewModel> BuildBinaryMetadataRows(SideBinaryMetadata? left, SideBinaryMetadata? right)
    {
        var rows = new List<CompareRowViewModel>();
        var leftRecognized = left is { FormatName.Length: > 0 };
        var rightRecognized = right is { FormatName.Length: > 0 };

        if (!leftRecognized && !rightRecognized)
        {
            var unrecognized = LanguageService.Instance.GetString("Compare_Unrecognized");
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.Equal, false, 0,
                string.Empty, unrecognized, string.Empty, false,
                string.Empty, unrecognized, string.Empty, false));
            return rows;
        }

        // 格式名行。 Format-name row.
        rows.Add(new CompareRowViewModel(
            FolderItemStatus.Equal, false, 0,
            string.Empty, leftRecognized ? left!.FormatName : "-", string.Empty, false,
            string.Empty, rightRecognized ? right!.FormatName : "-", string.Empty, false));

        // 摘要行（按行拆分）。 Summary rows (split by line).
        var leftSummary = leftRecognized ? SplitLines(left!.Summary) : [];
        var rightSummary = rightRecognized ? SplitLines(right!.Summary) : [];
        var summaryCount = Math.Max(leftSummary.Count, rightSummary.Count);
        for (var i = 0; i < summaryCount; i++)
        {
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.Equal, false, 1,
                string.Empty, i < leftSummary.Count ? leftSummary[i] : "-", string.Empty, false,
                string.Empty, i < rightSummary.Count ? rightSummary[i] : "-", string.Empty, false));
        }

        // 元数据键值对（左右并排，取两字典键并集）。 Metadata key-values (side by side, key union).
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (leftRecognized)
        {
            foreach (var key in left!.Metadata.Keys)
            {
                keys.Add(key);
            }
        }

        if (rightRecognized)
        {
            foreach (var key in right!.Metadata.Keys)
            {
                keys.Add(key);
            }
        }

        foreach (var key in keys)
        {
            var leftValue = leftRecognized && left!.Metadata.TryGetValue(key, out var lv) ? lv : "-";
            var rightValue = rightRecognized && right!.Metadata.TryGetValue(key, out var rv) ? rv : "-";
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.Equal, false, 1,
                string.Empty, key, leftValue, false,
                string.Empty, key, rightValue, false));
        }

        // 条目行（左右都显示条目名，缺失侧 "-"）。 Entry rows (both sides show the entry name, "-" when missing).
        var leftEntries = leftRecognized ? left!.Entries : [];
        var rightEntries = rightRecognized ? right!.Entries : [];
        var entryCount = Math.Max(leftEntries.Count, rightEntries.Count);
        for (var i = 0; i < entryCount; i++)
        {
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.Equal, false, 1,
                string.Empty, i < leftEntries.Count ? leftEntries[i] : "-", string.Empty, false,
                string.Empty, i < rightEntries.Count ? rightEntries[i] : "-", string.Empty, false));
        }

        // 组件行（名称 + 大小）。 Component rows (name + size).
        var leftComponents = leftRecognized ? left!.Components : [];
        var rightComponents = rightRecognized ? right!.Components : [];
        var componentCount = Math.Max(leftComponents.Count, rightComponents.Count);
        for (var i = 0; i < componentCount; i++)
        {
            var leftComponent = i < leftComponents.Count ? leftComponents[i] : null;
            var rightComponent = i < rightComponents.Count ? rightComponents[i] : null;
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.Equal, false, 1,
                string.Empty, leftComponent?.Name ?? "-", leftComponent is null ? "-" : FormatSize(leftComponent.SizeBytes), false,
                string.Empty, rightComponent?.Name ?? "-", rightComponent is null ? "-" : FormatSize(rightComponent.SizeBytes), false));
        }

        return rows;
    }

    /// <summary>
    /// <para>按 '\n' 拆分文本并去除行尾 '\r'；空文本返回空列表。</para>
    /// Splits text by '\n' and trims trailing '\r'; an empty text yields an empty list.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            result.Add(raw.TrimEnd('\r'));
        }

        return result;
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
}

using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>对比视图中的一个文件列：列头承载刷新、返回上一级、路径输入、确认加载与关闭按钮，列体是一份
/// 行列表——浏览目录与文件夹对比显示目录树行（缩进、展开箭头、资源管理器图标），浏览文件显示文本行，
/// 文件对比显示差异行。列可以任意增加，对比时只使用其中两列。</para>
/// One file column of the compare view: its header carries reload, go-up, the path box, confirm-load and
/// close buttons, and its body is a row list — browsing a folder and a folder comparison show directory-tree
/// rows (indentation, expander, explorer glyphs), browsing a file shows text rows, and a file comparison
/// shows diff rows. Columns can be added freely; a comparison only uses two of them.
/// </summary>
public sealed partial class CompareColumnViewModel : ObservableObject, IDisposable
{
    private readonly CompareViewModel _owner;
    private CancellationTokenSource _loadCts = new();
    private IReadOnlyList<CompareRowViewModel>? _rows;
    private CompareTreeNodeViewModel? _treeRoot;
    private CompareColumnSide _side;

    /// <summary>创建文件列；列必须由所属对比文档创建，命令会回写到文档。</summary>
    /// <param name="owner"><para>所属对比文档。</para>The owning compare document.</param>
    internal CompareColumnViewModel(CompareViewModel owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// <para>列中输入的路径：目录按目录树浏览，文件按内容/元数据浏览。</para>
    /// The path entered in this column: a directory browses as a tree, a file by content or metadata.
    /// </summary>
    [ObservableProperty] private string _path = string.Empty;

    /// <summary>
    /// <para>本列是否正在加载内容（驱动列头的小进度条）。</para>
    /// Whether this column is loading content (drives the small progress bar in its header).
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// <para>本列显示的行：浏览目录/文件夹对比为目录树行，浏览文件为文本行，文件对比为差异行；
    /// 为空表示本列尚无内容。</para>
    /// The rows shown by this column: directory-tree rows while browsing a folder or comparing folders, text
    /// rows while browsing a file and diff rows during a file comparison; <c>null</c> means no content yet.
    /// </summary>
    public IReadOnlyList<CompareRowViewModel>? Rows
    {
        get => _rows;
        internal set
        {
            if (ReferenceEquals(_rows, value))
            {
                return;
            }

            _rows = value;
            OnPropertyChanged();
            NotifyContentChanged();
        }
    }

    /// <summary>
    /// <para>浏览目录时的树根（保持折叠，双击或展开箭头才加载子项）；浏览文件、文件夹对比与文件对比时为
    /// <c>null</c>（对比树由文档按比对结果构建）。</para>
    /// The tree root while browsing a folder (kept collapsed; children load on a double-click or through the
    /// expander); <c>null</c> while browsing a file or comparing (the compare tree is built by the document
    /// from the comparison result).
    /// </summary>
    public CompareTreeNodeViewModel? TreeRoot
    {
        get => _treeRoot;
        private set
        {
            if (ReferenceEquals(_treeRoot, value))
            {
                return;
            }

            _treeRoot = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// <para>本列在对比中的角色。</para>
    /// This column's role in the comparison.
    /// </summary>
    public CompareColumnSide Side
    {
        get => _side;
        internal set
        {
            if (_side == value)
            {
                return;
            }

            _side = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBrowsing));
            OnPropertyChanged(nameof(IsCompareSide));
            OnPropertyChanged(nameof(UsesRightCells));
            OnPropertyChanged(nameof(ShowLeftCells));
            OnPropertyChanged(nameof(ShowRightCells));
            NotifyCompareStateChanged();
            NotifyContentChanged();
        }
    }

    /// <summary>
    /// <para>是否为浏览列（未参与对比）。</para>
    /// Whether the column browses instead of showing comparison cells.
    /// </summary>
    public bool IsBrowsing => Side == CompareColumnSide.Browse;

    /// <summary>
    /// <para>是否参与对比。</para>
    /// Whether the column takes part in the comparison.
    /// </summary>
    public bool IsCompareSide => Side != CompareColumnSide.Browse;

    /// <summary>
    /// <para>是否显示对比行的右侧单元格（浏览列与对比左列共用左侧单元格布局）。</para>
    /// Whether the column shows the comparison rows' right cells (browsing columns and the compare left
    /// column share the left-cell layout).
    /// </summary>
    public bool UsesRightCells => Side == CompareColumnSide.Right;

    /// <summary>
    /// <para>路径框是否已有内容。</para>
    /// Whether the path box has content.
    /// </summary>
    public bool HasPath => !string.IsNullOrWhiteSpace(Path);

    /// <summary>
    /// <para>是否显示空态提示（本列尚无内容）。</para>
    /// Whether the empty-state hint is shown (the column has no content yet).
    /// </summary>
    public bool ShowHint => Rows is null;

    /// <summary>
    /// <para>是否显示左侧单元格行列表（浏览列与对比左列）。</para>
    /// Whether the left-cell row list is shown (browsing columns and the compare left column).
    /// </summary>
    public bool ShowLeftCells => Rows is not null && !UsesRightCells;

    /// <summary>
    /// <para>是否显示右侧单元格行列表（仅对比右列）。</para>
    /// Whether the right-cell row list is shown (the compare right column only).
    /// </summary>
    public bool ShowRightCells => Rows is not null && UsesRightCells;

    /// <summary>
    /// <para>对比模式下是否显示十六进制原始数据切换按钮（两侧都有文件的二进制对比）。</para>
    /// Whether the raw-hex toggle is shown in compare mode (a binary comparison with a file on both sides).
    /// </summary>
    public bool ShowHexToggle => IsCompareSide && _owner.IsBinaryMode && _owner.CanShowHexView;

    /// <summary>
    /// <para>对比模式下是否显示上一处/下一处差异按钮（文本与十六进制对比）。</para>
    /// Whether the previous/next-difference buttons are shown in compare mode (text and hex comparisons).
    /// </summary>
    public bool ShowDiffNavigation => IsCompareSide && (_owner.IsTextMode || _owner.IsBinaryMode);

    /// <summary>
    /// <para>行列表的选择模式：文本对比允许拖拽选中多行（用于「复制到另一侧」），其余情况单选。</para>
    /// The row list's selection mode: a text comparison allows dragging a multi-row selection (used by "copy to the other
    /// side"), everything else is single selection.
    /// </summary>
    public SelectionMode RowSelectionMode => _owner.IsTextMode ? SelectionMode.Multiple : SelectionMode.Single;

    /// <summary>
    /// <para>十六进制原始数据切换命令（转发到所属文档，两列共享同一份对比结果）。</para>
    /// The raw-hex toggle command (forwarded to the document; both columns share one comparison).
    /// </summary>
    public IRelayCommand ToggleHexViewCommand => _owner.ToggleHexViewCommand;

    /// <summary>
    /// <para>上一处差异命令（转发到所属文档）。</para>
    /// The previous-difference command (forwarded to the document).
    /// </summary>
    public IRelayCommand PrevDiffCommand => _owner.PrevDiffCommand;

    /// <summary>
    /// <para>下一处差异命令（转发到所属文档）。</para>
    /// The next-difference command (forwarded to the document).
    /// </summary>
    public IRelayCommand NextDiffCommand => _owner.NextDiffCommand;

    /// <summary>
    /// <para>是否可以关闭本列（至少保留一列）。</para>
    /// Whether the column can be closed (at least one column is kept).
    /// </summary>
    public bool CanClose => _owner.Columns.Count > 1;

    /// <summary>
    /// <para>本列对应的文件是否有未保存的编辑（文本对比中修改文本或复制到另一侧之后）。</para>
    /// Whether this column's file has unsaved edits (after text edits or a copy to the other side in a text comparison).
    /// </summary>
    public bool IsDirty => IsCompareSide && _owner.IsFileDirty(Side);

    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasPath));
        NotifyContentChanged();
    }

    /// <summary>
    /// <para>确认按钮：把路径框中的路径加载到本列（目录显示折叠的目录树，文件显示内容/元数据）；
    /// 本列参与对比时改为用当前两列重新执行对比。</para>
    /// Confirm button: loads the path from the box into this column (a directory shows a collapsed tree, a
    /// file shows content/metadata); a participating column re-runs the comparison with the current two
    /// columns instead.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmPathAsync()
    {
        if (await ReloadOrCompareAsync())
        {
            return;
        }

        var path = (Path ?? string.Empty).Trim();
        if (path.Length == 0)
        {
            _owner.ReportError(LanguageService.Instance.GetString("Compare_EmptyPath"));
            return;
        }

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            _owner.ReportError(string.Format(LanguageService.Instance.GetString("Compare_PathNotFound"), path));
            return;
        }

        Path = path;
        await LoadPathAsync(path, isDirectory);
    }

    /// <summary>
    /// <para>刷新按钮：浏览列按当前路径重新扫描磁盘（外部改动后同步）；参与对比时由文档同时刷新两侧
    /// （重新比对目录或重新读取该文件对）。</para>
    /// Reload button: a browsing column re-scans the disk from its current path (syncing external changes),
    /// while a participating column asks the document to refresh both sides (re-comparing the folders or
    /// re-reading the file pair).
    /// </summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (await ReloadOrCompareAsync())
        {
            return;
        }

        var path = (Path ?? string.Empty).Trim();
        if (path.Length == 0)
        {
            _owner.ReportError(LanguageService.Instance.GetString("Compare_EmptyPath"));
            return;
        }

        await LoadPathAsync(path, Directory.Exists(path));
    }

    /// <summary>
    /// <para>返回上一级按钮：浏览列把本列内容切到当前目录（或文件所在目录）的父目录；参与对比时由文档
    /// 同时把两侧切到各自的父目录并重新比对。</para>
    /// Go-up button: a browsing column switches its content to the parent of the current directory (or of the
    /// current file's directory); while comparing, the document moves both sides to their parents and
    /// re-compares.
    /// </summary>
    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (await ReloadOrCompareAsync(allowGoUp: true))
        {
            return;
        }

        var path = (Path ?? string.Empty).Trim();
        if (path.Length == 0)
        {
            _owner.ReportError(LanguageService.Instance.GetString("Compare_EmptyPath"));
            return;
        }

        var directory = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || Directory.GetParent(directory) is not { } parent)
        {
            _owner.ReportError(LanguageService.Instance.GetString("Compare_NoParentFolder"));
            return;
        }

        Path = parent.FullName;
        await LoadPathAsync(parent.FullName, isDirectory: true);
    }

    /// <summary>
    /// <para>关闭本列命令。</para>
    /// Closes this column.
    /// </summary>
    [RelayCommand]
    private async Task Close() => await _owner.RemoveColumnAsync(this);

    /// <summary>
    /// <para>保存本列文件的编辑（文本对比中按当前行写回文件，保留原编码与换行风格）。</para>
    /// Saves this column's file edits (a text comparison writes the current rows back, preserving the original encoding
    /// and line ending).
    /// </summary>
    [RelayCommand]
    private async Task Save() => await _owner.SaveSideAsync(this);

    /// <summary>
    /// <para>展开/折叠本列浏览树上的一个节点：先按需扫描子项，再切换展开状态并重建可见行。</para>
    /// Expands or collapses a node of this column's browsing tree: children are scanned on demand first, then
    /// the expansion state flips and the visible rows are rebuilt.
    /// </summary>
    /// <param name="node"><para>被展开/折叠的节点。</para>The node to expand or collapse.</param>
    internal async Task ToggleTreeExpansionAsync(CompareTreeNodeViewModel node)
    {
        if (TreeRoot is null)
        {
            return;
        }

        if (!node.IsExpanded)
        {
            await node.EnsureChildrenLoadedAsync();
        }

        node.IsExpanded = !node.IsExpanded;
        Rows = ComparisonRowBuilder.ForTree([TreeRoot], compared: false);
    }

    /// <summary>
    /// <para>浏览状态下进入某个目录：把该目录设为本列路径并重建内容（未对比时双击文件夹的行为）。</para>
    /// Enters a directory while browsing: the directory becomes this column's path and the content is rebuilt
    /// (the double-click behaviour when not comparing).
    /// </summary>
    /// <param name="node"><para>要进入的目录节点。</para>The directory node to enter.</param>
    internal async Task EnterDirectoryAsync(CompareTreeNodeViewModel node)
    {
        if (!node.IsDirectory || node.FullPath.Length == 0 || !Directory.Exists(node.FullPath))
        {
            return;
        }

        Path = node.FullPath;
        await LoadPathAsync(node.FullPath, isDirectory: true);
    }

    /// <summary>
    /// <para>把路径加载到本列：目录挂一棵折叠的浏览树（子项按需扫描），文件按文本行或固件元数据构造行。</para>
    /// Loads a path into this column: a directory gets a collapsed browsing tree (children scanned on demand),
    /// while a file's rows come from text lines or firmware metadata.
    /// </summary>
    /// <param name="path"><para>目录或文件路径。</para>The directory or file path.</param>
    /// <param name="isDirectory"><para>是否为目录。</para>Whether the path is a directory.</param>
    internal async Task LoadPathAsync(string path, bool isDirectory)
    {
        var ct = BeginLoad();
        IsBusy = true;
        try
        {
            if (isDirectory)
            {
                // 导入目录后保持折叠：只挂根节点，子项等双击或展开箭头。
                // An imported folder stays collapsed: only the root is attached, children wait for a
                // double-click or the expander arrow.
                var root = CompareTreeNodeViewModel.ForBrowsing(path, isDirectory: true, ct);
                TreeRoot = root;
                Rows = ComparisonRowBuilder.ForTree([root], compared: false);
            }
            else
            {
                var rows = await _owner.BuildFileRowsAsync(path, ct);
                ct.ThrowIfCancellationRequested();
                TreeRoot = null;
                Rows = rows;
            }

            NotifyContentChanged();
            _owner.ClearError();
        }
        catch (OperationCanceledException)
        {
            // 取消：保持当前状态。
            // Cancelled: keep the current state.
        }
        catch (Exception ex)
        {
            Rows = null;
            TreeRoot = null;
            _owner.ReportError(ex.Message);
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
        finally
        {
            // 仅当仍是当前加载时清除 busy：被取消的旧加载不得清除新加载的 busy。
            // Clear busy only while this load is still the current one.
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// <para>清空本列内容并回到未加载状态（结束对比时把对比列还原为浏览列）。</para>
    /// Clears this column and returns it to the unloaded state (used when a compare column falls back to
    /// browsing).
    /// </summary>
    internal void ClearContent()
    {
        BeginLoad();
        TreeRoot = null;
        Rows = null;
        NotifyContentChanged();
    }

    /// <summary>
    /// <para>按当前路径重新加载本列内容（文件被复制/移动/删除/重命名后由视图调用）。</para>
    /// Reloads this column's content from its current path (called by the view after a file was copied, moved, deleted
    /// or renamed).
    /// </summary>
    internal async Task ReloadContentAsync()
    {
        var path = (Path ?? string.Empty).Trim();
        if (path.Length == 0)
        {
            return;
        }

        await LoadPathAsync(path, Directory.Exists(path));
    }

    /// <summary>释放本列的加载令牌，取消尚未完成的读取与子目录扫描。</summary>
    public void Dispose()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
    }

    /// <summary>
    /// <para>对比模式下把本次操作交给文档（刷新/返回上一级都需要同时作用于两侧）；
    /// 返回是否已由对比模式接管。</para>
    /// Hands the operation to the document while this column participates in a comparison (both refresh and
    /// go-up must act on both sides); returns whether compare mode took over.
    /// </summary>
    /// <param name="allowGoUp">
    /// <para>为 true 时把本次操作按「返回上一级」处理（两侧同时上移），否则按刷新处理。</para>
    /// When true the operation is treated as "go up" (both sides move together); otherwise it refreshes.
    /// </param>
    private async Task<bool> ReloadOrCompareAsync(bool allowGoUp = false)
    {
        if (!_owner.IsComparing || !IsCompareSide)
        {
            return false;
        }

        if (allowGoUp)
        {
            await _owner.GoUpCompareAsync();
            return true;
        }

        await _owner.RefreshCompareAsync();
        return true;
    }

    /// <summary>
    /// <para>取消并重建本列的加载令牌，返回新令牌。</para>
    /// Cancels and rebuilds this column's load token, returning the new one.
    /// </summary>
    private CancellationToken BeginLoad()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    /// <summary>通知所有由内容状态派生的可见性属性。</summary>
    private void NotifyContentChanged()
    {
        OnPropertyChanged(nameof(ShowHint));
        OnPropertyChanged(nameof(ShowLeftCells));
        OnPropertyChanged(nameof(ShowRightCells));
        OnPropertyChanged(nameof(HasPath));
        OnPropertyChanged(nameof(CanClose));

        // 文档级的空态提示由各列内容推导，列状态变化后必须让文档重算。
        // The document-level empty hint is derived from every column's content, so the document has to
        // re-evaluate it after a column changes.
        _owner.NotifyColumnsChanged();
    }

    /// <summary>
    /// <para>通知列关闭按钮的可用性（列数变化时由文档调用；不反向通知文档，避免递归）。</para>
    /// Notifies the close button's availability (called by the document when the column count changes; it
    /// does not notify the document back, which would recurse).
    /// </summary>
    internal void NotifyCanCloseChanged() => OnPropertyChanged(nameof(CanClose));

    /// <summary>通知「已修改」状态（决定保存按钮是否显示）。</summary>
    internal void NotifyDirtyChanged() => OnPropertyChanged(nameof(IsDirty));

    /// <summary>通知所有由对比模式派生的可见性属性（文档模式变化时由文档调用）。</summary>
    internal void NotifyCompareStateChanged()
    {
        OnPropertyChanged(nameof(ShowHexToggle));
        OnPropertyChanged(nameof(ShowDiffNavigation));
        OnPropertyChanged(nameof(RowSelectionMode));
    }
}

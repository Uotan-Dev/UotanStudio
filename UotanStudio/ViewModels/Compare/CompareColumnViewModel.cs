using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// One file column of a comparison: its header carries the path box and the reload, go-up, confirm and
/// close commands, and its body shows directory-tree, text or diff rows.
/// <para>比较中的一个文件列：列头承载路径框与刷新、返回上一级、确认加载和关闭命令，列体显示目录树行、
/// 文本行或差异行。</para>
/// </summary>
public sealed partial class CompareColumnViewModel : ObservableObject, IDisposable
{
    private readonly CompareViewModel _owner;
    private CancellationTokenSource _loadCts = new();
    private IReadOnlyList<CompareRowViewModel>? _rows;
    private CompareTreeNodeViewModel? _treeRoot;
    private ObservableCollection<CompareRowViewModel>? _ownRows;
    private bool _isDirty;
    private bool _hasEditableRows;
    private bool _loadingEditorText;
    private CompareColumnSide _side;

    /// <summary>
    /// Creates a column belonging to the given compare document; commands route back to that document.
    /// <para>创建文件列；列由所属对比文档创建，命令回写到该文档。</para>
    /// </summary>
    /// <param name="owner">The owning compare document. <para>所属对比文档。</para></param>
    internal CompareColumnViewModel(CompareViewModel owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// The path entered in this column: a directory browses as a tree, a file by content or metadata.
    /// <para>列中输入的路径：目录按目录树浏览，文件按内容/元数据浏览。</para>
    /// </summary>
    [ObservableProperty] private string _path = string.Empty;

    /// <summary>
    /// Whether this column is loading content (drives the small progress bar in its header).
    /// <para>本列是否正在加载内容（驱动列头的小进度条）。</para>
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// The rows shown by this column: directory-tree rows while browsing a folder or comparing folders, text
    /// rows while browsing a file and diff rows during a file comparison; <c>null</c> means no content yet.
    /// <para>本列显示的行：浏览目录/文件夹对比为目录树行，浏览文件为文本行，文件对比为差异行；
    /// 为空表示本列尚无内容。</para>
    /// </summary>
    /// <value>The rows shown by this column, or <c>null</c> when it has no content yet. <para>本列显示的行；尚无内容时为 <c>null</c>。</para></value>
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
    /// The tree root while browsing a folder (kept collapsed; children load on demand); <c>null</c> otherwise.
    /// <para>浏览目录时的树根（保持折叠，子项按需加载）；其余情况为 <c>null</c>。</para>
    /// </summary>
    /// <value>The browsing tree root, or <c>null</c> when the column is not browsing a folder. <para>浏览目录时的树根；本列不在浏览目录时为 <c>null</c>。</para></value>
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
    /// This column's role in the comparison.
    /// <para>本列在对比中的角色。</para>
    /// </summary>
    /// <value>This column's role in the comparison. <para>本列在对比中的角色。</para></value>
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
            OnPropertyChanged(nameof(IsBrowsingTextEditor));
            OnPropertyChanged(nameof(UsesRightCells));
            OnPropertyChanged(nameof(ShowLeftCells));
            OnPropertyChanged(nameof(ShowRightCells));
            NotifyCompareStateChanged();
            NotifyContentChanged();

            if (value != CompareColumnSide.Browse)
            {
                IsTextEditorVisible = false;
            }
        }
    }

    /// <summary>
    /// Whether the column browses instead of showing comparison cells.
    /// <para>是否为浏览列（未参与对比）。</para>
    /// </summary>
    /// <value>Whether the column browses instead of showing comparison cells. <para>是否为浏览列（未参与对比）。</para></value>
    public bool IsBrowsing => Side == CompareColumnSide.Browse;

    /// <summary>
    /// Whether the column takes part in the comparison.
    /// <para>是否参与对比。</para>
    /// </summary>
    /// <value>Whether the column takes part in the comparison. <para>是否参与对比。</para></value>
    public bool IsCompareSide => Side != CompareColumnSide.Browse;

    /// <summary>
    /// Whether the column shows the comparison rows' right cells (browsing columns and the compare left
    /// column share the left-cell layout).
    /// <para>是否显示对比行的右侧单元格（浏览列与对比左列共用左侧单元格布局）。</para>
    /// </summary>
    /// <value>Whether the column shows the comparison rows' right cells. <para>是否显示对比行的右侧单元格。</para></value>
    public bool UsesRightCells => Side == CompareColumnSide.Right;

    /// <summary>
    /// Whether the path box has content.
    /// <para>路径框是否已有内容。</para>
    /// </summary>
    /// <value>Whether the path box has content. <para>路径框是否已有内容。</para></value>
    public bool HasPath => !string.IsNullOrWhiteSpace(Path);

    /// <summary>
    /// Whether the empty-state hint is shown (the column has no content yet: neither a row list nor text
    /// content is visible).
    /// <para>是否显示空态提示（本列尚无内容：既没有行列表，也没有可编辑文本内容）。</para>
    /// </summary>
    /// <value>Whether the empty-state hint is shown. <para>是否显示空态提示。</para></value>
    public bool ShowHint => (Rows is null || Rows.Count == 0) && !IsTextEditorVisible && !IsCompareTextEditorVisible;

    /// <summary>
    /// Whether the left-cell row list is shown (browsing columns and the compare left column).
    /// <para>是否显示左侧单元格行列表（浏览列与对比左列）。</para>
    /// </summary>
    /// <value>Whether the left-cell row list is shown. <para>是否显示左侧单元格行列表。</para></value>
    public bool ShowLeftCells => Rows is not null && !UsesRightCells && !IsCompareTextEditorVisible;

    /// <summary>
    /// Whether the right-cell row list is shown (the compare right column only).
    /// <para>是否显示右侧单元格行列表（仅对比右列）。</para>
    /// </summary>
    /// <value>Whether the right-cell row list is shown. <para>是否显示右侧单元格行列表。</para></value>
    public bool ShowRightCells => Rows is not null && UsesRightCells && !IsCompareTextEditorVisible;

    /// <summary>
    /// Whether this column shows the comparison's per-side text diff during a text comparison (one aligned
    /// line per slot, slashes mark fillers).
    /// <para>文本对比时本列是否显示逐侧的文本差异（每行对应一个对齐槽，填充槽显示斜杠）。</para>
    /// </summary>
    /// <value>Whether this column shows the compare-side text diff. <para>本列是否显示对比侧的文本差异。</para></value>
    public bool IsCompareTextEditorVisible => IsCompareSide && _owner.IsTextEditorMode;

    /// <summary>
    /// Whether the raw-hex toggle is shown in compare mode (a binary comparison with a file on both sides).
    /// <para>对比模式下是否显示十六进制原始数据切换按钮（两侧都有文件的二进制对比）。</para>
    /// </summary>
    /// <value>Whether the raw-hex toggle is shown. <para>是否显示十六进制原始数据切换按钮。</para></value>
    public bool ShowHexToggle => IsCompareSide && _owner.IsBinaryMode && _owner.CanShowHexView;

    /// <summary>
    /// Whether the previous/next-difference buttons are shown in compare mode (text and hex comparisons).
    /// <para>对比模式下是否显示上一处/下一处差异按钮（文本与十六进制对比）。</para>
    /// </summary>
    /// <value>Whether the difference-navigation buttons are shown. <para>是否显示差异导航按钮。</para></value>
    public bool ShowDiffNavigation => IsCompareSide && (_owner.IsTextMode || _owner.IsBinaryMode);

    /// <summary>
    /// The row list's selection mode: a text comparison with editable rows allows dragging a multi-row
    /// selection, everything else is single selection.
    /// <para>行列表的选择模式：可编辑行的文本对比允许拖拽选中多行，其余情况单选。</para>
    /// </summary>
    /// <value>The selection mode applied to this column's row list. <para>本列行列表采用的选择模式。</para></value>
    public SelectionMode RowSelectionMode => _hasEditableRows ? SelectionMode.Multiple : SelectionMode.Single;

    /// <summary>
    /// The raw-hex toggle command (forwarded to the document; both columns share one comparison).
    /// <para>十六进制原始数据切换命令（转发到所属文档，两列共享同一份对比结果）。</para>
    /// </summary>
    /// <value>The command that toggles the raw-hex view. <para>切换十六进制原始数据视图的命令。</para></value>
    public IRelayCommand ToggleHexViewCommand => _owner.ToggleHexViewCommand;

    /// <summary>
    /// The previous-difference command (forwarded to the document).
    /// <para>上一处差异命令（转发到所属文档）。</para>
    /// </summary>
    /// <value>The command that moves to the previous difference. <para>跳转到上一处差异的命令。</para></value>
    public IRelayCommand PrevDiffCommand => _owner.PrevDiffCommand;

    /// <summary>
    /// The next-difference command (forwarded to the document).
    /// <para>下一处差异命令（转发到所属文档）。</para>
    /// </summary>
    /// <value>The command that moves to the next difference. <para>跳转到下一处差异的命令。</para></value>
    public IRelayCommand NextDiffCommand => _owner.NextDiffCommand;

    /// <summary>
    /// Whether the column can be closed (at least one column is kept).
    /// <para>是否可以关闭本列（至少保留一列）。</para>
    /// </summary>
    /// <value>Whether the column can be closed. <para>是否可以关闭本列。</para></value>
    public bool CanClose => _owner.Columns.Count > 1;

    /// <summary>
    /// Whether this column's file has unsaved edits (after text edits or a copy to the other side in a text comparison).
    /// <para>本列对应的文件是否有未保存的编辑（文本对比中修改文本或复制到另一侧之后）。</para>
    /// </summary>
    /// <value>Whether this column's file has unsaved edits. <para>本列对应的文件是否有未保存的编辑。</para></value>
    public bool IsDirty => IsCompareSide ? _owner.IsFileDirty(Side) : _isDirty;

    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasPath));
        NotifyContentChanged();
    }

    /// <summary>
    /// Confirm button: loads the path box's path into this column (a directory shows a collapsed tree, a
    /// file shows content or metadata); a participating column re-runs the comparison instead.
    /// <para>确认按钮：把路径框中的路径加载到本列（目录显示折叠的目录树，文件显示内容/元数据）；
    /// 本列参与对比时则重新执行对比。</para>
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
    /// Reload button: a browsing column re-scans the disk from its current path (syncing external changes),
    /// while a participating column asks the document to refresh both sides.
    /// <para>刷新按钮：浏览列按当前路径重新扫描磁盘（外部改动后同步）；参与对比的列则请求文档同时刷新两侧
    /// （重新比对目录或重新读取该文件对）。</para>
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
    /// Go-up button: a browsing column switches its content to the parent of the current path's directory;
    /// while comparing, the document moves both sides to their parents and re-compares.
    /// <para>返回上一级按钮：浏览列把本列内容切到当前路径所在目录的父目录；参与对比时由文档
    /// 同时把两侧切到各自的父目录并重新比对。</para>
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
    /// Closes this column.
    /// <para>关闭本列命令。</para>
    /// </summary>
    [RelayCommand]
    private async Task Close() => await _owner.RemoveColumnAsync(this);

    /// <summary>
    /// Saves this column's file edits (a text comparison writes the current rows back, preserving the original encoding
    /// and line ending).
    /// <para>保存本列文件的编辑（文本对比中按当前行写回文件，保留原编码与换行风格）。</para>
    /// </summary>
    [RelayCommand]
    private async Task Save()
    {
        if (IsCompareSide)
        {
            await _owner.SaveSideAsync(this);
            return;
        }

        if (IsTextEditorVisible)
        {
            var editorPath = (Path ?? string.Empty).Trim();
            if (editorPath.Length == 0)
            {
                return;
            }

            try
            {
                await _owner.Comparison.SaveTextAsync(editorPath, EditorLines(), CancellationToken.None);
                _isDirty = false;
                NotifyDirtyChanged();
                _owner.ClearError();
            }
            catch (Exception ex)
            {
                _owner.ReportError(UserError.Describe(ex));
                LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
            }

            return;
        }

        if (_ownRows is null || !_hasEditableRows)
        {
            return;
        }

        var path = (Path ?? string.Empty).Trim();
        List<string> lines = [];
        foreach (var row in _ownRows)
        {
            if (row.IsEditable && row.HasLeftLine)
            {
                lines.Add(row.LeftText);
            }
        }

        try
        {
            await _owner.Comparison.SaveTextAsync(path, lines, CancellationToken.None);
            _isDirty = false;
            NotifyDirtyChanged();
            _owner.ClearError();
        }
        catch (Exception ex)
        {
            _owner.ReportError(UserError.Describe(ex));
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    /// <summary>
    /// Marks this column's own text file as modified while browsing.
    /// <para>浏览状态下把本列自己的文本文件标记为已修改。</para>
    /// </summary>
    internal void MarkDirty()
    {
        if (_isDirty)
        {
            return;
        }

        _isDirty = true;
        NotifyDirtyChanged();
        _owner.NotifyColumnsChanged();
    }

    /// <summary>
    /// Whether the column shows multi-line editable text while browsing a text file (cross-line selection,
    /// line insert/delete, undo/redo, multi-line paste).
    /// <para>浏览文本文件时是否显示可编辑的多行文本（跨行选择、增删行、撤销/重做、多行粘贴）。</para>
    /// </summary>
    /// <value>Whether the column's text is shown for editing. <para>本列文本是否处于可编辑显示状态。</para></value>
    public bool IsTextEditorVisible
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsBrowsingTextEditor));
                OnPropertyChanged(nameof(ShowLeftCells));
                OnPropertyChanged(nameof(ShowRightCells));
                OnPropertyChanged(nameof(ShowHint));
            }
        }
    }

    /// <summary>
    /// Whether the single-document text content is shown: only a browsing column shows it; once the column
    /// compares it yields to the per-side diff, otherwise the two would stack in one column.
    /// <para>是否显示单文档文本内容：只有浏览列才显示它；参与对比后必须让位给逐侧对比内容，
    /// 否则两者会叠在同一列里。</para>
    /// </summary>
    /// <value>Whether the single-document text content is shown. <para>是否显示单文档文本内容。</para></value>
    public bool IsBrowsingTextEditor => IsTextEditorVisible && IsBrowsing;

    /// <summary>
    /// The text held for editing while browsing a text file: lines are joined with <c>\n</c> and the save
    /// service restores the file's original line ending.
    /// <para>浏览文本文件时用于编辑的文本：每行以 <c>\n</c> 连接，保存时由服务还原原换行风格。</para>
    /// </summary>
    /// <value>The text content held for editing in this column. <para>本列中用于编辑的文本内容。</para></value>
    public string EditorText
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || _loadingEditorText)
            {
                return;
            }

            MarkDirty();
        }
    } = string.Empty;

    /// <summary>
    /// Loads text lines into this column's editable text while browsing without marking it dirty.
    /// <para>浏览状态下把文本行载入本列的可编辑文本，且不标记为已修改。</para>
    /// </summary>
    /// <param name="lines">The text lines to load. <para>要载入的文本行。</para></param>
    private void LoadEditorText(IReadOnlyList<string> lines)
    {
        _loadingEditorText = true;
        try
        {
            EditorText = string.Join('\n', lines);
        }
        finally
        {
            _loadingEditorText = false;
        }

        _isDirty = false;
        IsTextEditorVisible = true;
        _ownRows = null;
        _hasEditableRows = false;
        Rows = null;
        TreeRoot = null;
        NotifyContentChanged();
        NotifyDirtyChanged();
    }

    /// <summary>
    /// Splits this column's editable text into lines.
    /// <para>把本列可编辑的文本拆分为行。</para>
    /// </summary>
    /// <returns>The text lines read from the editable content. <para>从可编辑内容读出的文本行。</para></returns>
    private List<string> EditorLines()
    {
        List<string> lines = [];
        foreach (var line in EditorText.Split('\n'))
        {
            lines.Add(line.TrimEnd('\r'));
        }

        return lines;
    }

    /// <summary>
    /// Splits one of this column's own text rows while browsing.
    /// <para>浏览状态下把本列的一行文本断开。</para>
    /// </summary>
    /// <param name="row">The row to split. <para>要断开的行。</para></param>
    /// <param name="leftSide">Whether the edit targets the row's left cell. <para>编辑是否作用于该行的左侧单元格。</para></param>
    /// <param name="caretIndex">The caret offset within the row's text. <para>该行文本内的光标偏移。</para></param>
    /// <returns>The new row index and caret position after the split. <para>断行后的新行索引与光标位置。</para></returns>
    internal (int Index, int Caret) SplitTextRow(CompareRowViewModel row, bool leftSide, int caretIndex)
    {
        if (_ownRows is null)
        {
            return (0, 0);
        }

        var result = CompareTextRowEditor.Split(_ownRows, row, leftSide, caretIndex);
        MarkDirty();
        return result;
    }

    /// <summary>
    /// Joins one of this column's own text rows with the next row while browsing.
    /// <para>浏览状态下把本列某行与下一行合并。</para>
    /// </summary>
    /// <param name="row">The row to join. <para>要合并的行。</para></param>
    /// <param name="leftSide">Whether the edit targets the row's left cell. <para>编辑是否作用于该行的左侧单元格。</para></param>
    /// <returns>The resulting row index and caret position. <para>合并后的行索引与光标位置。</para></returns>
    internal (int Index, int Caret) JoinTextRowWithNext(CompareRowViewModel row, bool leftSide)
    {
        if (_ownRows is null)
        {
            return (0, 0);
        }

        var result = CompareTextRowEditor.JoinWithNext(_ownRows, row, leftSide);
        MarkDirty();
        return result;
    }

    /// <summary>
    /// Joins one of this column's own text rows with the previous row while browsing.
    /// <para>浏览状态下把本列某行并入上一行。</para>
    /// </summary>
    /// <param name="row">The row to join. <para>要合并的行。</para></param>
    /// <param name="leftSide">Whether the edit targets the row's left cell. <para>编辑是否作用于该行的左侧单元格。</para></param>
    /// <returns>The resulting row index and caret position. <para>合并后的行索引与光标位置。</para></returns>
    internal (int Index, int Caret) JoinTextRowWithPrevious(CompareRowViewModel row, bool leftSide)
    {
        if (_ownRows is null)
        {
            return (0, 0);
        }

        var result = CompareTextRowEditor.JoinWithPrevious(_ownRows, row, leftSide);
        MarkDirty();
        return result;
    }

    /// <summary>
    /// Deletes the rows selected in this column while browsing.
    /// <para>浏览状态下删除本列所选的行。</para>
    /// </summary>
    /// <param name="indices">The indices of the selected rows. <para>所选行的索引。</para></param>
    internal void DeleteSelectedTextRows(IReadOnlyList<int> indices)
    {
        if (_ownRows is null)
        {
            return;
        }

        CompareTextRowEditor.DeleteSelected(_ownRows, indices, leftSide: true);
        MarkDirty();
    }

    /// <summary>
    /// Expands or collapses a node of this column's browsing tree: children are scanned on demand first, then
    /// the expansion state flips and the visible rows are rebuilt.
    /// <para>展开/折叠本列浏览树上的一个节点：先按需扫描子项，再切换展开状态并重建可见行。</para>
    /// </summary>
    /// <param name="node">The node to expand or collapse. <para>被展开/折叠的节点。</para></param>
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
        Rows = BuildBrowseRows();
    }

    /// <summary>
    /// The rows a browsing column should show: an imported folder shows its contents (the root takes no row)
    /// and an expanded subfolder inserts its children in place.
    /// <para>浏览列当前应有的行：导入的文件夹显示其内容（根节点不占一行），展开的子文件夹把子项插在原位。</para>
    /// </summary>
    /// <returns>The rows built from the current tree state. <para>按当前树状态构造出的行。</para></returns>
    private List<CompareRowViewModel> BuildBrowseRows()
        => TreeRoot is null ? [] : ComparisonRowBuilder.ForTree(TreeRoot.Children, compared: false);

    /// <summary>
    /// Enters a directory while browsing: the directory becomes this column's path and the content is rebuilt
    /// (the double-click behaviour when not comparing).
    /// <para>浏览状态下进入某个目录：把该目录设为本列路径并重建内容（未对比时双击文件夹的行为）。</para>
    /// </summary>
    /// <param name="node">The directory node to enter. <para>要进入的目录节点。</para></param>
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
    /// Loads a path into this column: a directory gets a collapsed browsing tree (children scanned on demand),
    /// while a file's rows come from text lines or firmware metadata.
    /// <para>把路径加载到本列：目录挂一棵折叠的浏览树（子项按需扫描），文件按文本行或固件元数据构造行。</para>
    /// </summary>
    /// <param name="path">The directory or file path. <para>目录或文件路径。</para></param>
    /// <param name="isDirectory">Whether the path is a directory. <para>是否为目录。</para></param>
    internal async Task LoadPathAsync(string path, bool isDirectory)
    {
        var ct = BeginLoad();
        IsBusy = true;
        try
        {
            if (isDirectory)
            {
                var root = CompareTreeNodeViewModel.ForBrowsing(path, isDirectory: true, ct);
                TreeRoot = root;
                await root.EnsureChildrenLoadedAsync();
                ct.ThrowIfCancellationRequested();
                _ownRows = null;
                _isDirty = false;
                _hasEditableRows = false;
                IsTextEditorVisible = false;
                Rows = BuildBrowseRows();
            }
            else
            {
                var textLines = await _owner.ReadTextLinesAsync(path, ct);
                ct.ThrowIfCancellationRequested();
                if (textLines is not null)
                {
                    LoadEditorText(textLines);
                    NotifyContentChanged();
                    OnPropertyChanged(nameof(RowSelectionMode));
                    _owner.ClearError();
                    return;
                }

                IsTextEditorVisible = false;
                var rows = await _owner.BuildFileRowsAsync(path, ct, (_, _) => MarkDirty());
                ct.ThrowIfCancellationRequested();
                TreeRoot = null;
                _ownRows = rows;
                _isDirty = false;
                _hasEditableRows = rows.Any(row => row.IsEditable);
                Rows = rows;
            }

            NotifyContentChanged();
            NotifyDirtyChanged();
            OnPropertyChanged(nameof(RowSelectionMode));
            _owner.ClearError();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Rows = null;
            TreeRoot = null;
            _owner.ReportError(UserError.Describe(ex));
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
        finally
        {
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Clears this column and returns it to the unloaded state (used when a compare column falls back to
    /// browsing).
    /// <para>清空本列内容并回到未加载状态（结束对比时把对比列还原为浏览列）。</para>
    /// </summary>
    internal void ClearContent()
    {
        BeginLoad();
        TreeRoot = null;
        _ownRows = null;
        _isDirty = false;
        _hasEditableRows = false;
        IsTextEditorVisible = false;
        Rows = null;
        NotifyContentChanged();
        NotifyDirtyChanged();
        OnPropertyChanged(nameof(RowSelectionMode));
    }

    /// <summary>
    /// Reloads this column's content from its current path after a file was copied, moved, deleted or renamed.
    /// <para>文件被复制、移动、删除或重命名后，按当前路径重新加载本列内容。</para>
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

    /// <summary>
    /// Releases this column's load token, cancelling the pending reads and subfolder scans.
    /// <para>释放本列的加载令牌，取消尚未完成的读取与子目录扫描。</para>
    /// </summary>
    public void Dispose()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
    }

    /// <summary>
    /// Hands the operation to the document while this column participates in a comparison (both refresh and
    /// go-up must act on both sides); returns whether compare mode took over.
    /// <para>对比模式下把本次操作交给文档（刷新/返回上一级都需要同时作用于两侧）；
    /// 返回是否已由对比模式接管。</para>
    /// </summary>
    /// <param name="allowGoUp">
    /// When true the operation is treated as "go up" (both sides move together); otherwise it refreshes.
    /// <para>为 true 时把本次操作按「返回上一级」处理（两侧同时上移），否则按刷新处理。</para>
    /// </param>
    /// <returns>Whether compare mode took over the operation. <para>是否已由对比模式接管本次操作。</para></returns>
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
    /// Cancels and rebuilds this column's load token, returning the new one.
    /// <para>取消并重建本列的加载令牌，返回新令牌。</para>
    /// </summary>
    /// <returns>The freshly created load token. <para>新建的加载令牌。</para></returns>
    private CancellationToken BeginLoad()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    /// <summary>
    /// Raises change notifications for the visibility properties derived from content state.
    /// <para>通知所有由内容状态派生的可见性属性。</para>
    /// </summary>
    private void NotifyContentChanged()
    {
        OnPropertyChanged(nameof(ShowHint));
        OnPropertyChanged(nameof(ShowLeftCells));
        OnPropertyChanged(nameof(ShowRightCells));
        OnPropertyChanged(nameof(HasPath));
        OnPropertyChanged(nameof(CanClose));

        _owner.NotifyColumnsChanged();
    }

    /// <summary>
    /// Notifies the close button's availability when the column count changes.
    /// <para>列数变化时通知列关闭按钮的可用性。</para>
    /// </summary>
    internal void NotifyCanCloseChanged() => OnPropertyChanged(nameof(CanClose));

    /// <summary>
    /// Notifies the dirty state that decides whether the save button is shown.
    /// <para>通知「已修改」状态（决定保存按钮是否显示）。</para>
    /// </summary>
    internal void NotifyDirtyChanged() => OnPropertyChanged(nameof(IsDirty));

    /// <summary>
    /// Raises change notifications for the visibility properties derived from the comparison mode.
    /// <para>通知所有由对比模式派生的可见性属性。</para>
    /// </summary>
    internal void NotifyCompareStateChanged()
    {
        OnPropertyChanged(nameof(ShowHexToggle));
        OnPropertyChanged(nameof(ShowDiffNavigation));
        OnPropertyChanged(nameof(RowSelectionMode));
        OnPropertyChanged(nameof(IsCompareTextEditorVisible));
        OnPropertyChanged(nameof(ShowLeftCells));
        OnPropertyChanged(nameof(ShowRightCells));
    }
}
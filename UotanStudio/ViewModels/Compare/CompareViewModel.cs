using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>对比文档：以「文件列」的形式并排展示目录树 / 文件内容，既可作为普通文件浏览器使用，也可以
/// 在其中两列之间开启对比。列数不设上限（浏览用），开启对比后只使用其中两列。</para>
/// <para>对比逻辑与 Beyond Compare 一致：目录对比把两侧压平成**同一份对齐行列表**——同名条目落在同一行、
/// 单侧条目在另一侧自动留空、差异行整行变色、两侧共用一套展开状态（一侧展开即两侧同步展开），两栏并排
/// 滚动；双击文件不占用当前视图，而是在新标签打开文件对比。</para>
/// Compare document: file columns laid out side by side, each showing a directory tree or a file's content,
/// so it doubles as a plain file browser while any two columns can be compared. The comparison follows
/// Beyond Compare's logic: a folder comparison flattens both sides into one aligned row list — same-name
/// entries share a row, single-side entries stay blank on the other side, differing rows are coloured as a
/// whole, both sides share one expansion state (expanding one side expands the other) and the panes scroll
/// together; double-clicking a file opens its comparison in a new tab instead of taking over this view.
/// </summary>
public sealed partial class CompareViewModel : Document
{
    /// <summary>
    /// <para>文档打开时的默认列数：两列既是最常见的浏览布局，也是对比所需的列数。</para>
    /// Columns created when the document opens: two columns is both the common browsing layout and the number
    /// a comparison needs.
    /// </summary>
    private const int DefaultColumnCount = 2;

    private readonly IComparisonService _comparison;

    /// <summary>Creates a compare document with the default file-comparison services.</summary>
    public CompareViewModel() : this(ComparisonService.CreateDefault()) { }

    /// <summary>Creates a document with an explicitly supplied comparison backend.</summary>
    internal CompareViewModel(IComparisonService comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        _comparison = comparison;
        for (var index = 0; index < DefaultColumnCount; index++)
        {
            Columns.Add(new CompareColumnViewModel(this));
        }

        Columns.CollectionChanged += OnColumnsChanged;
    }

    private CancellationTokenSource _loadCts = new();
    private CompareTreeNodeViewModel? _compareTreeRoot;
    private ComparisonMetadata? _leftMetadata;
    private ComparisonMetadata? _rightMetadata;
    private CompareHexRowCollection? _currentHexRows;
    private string _statsText = string.Empty;
    private string _folderSummaryText = string.Empty;
    private string _leftPath = string.Empty;
    private string _rightPath = string.Empty;
    private string _fileLeftPath = string.Empty;
    private string _fileRightPath = string.Empty;
    private CompareColumnViewModel? _leftColumn;
    private CompareColumnViewModel? _rightColumn;
    private List<int> _selectedRowIndices = [];
    private bool _dirtyLeft;
    private bool _dirtyRight;
    private ObservableCollection<CompareRowViewModel>? _textRows;
    private CompareTextSlotModel? _textModel;
    private readonly List<TextUndoEntry> _textUndo = [];
    private readonly List<TextUndoEntry> _textRedo = [];
    private DateTime _lastTextUndoUtc;
    private bool _lastTextUndoWasStructural;
    private long _textUndoBytes;

    /// <summary>文本对比撤销历史的内存上限（字节估算）：超过后丢弃最早的撤销点，避免大文件长时间编辑占用过多内存。</summary>
    /// Memory cap (estimated bytes) of the text-comparison undo history: the oldest points are dropped beyond it so a long editing
    /// session on a large file stays bounded.
    private const long MaxTextUndoBytes = 24L * 1024 * 1024;

    /// <summary>
    /// <para>文档中的文件列；可以任意追加，对比只使用其中两列。</para>
    /// The document's file columns; any number can be appended and a comparison uses two of them.
    /// </summary>
    public ObservableCollection<CompareColumnViewModel> Columns { get; } = [];

    /// <summary>
    /// <para>对比后端：列内容读取（文本/元数据）、目录比对与文件复制的边界。</para>
    /// The comparison backend: the boundary for column content reads (text/metadata), directory comparison and
    /// file copy.
    /// </summary>
    internal IComparisonService Comparison => _comparison;

    /// <summary>
    /// <para>左侧列路径：对比中为左列路径，否则为第一个文件列的路径（供会话快照持久化）。</para>
    /// The left column's path: the compare left column while comparing, otherwise the first file column (used
    /// by the session snapshot).
    /// </summary>
    public string LeftPath => (_leftColumn ?? Columns.ElementAtOrDefault(0))?.Path ?? string.Empty;

    /// <summary>
    /// <para>右侧列路径：对比中为右列路径，否则为第二个文件列的路径（供会话快照持久化）。</para>
    /// The right column's path: the compare right column while comparing, otherwise the second file column
    /// (used by the session snapshot).
    /// </summary>
    public string RightPath => (_rightColumn ?? Columns.ElementAtOrDefault(1))?.Path ?? string.Empty;

    /// <summary>
    /// <para>文档中是否还没有任何内容（驱动拖入提示的显示）。</para>
    /// Whether the document holds no content yet (drives the drop hint).
    /// </summary>
    public bool IsEmptyDocument => Columns.All(column => !column.HasPath);

    /// <summary>
    /// <para>是否正在对比：目录对比时两列显示同一份对齐的目录树行，文件对比时显示对齐的差异行。</para>
    /// Whether a comparison is active: a folder comparison shows both columns the same aligned tree rows, and a
    /// file comparison shows aligned diff rows.
    /// </summary>
    [ObservableProperty] private bool _isComparing;

    /// <summary>
    /// <para>当前选中行在 <see cref="Rows"/> 中的索引；无选中时重置为 -1。两栏共享同一份行列表，
    /// 因此选中索引就是两侧共同的行号。</para>
    /// The index of the selected row within <see cref="Rows"/>; -1 when nothing is selected. Both panes share
    /// one row list, so the selection index is the row number on both sides.
    /// </summary>
    [ObservableProperty] private int _selectedIndex = -1;

    /// <summary>
    /// <para>差异导航滚动请求：由上一处/下一处差异命令写入目标行索引，视图据此把两列
    /// 顶部对齐滚动到该行；普通点选不写此属性（不触发滚动）。先写 -1 再写目标值以保证
    /// 同一索引重复导航也能再次触发属性通知。</para>
    /// Diff-navigation scroll request: written by the prev/next-diff commands with the target row index; the
    /// view top-aligns both columns to that row. Plain clicks never write it (no scroll happens). Written as
    /// -1 first, then the target, so re-navigating to the same index still raises a property-change
    /// notification.
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
    /// <para>相同条目数（文件夹对比）。</para>
    /// Count of equal items (folder comparison).
    /// </summary>
    [ObservableProperty] private int _equalCount;

    /// <summary>
    /// <para>不同条目数（文件夹对比）。</para>
    /// Count of different items (folder comparison).
    /// </summary>
    [ObservableProperty] private int _differentCount;

    /// <summary>
    /// <para>仅左侧条目数（文件夹对比）。</para>
    /// Count of left-only items (folder comparison).
    /// </summary>
    [ObservableProperty] private int _leftOnlyCount;

    /// <summary>
    /// <para>仅右侧条目数（文件夹对比）。</para>
    /// Count of right-only items (folder comparison).
    /// </summary>
    [ObservableProperty] private int _rightOnlyCount;

    /// <summary>
    /// <para>当前对比模式；未对比时为 <see cref="CompareMode.None"/>。</para>
    /// The active comparison mode; <see cref="CompareMode.None"/> while not comparing.
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
            OnPropertyChanged(nameof(IsFolderMode));
            OnPropertyChanged(nameof(IsTextMode));
            OnPropertyChanged(nameof(IsBinaryMode));
            NotifyColumnsCompareState();
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
    /// <para>当前对比结果行；两栏共享同一份列表（分别渲染左侧/右侧单元格），因此同名条目落在同一行、
    /// 单侧条目在另一侧留空。目录对比给的是压平后的目录树行，文件对比给的是差异行。</para>
    /// The current comparison rows; both panes share one list (rendering the left/right cells respectively), so
    /// same-name entries share a row and single-side entries stay blank on the other side. A folder comparison
    /// supplies flattened tree rows and a file comparison supplies diff rows.
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
            if (_leftColumn is not null)
            {
                _leftColumn.Rows = value;
            }

            if (_rightColumn is not null)
            {
                _rightColumn.Rows = value;
            }
        }
    }

    private IReadOnlyList<CompareRowViewModel>? _rows;

    /// <summary>
    /// <para>是否处于文件夹对比模式。</para>
    /// Whether folder-compare mode is active.
    /// </summary>
    public bool IsFolderMode => Mode == CompareMode.Folder;

    /// <summary>
    /// <para>是否处于文本对比模式。</para>
    /// Whether text-compare mode is active.
    /// </summary>
    public bool IsTextMode => Mode == CompareMode.Text;

    /// <summary>
    /// <para>文本对比是否使用项目文本编辑器（两侧各一个编辑器，行=对齐槽，填充槽显示斜杠）。</para>
    /// Whether the text comparison uses the project's text editors (one per side, one line per aligned slot with slashes for
    /// fillers).
    /// </summary>
    public bool IsTextEditorMode => IsComparing && Mode == CompareMode.Text && _textModel is not null;

    /// <summary>某一侧编辑器应显示的文本（一行一槽，填充槽显示斜杠）。 One side's editor text (one line per slot, slashes for fillers).</summary>
    internal string ProjectText(bool leftSide) => _textModel?.Project(leftSide) ?? string.Empty;

    /// <summary>某一侧需要整行着色的槽号（0 起）。 The slot indices (0-based) to colour on one side.</summary>
    internal List<int> HighlightedSlots(bool leftSide) => _textModel?.HighlightedSlots(leftSide) ?? [];

    /// <summary>某一侧的真实行（跳过填充槽），保存用。 One side's real lines with fillers skipped, used for saving.</summary>
    internal List<string> RealLines(bool leftSide) => _textModel?.RealLines(leftSide) ?? [];

    /// <summary>
    /// <para>把编辑器的一次改动翻译成槽操作：编辑涉及的整行范围由视图换算成「起始槽号 + 原槽数 + 替换后的行」，
    /// 这里统一落到模型上——行数不变时只改该侧内容；行数变化时按「本侧加行→对侧补填充槽 / 本侧删行→本侧留填充槽」
    /// 维护两侧等长，并重建行投影、标记该侧已修改。</para>
    /// Translates one editor change into slot operations: the view converts the affected line range into "first slot + removed
    /// slots + replacement lines", and this applies it to the model — an unchanged line count only touches that side's
    /// content, while a changed count keeps both sides equal (a new line adds a filler on the other side, a deleted line
    /// leaves a filler on this side) and rebuilds the row projection and the modified flag.
    /// </summary>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side changed.</param>
    /// <param name="startSlot"><para>起始槽号（0 起）。</para>The first slot index (0-based).</param>
    /// <param name="removedSlots"><para>被替换掉的槽数。</para>The number of replaced slots.</param>
    /// <param name="lines"><para>替换后的行。</para>The replacement lines.</param>
    /// <returns><para>光标应回到的槽号（0 起）与列。</para>The slot index and column the caret should return to.</returns>
    internal (int Slot, int Column) ApplyEditorLines(bool leftSide, int startSlot, int removedSlots, IReadOnlyList<string> lines)
    {
        if (_textModel is null)
        {
            return (startSlot, 0);
        }

        if (removedSlots == lines.Count)
        {
            for (var offset = 0; offset < lines.Count; offset++)
            {
                _textModel.SetLine(Math.Clamp(startSlot + offset, 0, _textModel.Slots.Count - 1), leftSide, lines[offset]);
            }
        }
        else
        {
            _textModel.ReplaceLines(startSlot, removedSlots, leftSide, lines);
        }

        // 两侧都没内容的槽直接收掉（合并/删除留下的全空槽），行数变化量只跟本侧文件的行数保持一致。
        // Slots empty on both sides are dropped (leftovers of a join or delete), so the row count only follows this side's file.
        _textModel.DropEmptySlots();
        MarkDirty(leftSide);
        RebuildTextRows();
        return (Math.Clamp(startSlot + lines.Count - 1, 0, Math.Max(0, _textModel.Slots.Count - 1)),
            lines.Count == 0 ? 0 : lines[^1].Length);
    }

    /// <summary>
    /// <para>把一侧编辑器中选中的整行复制到另一侧相同的槽范围（Beyond Compare 的「复制到另一侧」）。</para>
    /// Copies the whole lines selected in one side's editor to the same slot range on the other side (Beyond Compare's "copy
    /// to other side").
    /// </summary>
    /// <param name="fromLeftToRight"><para>是否从左复制到右。</para>Whether to copy from left to right.</param>
    /// <param name="startSlot"><para>起始槽号（0 起）。</para>The first slot index (0-based).</param>
    /// <param name="count"><para>复制的行数。</para>The number of lines to copy.</param>
    internal void CopySlots(bool fromLeftToRight, int startSlot, int count)
    {
        if (_textModel is null || count <= 0)
        {
            return;
        }

        var lines = new List<string>();
        for (var offset = 0; offset < count; offset++)
        {
            var index = startSlot + offset;
            if (index < 0 || index >= _textModel.Slots.Count)
            {
                break;
            }

            var slot = _textModel.Slots[index];
            if (fromLeftToRight ? slot.HasLeft : slot.HasRight)
            {
                lines.Add(fromLeftToRight ? slot.Left : slot.Right);
            }
        }

        _textModel.ReplaceLines(startSlot, count, leftSide: !fromLeftToRight, lines);
        MarkDirty(!fromLeftToRight);
        RebuildTextRows();
    }

    /// <summary>按槽模型重建行投影（供汇总、差异导航与属性面板使用；文本对比的编辑面是编辑器）。</summary>
    /// Rebuilds the row projection from the slot model (used by the summary, diff navigation and the properties panel; the text
    /// editors are the editing surface).
    private void RebuildTextRows()
    {
        if (_textModel is null)
        {
            return;
        }

        var rows = new ObservableCollection<CompareRowViewModel>();
        for (var index = 0; index < _textModel.Slots.Count; index++)
        {
            var slot = _textModel.Slots[index];
            var differs = slot.Status != FolderItemStatus.Equal;
            rows.Add(new CompareRowViewModel(
                slot.Status,
                depth: 0,
                leftLine: slot.HasLeft ? (index + 1).ToString() : string.Empty,
                leftText: slot.HasLeft ? slot.Left : string.Empty,
                leftSub: string.Empty,
                isLeftDiff: slot.HasLeft && differs,
                rightLine: slot.HasRight ? (index + 1).ToString() : string.Empty,
                rightText: slot.HasRight ? slot.Right : string.Empty,
                rightSub: string.Empty,
                isRightDiff: slot.HasRight && differs,
                menu: CompareRowMenuKind.ComparedText,
                compared: true,
                hasLeftLine: slot.HasLeft,
                hasRightLine: slot.HasRight));
        }

        _textRows = rows;
        Rows = rows;
        OnPropertyChanged(nameof(IsTextEditorMode));
        NotifyColumnsCompareState();
    }

    /// <summary>
    /// <para>文本对比的一个撤销点：槽状态快照 + 两侧光标所在槽号。两侧编辑器共享同一份撤销历史，
    /// 因此一次「两侧各改一处」的结构编辑永远一起撤销/重做，不会把两侧弄错位。</para>
    /// One undo point of a text comparison: a slot-state snapshot plus both sides' caret slots. Both editors share this single
    /// history, so a structural edit that touched both sides is always undone/redone as a unit and the panes can never drift.
    /// </summary>
    private sealed record TextUndoEntry(
        List<CompareTextSlotModel.SlotState> Slots,
        int LeftSlot,
        int RightSlot);

    /// <summary>文本对比是否可以撤销/重做。 Whether the text comparison can undo/redo.</summary>
    internal bool CanUndoText => _textUndo.Count > 0;

    /// <summary>文本对比是否可以重做。 Whether the text comparison can redo.</summary>
    internal bool CanRedoText => _textRedo.Count > 0;

    /// <summary>
    /// <para>记录一个撤销点（在把编辑器改动落到槽模型之前调用）：连续的行内输入在 600ms 内合并成一个撤销点，
    /// 结构化操作（改行数）每次都单独成点。</para>
    /// Records an undo point (called before the editor change is applied to the slot model): consecutive in-line typing within
    /// 600 ms coalesces into one point, while a structural change (line count touched) always gets its own point.
    /// </summary>
    /// <param name="structural"><para>是否改变了行数。</para>Whether the line count changed.</param>
    /// <param name="leftSlot"><para>左侧光标槽号。</para>The left caret slot.</param>
    /// <param name="rightSlot"><para>右侧光标槽号。</para>The right caret slot.</param>
    internal void RecordTextUndoPoint(bool structural, int leftSlot, int rightSlot)
    {
        if (_textModel is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var coalesce = !structural && !_lastTextUndoWasStructural && _textUndo.Count > 0
            && (now - _lastTextUndoUtc).TotalMilliseconds < 600;
        _lastTextUndoWasStructural = structural;
        _lastTextUndoUtc = now;
        if (coalesce)
        {
            // 合并到上一个点：保留更早的快照（那才是这次连续输入前的状态）。
            // Coalesce into the previous point: the earlier snapshot is the state before the whole burst of typing.
            _textRedo.Clear();
            return;
        }

        var entry = new TextUndoEntry(_textModel.CaptureState(), leftSlot, rightSlot);
        _textUndo.Add(entry);
        _textUndoBytes += EstimateBytes(entry);
        _textRedo.Clear();
        // 同时按条数与内存估算上限裁剪：大文件不会因为撤销历史把内存吃爆。
        // Trim by both point count and estimated memory so undo history cannot blow up the memory footprint of a large file.
        while (_textUndo.Count > 1 && (_textUndo.Count > 100 || _textUndoBytes > MaxTextUndoBytes))
        {
            _textUndoBytes -= EstimateBytes(_textUndo[0]);
            _textUndo.RemoveAt(0);
        }
    }

    /// <summary>估算一个撤销点的内存占用（两侧文本字符数 + 每槽固定开销）。</summary>
    /// Estimates one undo point's memory footprint (both sides' characters plus a fixed cost per slot).
    private static long EstimateBytes(TextUndoEntry entry)
    {
        long bytes = 0;
        foreach (var slot in entry.Slots)
        {
            bytes += ((slot.Left.Length + slot.Right.Length) * 2L) + 32;
        }

        return bytes;
    }

    /// <summary>
    /// <para>撤销一次文本对比编辑：恢复上一个槽状态快照（当前状态进入重做栈），返回两侧光标应回到的槽号。</para>
    /// Undoes one text-comparison edit: restores the previous slot snapshot (the current state moves to the redo stack) and
    /// returns the slots both carets should return to.
    /// </summary>
    internal (int LeftSlot, int RightSlot)? UndoText()
    {
        if (_textModel is null || _textUndo.Count == 0)
        {
            return null;
        }

        var entry = _textUndo[^1];
        _textUndo.RemoveAt(_textUndo.Count - 1);
        _textRedo.Add(new TextUndoEntry(_textModel.CaptureState(), entry.LeftSlot, entry.RightSlot));
        _textModel.RestoreState(entry.Slots);
        _lastTextUndoWasStructural = true;
        MarkDirty(leftSide: true);
        MarkDirty(leftSide: false);
        RebuildTextRows();
        return (entry.LeftSlot, entry.RightSlot);
    }

    /// <summary>重做一次文本对比编辑（与撤销对称）。 Redoes one text-comparison edit (the mirror of undo).</summary>
    internal (int LeftSlot, int RightSlot)? RedoText()
    {
        if (_textModel is null || _textRedo.Count == 0)
        {
            return null;
        }

        var entry = _textRedo[^1];
        _textRedo.RemoveAt(_textRedo.Count - 1);
        _textUndo.Add(new TextUndoEntry(_textModel.CaptureState(), entry.LeftSlot, entry.RightSlot));
        _textModel.RestoreState(entry.Slots);
        MarkDirty(leftSide: true);
        MarkDirty(leftSide: false);
        RebuildTextRows();
        return (entry.LeftSlot, entry.RightSlot);
    }

    /// <summary>
    /// <para>把编辑器里选中的内容复制到另一侧的相同位置：整行选中时复制这些槽；只选中一行里的一段文字时，
    /// 用选中的文字替换对侧同一行相同列范围的内容（其余部分保留）。</para>
    /// Copies the editor selection to the same place on the other side: a whole-line selection copies those slots, while a partial
    /// selection inside one line replaces the counterpart's same column range with the selected text (the rest of the line is kept).
    /// </summary>
    /// <param name="fromLeftToRight"><para>是否从左复制到右。</para>Whether to copy from left to right.</param>
    /// <param name="startSlot"><para>选区起始槽号（0 起）。</para>The selection's first slot (0-based).</param>
    /// <param name="endSlot"><para>选区结束槽号（0 起）。</para>The selection's last slot (0-based).</param>
    /// <param name="selectedText"><para>仅单行内部分选中时给出的选中文字；整行选中时为 <c>null</c>。</para>The selected text for a partial single-line selection; <c>null</c> for whole-line selections.</param>
    /// <param name="startColumn"><para>部分选中的起始列（0 起）。</para>The partial selection's start column (0-based).</param>
    /// <param name="endColumn"><para>部分选中的结束列（0 起）。</para>The partial selection's end column (0-based).</param>
    internal void CopyEditorSelection(
        bool fromLeftToRight,
        int startSlot,
        int endSlot,
        string? selectedText,
        int startColumn,
        int endColumn)
    {
        if (_textModel is null)
        {
            return;
        }

        startSlot = Math.Max(0, Math.Min(startSlot, _textModel.Slots.Count - 1));
        endSlot = Math.Max(startSlot, Math.Min(endSlot, _textModel.Slots.Count - 1));
        if (selectedText is not null && startSlot == endSlot)
        {
            // 单行内部分选中：按相同列范围替换对侧该行的内容。
            // A partial selection inside one line: replace the counterpart's same column range.
            var target = _textModel.TextAt(startSlot, leftSide: !fromLeftToRight);
            var from = Math.Clamp(startColumn, 0, target.Length);
            var to = Math.Clamp(endColumn, from, target.Length);
            _textModel.SetLine(startSlot, leftSide: !fromLeftToRight, target[..from] + selectedText + target[to..]);
            MarkDirty(!fromLeftToRight);
            RebuildTextRows();
            return;
        }

        CopySlots(fromLeftToRight, startSlot, endSlot - startSlot + 1);
    }

    /// <summary>
    /// <para>是否处于二进制对比模式。</para>
    /// Whether binary-compare mode is active.
    /// </summary>
    public bool IsBinaryMode => Mode == CompareMode.Binary;

    /// <summary>
    /// <para>是否具备打开十六进制差异视图的条件：两侧都有文件（单侧文件没有可比对的差异区间）。</para>
    /// Whether the hex-diff view is available: both sides must have a file (a single side has no diffable
    /// ranges).
    /// </summary>
    public bool CanShowHexView => _fileLeftPath.Length > 0 && _fileRightPath.Length > 0;

    /// <summary>
    /// <para>状态栏汇总文本（目录对比的状态计数，或文件对比的差异汇总）。</para>
    /// The status-bar summary text (folder-status counts, or a file-diff summary).
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

    partial void OnSelectedIndexChanged(int value)
    {
        var max = Rows is null || Rows.Count == 0 ? -1 : Rows.Count - 1;
        var clamped = value < -1 ? -1 : value > max ? max : value;
        if (clamped != value)
        {
            SelectedIndex = clamped;
        }
    }

    /// <summary>
    /// <para>把一个路径加入对比文档：优先复用还没有内容的文件列，不足时在末尾追加新列，
    /// 并按其类型显示目录树或文件内容（不做对比）。</para>
    /// Adds a path to the compare document: reuses the first empty file column, appends a new column when none
    /// is free, and shows a directory tree or the file's content (no comparison runs).
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

        var column = ReserveColumn();
        column.Path = fullPath;
        return column.LoadPathAsync(fullPath, isDirectory);
    }

    /// <summary>
    /// <para>打开文件比较：两个文件分别放入前两个文件列并立即开始对比（一侧留空时该侧留白，
    /// 用于「在新标签打开单侧文件对比」）。</para>
    /// Opens a file comparison: the two files fill the first two file columns and the comparison starts
    /// immediately (an empty side stays blank, which is how a single-side file comparison is opened in a new
    /// tab).
    /// </summary>
    /// <param name="leftPath"><para>左文件路径；可留空。</para>Left file path; may be empty.</param>
    /// <param name="rightPath"><para>右文件路径；可留空。</para>Right file path; may be empty.</param>
    public Task OpenCompareFilesAsync(string leftPath, string rightPath)
        => OpenComparePairAsync(leftPath, rightPath);

    /// <summary>
    /// <para>打开文件夹比较：两个目录分别放入前两个文件列并立即开始对比。</para>
    /// Opens a folder comparison: the two directories fill the first two file columns and the comparison
    /// starts immediately.
    /// </summary>
    /// <param name="leftDirectory"><para>左目录路径。</para>Left directory path.</param>
    /// <param name="rightDirectory"><para>右目录路径。</para>Right directory path.</param>
    public Task OpenCompareFoldersAsync(string leftDirectory, string rightDirectory)
        => OpenComparePairAsync(leftDirectory, rightDirectory);

    /// <summary>
    /// <para>按路径加载文件列（拖入的文件/文件夹与「添加文件夹」对话框共用）：每个路径占用一个文件列
    /// （优先复用空列），不做自动对比——需要对比时由「开启对比」按钮明确触发。</para>
    /// Loads columns from paths (shared by dropped files/folders and the "add folder" dialog): each path takes
    /// one file column (reusing empty columns first) without starting a comparison; the compare button triggers
    /// that explicitly.
    /// </summary>
    /// <param name="paths"><para>待加载的路径列表。</para>The paths to load.</param>
    /// <returns><para>是否消费了全部路径（路径全部存在且至少一个）。</para>Whether every path was consumed (all exist and at least one).</returns>
    public async Task<bool> TryAddPathsAsync(IReadOnlyList<string> paths)
    {
        if (IsComparing)
        {
            // 对比进行中列数固定为两列：拖入新路径只会被忽略，改为明确提示先结束对比。
            // A comparison keeps exactly two columns: a dropped path is rejected with an explicit hint instead
            // of silently changing the layout.
            ReportError(LanguageService.Instance.GetString("Compare_AddColumnWhileComparing"));
            return false;
        }

        var loaded = 0;
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            var isDirectory = Directory.Exists(fullPath);
            if (!isDirectory && !File.Exists(fullPath))
            {
                continue;
            }

            var column = ReserveColumn();
            column.Path = fullPath;
            await column.LoadPathAsync(fullPath, isDirectory);
            loaded++;
        }

        // 恰好拖入两个路径时直接开始对比（与「工具」菜单的语义一致）；其余情况只加载为浏览列。
        // Exactly two dropped paths start the comparison straight away (matching the Tools menu); anything else only loads columns.
        if (loaded == 2 && loaded == paths.Count && !IsComparing)
        {
            await StartCompareAsync(allowSingleSide: false);
        }

        return loaded > 0 && loaded == paths.Count;
    }

    /// <summary>
    /// <para>关闭文档时释放列与对比状态（文档关闭事件调用），取消进行中的加载并回收懒加载的
    /// 十六进制集合。</para>
    /// Releases the columns and comparison state when the document closes (called by the document-close event),
    /// cancelling in-flight loads and disposing the lazily-loaded hex collection.
    /// </summary>
    public void Cleanup()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        _currentHexRows?.Dispose();
        _currentHexRows = null;

        foreach (var column in Columns)
        {
            column.Dispose();
        }

        Columns.Clear();
        _leftColumn = null;
        _rightColumn = null;
        ResetCompareState();
        IsComparing = false;
        StatsText = string.Empty;
        IsBusy = false;
        HasError = false;
        ErrorMessage = string.Empty;
        NotifyColumnsChanged();
    }

    /// <summary>
    /// <para>开启/结束对比命令：浏览状态校验两列后开始对比，对比中再次点击结束对比并把两列还原为浏览
    /// 状态（目录重新折叠为普通浏览树）。</para>
    /// Start/stop-compare command: validates the two columns and starts the comparison while browsing, and ends
    /// it (restoring both columns to browsing, with directories collapsed back into plain browsing trees) when
    /// pressed again.
    /// </summary>
    [RelayCommand]
    private async Task ToggleCompareAsync()
    {
        if (IsComparing)
        {
            await EndCompareAsync();
            return;
        }

        await StartCompareAsync(allowSingleSide: false);
    }

    /// <summary>
    /// <para>下一处差异命令：在文本/十六进制模式下从选中行之后查找差异行并选中，绕回循环。</para>
    /// Next-diff command: in text/hex mode finds the next diff row after the selected one and selects it,
    /// wrapping around.
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
    /// <para>上一处差异命令：在文本/十六进制模式下从选中行之前查找差异行并选中，绕回循环。</para>
    /// Previous-diff command: in text/hex mode finds the previous diff row before the selected one and selects
    /// it, wrapping around.
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
    /// Toggle-binary-view command: switches between the metadata and hex-diff views and rebuilds the rows; the
    /// old hex collection is disposed first and switching back to metadata reuses the cached metadata to avoid
    /// re-reading.
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

        // 单侧（对侧没有对应文件）没有可比对的第二个文件，无法产出差异区间，保持在元数据视图。
        // A single side (no counterpart file) has no second file to diff against, so hex stays unavailable.
        if (!CanShowHexView)
        {
            return;
        }

        var ct = BeginLoad();
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            var maxLength = Math.Max(new FileInfo(_fileLeftPath).Length, new FileInfo(_fileRightPath).Length);
            var rowDiffBitmap = new byte[BinaryDiffService.RowBitmapLength(maxLength)];
            var stats = await _comparison.CompareBinaryAsync(_fileLeftPath, _fileRightPath, rowDiffBitmap, ct);
            if (ct.IsCancellationRequested)
            {
                // 协作式取消：位图/统计基于部分扫描结果，直接丢弃并保持当前视图。
                // Cooperative cancellation: the bitmap/stats came from a partial scan, discard them and keep the
                // current view.
                return;
            }

            _currentHexRows?.Dispose();
            var hex = new CompareHexRowCollection(_fileLeftPath, _fileRightPath, rowDiffBitmap);
            _currentHexRows = hex;
            Rows = hex;
            BinaryViewKind = BinaryViewKind.Hex;
            SelectedIndex = -1;
            StatsText = string.Format(
                LanguageService.Instance.GetString("Compare_HexSummary"),
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
            // Clear busy only while this load is still the current one: a cancelled older load must not clear
            // the newer load's busy state.
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// <para>在文档末尾追加一个空文件列。</para>
    /// Appends an empty file column to the document.
    /// </summary>
    /// <returns><para>新建的文件列。</para>The new file column.</returns>
    internal CompareColumnViewModel AddColumn()
    {
        var column = new CompareColumnViewModel(this);
        Columns.Add(column);
        return column;
    }

    /// <summary>
    /// <para>移除一个文件列；关闭参与对比的列会先结束对比（并等待两列的浏览内容载入完成，避免移除后仍有
    /// 后台读取在跑），最后一列不会被移除。</para>
    /// Removes a file column; closing a compare column ends the comparison first (awaiting the columns' browsing
    /// reload so no background read outlives the removal), and the last remaining column is never removed.
    /// </summary>
    /// <param name="column"><para>被移除的文件列。</para>The column to remove.</param>
    internal async Task RemoveColumnAsync(CompareColumnViewModel column)
    {
        if (!Columns.Contains(column) || Columns.Count <= 1)
        {
            return;
        }

        if (IsComparing && column.IsCompareSide)
        {
            // 结束对比会重载两列的浏览内容，随后的移除落到浏览状态上。
            // Ending the comparison reloads both columns' browsing content; the removal then applies to that
            // browsing state.
            await EndCompareAsync();
        }

        Columns.Remove(column);
        column.Dispose();
    }

    /// <summary>
    /// <para>用当前两列重新执行当前对比（目录对比重新比对目录并重建对齐行列表，文件对比重新读取该文件对）。
    /// 对比中任一侧点击刷新都会走这里，因此左右两侧同时刷新。</para>
    /// Re-runs the active comparison with the current two columns (a folder comparison re-compares the
    /// directories and rebuilds the aligned rows, a file comparison re-reads the pair). Refreshing either side
    /// lands here, so both sides refresh together.
    /// </summary>
    internal async Task RefreshCompareAsync()
    {
        if (!IsComparing)
        {
            await StartCompareAsync(allowSingleSide: false);
            return;
        }

        var left = _leftColumn;
        var right = _rightColumn;
        if (left is null || right is null)
        {
            return;
        }

        _leftPath = left.Path;
        _rightPath = right.Path;
        await LoadCompareAsync();
    }

    /// <summary>
    /// <para>对比中「返回上一级」：把左右两侧同时切到各自的父目录并重新比对（一侧已在根目录时整体不动作）。
    /// 两侧同时操作，保证对齐关系不被破坏。</para>
    /// "Go up" while comparing: moves both sides to their parent directories at once and re-compares (a side
    /// already at a root aborts the whole move). Both sides move together so the alignment stays intact.
    /// </summary>
    internal async Task GoUpCompareAsync()
    {
        if (!IsComparing)
        {
            return;
        }

        var leftParent = ParentDirectoryOf(_leftPath);
        var rightParent = ParentDirectoryOf(_rightPath);
        if (leftParent is null || rightParent is null)
        {
            ReportError(LanguageService.Instance.GetString("Compare_NoParentFolder"));
            return;
        }

        _leftPath = leftParent;
        _rightPath = rightParent;
        if (_leftColumn is not null)
        {
            _leftColumn.Path = leftParent;
        }

        if (_rightColumn is not null)
        {
            _rightColumn.Path = rightParent;
        }

        ClearError();
        await LoadCompareAsync();
    }

    /// <summary>
    /// <para>展开/折叠文件夹对比中的节点：两侧共享同一节点，因此一侧展开即两侧同步展开，
    /// 并立即重建对齐行列表（子项紧跟在父项后面）。</para>
    /// Expands or collapses a node of the folder comparison: both sides share one node, so expanding on one
    /// side expands the other, and the aligned row list is rebuilt immediately (children follow their parent).
    /// </summary>
    /// <param name="node"><para>被展开/折叠的对比节点。</para>The comparison node to expand or collapse.</param>
    internal async Task ToggleTreeExpansionAsync(CompareTreeNodeViewModel node)
    {
        if (!IsComparing || _compareTreeRoot is null)
        {
            return;
        }

        if (!node.IsExpanded)
        {
            await node.EnsureChildrenLoadedAsync();
        }

        node.IsExpanded = !node.IsExpanded;
        Rows = ComparisonRowBuilder.ForTree([_compareTreeRoot], compared: true);
    }

    /// <summary>
    /// <para>文件夹对比中双击文件：按相对路径解析两侧的完整路径（对侧不存在时留空），并请求在新标签中
    /// 打开该文件的对比——当前视图保持目录对比不被打断。</para>
    /// Double-clicking a file in a folder comparison: resolves both sides' full paths from the relative path
    /// (empty when the counterpart is missing) and asks for the file comparison to open in a new tab, leaving
    /// the current view's folder comparison untouched.
    /// </summary>
    /// <param name="node"><para>被双击的文件节点。</para>The double-clicked file node.</param>
    internal void OpenFileComparisonInNewTab(CompareTreeNodeViewModel node)
    {
        if (!IsComparing || Mode != CompareMode.Folder || node.IsDirectory)
        {
            return;
        }

        var relative = node.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        var left = Path.Combine(_leftPath, relative);
        var right = Path.Combine(_rightPath, relative);
        var leftPath = File.Exists(left) ? left : string.Empty;
        var rightPath = File.Exists(right) ? right : string.Empty;
        if (leftPath.Length == 0 && rightPath.Length == 0)
        {
            ReportError(string.Format(LanguageService.Instance.GetString("Compare_PathNotFound"), node.Name));
            return;
        }

        WeakReferenceMessenger.Default.Send(new OpenFileCompareInNewTabRequestMessage
        {
            LeftPath = leftPath,
            RightPath = rightPath,
        });
    }

    /// <summary>
    /// <para>记录一条操作错误（列加载、路径校验、对比失败等）。</para>
    /// Records an operation error (column load, path validation, comparison failure).
    /// </summary>
    /// <param name="message"><para>错误文本。</para>The error text.</param>
    internal void ReportError(string message)
    {
        HasError = true;
        ErrorMessage = message;
    }

    /// <summary>清除当前错误提示。</summary>
    internal void ClearError()
    {
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>列状态变化后刷新文档级派生属性（空文档提示）。</summary>
    internal void NotifyColumnsChanged() => OnPropertyChanged(nameof(IsEmptyDocument));

    /// <summary>
    /// <para>某一侧的文件是否被编辑过（文本对比中修改文本或「复制到另一侧」之后置位，保存后清除）。</para>
    /// Whether one side's file has been edited (set after text edits or "copy to the other side" in a text comparison,
    /// cleared by saving).
    /// </summary>
    /// <param name="side"><para>列的角色（左/右）。</para>The column's role (left/right).</param>
    internal bool IsFileDirty(CompareColumnSide side) => side == CompareColumnSide.Right ? _dirtyRight : _dirtyLeft;

    /// <summary>记录文本编辑（文本对比中任一侧文本被修改时由行模型回调）。</summary>
    internal void MarkRowEdited(CompareRowViewModel row, bool leftSide) => MarkDirty(leftSide);

    /// <summary>标记某一侧文件为已修改并刷新该列头的保存按钮。</summary>
    private void MarkDirty(bool leftSide)
    {
        if (leftSide)
        {
            _dirtyLeft = true;
        }
        else
        {
            _dirtyRight = true;
        }

        NotifyColumnsDirtyState();
    }

    /// <summary>清除某一侧的已修改标记。</summary>
    private void ClearDirty(bool leftSide)
    {
        if (leftSide)
        {
            _dirtyLeft = false;
        }
        else
        {
            _dirtyRight = false;
        }

        NotifyColumnsDirtyState();
    }

    /// <summary>把「已修改」状态变化通知到各列（决定保存按钮是否显示）。</summary>
    private void NotifyColumnsDirtyState()
    {
        foreach (var column in Columns)
        {
            column.NotifyDirtyChanged();
        }
    }

    /// <summary>
    /// <para>记录两栏当前选中的行索引（视图在选中变化时写回）：文本对比的「复制到另一侧」按这些行执行，
    /// 目录对比的复制按钮不再依赖它（按行列表选中项定位条目）。</para>
    /// Records the currently selected row indices (written back by the view on selection changes): "copy to the other
    /// side" in a text comparison acts on them, while the folder comparison's copy button locates its entry through the
    /// row list's selection instead.
    /// </summary>
    /// <param name="indices"><para>选中的行索引。</para>The selected row indices.</param>
    internal void UpdateRowSelection(IEnumerable<int> indices) => _selectedRowIndices = [.. indices];

    /// <summary>
    /// <para>把选中行在某一侧的文本复制到同一行的另一侧（文本对比专用）：目标侧没有该行时补出一行，
    /// 源侧没有该行时把目标侧该行删除（与 Beyond Compare 的「复制到另一侧」一致），并标记目标侧文件已修改。</para>
    /// Copies the selected rows' text from one side to the same rows on the other side (text comparisons only): a target
    /// side without the row gains one, and a source side without the row deletes the target's row (matching Beyond
    /// Compare's "copy to other side"), marking the target file modified.
    /// </summary>
    /// <param name="fromLeftToRight"><para>是否从左复制到右。</para>Whether to copy left to right.</param>
    internal void CopySelectedTextRows(bool fromLeftToRight)
    {
        if (_textModel is null || _selectedRowIndices.Count == 0)
        {
            return;
        }

        foreach (var index in _selectedRowIndices)
        {
            // 每选中的一槽单独应用：源侧有内容就复制过去，没有就把目标侧退成填充（等价于删掉目标那一行）。
            // Each selected slot is applied on its own: the source side's content is copied over, or the target falls back to a
            // filler when the source has no line there (equivalent to deleting the target's line).
            CopySlots(fromLeftToRight, index, 1);
        }
    }

    /// <summary>
    /// <para>把某一行在某一侧断开成两行（行内回车）：光标前留在原行，光标后成为紧随其后的新行，
    /// 另一侧内容留在原行，因此新行只存在于被编辑的一侧。</para>
    /// Splits one row into two on the edited side (Enter inside a line): the text before the caret stays in the row and
    /// the text after it becomes the immediately following row, while the other side's line stays with the original row —
    /// so the new row exists only on the edited side.
    /// </summary>
    /// <param name="row"><para>被断开的行。</para>The row being split.</param>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side is edited.</param>
    /// <param name="caretIndex"><para>光标位置。</para>The caret position.</param>
    /// <returns><para>新行的索引与应放置的光标位置。</para>The new row's index and the caret position to restore.</returns>
    internal (int Index, int Caret) SplitTextRow(CompareRowViewModel row, bool leftSide, int caretIndex)
    {
        if (_textRows is null)
        {
            return (0, 0);
        }

        var slot = IndexOfRow(row);
        if (_textModel is null || slot < 0)
        {
            return (0, 0);
        }

        var text = _textModel.TextAt(slot, leftSide);
        var caret = Math.Clamp(caretIndex, 0, text.Length);
        _textModel.ReplaceLines(slot, 1, leftSide, [text[..caret], text[caret..]]);
        MarkDirty(leftSide);
        RebuildTextRows();
        return (slot + 1, 0);
    }

    /// <summary>
    /// <para>把某一行与下一行合并（行尾 Delete）：下一行在编辑侧的文本接到本行末尾；下一行在两侧都没有内容时
    /// 整行移除。</para>
    /// Joins a row with the next one (Delete at the end of a line): the next row's text on the edited side is appended and
    /// the next row is removed entirely once it has no content on either side.
    /// </summary>
    /// <param name="row"><para>合并的起始行。</para>The row the join starts from.</param>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side is edited.</param>
    /// <returns><para>光标应回到的行索引与位置。</para>The row index and caret position to restore.</returns>
    internal (int Index, int Caret) JoinTextRowWithNext(CompareRowViewModel row, bool leftSide)
    {
        if (_textRows is null)
        {
            return (0, 0);
        }

        var slot = IndexOfRow(row);
        if (_textModel is null || slot < 0)
        {
            return (0, 0);
        }

        var caret = _textModel.TextAt(slot, leftSide).Length;
        _textModel.JoinLines(slot, leftSide);
        MarkDirty(leftSide);
        RebuildTextRows();
        return (slot, caret);
    }

    /// <summary>
    /// <para>把某一行并入上一行（行首 Backspace）：本行在编辑侧的文本接到上一行末尾；本行在两侧都没有内容时
    /// 整行移除。</para>
    /// Merges a row into the previous one (Backspace at the start of a line): this row's text on the edited side is
    /// appended to the previous row, and this row is removed entirely once it has no content on either side.
    /// </summary>
    /// <param name="row"><para>被合并的行。</para>The row being merged.</param>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side is edited.</param>
    /// <returns><para>光标应回到的行索引与位置。</para>The row index and caret position to restore.</returns>
    internal (int Index, int Caret) JoinTextRowWithPrevious(CompareRowViewModel row, bool leftSide)
    {
        if (_textRows is null)
        {
            return (0, 0);
        }

        var slot = IndexOfRow(row);
        if (_textModel is null || slot <= 0)
        {
            return (Math.Max(slot, 0), 0);
        }

        var caret = _textModel.TextAt(slot - 1, leftSide).Length;
        _textModel.JoinLines(slot - 1, leftSide);
        MarkDirty(leftSide);
        RebuildTextRows();
        return (slot - 1, caret);
    }

    /// <summary>
    /// <para>删除选中的行（在该侧清空这些行）：该侧的行被移除，另一侧仍有内容的行保留自己的内容；
    /// 两侧都没有内容的行整行移除。</para>
    /// Deletes the selected rows on the edited side: that side's lines disappear while the other side keeps its content,
    /// and a row that loses its content on both sides is removed entirely.
    /// </summary>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side is edited.</param>
    internal void DeleteSelectedTextRows(bool leftSide)
    {
        if (_textModel is null || _selectedRowIndices.Count == 0)
        {
            return;
        }

        foreach (var index in _selectedRowIndices.OrderByDescending(index => index))
        {
            _textModel.ReplaceLines(index, 1, leftSide, []);
        }

        _textModel.DropEmptySlots();
        MarkDirty(leftSide);
        RebuildTextRows();
    }

    /// <summary>取行在投影行列表中的索引（行列表与槽一一对应）。</summary>
    private int IndexOfRow(CompareRowViewModel row)
    {
        if (_textRows is null)
        {
            return -1;
        }

        for (var index = 0; index < _textRows.Count; index++)
        {
            if (ReferenceEquals(_textRows[index], row))
            {
                return index;
            }
        }

        return -1;
    }


    /// <summary>
    /// <para>保存某一侧文本对比的编辑：把该侧当前的所有行按顺序写回文件（保留原编码与换行风格），
    /// 完成后清除「已修改」标记。</para>
    /// Saves one side's text-comparison edits: writes that side's current lines back to the file in order (preserving the
    /// original encoding and line ending) and clears the modified flag afterwards.
    /// </summary>
    /// <param name="column"><para>发起保存的列。</para>The column that raised the save.</param>
    internal async Task SaveSideAsync(CompareColumnViewModel column)
    {
        if (!IsComparing || !column.IsCompareSide || Rows is null)
        {
            return;
        }

        var leftSide = column.Side != CompareColumnSide.Right;
        var path = leftSide ? _fileLeftPath : _fileRightPath;
        if (path.Length == 0)
        {
            ReportError(LanguageService.Instance.GetString("Compare_SingleSideFile"));
            return;
        }

        // 槽模型是唯一的真相：保存该侧真实行（填充槽不写入文件，空行照写）。
        // The slot model is the single source of truth: save that side's real lines (fillers are never written, blank lines are).
        var lines = RealLines(leftSide);
        if (_textModel is null)
        {
            ReportError(LanguageService.Instance.GetString("Compare_SingleSideFile"));
            return;
        }

        try
        {
            await _comparison.SaveTextAsync(path, lines, CancellationToken.None);
            ClearDirty(leftSide);
            ClearError();
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    /// <summary>
    /// <para>构建浏览一个文件所需的行：文本文件按行（1 起行号），二进制文件按固件元数据（单侧）。</para>
    /// Builds the rows for browsing one file: text files by line (1-based numbers), binary files by firmware
    /// metadata (single side).
    /// </summary>
    /// <param name="path"><para>文件路径。</para>The file path.</param>
    /// <param name="cancellationToken"><para>取消令牌。</para>The cancellation token.</param>
    /// <param name="edited">文本被编辑时的回调（浏览列用它把自己的文件标记为已修改）。
    /// <para>Callback invoked when the text is edited (a browsing column uses it to mark its own file modified).</para></param>
    /// <returns><para>该文件的展示行（可结构编辑的可观察集合）。</para>The rows that display the file (an observable collection that supports structural editing).</returns>
    internal async Task<ObservableCollection<CompareRowViewModel>> BuildFileRowsAsync(
        string path,
        CancellationToken cancellationToken,
        Action<CompareRowViewModel, bool>? edited = null)
    {
        var isText = await _comparison.IsTextLikeFileAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (isText)
        {
            var lines = await _comparison.ReadTextAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return [.. await Task.Run(() => ComparisonRowBuilder.ForText(lines, edited), cancellationToken)];
        }

        var metadata = await _comparison.ReadMetadataAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return [.. ComparisonRowBuilder.ForMetadata(metadata, null, LanguageService.Instance.GetString("Compare_Unrecognized"))];
    }

    /// <summary>
    /// <para>读取一个文本文件的行；文件不是文本（固件扩展名或含 NUL）时返回 <c>null</c>，调用方改用元数据视图。
    /// 浏览列的文本编辑直接使用项目文本编辑器，因此只需要行内容。</para>
    /// Reads a text file's lines; returns <c>null</c> when the file is not textual (firmware extension or NUL bytes) so the
    /// caller falls back to the metadata view. A browsing column edits text with the project's text editor, so the lines are
    /// all it needs.
    /// </summary>
    /// <param name="path"><para>文件路径。</para>The file path.</param>
    /// <param name="cancellationToken"><para>取消令牌。</para>The cancellation token.</param>
    internal async Task<IReadOnlyList<string>?> ReadTextLinesAsync(string path, CancellationToken cancellationToken)
    {
        var isText = await _comparison.IsTextLikeFileAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return isText ? await _comparison.ReadTextAsync(path, cancellationToken) : null;
    }

    /// <summary>
    /// <para>取路径的父目录：目录取其父目录，文件取所在目录的父目录；已在根目录时返回 <c>null</c>。</para>
    /// Returns the parent directory of a path: for a directory its parent, for a file the parent of its
    /// directory; <c>null</c> at a root.
    /// </summary>
    private static string? ParentDirectoryOf(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? null : Directory.GetParent(directory)?.FullName;
    }

    /// <summary>
    /// <para>把两个路径放入前两个文件列并开始对比（一侧留空表示单侧文件对比）。</para>
    /// Places the two paths into the first two file columns and starts the comparison (an empty side means a
    /// single-side file comparison).
    /// </summary>
    private async Task OpenComparePairAsync(string leftPath, string rightPath)
    {
        if (string.IsNullOrWhiteSpace(leftPath) && string.IsNullOrWhiteSpace(rightPath))
        {
            return;
        }

        if (IsComparing)
        {
            await EndCompareAsync();
        }

        EnsureColumnCount(2);

        // 命令语义是「对比这两个路径」：只保留前两列，多余的浏览列一并关闭。
        // The command's contract is "compare these two paths": keep only the first two columns and close any
        // extra browsing columns.
        while (Columns.Count > 2)
        {
            await RemoveColumnAsync(Columns[^1]);
        }

        Columns[0].Path = string.IsNullOrWhiteSpace(leftPath) ? string.Empty : Path.GetFullPath(leftPath);
        Columns[1].Path = string.IsNullOrWhiteSpace(rightPath) ? string.Empty : Path.GetFullPath(rightPath);
        await StartCompareAsync(allowSingleSide: true);
    }

    /// <summary>
    /// <para>开启对比：要求两个文件列有内容，两侧同为目录时保持目录树外观并逐行对齐，同为文件时直接对比；
    /// 一侧为文件一侧为目录会被拒绝。只有「对比指定路径」（在新标签打开文件对比等）允许单侧留空。</para>
    /// Starts the comparison: two columns must hold content; two directories keep the tree look with aligned rows,
    /// two files are compared directly, and a file against a directory is rejected. Only an explicit "compare
    /// these paths" request (opening a file comparison in a new tab) may leave one side empty.
    /// </summary>
    /// <param name="allowSingleSide"><para>是否允许单侧文件对比（对侧留空）。</para>Whether a single-side file comparison (an empty counterpart) is allowed.</param>
    private async Task StartCompareAsync(bool allowSingleSide)
    {
        var loaded = Columns.Where(column => column.HasPath).ToList();
        if (loaded.Count is 0 or > 2 || (loaded.Count == 1 && !allowSingleSide))
        {
            ReportError(string.Format(
                LanguageService.Instance.GetString("Compare_NeedTwoColumns"), loaded.Count));
            return;
        }

        CompareColumnViewModel? left;
        CompareColumnViewModel? right;
        if (loaded.Count == 2)
        {
            left = loaded[0];
            right = loaded[1];
        }
        else
        {
            // 单侧：内容列保持它在文档中的位置（第 1 列 → 左，第 2 列 → 右），另一列为空。
            // Single side: the populated column keeps its position (first column → left, second → right) and the
            // other stays empty.
            left = Columns.ElementAtOrDefault(0);
            right = Columns.ElementAtOrDefault(1);
            if (ReferenceEquals(left, right))
            {
                right = null;
            }
        }

        var leftPath = left?.Path ?? string.Empty;
        var rightPath = right?.Path ?? string.Empty;
        var leftIsDirectory = Directory.Exists(leftPath);
        var rightIsDirectory = Directory.Exists(rightPath);
        var leftIsFile = File.Exists(leftPath);
        var rightIsFile = File.Exists(rightPath);

        if (leftIsDirectory || rightIsDirectory)
        {
            if (!leftIsDirectory || !rightIsDirectory)
            {
                ReportError(LanguageService.Instance.GetString("Compare_MixedKinds"));
                return;
            }
        }
        else if (!leftIsFile && !rightIsFile)
        {
            ReportError(string.Format(
                LanguageService.Instance.GetString("Compare_NeedTwoColumns"), loaded.Count));
            return;
        }

        ResetCompareState();
        _leftColumn = left;
        _rightColumn = right;
        _leftPath = leftPath;
        _rightPath = rightPath;
        if (left is not null)
        {
            left.Side = CompareColumnSide.Left;
        }

        if (right is not null)
        {
            right.Side = CompareColumnSide.Right;
        }

        IsComparing = true;
        ClearError();
        await LoadCompareAsync();
    }

    /// <summary>
    /// <para>结束对比：释放对比结果与懒加载集合，并把两列还原为浏览状态（目录重新折叠为普通浏览树）。</para>
    /// Ends the comparison: releases the result and lazily-loaded collections, and restores both columns to
    /// browsing (directories collapse back into plain browsing trees).
    /// </summary>
    private async Task EndCompareAsync()
    {
        var left = _leftColumn;
        var right = _rightColumn;
        _leftColumn = null;
        _rightColumn = null;
        IsComparing = false;
        ResetCompareState();
        StatsText = string.Empty;
        NotifyColumnsChanged();

        foreach (var column in new[] { left, right })
        {
            if (column is null || !Columns.Contains(column))
            {
                continue;
            }

            column.Side = CompareColumnSide.Browse;
            var path = column.Path;
            if (path.Length == 0)
            {
                column.ClearContent();
                continue;
            }

            await column.LoadPathAsync(path, Directory.Exists(path));
        }
    }

    /// <summary>
    /// <para>按当前两侧路径执行对比：两个目录走文件夹对比，其余按文件对比（含单侧）。</para>
    /// Runs the comparison for the current pair of paths: two directories use the folder comparison and anything
    /// else uses the file comparison (including a single side).
    /// </summary>
    private async Task LoadCompareAsync()
    {
        if (Directory.Exists(_leftPath) && Directory.Exists(_rightPath))
        {
            await CompareFoldersAsync();
            return;
        }

        _fileLeftPath = File.Exists(_leftPath) ? _leftPath : string.Empty;
        _fileRightPath = File.Exists(_rightPath) ? _rightPath : string.Empty;
        if (_fileLeftPath.Length == 0 && _fileRightPath.Length == 0)
        {
            ReportError(string.Format(
                LanguageService.Instance.GetString("Compare_PathNotFound"), _leftPath.Length > 0 ? _leftPath : _rightPath));
            return;
        }

        await LoadFilesAsync();
    }

    /// <summary>
    /// <para>加载文件对比：嗅探两侧文件选择文本/二进制模式并构建对应行与状态；对侧没有对应文件时
    /// 只读取存在的一侧，另一侧整列留空（对齐行号仍逐行对应）。</para>
    /// Loads a file comparison: sniffs both files to pick text/binary mode and builds the matching rows and
    /// status; when a side has no counterpart only the existing side is read and the other column stays empty
    /// (row numbers still line up).
    /// </summary>
    private async Task LoadFilesAsync()
    {
        var hasLeft = _fileLeftPath.Length > 0;
        var hasRight = _fileRightPath.Length > 0;
        if (!hasLeft && !hasRight)
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
        // 重新读取磁盘内容意味着放弃此前的内存编辑（刷新/重新对比的语义）。
        // Re-reading from disk abandons previous in-memory edits (the semantics of refresh/re-compare).
        _dirtyLeft = false;
        _dirtyRight = false;
        _selectedRowIndices = [];
        NotifyColumnsDirtyState();
        try
        {
            var leftIsText = hasLeft && await _comparison.IsTextLikeFileAsync(_fileLeftPath, ct);
            ct.ThrowIfCancellationRequested();
            var rightIsText = hasRight && await _comparison.IsTextLikeFileAsync(_fileRightPath, ct);
            ct.ThrowIfCancellationRequested();

            if (!hasLeft || !hasRight)
            {
                await LoadSingleSideAsync(hasLeft, hasLeft ? leftIsText : rightIsText, ct);
                return;
            }

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
            // Clear busy only while this load is still the current one: a cancelled older load must not clear the
            // newer load's busy state.
            if (ct == _loadCts.Token)
            {
                IsBusy = false;
            }

            // 列头的十六进制/差异导航按钮可见性随本次加载后的模式与文件对重新求值。
            // The header's hex/navigation button visibility is re-evaluated from the mode and file pair this load
            // produced.
            NotifyColumnsCompareState();
        }
    }

    /// <summary>
    /// <para>加载单侧文件内容：文本按行、二进制按固件元数据，内容落在存在的一侧，另一侧留空。</para>
    /// Loads a single side's file content: text by line, binary by firmware metadata, with the content on the
    /// side that has the file and the other side left empty.
    /// </summary>
    /// <param name="useLeftSide"><para>文件是否在左侧。</para>Whether the file is on the left.</param>
    /// <param name="isText"><para>是否为文本文件。</para>Whether the file is textual.</param>
    /// <param name="ct"><para>取消令牌。</para>The cancellation token.</param>
    private async Task LoadSingleSideAsync(bool useLeftSide, bool isText, CancellationToken ct)
    {
        var path = useLeftSide ? _fileLeftPath : _fileRightPath;
        var unrecognized = LanguageService.Instance.GetString("Compare_Unrecognized");
        if (isText)
        {
            var lines = await _comparison.ReadTextAsync(path, ct);
            ct.ThrowIfCancellationRequested();

            Mode = CompareMode.Text;
            _textModel = new CompareTextSlotModel();
            _textModel.LoadSingleSide(lines, useLeftSide);
            RebuildTextRows();
            StatsText = LanguageService.Instance.GetString("Compare_SingleSideFile");
        }
        else
        {
            var metadata = await _comparison.ReadMetadataAsync(path, ct);
            ct.ThrowIfCancellationRequested();

            Mode = CompareMode.Binary;
            _leftMetadata = useLeftSide ? metadata : null;
            _rightMetadata = useLeftSide ? null : metadata;
            BinaryViewKind = BinaryViewKind.Metadata;
            Rows = useLeftSide
                ? ComparisonRowBuilder.ForMetadata(metadata, null, unrecognized)
                : ComparisonRowBuilder.ForMetadata(null, metadata, unrecognized);
            StatsText = LanguageService.Instance.GetString("Compare_SingleSideFile");
        }

        SelectedIndex = -1;
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// <para>加载双侧文本比较：流式 DiffPlex 行差异并生成对比行与差异汇总。</para>
    /// Loads a two-sided text comparison: a streamed DiffPlex line diff producing rows and a diff summary.
    /// </summary>
    private async Task LoadTextBothSidesAsync(CancellationToken ct)
    {
        var lines = await _comparison.CompareTextAsync(_fileLeftPath, _fileRightPath, ct);
        ct.ThrowIfCancellationRequested();

        // 文本对比使用「槽模型 + 两侧文本编辑器」：行号=对齐序号，填充槽显示斜杠，编辑由编辑器承担。
        // A text comparison uses the slot model together with the project's text editors: one line per aligned slot with
        // slashes for fillers, and the editors as the editing surface.
        _textModel = new CompareTextSlotModel();
        _textModel.Load(lines);
        RebuildTextRows();
        var diffCount = _textModel.Slots.Count(slot => slot.Status != FolderItemStatus.Equal);
        SelectedIndex = -1;
        StatsText = string.Format(
            LanguageService.Instance.GetString("Compare_TextSummary"),
            diffCount,
            _textModel.Slots.Count);
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// <para>加载双侧二进制比较：识别并读取两侧元数据生成元数据行（差异统计仅在切换十六进制
    /// 原始视图时计算）。</para>
    /// Loads a two-sided binary comparison: detects and reads both sides' metadata into metadata rows (diff
    /// stats are computed only when switching to the hex-raw view).
    /// </summary>
    private async Task LoadBinaryBothSidesAsync(CancellationToken ct)
    {
        try
        {
            var leftMeta = await _comparison.ReadMetadataAsync(_fileLeftPath, ct);
            ct.ThrowIfCancellationRequested();
            var rightMeta = await _comparison.ReadMetadataAsync(_fileRightPath, ct);
            ct.ThrowIfCancellationRequested();

            _leftMetadata = leftMeta;
            _rightMetadata = rightMeta;
            BinaryViewKind = BinaryViewKind.Metadata;
            Rows = ComparisonRowBuilder.ForMetadata(leftMeta, rightMeta, LanguageService.Instance.GetString("Compare_Unrecognized"));
            SelectedIndex = -1;
            StatsText = string.Empty;

            // 不在此跑全文件差异扫描：二进制模式下汇总不显示差异计数，且多 GB 固件的全量扫描
            // 耗时数十秒，会让 busy 指示一直转；差异统计在切换十六进制原始视图时才需要
            // （CompareStats 位图重载），由 ToggleHexView 计算。
            // No full-file diff scan here: in binary mode the summary shows no diff counts, and a full scan of
            // multi-GB firmware takes tens of seconds, keeping the busy indicator spinning; diff stats are only
            // needed when switching to the hex-raw view (the CompareStats bitmap overload), computed by
            // ToggleHexView.
            HasError = false;
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 取消：保持当前状态，不向上传播（切换十六进制视图/重新加载会取消进行中的加载，
            // 若此处不捕获，后台 Task 抛出的 OCE 可能成为未处理异常导致崩溃）。
            // Cancelled: keep the current state and do not propagate — toggling the hex view or reloading cancels
            // the in-flight load, and without this catch the OCE thrown inside the background Task could surface
            // as an unhandled exception and crash the app.
        }
    }

    /// <summary>
    /// <para>清空一次对比的全部派生状态（路径、对比树、元数据缓存、计数与行）。</para>
    /// Clears every derived state of one comparison (paths, compare tree, metadata caches, counts and rows).
    /// </summary>
    private void ResetCompareState()
    {
        _leftPath = string.Empty;
        _rightPath = string.Empty;
        _fileLeftPath = string.Empty;
        _fileRightPath = string.Empty;
        _folderSummaryText = string.Empty;
        _compareTreeRoot = null;
        _textRows = null;
        _textModel = null;
        _textUndo.Clear();
        _textRedo.Clear();
        _lastTextUndoWasStructural = false;
        _currentHexRows?.Dispose();
        _currentHexRows = null;
        _leftMetadata = null;
        _rightMetadata = null;
        _dirtyLeft = false;
        _dirtyRight = false;
        _selectedRowIndices = [];
        Rows = null;
        Mode = CompareMode.None;
        BinaryViewKind = BinaryViewKind.Metadata;
        SelectedIndex = -1;
        EqualCount = 0;
        DifferentCount = 0;
        LeftOnlyCount = 0;
        RightOnlyCount = 0;
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
    /// <para>取第一个还没有内容的文件列，没有空列时在末尾追加。</para>
    /// Returns the first file column without content, appending a new one when none is free.
    /// </summary>
    private CompareColumnViewModel ReserveColumn()
    {
        foreach (var column in Columns)
        {
            if (!column.HasPath)
            {
                return column;
            }
        }

        return AddColumn();
    }

    /// <summary>把文件列数量补足到指定值。</summary>
    private void EnsureColumnCount(int count)
    {
        while (Columns.Count < count)
        {
            AddColumn();
        }
    }

    /// <summary>列集合变化后刷新文档级派生属性与各列的关闭按钮状态。</summary>
    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyColumnsChanged();
        foreach (var column in Columns)
        {
            column.NotifyCanCloseChanged();
        }
    }

    /// <summary>把对比模式派生的可见性变化通知到每一列（列头的差异导航/十六进制按钮）。</summary>
    private void NotifyColumnsCompareState()
    {
        foreach (var column in Columns)
        {
            column.NotifyCompareStateChanged();
        }
    }
}

using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using UotanStudio.Services.Compare;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>对比面板的统一行模型：以一行数据同时驱动左右两栏渲染（文本行的行号/内容、目录树行的名称/图标/大小、
/// 十六进制行的偏移/字节串/ASCII 串）。左右两栏共用同一份行列表，所以同名条目天然落在同一行、单侧条目在另一侧
/// 自动留空；文本对比中该行可编辑，两侧文本互改后整行差异状态即时重算。</para>
/// Unified row model for the compare panel: one row drives both panes (line numbers/text for text rows, name/glyph/size
/// for tree rows, offsets/bytes/ASCII for hex rows). Both panes share one list, so same-name entries share a row and
/// single-side entries stay blank on the other side; in a text comparison the row is editable and the whole-row diff
/// state is recomputed as either side's text changes.
/// </summary>
public sealed class CompareRowViewModel : ObservableObject
{
    /// <summary>
    /// <para>仅左侧内容的淡色背景画刷（橙色调，半透明叠加可辨识）。静态共享画刷使用不可变类型以避免线程亲和性。</para>
    /// Tint brush for content that exists on the left only (orange, translucent so the theme shows through).
    /// Static shared brushes use immutable types to avoid dispatcher thread affinity.
    /// </summary>
    internal static readonly IBrush LeftOnlyTintBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0xE6, 0x7E, 0x22));

    /// <summary>
    /// <para>仅右侧内容的淡色背景画刷（蓝色调，半透明叠加可辨识）。静态共享画刷使用不可变类型以避免线程亲和性。</para>
    /// Tint brush for content that exists on the right only (blue, translucent so the theme shows through).
    /// Static shared brushes use immutable types to avoid dispatcher thread affinity.
    /// </summary>
    internal static readonly IBrush RightOnlyTintBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x90, 0xFF));

    private readonly Action<CompareRowViewModel, bool>? _edited;
    private string _leftText;
    private string _rightText;
    private bool _hasLeftLine;
    private bool _hasRightLine;
    private bool _isLeftDiff;
    private bool _isRightDiff;

    /// <summary>
    /// <para>创建对比行视图模型；可选参数用于目录树行、可编辑文本行与右键菜单。</para>
    /// Creates a compare-row view model; the optional parameters cover tree rows, editable text rows and the context
    /// menu.
    /// </summary>
    /// <param name="status">行状态。<para>Row status.</para></param>
    /// <param name="depth">缩进深度（0 起）。<para>Indentation depth (0-based).</para></param>
    /// <param name="leftLine">左侧行号或十六进制偏移文本；缺失侧为空串。<para>Left line number or hex offset text; empty when the side is absent.</para></param>
    /// <param name="leftText">左侧主文本：行内容 / 名称 / 十六进制字节串。<para>Left primary text: line content, name or hex bytes.</para></param>
    /// <param name="leftSub">左侧副文本：大小或 ASCII 串。<para>Left secondary text: size or ASCII.</para></param>
    /// <param name="isLeftDiff">是否高亮左侧。<para>Whether the left side is highlighted.</para></param>
    /// <param name="rightLine">右侧行号或十六进制偏移文本；缺失侧为空串。<para>Right line number or hex offset text; empty when the side is absent.</para></param>
    /// <param name="rightText">右侧主文本。<para>Right primary text.</para></param>
    /// <param name="rightSub">右侧副文本。<para>Right secondary text.</para></param>
    /// <param name="isRightDiff">是否高亮右侧。<para>Whether the right side is highlighted.</para></param>
    /// <param name="tintSingleSide">单侧条目是否按侧加淡色背景（目录树行使用；文本行的单侧结构由整行高亮表达）。
    /// <para>Whether a single-side entry tints its own side (tree rows; text rows express single-sidedness through the
    /// whole-row highlight).</para></param>
    /// <param name="treeNode">树行对应的节点（展开/折叠与「在新标签打开文件对比」的目标）；文本行与十六进制行为
    /// <c>null</c>。<para>The node behind a tree row (expansion and open-in-new-tab target); <c>null</c> for text and hex
    /// rows.</para></param>
    /// <param name="leftIcon">左侧图标与配色；非树行或该侧无内容时为 <c>null</c>。
    /// <para>The left glyph and colour; <c>null</c> for non-tree rows or when that side has no content.</para></param>
    /// <param name="rightIcon">右侧图标与配色。<para>The right glyph and colour.</para></param>
    /// <param name="editable">该行是否可在文本对比中编辑。<para>Whether the row is editable in a text comparison.</para></param>
    /// <param name="menu">该行提供的右键菜单类型。<para>The context menu this row offers.</para></param>
    /// <param name="hasLeftLine">左侧是否存在该行内容；<c>null</c> 时按文本非空推断（区分空行与缺失行）。
    /// <para>Whether the left side has this row's content; <c>null</c> infers it from non-empty text (distinguishing an
    /// empty line from a missing line).</para></param>
    /// <param name="hasRightLine">右侧是否存在该行内容。<para>Whether the right side has this row's content.</para></param>
    /// <param name="edited">文本被编辑时的回调（参数为行与是否为左侧），供文档标记对应文件已修改。
    /// <para>Callback invoked when the text is edited (row and whether the left side changed), letting the document mark
    /// that file as modified.</para></param>
    public CompareRowViewModel(
        FolderItemStatus status,
        int depth,
        string leftLine,
        string leftText,
        string leftSub,
        bool isLeftDiff,
        string rightLine,
        string rightText,
        string rightSub,
        bool isRightDiff,
        bool tintSingleSide = false,
        CompareTreeNodeViewModel? treeNode = null,
        ExplorerIcon? leftIcon = null,
        ExplorerIcon? rightIcon = null,
        bool editable = false,
        CompareRowMenuKind menu = CompareRowMenuKind.None,
        bool compared = false,
        bool? hasLeftLine = null,
        bool? hasRightLine = null,
        Action<CompareRowViewModel, bool>? edited = null)
    {
        Status = status;
        Depth = depth;
        LeftLine = leftLine;
        _leftText = leftText;
        LeftSub = leftSub;
        _isLeftDiff = isLeftDiff;
        RightLine = rightLine;
        _rightText = rightText;
        RightSub = rightSub;
        _isRightDiff = isRightDiff;
        TintSingleSide = tintSingleSide;
        TreeNode = treeNode;
        LeftIcon = leftIcon;
        RightIcon = rightIcon;
        IsEditable = editable;
        Menu = menu;
        IsComparedRow = compared;
        _hasLeftLine = hasLeftLine ?? !string.IsNullOrEmpty(leftText);
        _hasRightLine = hasRightLine ?? !string.IsNullOrEmpty(rightText);
        _edited = edited;
    }

    /// <summary>
    /// <para>行状态（相同/不同/仅左/仅右）；文本行编辑后由 <see cref="RefreshDiffState"/> 重算。</para>
    /// Row status (equal/different/left-only/right-only), recomputed for edited text rows by
    /// <see cref="RefreshDiffState"/>.
    /// </summary>
    public FolderItemStatus Status { get; private set; }

    /// <summary>
    /// <para>缩进深度（0 起）。</para>
    /// Indentation depth (0-based).
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// <para>左侧行号或十六进制偏移文本；缺失侧为空串。</para>
    /// Left line number or hex offset text; empty when the side is absent.
    /// </summary>
    public string LeftLine { get; }

    /// <summary>
    /// <para>左侧主文本；文本对比中可编辑（写入即触发差异状态重算与文档的「已修改」标记）。</para>
    /// Left primary text; editable in a text comparison (writing recomputes the diff state and marks the document
    /// modified).
    /// </summary>
    public string LeftText
    {
        get => _leftText;
        set => SetSideText(leftSide: true, value, present: _hasLeftLine || !string.IsNullOrEmpty(value));
    }

    /// <summary>
    /// <para>左侧副文本：大小或 ASCII 串。</para>
    /// Left secondary text: size or ASCII.
    /// </summary>
    public string LeftSub { get; }

    /// <summary>
    /// <para>左侧是否差异高亮（整行变色）。</para>
    /// Whether the left side is diff-highlighted (whole-row colouring).
    /// </summary>
    public bool IsLeftDiff
    {
        get => _isLeftDiff;
        private set => SetProperty(ref _isLeftDiff, value);
    }

    /// <summary>
    /// <para>右侧行号或十六进制偏移文本；缺失侧为空串。</para>
    /// Right line number or hex offset text; empty when the side is absent.
    /// </summary>
    public string RightLine { get; }

    /// <summary>
    /// <para>右侧主文本；文本对比中可编辑。</para>
    /// Right primary text; editable in a text comparison.
    /// </summary>
    public string RightText
    {
        get => _rightText;
        set => SetSideText(leftSide: false, value, present: _hasRightLine || !string.IsNullOrEmpty(value));
    }

    /// <summary>
    /// <para>右侧副文本：大小或 ASCII 串。</para>
    /// Right secondary text: size or ASCII.
    /// </summary>
    public string RightSub { get; }

    /// <summary>
    /// <para>右侧是否差异高亮。</para>
    /// Whether the right side is diff-highlighted.
    /// </summary>
    public bool IsRightDiff
    {
        get => _isRightDiff;
        private set => SetProperty(ref _isRightDiff, value);
    }

    /// <summary>
    /// <para>单侧条目是否按侧着色（目录树行）。</para>
    /// Whether a single-side entry tints its own side (tree rows).
    /// </summary>
    internal bool TintSingleSide { get; }

    /// <summary>
    /// <para>仅左侧内容的左侧淡色背景画刷；其他情况为 <c>null</c>。</para>
    /// Left tint brush for left-only content; <c>null</c> otherwise.
    /// </summary>
    public IBrush? LeftTintBrush => TintSingleSide && Status == FolderItemStatus.LeftOnly ? LeftOnlyTintBrush : null;

    /// <summary>
    /// <para>仅右侧内容的右侧淡色背景画刷；其他情况为 <c>null</c>。</para>
    /// Right tint brush for right-only content; <c>null</c> otherwise.
    /// </summary>
    public IBrush? RightTintBrush => TintSingleSide && Status == FolderItemStatus.RightOnly ? RightOnlyTintBrush : null;

    /// <summary>
    /// <para>状态色块画刷（相同=绿、不同=印度红、仅左=橙、仅右=道奇蓝，其余灰）。</para>
    /// Status colour-block brush (equal=green, different=indian red, left-only=orange, right-only=dodger blue,
    /// anything else gray).
    /// </summary>
    public IBrush StatusBrush => CompareStatusBrushes.For(Status);

    /// <summary>
    /// <para>树行对应的节点；文本行与十六进制行为 <c>null</c>。</para>
    /// The node behind a tree row; <c>null</c> for text and hex rows.
    /// </summary>
    public CompareTreeNodeViewModel? TreeNode { get; }

    /// <summary>
    /// <para>是否为目录树行（树行渲染图标与名称，文本行渲染行号与文本）。</para>
    /// Whether this is a tree row (tree rows render a glyph and name, text rows render a line number and text).
    /// </summary>
    public bool IsTreeRow => TreeNode is not null;

    /// <summary>
    /// <para>该行是否可在文本对比中编辑。</para>
    /// Whether the row can be edited in a text comparison.
    /// </summary>
    public bool IsEditable { get; }

    /// <summary>
    /// <para>该行提供的右键菜单类型。</para>
    /// The context menu this row offers.
    /// </summary>
    public CompareRowMenuKind Menu { get; }

    /// <summary>
    /// <para>该行是否属于对比结果（对比中单侧缺失的行用斜杠占位，让同行对应关系一眼可见）。</para>
    /// Whether the row belongs to a comparison (a side missing the row shows a slash placeholder so the one-row-per-entry
    /// alignment stays visible).
    /// </summary>
    public bool IsComparedRow { get; }

    /// <summary>
    /// <para>左侧是否需要斜杠占位（对比中该侧没有这一行）。</para>
    /// Whether the left side needs a slash placeholder (the comparison has no such row on that side).
    /// </summary>
    public bool ShowLeftFiller => IsComparedRow && !IsEditable && !HasLeftLine;

    /// <summary>
    /// <para>右侧是否需要斜杠占位。</para>
    /// Whether the right side needs a slash placeholder.
    /// </summary>
    public bool ShowRightFiller => IsComparedRow && !IsEditable && !HasRightLine;

    /// <summary>
    /// <para>可编辑行在对比中缺失该侧时显示的占位符（编辑框的 PlaceholderText）。</para>
    /// The placeholder shown by an editable row when the comparison has no such line on that side (the editor's
    /// placeholder text).
    /// </summary>
    public string? ComparedFiller => IsComparedRow && IsEditable ? "/" : null;

    /// <summary>是否显示条目右键菜单（浏览或对比状态下的文件/文件夹行）。 Whether the entry context menu is shown.</summary>
    public bool ShowEntryMenu => Menu is CompareRowMenuKind.BrowsingEntry or CompareRowMenuKind.ComparedEntry;

    /// <summary>浏览状态下是否显示「复制/移动到左边/右边」四项。 Whether the browsing copy/move items are shown.</summary>
    public bool ShowBrowsingTransfers => Menu == CompareRowMenuKind.BrowsingEntry;

    /// <summary>对比状态下是否显示「复制/移动到另一边」。 Whether the compared copy/move items are shown.</summary>
    public bool ShowComparedTransfers => Menu == CompareRowMenuKind.ComparedEntry;

    /// <summary>浏览状态下的目录行是否显示「进入此文件夹」。 Whether the "enter this folder" item is shown.</summary>
    public bool ShowBrowsingEntryFolder => Menu == CompareRowMenuKind.BrowsingEntry && TreeNode is { IsDirectory: true };

    /// <summary>文本对比中是否显示「复制到另一侧」。 Whether the text "copy to the other side" item is shown.</summary>
    public bool ShowTextTransfer => Menu == CompareRowMenuKind.ComparedText;

    /// <summary>
    /// <para>左侧是否存在该行内容（区分「空行」与「该侧没有这一行」）。</para>
    /// Whether the left side has this row's content (distinguishing an empty line from a missing line).
    /// </summary>
    public bool HasLeftLine
    {
        get => _hasLeftLine;
        private set
        {
            if (SetProperty(ref _hasLeftLine, value))
            {
                OnPropertyChanged(nameof(IsLeftEmpty));
                OnPropertyChanged(nameof(ShowLeftText));
            }
        }
    }

    /// <summary>
    /// <para>右侧是否存在该行内容。</para>
    /// Whether the right side has this row's content.
    /// </summary>
    public bool HasRightLine
    {
        get => _hasRightLine;
        private set
        {
            if (SetProperty(ref _hasRightLine, value))
            {
                OnPropertyChanged(nameof(IsRightEmpty));
                OnPropertyChanged(nameof(ShowRightText));
            }
        }
    }

    /// <summary>
    /// <para>左侧是否为空行（该侧没有这一行）。</para>
    /// Whether the left side lacks this row.
    /// </summary>
    public bool IsLeftEmpty => !HasLeftLine;

    /// <summary>
    /// <para>右侧是否为空行（该侧没有这一行）。</para>
    /// Whether the right side lacks this row.
    /// </summary>
    public bool IsRightEmpty => !HasRightLine;

    /// <summary>
    /// <para>是否显示左侧只读文本（非可编辑行且该侧存在内容）。</para>
    /// Whether the left read-only text is shown (a non-editable row whose side has content).
    /// </summary>
    public bool ShowLeftText => !IsEditable && HasLeftLine;

    /// <summary>
    /// <para>是否显示右侧只读文本。</para>
    /// Whether the right read-only text is shown.
    /// </summary>
    public bool ShowRightText => !IsEditable && HasRightLine;

    /// <summary>
    /// <para>是否显示左侧行号/偏移列：只有真正带行号的行（文本行、十六进制行）才显示——目录树行没有行号，
    /// 不能白占一列宽度把图标和文件名顶开。</para>
    /// Whether the left line-number column is shown: only rows that really carry one (text and hex rows). A directory-tree row has no
    /// line number and must not reserve a column that pushes the icon and the file name apart.
    /// </summary>
    public bool ShowLeftLineNumber => !IsTreeRow && LeftLine.Length > 0;

    /// <summary>是否显示右侧行号/偏移列（同左侧规则）。 Whether the right line-number column is shown (same rule).</summary>
    public bool ShowRightLineNumber => !IsTreeRow && RightLine.Length > 0;

    /// <summary>
    /// <para>是否显示左侧图标（目录树行且左侧有内容）。</para>
    /// Whether the left glyph is shown (a tree row with content on the left).
    /// </summary>
    public bool ShowLeftIcon => LeftIcon is not null && !IsLeftEmpty;

    /// <summary>
    /// <para>是否显示右侧图标。</para>
    /// Whether the right glyph is shown.
    /// </summary>
    public bool ShowRightIcon => RightIcon is not null && !IsRightEmpty;

    /// <summary>左侧图标；非树行或该侧无内容时取通用文件图标（不会显示）。 The left glyph (never shown for non-tree rows).</summary>
    public MaterialIconKind LeftIconKind => LeftIcon?.Kind ?? MaterialIconKind.FileOutline;

    /// <summary>左侧图标颜色。 The colour of the left glyph.</summary>
    public IBrush? LeftIconBrush => LeftIcon?.Brush;

    /// <summary>右侧图标；非树行或该侧无内容时取通用文件图标（不会显示）。 The right glyph (never shown for non-tree rows).</summary>
    public MaterialIconKind RightIconKind => RightIcon?.Kind ?? MaterialIconKind.FileOutline;

    /// <summary>右侧图标颜色。 The colour of the right glyph.</summary>
    public IBrush? RightIconBrush => RightIcon?.Brush;

    private ExplorerIcon? LeftIcon { get; }

    private ExplorerIcon? RightIcon { get; }

    /// <summary>
    /// <para>把一侧文本写入行模型（文本编辑与「复制到另一侧」共用）：更新该侧文本与存在性、重算整行差异状态，
    /// 并通知文档标记该侧文件已修改。</para>
    /// Writes one side's text into the row model (shared by text editing and "copy to the other side"): updates the
    /// text and presence, recomputes the whole-row diff state and tells the document that side's file changed.
    /// </summary>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side is written.</param>
    /// <param name="text"><para>新的文本。</para>The new text.</param>
    /// <param name="present"><para>该侧是否保留这一行（false 表示这一行在该侧不存在，例如被复制删掉的行）。</para>Whether that side keeps this line (false means the line is absent on that side).</param>
    internal void SetSideText(bool leftSide, string text, bool present)
    {
        var currentText = leftSide ? _leftText : _rightText;
        var currentPresent = leftSide ? _hasLeftLine : _hasRightLine;
        if (string.Equals(currentText, text, StringComparison.Ordinal) && currentPresent == present)
        {
            return;
        }

        if (leftSide)
        {
            _leftText = text;
            HasLeftLine = present;
            OnPropertyChanged(nameof(LeftText));
        }
        else
        {
            _rightText = text;
            HasRightLine = present;
            OnPropertyChanged(nameof(RightText));
        }

        RefreshDiffState();
        _edited?.Invoke(this, leftSide);
    }

    /// <summary>
    /// <para>按当前两侧文本重算整行差异状态（仅可编辑文本行）：两侧都有内容时相同/不同，只有一侧时按单侧，
    /// 「不同」时两侧整行高亮——与 DiffPlex 的行差异语义一致，因此编辑后颜色即时更新。</para>
    /// Recomputes the whole-row diff state from both sides' current text (editable text rows only): equal/different when
    /// both sides have content, single-side when only one does, and both sides highlighted when they differ — matching
    /// DiffPlex's row-diff semantics so the colours update live while editing.
    /// </summary>
    internal void RefreshDiffState()
    {
        if (!IsEditable)
        {
            return;
        }

        var equal = HasLeftLine && HasRightLine && string.Equals(_leftText, _rightText, StringComparison.Ordinal);
        Status = !HasLeftLine && !HasRightLine
            ? FolderItemStatus.Equal
            : HasLeftLine && HasRightLine
                ? equal ? FolderItemStatus.Equal : FolderItemStatus.Different
                : HasLeftLine ? FolderItemStatus.LeftOnly : FolderItemStatus.RightOnly;
        IsLeftDiff = HasLeftLine && !equal;
        IsRightDiff = HasRightLine && !equal;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(LeftTintBrush));
        OnPropertyChanged(nameof(RightTintBrush));
    }

    /// <summary>
    /// <para>按本行的属性创建一个可编辑的兄弟行（断行/并行等结构编辑新增行用），继承右键菜单类型与「已修改」
    /// 回调，因此无论浏览还是对比都能把改动记到正确的文件上。</para>
    /// Creates an editable sibling row with this row's attributes (used when a structural edit inserts a row), inheriting the
    /// context-menu kind and the "edited" callback so the change is always recorded against the right file, whether
    /// browsing or comparing.
    /// </summary>
    /// <param name="leftText"><para>左侧文本。</para>The left text.</param>
    /// <param name="rightText"><para>右侧文本。</para>The right text.</param>
    /// <param name="hasLeft"><para>左侧是否有这一行。</para>Whether the left side has the row.</param>
    /// <param name="hasRight"><para>右侧是否有这一行。</para>Whether the right side has the row.</param>
    internal CompareRowViewModel CreateEditableSibling(string leftText, string rightText, bool hasLeft, bool hasRight)
        => new(
            FolderItemStatus.Equal,
            depth: 0,
            leftLine: string.Empty,
            leftText: leftText,
            leftSub: string.Empty,
            isLeftDiff: false,
            rightLine: string.Empty,
            rightText: rightText,
            rightSub: string.Empty,
            isRightDiff: false,
            editable: true,
            menu: Menu,
            compared: IsComparedRow,
            hasLeftLine: hasLeft,
            hasRightLine: hasRight,
            edited: _edited);

    /// <summary>通知「该侧被编辑」（结构编辑由列表助手调用，用于标记文件已修改）。</summary>
    internal void NotifyEdited(bool leftSide) => _edited?.Invoke(this, leftSide);

    /// <summary>
    /// <para>从文本差异行构造对比行：Equal→相同、Added→仅右、Deleted→仅左、Modified→不同；行号 1-based 转文本，
    /// 缺失侧行号与文本保持为空。「有这一行」由行号是否大于 0 决定，因此空行与缺失行可以区分。</para>
    /// Builds a compare row from a text-diff line: Equal→Equal, Added→RightOnly, Deleted→LeftOnly, Modified→Different;
    /// 1-based line numbers become text and the missing side keeps empty line numbers and text. Whether a side "has" the
    /// row comes from its line number being greater than zero, so an empty line is distinguishable from a missing line.
    /// </summary>
    /// <param name="line">文本差异行。<para>Text diff line.</para></param>
    /// <param name="edited">文本被编辑时的回调（供文档标记「已修改」）。<para>Callback invoked when the text is edited.</para></param>
    /// <returns><para>渲染该行所需的对比行。</para> The compare row that renders the line.</returns>
    public static CompareRowViewModel ForText(TextDiffLine line, Action<CompareRowViewModel, bool>? edited = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        var status = line.Kind switch
        {
            DiffLineKind.Equal => FolderItemStatus.Equal,
            DiffLineKind.Added => FolderItemStatus.RightOnly,
            DiffLineKind.Deleted => FolderItemStatus.LeftOnly,
            DiffLineKind.Modified => FolderItemStatus.Different,
            _ => FolderItemStatus.Equal,
        };

        return new CompareRowViewModel(
            status,
            depth: 0,
            leftLine: line.LeftLine > 0 ? line.LeftLine.ToString() : string.Empty,
            leftText: line.LeftText,
            leftSub: string.Empty,
            isLeftDiff: line.Kind is DiffLineKind.Deleted or DiffLineKind.Modified,
            rightLine: line.RightLine > 0 ? line.RightLine.ToString() : string.Empty,
            rightText: line.RightText,
            rightSub: string.Empty,
            isRightDiff: line.Kind is DiffLineKind.Added or DiffLineKind.Modified,
            editable: true,
            menu: CompareRowMenuKind.ComparedText,
            compared: true,
            hasLeftLine: line.LeftLine > 0,
            hasRightLine: line.RightLine > 0,
            edited: edited);
    }
}
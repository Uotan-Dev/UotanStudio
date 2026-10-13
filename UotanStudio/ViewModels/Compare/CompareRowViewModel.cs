using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using UotanStudio.Services.Compare;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// Row model that pairs one entry across both sides of a comparison, covering text, tree, and hex rows in one list.
/// <para>把同一条目在左右两侧对齐的行模型，用一份列表覆盖文本行、目录树行与十六进制行。</para>
/// </summary>
public sealed class CompareRowViewModel : ObservableObject
{
    /// <summary>
    /// Tint brush for content that exists on the left only (orange, translucent so the theme shows through).
    /// Static shared brushes use immutable types to avoid dispatcher thread affinity.
    /// <para>仅左侧内容的淡色背景画刷（橙色调，半透明叠加可辨识）。静态共享画刷使用不可变类型以避免线程亲和性。</para>
    /// </summary>
    internal static readonly IBrush LeftOnlyTintBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0xE6, 0x7E, 0x22));

    /// <summary>
    /// Tint brush for content that exists on the right only (blue, translucent so the theme shows through).
    /// Static shared brushes use immutable types to avoid dispatcher thread affinity.
    /// <para>仅右侧内容的淡色背景画刷（蓝色调，半透明叠加可辨识）。静态共享画刷使用不可变类型以避免线程亲和性。</para>
    /// </summary>
    internal static readonly IBrush RightOnlyTintBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x90, 0xFF));

    private readonly Action<CompareRowViewModel, bool>? _edited;
    private string _leftText;
    private string _rightText;

    /// <summary>
    /// Creates a row model pairing one entry across both sides, with optional tree, icon, editable, menu, and callback inputs.
    /// <para>创建一条把同一条目在左右两侧对齐的行模型，可选参数涵盖目录树、图标、可编辑标记、菜单与编辑回调。</para>
    /// </summary>
    /// <param name="status">Row status.<para>行状态。</para></param>
    /// <param name="depth">Indentation depth (0-based).<para>缩进深度，从 0 起。</para></param>
    /// <param name="leftLine">Left line number or hex offset text.<para>左侧行号或十六进制偏移文本。</para></param>
    /// <param name="leftText">Left primary text: line, name, or hex bytes.<para>左侧主文本：行内容、名称或十六进制字节串。</para></param>
    /// <param name="leftSub">Left secondary text: size or ASCII.<para>左侧副文本：大小或 ASCII 串。</para></param>
    /// <param name="isLeftDiff">Whether the left side is diff-highlighted.<para>左侧是否差异高亮。</para></param>
    /// <param name="rightLine">Right line number or hex offset text.<para>右侧行号或十六进制偏移文本。</para></param>
    /// <param name="rightText">Right primary text.<para>右侧主文本。</para></param>
    /// <param name="rightSub">Right secondary text.<para>右侧副文本。</para></param>
    /// <param name="isRightDiff">Whether the right side is diff-highlighted.<para>右侧是否差异高亮。</para></param>
    /// <param name="tintSingleSide">Whether a single-side entry tints its own side.<para>单侧条目是否按侧着色。</para></param>
    /// <param name="treeNode">The node behind a tree row; <c>null</c> for text and hex rows.<para>树行对应的节点；文本行与十六进制行为 <c>null</c>。</para></param>
    /// <param name="leftIcon">The left glyph and colour, or <c>null</c>.<para>左侧图标与配色，或为 <c>null</c>。</para></param>
    /// <param name="rightIcon">The right glyph and colour, or <c>null</c>.<para>右侧图标与配色，或为 <c>null</c>。</para></param>
    /// <param name="editable">Whether the row can be edited in a text comparison.<para>该行在文本对比中是否可编辑。</para></param>
    /// <param name="menu">The context-menu kind this row offers.<para>该行提供的右键菜单类型。</para></param>
    /// <param name="compared">Whether the row belongs to a comparison result.<para>该行是否属于对比结果。</para></param>
    /// <param name="hasLeftLine">Whether the left side has this row; inferred from text when <c>null</c>.<para>左侧是否存在该行；<c>null</c> 时按文本推断。</para></param>
    /// <param name="hasRightLine">Whether the right side has this row; inferred from text when <c>null</c>.<para>右侧是否存在该行；<c>null</c> 时按文本推断。</para></param>
    /// <param name="edited">Callback invoked when the row's text is edited.<para>行文本被编辑时触发的回调。</para></param>
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
        IsLeftDiff = isLeftDiff;
        RightLine = rightLine;
        _rightText = rightText;
        RightSub = rightSub;
        IsRightDiff = isRightDiff;
        TintSingleSide = tintSingleSide;
        TreeNode = treeNode;
        LeftIcon = leftIcon;
        RightIcon = rightIcon;
        IsEditable = editable;
        Menu = menu;
        IsComparedRow = compared;
        HasLeftLine = hasLeftLine ?? !string.IsNullOrEmpty(leftText);
        HasRightLine = hasRightLine ?? !string.IsNullOrEmpty(rightText);
        _edited = edited;
    }

    /// <summary>
    /// The row's comparison status.
    /// <para>本行的对比状态。</para>
    /// </summary>
    /// <value>Equal, different, left-only, or right-only; recomputed by <see cref="RefreshDiffState"/> after text edits.<para>相同、不同、仅左或仅右；文本行编辑后由 <see cref="RefreshDiffState"/> 重算。</para></value>
    public FolderItemStatus Status { get; private set; }

    /// <summary>
    /// Indentation depth of the row.
    /// <para>本行的缩进深度。</para>
    /// </summary>
    /// <value>The 0-based indentation level.<para>从 0 起的缩进层级。</para></value>
    public int Depth { get; }

    /// <summary>
    /// Left line-number or offset label.
    /// <para>左侧行号或偏移标签。</para>
    /// </summary>
    /// <value>Left line number or hex offset text; empty when the side is absent.<para>左侧行号或十六进制偏移文本；缺失侧为空串。</para></value>
    public string LeftLine { get; }

    /// <summary>
    /// Left primary text of the row.
    /// <para>本行的左侧主文本。</para>
    /// </summary>
    /// <value>The left line content, tree name, or hex bytes; writing recomputes the row diff state.<para>左侧行内容、目录树名称或十六进制字节串；写入时重算整行差异状态。</para></value>
    public string LeftText
    {
        get => _leftText;
        set => SetSideText(leftSide: true, value, present: HasLeftLine || !string.IsNullOrEmpty(value));
    }

    /// <summary>
    /// Left secondary text of the row.
    /// <para>本行的左侧副文本。</para>
    /// </summary>
    /// <value>Size or ASCII string shown under the left primary text.<para>显示在左侧主文本下方的大小或 ASCII 串。</para></value>
    public string LeftSub { get; }

    /// <summary>
    /// Whether the left side carries a diff highlight.
    /// <para>左侧是否处于差异高亮状态。</para>
    /// </summary>
    /// <value><c>true</c> when the whole-row colouring marks the left side as differing.<para>整行变色把左侧标记为差异时为 <c>true</c>。</para></value>
    public bool IsLeftDiff
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Right line-number or offset label.
    /// <para>右侧行号或偏移标签。</para>
    /// </summary>
    /// <value>Right line number or hex offset text; empty when the side is absent.<para>右侧行号或十六进制偏移文本；缺失侧为空串。</para></value>
    public string RightLine { get; }

    /// <summary>
    /// Right primary text of the row.
    /// <para>本行的右侧主文本。</para>
    /// </summary>
    /// <value>The right line content, tree name, or hex bytes; editable in a text comparison.<para>右侧行内容、目录树名称或十六进制字节串；文本对比中可编辑。</para></value>
    public string RightText
    {
        get => _rightText;
        set => SetSideText(leftSide: false, value, present: HasRightLine || !string.IsNullOrEmpty(value));
    }

    /// <summary>
    /// Right secondary text of the row.
    /// <para>本行的右侧副文本。</para>
    /// </summary>
    /// <value>Size or ASCII string shown under the right primary text.<para>显示在右侧主文本下方的大小或 ASCII 串。</para></value>
    public string RightSub { get; }

    /// <summary>
    /// Whether the right side carries a diff highlight.
    /// <para>右侧是否处于差异高亮状态。</para>
    /// </summary>
    /// <value><c>true</c> when the whole-row colouring marks the right side as differing.<para>整行变色把右侧标记为差异时为 <c>true</c>。</para></value>
    public bool IsRightDiff
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Whether single-side entries tint their own side.
    /// <para>单侧条目是否按侧着色。</para>
    /// </summary>
    /// <value><c>true</c> for rows whose side-specific tint is applied (tree rows).<para>应用按侧淡色背景的目录树行为 <c>true</c>。</para></value>
    internal bool TintSingleSide { get; }

    /// <summary>
    /// Left background tint for left-only content.
    /// <para>仅左侧内容的左侧背景色。</para>
    /// </summary>
    /// <value>The left tint brush when the row is left-only, or <c>null</c> otherwise.<para>本行为仅左时返回左侧淡色画刷，其他情况为 <c>null</c>。</para></value>
    public IBrush? LeftTintBrush => TintSingleSide && Status == FolderItemStatus.LeftOnly ? LeftOnlyTintBrush : null;

    /// <summary>
    /// Right background tint for right-only content.
    /// <para>仅右侧内容的右侧背景色。</para>
    /// </summary>
    /// <value>The right tint brush when the row is right-only, or <c>null</c> otherwise.<para>本行为仅右时返回右侧淡色画刷，其他情况为 <c>null</c>。</para></value>
    public IBrush? RightTintBrush => TintSingleSide && Status == FolderItemStatus.RightOnly ? RightOnlyTintBrush : null;

    /// <summary>
    /// Colour block reflecting the row's status.
    /// <para>反映本行对比状态的颜色块。</para>
    /// </summary>
    /// <value>The brush for the current status.<para>当前状态对应的画刷。</para></value>
    public IBrush StatusBrush => CompareStatusBrushes.For(Status);

    /// <summary>
    /// Tree node carried by this row.
    /// <para>本行携带的目录树节点。</para>
    /// </summary>
    /// <value>The node behind a tree row; <c>null</c> for text and hex rows.<para>树行对应的节点；文本行与十六进制行为 <c>null</c>。</para></value>
    public CompareTreeNodeViewModel? TreeNode { get; }

    /// <summary>
    /// Whether the row renders as a tree entry.
    /// <para>本行是否以目录树条目呈现。</para>
    /// </summary>
    /// <value><c>true</c> when the row has a tree node and shows glyph and name instead of line number and text.<para>本行持有树节点、渲染图标与名称而非行号与文本时为 <c>true</c>。</para></value>
    public bool IsTreeRow => TreeNode is not null;

    /// <summary>
    /// Whether the row is editable in a text comparison.
    /// <para>本行在文本对比中是否可编辑。</para>
    /// </summary>
    /// <value><c>true</c> when both sides accept text edits for this row.<para>本行两侧都接受文本编辑时为 <c>true</c>。</para></value>
    public bool IsEditable { get; }

    /// <summary>
    /// Context-menu kind offered by the row.
    /// <para>本行提供的右键菜单类型。</para>
    /// </summary>
    /// <value>The <see cref="CompareRowMenuKind"/> this row selects into.<para>本行所对应的 <see cref="CompareRowMenuKind"/>。</para></value>
    public CompareRowMenuKind Menu { get; }

    /// <summary>
    /// Whether the row belongs to a comparison result.
    /// <para>本行是否属于对比结果。</para>
    /// </summary>
    /// <value><c>true</c> when a side missing this row shows a slash placeholder to keep entries aligned.<para>某一侧缺失时该侧显示斜杠占位以维持同行对齐时为 <c>true</c>。</para></value>
    public bool IsComparedRow { get; }

    /// <summary>
    /// Whether the left side needs a slash placeholder.
    /// <para>左侧是否需要斜杠占位。</para>
    /// </summary>
    /// <value><c>true</c> when the comparison has no such row on the left.<para>对比中左侧没有该行时为 <c>true</c>。</para></value>
    public bool ShowLeftFiller => IsComparedRow && !IsEditable && !HasLeftLine;

    /// <summary>
    /// Whether the right side needs a slash placeholder.
    /// <para>右侧是否需要斜杠占位。</para>
    /// </summary>
    /// <value><c>true</c> when the comparison has no such row on the right.<para>对比中右侧没有该行时为 <c>true</c>。</para></value>
    public bool ShowRightFiller => IsComparedRow && !IsEditable && !HasRightLine;

    /// <summary>
    /// Placeholder text for an editable row missing a side.
    /// <para>可编辑行缺失一侧时的占位文本。</para>
    /// </summary>
    /// <value>"/" for a compared editable row whose side has no line; <c>null</c> otherwise.<para>对比中可编辑行在某侧没有对应行时为 "/"，其他情况为 <c>null</c>。</para></value>
    public string? ComparedFiller => IsComparedRow && IsEditable ? "/" : null;

    /// <summary>Whether the entry context menu is shown.<para>是否显示条目右键菜单。</para></summary>
    /// <value><c>true</c> for a file or folder row in browsing or compared state.<para>处于浏览或对比状态的文件/文件夹行为 <c>true</c>。</para></value>
    public bool ShowEntryMenu => Menu is CompareRowMenuKind.BrowsingEntry or CompareRowMenuKind.ComparedEntry;

    /// <summary>Whether the browsing copy/move items are shown.<para>浏览状态下是否显示复制/移动到左边或右边四项。</para></summary>
    /// <value><c>true</c> when the row's menu kind is the browsing entry kind.<para>本行菜单类型为浏览条目时为 <c>true</c>。</para></value>
    public bool ShowBrowsingTransfers => Menu == CompareRowMenuKind.BrowsingEntry;

    /// <summary>Whether the compared copy/move items are shown.<para>对比状态下是否显示复制/移动到另一边。</para></summary>
    /// <value><c>true</c> when the row's menu kind is the compared entry kind.<para>本行菜单类型为对比条目时为 <c>true</c>。</para></value>
    public bool ShowComparedTransfers => Menu == CompareRowMenuKind.ComparedEntry;

    /// <summary>Whether the enter-this-folder item is shown.<para>是否显示「进入此文件夹」项。</para></summary>
    /// <value><c>true</c> for a directory row inside the browsing entry menu.<para>浏览条目菜单下的目录行为 <c>true</c>。</para></value>
    public bool ShowBrowsingEntryFolder => Menu == CompareRowMenuKind.BrowsingEntry && TreeNode is { IsDirectory: true };

    /// <summary>Whether the text copy-to-other-side item is shown.<para>是否显示文本对比的「复制到另一侧」项。</para></summary>
    /// <value><c>true</c> when the row's menu kind is the compared text kind.<para>本行菜单类型为对比文本时为 <c>true</c>。</para></value>
    public bool ShowTextTransfer => Menu == CompareRowMenuKind.ComparedText;

    /// <summary>
    /// Whether the left side has this row's content.
    /// <para>左侧是否存在本行内容。</para>
    /// </summary>
    /// <value><c>true</c> when the left side has a line here, distinguishing an empty line from a missing one.<para>左侧在此处有行内容时为 <c>true</c>，用于区分空行与缺失行。</para></value>
    public bool HasLeftLine
    {
        get => field;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsLeftEmpty));
                OnPropertyChanged(nameof(ShowLeftText));
            }
        }
    }

    /// <summary>
    /// Whether the right side has this row's content.
    /// <para>右侧是否存在本行内容。</para>
    /// </summary>
    /// <value><c>true</c> when the right side has a line here, distinguishing an empty line from a missing one.<para>右侧在此处有行内容时为 <c>true</c>，用于区分空行与缺失行。</para></value>
    public bool HasRightLine
    {
        get => field;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsRightEmpty));
                OnPropertyChanged(nameof(ShowRightText));
            }
        }
    }

    /// <summary>
    /// Whether the left side lacks this row.
    /// <para>左侧是否缺少本行。</para>
    /// </summary>
    /// <value><c>true</c> when the left side has no line here.<para>左侧在此处没有行内容时为 <c>true</c>。</para></value>
    public bool IsLeftEmpty => !HasLeftLine;

    /// <summary>
    /// Whether the right side lacks this row.
    /// <para>右侧是否缺少本行。</para>
    /// </summary>
    /// <value><c>true</c> when the right side has no line here.<para>右侧在此处没有行内容时为 <c>true</c>。</para></value>
    public bool IsRightEmpty => !HasRightLine;

    /// <summary>
    /// Whether the left read-only text is shown.
    /// <para>是否显示左侧只读文本。</para>
    /// </summary>
    /// <value><c>true</c> for a non-editable row whose left side has content.<para>非可编辑行且左侧存在内容时为 <c>true</c>。</para></value>
    public bool ShowLeftText => !IsEditable && HasLeftLine;

    /// <summary>
    /// Whether the right read-only text is shown.
    /// <para>是否显示右侧只读文本。</para>
    /// </summary>
    /// <value><c>true</c> for a non-editable row whose right side has content.<para>非可编辑行且右侧存在内容时为 <c>true</c>。</para></value>
    public bool ShowRightText => !IsEditable && HasRightLine;

    /// <summary>
    /// Whether the left line-number column is shown.
    /// <para>是否显示左侧行号或偏移列。</para>
    /// </summary>
    /// <value><c>true</c> only for text and hex rows carrying a line number.<para>只有携带行号的文本行与十六进制行为 <c>true</c>。</para></value>
    public bool ShowLeftLineNumber => !IsTreeRow && LeftLine.Length > 0;

    /// <summary>Whether the right line-number column is shown.<para>是否显示右侧行号或偏移列。</para></summary>
    /// <value><c>true</c> only for text and hex rows carrying a line number.<para>只有携带行号的文本行与十六进制行为 <c>true</c>。</para></value>
    public bool ShowRightLineNumber => !IsTreeRow && RightLine.Length > 0;

    /// <summary>
    /// Whether the left glyph is shown.
    /// <para>是否显示左侧图标。</para>
    /// </summary>
    /// <value><c>true</c> for a tree row whose left side has content.<para>目录树行且左侧有内容时为 <c>true</c>。</para></value>
    public bool ShowLeftIcon => LeftIcon is not null && !IsLeftEmpty;

    /// <summary>
    /// Whether the right glyph is shown.
    /// <para>是否显示右侧图标。</para>
    /// </summary>
    /// <value><c>true</c> for a tree row whose right side has content.<para>目录树行且右侧有内容时为 <c>true</c>。</para></value>
    public bool ShowRightIcon => RightIcon is not null && !IsRightEmpty;

    /// <summary>The kind of the left glyph.<para>左侧图标的类型。</para></summary>
    /// <value>The tree-row icon, or a generic file icon when no glyph is rendered.<para>目录树行的图标；不渲染图标时回退为通用文件图标。</para></value>
    public MaterialIconKind LeftIconKind => LeftIcon?.Kind ?? MaterialIconKind.FileOutline;

    /// <summary>The colour of the left glyph.<para>左侧图标的颜色。</para></summary>
    /// <value>The brush paired with the left icon, or <c>null</c>.<para>左侧图标对应的画刷，或为 <c>null</c>。</para></value>
    public IBrush? LeftIconBrush => LeftIcon?.Brush;

    /// <summary>The kind of the right glyph.<para>右侧图标的类型。</para></summary>
    /// <value>The tree-row icon, or a generic file icon when no glyph is rendered.<para>目录树行的图标；不渲染图标时回退为通用文件图标。</para></value>
    public MaterialIconKind RightIconKind => RightIcon?.Kind ?? MaterialIconKind.FileOutline;

    /// <summary>The colour of the right glyph.<para>右侧图标的颜色。</para></summary>
    /// <value>The brush paired with the right icon, or <c>null</c>.<para>右侧图标对应的画刷，或为 <c>null</c>。</para></value>
    public IBrush? RightIconBrush => RightIcon?.Brush;

    private ExplorerIcon? LeftIcon { get; }

    private ExplorerIcon? RightIcon { get; }

    /// <summary>
    /// Writes one side's text into the row, updates its presence, recomputes the whole-row diff state, and signals the edit.
    /// <para>写入一侧的文本、更新其存在性、重算整行差异状态并发出编辑信号。</para>
    /// </summary>
    /// <param name="leftSide">Whether the left side is written.<para>是否为左侧。</para></param>
    /// <param name="text">The new text.<para>新的文本。</para></param>
    /// <param name="present">Whether that side keeps this line.<para>该侧是否保留这一行。</para></param>
    internal void SetSideText(bool leftSide, string text, bool present)
    {
        var currentText = leftSide ? _leftText : _rightText;
        var currentPresent = leftSide ? HasLeftLine : HasRightLine;
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
    /// Recomputes the whole-row diff state from both sides' current text, applying it to editable text rows only.
    /// <para>根据两侧当前文本重算整行差异状态，仅作用于可编辑的文本行。</para>
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
    /// Creates an editable sibling row inheriting this row's context-menu kind and edited callback.
    /// <para>创建一个可编辑的兄弟行，沿用本行的右键菜单类型与编辑回调。</para>
    /// </summary>
    /// <param name="leftText">The left text.<para>左侧文本。</para></param>
    /// <param name="rightText">The right text.<para>右侧文本。</para></param>
    /// <param name="hasLeft">Whether the left side has the row.<para>左侧是否有这一行。</para></param>
    /// <param name="hasRight">Whether the right side has the row.<para>右侧是否有这一行。</para></param>
    /// <returns>A new editable row carrying the same menu kind and edit callback.<para>新的可编辑行，携带相同的菜单类型与编辑回调。</para></returns>
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

    /// <summary>
    /// Signals that the given side's text was edited.
    /// <para>通知指定一侧的文本已被编辑。</para>
    /// </summary>
    /// <param name="leftSide">Whether the left side was edited.<para>是否为左侧被编辑。</para></param>
    internal void NotifyEdited(bool leftSide) => _edited?.Invoke(this, leftSide);

    /// <summary>
    /// Builds a compare row from a text-diff line, mapping each diff kind to the corresponding row status.
    /// <para>根据文本差异行构造对比行，把每种差异类型映射为对应的行状态。</para>
    /// </summary>
    /// <param name="line">The text-diff line to render.<para>要渲染的文本差异行。</para></param>
    /// <param name="edited">Optional callback invoked when the row's text is edited.<para>可选，行文本被编辑时触发的回调。</para></param>
    /// <returns>The compare row that renders the line.<para>渲染该差异行所需的对比行。</para></returns>
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
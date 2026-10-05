using UotanStudio.Services.Compare;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.ViewModels.Compare;

/// <summary>Projects comparison data into aligned rows without reading files or owning document state.</summary>
internal static class ComparisonRowBuilder
{
    /// <summary>
    /// <para>把目录树按当前展开状态压平成行列表：展开的节点后面紧跟其子项（缩进 +1）。
    /// 左右两栏共用同一份列表，因此同名条目落在同一行、单侧缺失的行在另一侧自动留空，
    /// 展开状态也天然同步。</para>
    /// Flattens the directory tree into rows by the current expansion state: an expanded node is
    /// immediately followed by its children (depth + 1). Both panes share one list, so same-name entries
    /// land on the same row, single-side rows stay blank on the other side, and expansion is inherently
    /// synchronised.
    /// </summary>
    /// <param name="roots"><para>树根（文件夹对比只有一个根：最初始的文件夹）。</para>The tree roots (a folder comparison has one root: the initial folder).</param>
    /// <param name="compared"><para>是否处于对比状态（决定该行的右键菜单是「另一边」还是「左边/右边」）。</para>Whether the rows belong to a comparison (which decides between the "other side" and "left/right" menu items).</param>
    internal static List<CompareRowViewModel> ForTree(IEnumerable<CompareTreeNodeViewModel> roots, bool compared)
    {
        var rows = new List<CompareRowViewModel>();
        foreach (var root in roots)
        {
            AppendTreeRow(root, 0, rows, compared);
        }

        return rows;
    }

    /// <summary>
    /// <para>由目录树节点构建一行：两侧名称、图标、大小与整行状态色都按侧给出；目录树行渲染展开箭头与
    /// 文件图标，缺失侧留空。</para>
    /// Builds one row from a directory-tree node: both sides' names, glyphs, sizes and whole-row status are
    /// per side; a tree row renders the expander and file glyph, and the missing side stays blank.
    /// </summary>
    private static void AppendTreeRow(
        CompareTreeNodeViewModel node,
        int depth,
        List<CompareRowViewModel> rows,
        bool compared)
    {
        rows.Add(ForTreeNode(node, depth, compared));
        if (!node.IsExpanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            AppendTreeRow(child, depth + 1, rows, compared);
        }
    }

    /// <summary>
    /// <para>由目录树节点构建一行：状态「不同」时整行（两侧）高亮，单侧条目按侧加淡色背景，
    /// 与之前拉平列表的整行变色规则一致。</para>
    /// Builds one row from a directory-tree node: a "different" status highlights the whole row on both
    /// sides and a single-side entry tints its own side, matching the flat list's previous whole-row
    /// colouring.
    /// </summary>
    private static CompareRowViewModel ForTreeNode(CompareTreeNodeViewModel node, int depth, bool compared)
    {
        var status = node.Status;
        var isDifferent = status == FolderItemStatus.Different;
        var leftIcon = node.LeftName.Length == 0
            ? (ExplorerIcon?)null
            : ExplorerIconCatalog.Resolve(node.LeftName, node.IsDirectory);
        var rightIcon = node.RightName.Length == 0
            ? (ExplorerIcon?)null
            : ExplorerIconCatalog.Resolve(node.RightName, node.IsDirectory);

        return new CompareRowViewModel(
            status ?? FolderItemStatus.Equal,
            depth,
            leftLine: string.Empty,
            leftText: node.LeftName,
            leftSub: SizeText(node.LeftSize, node.IsDirectory),
            isLeftDiff: isDifferent,
            rightLine: string.Empty,
            rightText: node.RightName,
            rightSub: SizeText(node.RightSize, node.IsDirectory),
            isRightDiff: isDifferent,
            tintSingleSide: true,
            treeNode: node,
            leftIcon: leftIcon,
            rightIcon: rightIcon,
            menu: compared ? CompareRowMenuKind.ComparedEntry : CompareRowMenuKind.BrowsingEntry,
            compared: compared,
            hasLeftLine: node.LeftName.Length > 0,
            hasRightLine: node.RightName.Length > 0);
    }

    /// <summary>目录或缺失侧的副文本为空，文件给出可读大小。</summary>
    private static string SizeText(long size, bool isDirectory)
        => isDirectory || size < 0 ? string.Empty : FormatSize(size);

    /// <summary>
    /// <para>构建浏览一个文本文件的行：内容落在左侧单元格，行号 1 起，可直接编辑（浏览状态也允许改文本，
    /// 保存后写回文件），没有对比状态色与右键菜单。</para>
    /// Builds the rows for browsing a text file: content in the left cells with one-based line numbers and direct editing
    /// (browsing may edit text too, saved back to the file), without comparison status colours or a context menu.
    /// </summary>
    /// <param name="lines"><para>文件行。</para>The file's lines.</param>
    /// <param name="edited"><para>文本被编辑时的回调（供所属列标记文件已修改）。</para>Callback invoked when the text is edited.</param>
    internal static List<CompareRowViewModel> ForText(
        IReadOnlyList<string> lines,
        Action<CompareRowViewModel, bool>? edited = null) =>
        lines.Select((line, index) => new CompareRowViewModel(
            FolderItemStatus.LeftOnly, 0, (index + 1).ToString(), line, string.Empty, true,
            string.Empty, string.Empty, string.Empty, false,
            editable: true, menu: CompareRowMenuKind.None, compared: false,
            hasLeftLine: true, hasRightLine: false, edited: edited)).ToList();

    /// <summary>
    /// <para>构建单侧文本对比的行：内容只落在有文件的一侧（行号 1 起），另一侧整列留空，
    /// 有文件的一侧带单侧淡色背景，与目录列表的单侧配色一致。</para>
    /// Builds a single-side text comparison's rows: content only on the side that has the file (one-based
    /// line numbers), the other column entirely blank, and the populated side tinted like the single-side
    /// colouring of a folder listing.
    /// </summary>
    /// <param name="lines"><para>文件行。</para>The file's lines.</param>
    /// <param name="leftSide"><para>文件是否在左侧。</para>Whether the file is on the left side.</param>
    /// <param name="edited">文本被编辑时的回调（供文档标记「已修改」）。<para>Callback invoked when the text is edited.</para></param>
    internal static List<CompareRowViewModel> ForTextSingleSide(
        IReadOnlyList<string> lines,
        bool leftSide,
        Action<CompareRowViewModel, bool>? edited = null) =>
        lines.Select((line, index) => leftSide
            ? new CompareRowViewModel(
                FolderItemStatus.LeftOnly, 0, (index + 1).ToString(), line, string.Empty, true,
                string.Empty, string.Empty, string.Empty, false,
                editable: true, menu: CompareRowMenuKind.ComparedText,
                compared: true, hasLeftLine: true, hasRightLine: false, edited: edited)
            : new CompareRowViewModel(
                FolderItemStatus.RightOnly, 0, string.Empty, string.Empty, string.Empty, false,
                (index + 1).ToString(), line, string.Empty, true,
                editable: true, menu: CompareRowMenuKind.ComparedText,
                compared: true, hasLeftLine: false, hasRightLine: true, edited: edited)).ToList();

    /// <summary>
    /// <para>由两侧元数据构建元数据视图行：格式名行、摘要行、键值对行（键并集）、条目行与组件行；
    /// 条目与组件按**名称**对齐（两侧名称并集，同名条目落在同一水平行；仅单侧条目在缺失侧
    /// 置空占位，不显示名称），键值对缺失侧取 "-"；**同名但值不同也判定为差异**——键值对值不同、
    /// 组件大小不同均标记为 Different 并整行差异高亮，仅单侧存在的键/组件标记为 LeftOnly/RightOnly；
    /// 两侧都无法识别时给出单条"无法识别"提示行。</para>
    /// Builds the metadata-view rows from both sides' metadata: a format-name row, summary rows,
    /// key-value rows (key union), entry rows and component rows. Entries and components are aligned
    /// **by name** (union of both sides' names; same-name items land on the same horizontal row, and
    /// single-side items leave the missing side blank), while key-values show "-" on the missing side.
    /// **Same-name items with different values also count as differences**: differing key-value pairs
    /// and differing component sizes are marked Different with a full-row diff highlight, and keys or
    /// components present on only one side are marked LeftOnly/RightOnly; a single "unrecognized" hint
    /// row is produced when neither side is recognized.
    /// </summary>
    internal static List<CompareRowViewModel> ForMetadata(ComparisonMetadata? left, ComparisonMetadata? right, string unrecognized)
    {
        var rows = new List<CompareRowViewModel>();
        var leftRecognized = left is { FormatName.Length: > 0 };
        var rightRecognized = right is { FormatName.Length: > 0 };

        var leftPlaceholder = left is null ? string.Empty : "-";
        var rightPlaceholder = right is null ? string.Empty : "-";

        if (!leftRecognized && !rightRecognized)
        {
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.Equal, 0,
                string.Empty, left is null ? string.Empty : unrecognized, string.Empty, false,
                string.Empty, right is null ? string.Empty : unrecognized, string.Empty, false,
                hasLeftLine: left is not null, hasRightLine: right is not null));
            return rows;
        }

        rows.Add(new CompareRowViewModel(
            FolderItemStatus.Equal, 0,
            string.Empty, leftRecognized ? left!.FormatName : leftPlaceholder, string.Empty, false,
            string.Empty, rightRecognized ? right!.FormatName : rightPlaceholder, string.Empty, false,
            hasLeftLine: left is not null, hasRightLine: right is not null));

        var leftSummary = leftRecognized ? SplitLines(left!.Summary) : [];
        var rightSummary = rightRecognized ? SplitLines(right!.Summary) : [];
        var summaryCount = Math.Max(leftSummary.Count, rightSummary.Count);
        for (var i = 0; i < summaryCount; i++)
        {
            rows.Add(new CompareRowViewModel(
                FolderItemStatus.Equal, 1,
                string.Empty, i < leftSummary.Count ? leftSummary[i] : leftPlaceholder, string.Empty, false,
                string.Empty, i < rightSummary.Count ? rightSummary[i] : rightPlaceholder, string.Empty, false,
                hasLeftLine: left is not null, hasRightLine: right is not null));
        }

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
            var leftValue = leftRecognized && left!.Metadata.TryGetValue(key, out var lv) ? lv : null;
            var rightValue = rightRecognized && right!.Metadata.TryGetValue(key, out var rv) ? rv : null;
            var hasLeft = leftValue is not null;
            var hasRight = rightValue is not null;
            var valueDiffers = hasLeft && hasRight
                && !string.Equals(leftValue, rightValue, StringComparison.Ordinal);

            var status = valueDiffers ? FolderItemStatus.Different
                : hasLeft && hasRight ? FolderItemStatus.Equal
                : hasLeft ? FolderItemStatus.LeftOnly
                : FolderItemStatus.RightOnly;

            rows.Add(new CompareRowViewModel(
                status, 1,
                string.Empty, left is null ? string.Empty : key, hasLeft ? leftValue! : leftPlaceholder, valueDiffers,
                string.Empty, right is null ? string.Empty : key, hasRight ? rightValue! : rightPlaceholder, valueDiffers,
                hasLeftLine: left is not null, hasRightLine: right is not null));
        }

        var leftEntries = leftRecognized ? left!.Entries : [];
        var rightEntries = rightRecognized ? right!.Entries : [];
        var entryNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in leftEntries)
        {
            entryNames.Add(entry);
        }

        foreach (var entry in rightEntries)
        {
            entryNames.Add(entry);
        }

        foreach (var name in entryNames)
        {
            var hasLeft = leftEntries.Contains(name);
            var hasRight = rightEntries.Contains(name);
            rows.Add(new CompareRowViewModel(
                hasLeft && hasRight ? FolderItemStatus.Equal : hasLeft ? FolderItemStatus.LeftOnly : FolderItemStatus.RightOnly,
                1,
                string.Empty, hasLeft ? name : string.Empty, string.Empty, false,
                string.Empty, hasRight ? name : string.Empty, string.Empty, false,
                hasLeftLine: hasLeft, hasRightLine: hasRight));
        }

        var leftComponents = leftRecognized ? left!.Components : [];
        var rightComponents = rightRecognized ? right!.Components : [];
        var componentNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var component in leftComponents)
        {
            componentNames.Add(component.Name);
        }

        foreach (var component in rightComponents)
        {
            componentNames.Add(component.Name);
        }

        foreach (var name in componentNames)
        {
            var leftComponent = leftComponents.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            var rightComponent = rightComponents.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            var hasLeft = leftComponent is not null;
            var hasRight = rightComponent is not null;
            var sizeDiffers = hasLeft && hasRight && leftComponent!.SizeBytes != rightComponent!.SizeBytes;

            var status = sizeDiffers ? FolderItemStatus.Different
                : hasLeft && hasRight ? FolderItemStatus.Equal
                : hasLeft ? FolderItemStatus.LeftOnly
                : FolderItemStatus.RightOnly;

            rows.Add(new CompareRowViewModel(
                status, 1,
                string.Empty, hasLeft ? leftComponent!.Name : string.Empty, hasLeft ? FormatSize(leftComponent!.SizeBytes) : string.Empty, sizeDiffers,
                string.Empty, hasRight ? rightComponent!.Name : string.Empty, hasRight ? FormatSize(rightComponent!.SizeBytes) : string.Empty, sizeDiffers,
                hasLeftLine: hasLeft, hasRightLine: hasRight));
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
    /// <para>将字节数格式化为可读大小文本（如 "1.5 MB"）；0 字节显示 "0 B"。</para>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 MB"); 0 bytes shows "0 B".
    /// </summary>
    private static string FormatSize(long bytes) => SizeFormatter.Format(bytes);
}
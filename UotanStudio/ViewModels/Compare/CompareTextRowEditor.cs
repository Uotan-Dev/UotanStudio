namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>文本行的结构编辑（断行、并行、删行）：浏览列与对比文档共用同一套实现，操作的是各自的行列表，
/// 新增行由被编辑行的 <see cref="CompareRowViewModel.CreateEditableSibling"/> 创建，因此两侧的差异状态重算与
/// 「已修改」标记都沿着同一路径生效。</para>
/// Structural editing of text rows (split, join, delete): browsing columns and the compare document share this single
/// implementation and operate on their own row list. New rows come from the edited row's
/// <see cref="CompareRowViewModel.CreateEditableSibling"/>, so the diff-state recomputation and the "modified" marking stay
/// on one path.
/// </summary>
internal static class CompareTextRowEditor
{
    /// <summary>
    /// <para>在某一行上按光标断成两行：光标前的文本留在原行，光标后的文本成为紧随其后的新行（只存在于被编辑的一侧）。</para>
    /// Splits a row at the caret: the text before it stays and the text after it becomes the following row, which exists only
    /// on the edited side.
    /// </summary>
    /// <param name="rows"><para>行列表。</para>The row list.</param>
    /// <param name="row"><para>被断开的行。</para>The row being split.</param>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side is edited.</param>
    /// <param name="caretIndex"><para>光标位置。</para>The caret position.</param>
    internal static (int Index, int Caret) Split(
        IList<CompareRowViewModel> rows,
        CompareRowViewModel row,
        bool leftSide,
        int caretIndex)
    {
        var index = rows.IndexOf(row);
        if (index < 0)
        {
            return (0, 0);
        }

        var text = leftSide ? row.LeftText : row.RightText;
        var caret = Math.Clamp(caretIndex, 0, text.Length);
        var inserted = leftSide
            ? row.CreateEditableSibling(text[caret..], string.Empty, hasLeft: true, hasRight: false)
            : row.CreateEditableSibling(string.Empty, text[caret..], hasLeft: false, hasRight: true);

        row.SetSideText(leftSide, text[..caret], present: true);
        rows.Insert(index + 1, inserted);
        row.RefreshDiffState();
        inserted.RefreshDiffState();
        row.NotifyEdited(leftSide);
        return (index + 1, 0);
    }

    /// <summary>把某一行与下一行合并（行尾 Delete）：下一行在该侧的文本接到本行末尾，空行整行移除。</summary>
    /// Joins a row with the next one (Delete at the end of a line): the next row's text on that side is appended and a row
    /// that ends up empty on both sides is removed.
    internal static (int Index, int Caret) JoinWithNext(
        IList<CompareRowViewModel> rows,
        CompareRowViewModel row,
        bool leftSide)
    {
        var index = rows.IndexOf(row);
        if (index < 0 || index + 1 >= rows.Count)
        {
            return (Math.Max(index, 0), leftSide ? row.LeftText.Length : row.RightText.Length);
        }

        var next = rows[index + 1];
        var caret = leftSide ? row.LeftText.Length : row.RightText.Length;
        if (leftSide ? next.HasLeftLine : next.HasRightLine)
        {
            row.SetSideText(
                leftSide,
                (leftSide ? row.LeftText : row.RightText) + (leftSide ? next.LeftText : next.RightText),
                present: true);
        }

        next.SetSideText(leftSide, string.Empty, present: false);
        RemoveWhenEmpty(rows, next);
        row.RefreshDiffState();
        row.NotifyEdited(leftSide);
        return (index, caret);
    }

    /// <summary>把某一行并入上一行（行首 Backspace）：本行在该侧的文本接到上一行末尾，空行整行移除。</summary>
    /// Merges a row into the previous one (Backspace at the start of a line): this row's text on that side is appended to the
    /// previous row and a row that ends up empty on both sides is removed.
    internal static (int Index, int Caret) JoinWithPrevious(
        IList<CompareRowViewModel> rows,
        CompareRowViewModel row,
        bool leftSide)
    {
        var index = rows.IndexOf(row);
        if (index <= 0)
        {
            return (Math.Max(index, 0), 0);
        }

        var previous = rows[index - 1];
        var caret = leftSide ? previous.LeftText.Length : previous.RightText.Length;
        if (leftSide ? row.HasLeftLine : row.HasRightLine)
        {
            previous.SetSideText(
                leftSide,
                (leftSide ? previous.LeftText : previous.RightText) + (leftSide ? row.LeftText : row.RightText),
                present: true);
        }

        row.SetSideText(leftSide, string.Empty, present: false);
        RemoveWhenEmpty(rows, row);
        previous.RefreshDiffState();
        previous.NotifyEdited(leftSide);
        return (index - 1, caret);
    }

    /// <summary>删除所选行在该侧的内容：该侧行消失，另一侧保留内容时该行变为单侧；两侧都空的行整行移除。</summary>
    /// Deletes the selected rows on the edited side: that side's lines disappear, a row whose other side still has content
    /// becomes single-side, and a row empty on both sides is removed.
    internal static void DeleteSelected(
        IList<CompareRowViewModel> rows,
        IEnumerable<int> indices,
        bool leftSide)
    {
        CompareRowViewModel? last = null;
        foreach (var index in indices.OrderByDescending(index => index))
        {
            if (index < 0 || index >= rows.Count)
            {
                continue;
            }

            var row = rows[index];
            row.SetSideText(leftSide, string.Empty, present: false);
            RemoveWhenEmpty(rows, row);
            last ??= row;
        }

        last?.NotifyEdited(leftSide);
    }

    /// <summary>两侧都没有内容时把行从列表里移除。</summary>
    private static void RemoveWhenEmpty(IList<CompareRowViewModel> rows, CompareRowViewModel row)
    {
        if (!row.HasLeftLine && !row.HasRightLine)
        {
            rows.Remove(row);
        }
    }
}

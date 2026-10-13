namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// Structural editing of text rows: split at the caret, join with the next or previous row, and delete on one side.
/// <para>文本行的结构编辑：按光标断行、与下一行或上一行合并，以及在某一侧删除所选行。</para>
/// </summary>
internal static class CompareTextRowEditor
{
    /// <summary>
    /// Splits a row at the caret: the text before it stays and the text after it becomes the following row.
    /// <para>在某一行按光标断成两行：光标前的文本留在原行，光标后的文本成为紧随其后的新行。</para>
    /// </summary>
    /// <param name="rows">The row list.<para>行列表。</para></param>
    /// <param name="row">The row being split.<para>被断开的行。</para></param>
    /// <param name="leftSide">Whether the left side is edited.<para>是否为左侧。</para></param>
    /// <param name="caretIndex">The caret position.<para>光标位置。</para></param>
    /// <returns>The index of the new row and its caret position.<para>新行的插入索引以及其中的光标位置。</para></returns>
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

    /// <summary>
    /// Joins a row with the next one: the next row's text on that side is appended and a emptied row is removed.
    /// <para>把本行与下一行合并：下一行在该侧的文本接到本行末尾，空行整行移除。</para>
    /// </summary>
    /// <param name="rows">The row list.<para>行列表。</para></param>
    /// <param name="row">The row being joined.<para>被合并的行。</para></param>
    /// <param name="leftSide">Whether the left side is edited.<para>是否为左侧。</para></param>
    /// <returns>The row index and caret position after the join.<para>合并完成后本行的索引与光标位置。</para></returns>
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

    /// <summary>
    /// Merges a row into the previous one: this row's text on that side is appended and an emptied row is removed.
    /// <para>把本行并入上一行：本行在该侧的文本接到上一行末尾，空行整行移除。</para>
    /// </summary>
    /// <param name="rows">The row list.<para>行列表。</para></param>
    /// <param name="row">The row being merged.<para>被并入上一行的行。</para></param>
    /// <param name="leftSide">Whether the left side is edited.<para>是否为左侧。</para></param>
    /// <returns>The previous row's index and the caret position after the merge.<para>合并后上一行的索引与光标位置。</para></returns>
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

    /// <summary>
    /// Deletes the selected rows on the edited side, removing any row that ends up empty on both sides.
    /// <para>在编辑一侧删除所选行的内容，并把两侧都变空的整行一并移除。</para>
    /// </summary>
    /// <param name="rows">The row list.<para>行列表。</para></param>
    /// <param name="indices">The indices of the rows to delete.<para>要删除的行的索引。</para></param>
    /// <param name="leftSide">Whether the left side is edited.<para>是否为左侧。</para></param>
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

    /// <summary>
    /// Removes the row from the list when both sides are empty.
    /// <para>两侧都没有内容时把该行从列表中移除。</para>
    /// </summary>
    /// <param name="rows">The row list.<para>行列表。</para></param>
    /// <param name="row">The row to test and remove.<para>待检测与移除的行。</para></param>
    private static void RemoveWhenEmpty(IList<CompareRowViewModel> rows, CompareRowViewModel row)
    {
        if (!row.HasLeftLine && !row.HasRightLine)
        {
            rows.Remove(row);
        }
    }
}
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// One aligned slot of a text comparison: each side occupies one line, so the slot index is the alignment
/// index. A side without content is a filler slot that is never written to the file.
/// <para>文本对比的一个对齐槽：两侧各占一行，槽号即对齐序号。某一侧没有内容即为填充槽，不会写入文件。</para>
/// </summary>
internal sealed class CompareTextSlot
{
    /// <summary>The left side's content.<para>左侧内容。</para></summary>
    /// <value>The left side's line text.<para>左侧的行文本。</para></value>
    public string Left { get; set; } = string.Empty;

    /// <summary>The right side's content.<para>右侧内容。</para></summary>
    /// <value>The right side's line text.<para>右侧的行文本。</para></value>
    public string Right { get; set; } = string.Empty;

    /// <summary>Whether the left side has this line.<para>左侧是否有这一行。</para></summary>
    /// <value>True when the left side keeps this line.<para>左侧保留这一行时为真。</para></value>
    public bool HasLeft { get; set; }

    /// <summary>Whether the right side has this line.<para>右侧是否有这一行。</para></summary>
    /// <value>True when the right side keeps this line.<para>右侧保留这一行时为真。</para></value>
    public bool HasRight { get; set; }

    /// <summary>The slot status, derived from both sides.<para>由两侧内容推导的槽状态。</para></summary>
    /// <value>The derived slot status.<para>推导出的槽状态。</para></value>
    public FolderItemStatus Status { get; private set; }

    /// <summary>Recomputes the slot status from both sides' content.<para>按两侧当前内容重算槽状态。</para></summary>
    public void RefreshStatus()
    {
        var equal = HasLeft && HasRight && string.Equals(Left, Right, StringComparison.Ordinal);
        Status = HasLeft && HasRight
            ? equal ? FolderItemStatus.Equal : FolderItemStatus.Different
            : HasLeft ? FolderItemStatus.LeftOnly
            : HasRight ? FolderItemStatus.RightOnly
            : FolderItemStatus.Equal;
    }

    /// <summary>
    /// Sets one side's content and whether that side keeps the line: a blank line and a missing line must differ, otherwise
    /// saving would drop blank lines.
    /// <para>把一侧内容设成给定文本并指定该侧是否保留这一行：空行与缺失行必须分开（否则保存会吃掉空行）。</para>
    /// </summary>
    /// <param name="leftSide">Whether the left side is set.<para>是否为左侧。</para></param>
    /// <param name="text">The line content for that side.<para>该侧的行内容。</para></param>
    /// <param name="present">Whether that side keeps the line; blank and missing lines must stay apart.<para>该侧是否保留这一行；空行与缺失行必须分开。</para></param>
    public void SetSide(bool leftSide, string text, bool present)
    {
        if (leftSide)
        {
            Left = text;
            HasLeft = present;
        }
        else
        {
            Right = text;
            HasRight = present;
        }

        RefreshStatus();
    }
}

/// <summary>
/// Slot model of a text comparison where both documents hold one line per slot.
/// Edits on one side touch only that side's slot content, so the other side's file is never rewritten.
/// <para>文本对比的槽模型：两侧文档一行一槽。某一侧的编辑只改动该侧槽内容，对侧文件不会因此被改写。</para>
/// </summary>
internal sealed class CompareTextSlotModel
{
    /// <summary>
    /// The placeholder text shown for a filler slot: writing content into a filler turns it into a real line.
    /// <para>填充槽显示的占位文本：向填充槽写入内容即使其成为真实行。</para>
    /// </summary>
    internal const string FillerText = "/";

    private readonly List<CompareTextSlot> _slots = [];

    /// <summary>The slot list as a read-only view.<para>槽列表（只读视图）。</para></summary>
    /// <value>The read-only list of slots.<para>只读的槽列表。</para></value>
    internal IReadOnlyList<CompareTextSlot> Slots => _slots;

    /// <summary>
    /// Builds slots from a line-diff result: the per-side line numbers decide whether that side has the row.
    /// <para>由逐行差异结果建立槽：各侧的行号决定该侧是否有这一行。</para>
    /// </summary>
    /// <param name="lines">The diffed lines, one per slot.<para>逐行差异结果，每行对应一个槽。</para></param>
    internal void Load(IReadOnlyList<TextDiffLine> lines)
    {
        _slots.Clear();
        foreach (var line in lines)
        {
            var slot = new CompareTextSlot();
            slot.SetSide(leftSide: true, line.LeftLine > 0 ? line.LeftText : string.Empty, present: line.LeftLine > 0);
            slot.SetSide(leftSide: false, line.RightLine > 0 ? line.RightText : string.Empty, present: line.RightLine > 0);
            _slots.Add(slot);
        }
    }

    /// <summary>
    /// Builds the slots of a single-side file: the other side is all filler slots.
    /// <para>为单侧文件建立槽：对侧全部是填充槽。</para>
    /// </summary>
    /// <param name="lines">The file's lines, one per slot.<para>文件的各行，每行一个槽。</para></param>
    /// <param name="leftSide">Whether the loaded side is the left.<para>加载的一侧是否为左侧。</para></param>
    internal void LoadSingleSide(IReadOnlyList<string> lines, bool leftSide)
    {
        _slots.Clear();
        foreach (var line in lines)
        {
            var slot = new CompareTextSlot();
            slot.SetSide(leftSide, line, present: true);
            _slots.Add(slot);
        }
    }

    /// <summary>
    /// Projects one side into text: one line per slot with <c>/</c> for a filler, so line numbers equal the
    /// alignment indices.
    /// <para>把某一侧投影为文本：一行一槽，填充槽显示 <c>/</c>，行号因此等于对齐序号。</para>
    /// </summary>
    /// <param name="leftSide">Whether the left side is projected.<para>是否为左侧。</para></param>
    /// <returns>The projected text of that side.<para>该侧的投影文本。</para></returns>
    internal string Project(bool leftSide)
        => string.Join('\n', _slots.Select(slot => leftSide
            ? slot.HasLeft ? slot.Left : FillerText
            : slot.HasRight ? slot.Right : FillerText));

    /// <summary>One side's real lines with filler slots skipped.<para>某一侧的真实行（跳过填充槽）。</para></summary>
    /// <param name="leftSide">Whether the left side is read.<para>是否为左侧。</para></param>
    /// <returns>That side's lines to write back to the file.<para>该侧需写回文件的行。</para></returns>
    internal List<string> RealLines(bool leftSide)
    {
        List<string> lines = [];
        foreach (var slot in _slots)
        {
            if (leftSide ? slot.HasLeft : slot.HasRight)
            {
                lines.Add(leftSide ? slot.Left : slot.Right);
            }
        }

        return lines;
    }

    /// <summary>
    /// The 0-based indices of slots that have content on the given side and a status other than equal.
    /// <para>某一侧有内容且槽状态非「相等」的槽号（0 起）。</para>
    /// </summary>
    /// <param name="leftSide">Whether the left side's indices are returned.<para>是否返回左侧的槽号。</para></param>
    /// <returns>The matching slot indices.<para>符合条件的槽号列表。</para></returns>
    internal List<int> HighlightedSlots(bool leftSide)
    {
        List<int> result = [];
        for (var index = 0; index < _slots.Count; index++)
        {
            var slot = _slots[index];
            if (slot.Status == FolderItemStatus.Equal)
            {
                continue;
            }

            if (leftSide ? slot.HasLeft : slot.HasRight)
            {
                result.Add(index);
            }
        }

        return result;
    }

    /// <summary>The status of a slot.<para>某一槽的状态。</para></summary>
    /// <param name="index">The slot index.<para>槽号。</para></param>
    /// <returns>The slot's status.<para>该槽的状态。</para></returns>
    internal FolderItemStatus StatusAt(int index) => _slots[index].Status;

    /// <summary>One side's content of a slot.<para>某一侧某槽的内容。</para></summary>
    /// <param name="index">The slot index.<para>槽号。</para></param>
    /// <param name="leftSide">Whether the left side's content is read.<para>是否读取左侧内容。</para></param>
    /// <returns>The slot's content on that side.<para>该槽在该侧的内容。</para></returns>
    internal string TextAt(int index, bool leftSide)
        => leftSide ? _slots[index].Left : _slots[index].Right;

    /// <summary>Whether a side has content in a slot.<para>某一侧某槽是否有内容。</para></summary>
    /// <param name="index">The slot index.<para>槽号。</para></param>
    /// <param name="leftSide">Whether the left side is checked.<para>是否检查左侧。</para></param>
    /// <returns>True when that side keeps the slot's line.<para>该侧保留该行时为真。</para></returns>
    internal bool HasAt(int index, bool leftSide) => leftSide ? _slots[index].HasLeft : _slots[index].HasRight;

    /// <summary>
    /// In-line edit: replaces one side's slot content. Writing into a filler turns it into a real line, and clearing the
    /// content turns it back into a filler (the line disappears from that side).
    /// <para>行内编辑：把某一侧第 <paramref name="index"/> 槽的内容换成新文本。填充槽被写进内容即变成真实行；
    /// 该侧内容被清空则退回填充槽（这一行在本侧消失）。</para>
    /// </summary>
    /// <param name="index">The slot index to write.<para>要写入的槽号。</para></param>
    /// <param name="leftSide">Whether the left side is edited. <para>是否为左侧。</para></param>
    /// <param name="text">The new line text; <c>/</c> counts as a filler. <para>新的行文本；等于占位符 <c>/</c> 时按填充槽处理。</para></param>
    internal void SetLine(int index, bool leftSide, string text)
    {
        if (index < 0 || index >= _slots.Count)
        {
            return;
        }

        var slot = _slots[index];
        var wasFiller = leftSide ? !slot.HasLeft : !slot.HasRight;
        var isFiller = wasFiller && string.Equals(text, FillerText, StringComparison.Ordinal);
        slot.SetSide(leftSide, isFiller ? string.Empty : text, present: !isFiller);
    }

    /// <summary>
    /// Inserts lines into one side of a slot (Enter or multi-line paste): this side gets real lines and the other side gets
    /// the same number of filler slots, so both documents stay the same length.
    /// <para>在某槽的某一侧插入若干行（Enter 断行、多行粘贴）：本侧插入真实行，对侧插入等量填充槽，
    /// 因此两侧行数始终保持一致。</para>
    /// </summary>
    /// <param name="index">The slot index to insert after. <para>插入到的槽号（新行插在该槽之后）。</para></param>
    /// <param name="leftSide">Whether the left side is edited. <para>是否为左侧。</para></param>
    /// <param name="lines">The lines to insert. <para>插入的行。</para></param>
    internal void InsertLines(int index, bool leftSide, IReadOnlyList<string> lines)
    {
        for (var offset = 0; offset < lines.Count; offset++)
        {
            var slot = new CompareTextSlot();
            slot.SetSide(leftSide, lines[offset], present: true);
            _slots.Insert(Math.Clamp(index + 1 + offset, 0, _slots.Count), slot);
        }
    }

    /// <summary>
    /// Replaces a range of one side's slots with the given text lines: surplus slots fall back to fillers and
    /// extra lines insert new slots with fillers on the other side.
    /// <para>把某一侧指定范围的槽替换成给定文本行：多出的槽退回填充，新行多于原槽时补插新槽（对侧为填充槽）。</para>
    /// </summary>
    /// <param name="startIndex">The first slot index. <para>起始槽号。</para></param>
    /// <param name="count">The number of slots replaced. <para>被替换的槽数。</para></param>
    /// <param name="leftSide">Whether the left side is edited. <para>是否为左侧。</para></param>
    /// <param name="lines">The new text lines. <para>新文本行。</para></param>
    internal void ReplaceLines(int startIndex, int count, bool leftSide, IReadOnlyList<string> lines)
    {
        if (_slots.Count == 0)
        {
            InsertLines(index: -1, leftSide, lines);
            return;
        }

        startIndex = Math.Clamp(startIndex, 0, _slots.Count - 1);
        count = Math.Clamp(count, 0, _slots.Count - startIndex);
        var shared = Math.Min(count, lines.Count);
        for (var offset = 0; offset < shared; offset++)
        {
            _slots[startIndex + offset].SetSide(leftSide, lines[offset], present: true);
        }

        for (var offset = shared; offset < count; offset++)
        {
            _slots[startIndex + offset].SetSide(leftSide, string.Empty, present: false);
        }

        if (lines.Count > count)
        {
            InsertLines(startIndex + count - 1, leftSide, [.. lines.Skip(count)]);
        }
    }

    /// <summary>
    /// Joins two neighbouring slots on one side: the first slot takes both lines' text and the second falls
    /// back to a filler.
    /// <para>合并相邻两槽在本侧的内容：前槽承接两行拼接文本，后槽退回填充。</para>
    /// </summary>
    /// <param name="index">The first slot index.<para>前一个槽的槽号。</para></param>
    /// <param name="leftSide">Whether the left side is joined.<para>是否合并左侧。</para></param>
    internal void JoinLines(int index, bool leftSide)
    {
        if (index < 0 || index + 1 >= _slots.Count)
        {
            return;
        }

        var first = _slots[index];
        var second = _slots[index + 1];
        var head = leftSide ? first.Left : first.Right;
        var tail = leftSide ? second.Left : second.Right;
        var merged = (leftSide ? first.HasLeft : first.HasRight) || (leftSide ? second.HasLeft : second.HasRight)
            ? head + tail
            : string.Empty;
        first.SetSide(leftSide, merged, present: merged.Length > 0 || (leftSide ? first.HasLeft : first.HasRight) || (leftSide ? second.HasLeft : second.HasRight));
        second.SetSide(leftSide, string.Empty, present: false);
    }

    /// <summary>Removes slots that have no content on either side.<para>丢弃两侧都没有内容的槽。</para></summary>
    internal void DropEmptySlots()
    {
        for (var index = _slots.Count - 1; index >= 0; index--)
        {
            if (!_slots[index].HasLeft && !_slots[index].HasRight)
            {
                _slots.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// A slot's state snapshot for undo and redo; the explicit presence flags keep blank lines and fillers
    /// apart.
    /// <para>槽状态快照（撤销/重做用）；显式的存在性标记可区分空行与填充槽。</para>
    /// </summary>
    /// <param name="Left">The snapshot's left content.<para>快照的左侧内容。</para></param>
    /// <param name="Right">The snapshot's right content.<para>快照的右侧内容。</para></param>
    /// <param name="HasLeft">Whether the left side kept the line.<para>左侧是否保留该行。</para></param>
    /// <param name="HasRight">Whether the right side kept the line.<para>右侧是否保留该行。</para></param>
    internal readonly record struct SlotState(string Left, string Right, bool HasLeft, bool HasRight);

    /// <summary>Captures every slot's state as an undo point.<para>捕获全部槽状态作为撤销点。</para></summary>
    /// <returns>The state snapshots of all slots.<para>全部槽的状态快照。</para></returns>
    internal List<SlotState> CaptureState()
        => [.. _slots.Select(slot => new SlotState(slot.Left, slot.Right, slot.HasLeft, slot.HasRight))];

    /// <summary>Restores the slots from a captured snapshot.<para>用快照恢复槽状态。</para></summary>
    /// <param name="state">The snapshot to restore from.<para>要恢复的快照。</para></param>
    internal void RestoreState(IReadOnlyList<SlotState> state)
    {
        _slots.Clear();
        foreach (var slot in state)
        {
            var restored = new CompareTextSlot();
            restored.SetSide(leftSide: true, slot.Left, slot.HasLeft);
            restored.SetSide(leftSide: false, slot.Right, slot.HasRight);
            _slots.Add(restored);
        }
    }
}
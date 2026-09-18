using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>文本对比的一个对齐槽：两侧文档各占一行，槽号即对齐序号。某一侧没有内容即为「填充槽」，
/// 在编辑器里显示为 <c>/</c>，保存时不会写入文件。</para>
/// One aligned slot of a text comparison: each side's document occupies one line, so the slot index is the alignment
/// index. A side without content is a filler slot, shown as <c>/</c> in the editor and never written to the file.
/// </summary>
internal sealed class CompareTextSlot
{
    /// <summary>左侧内容。 The left content.</summary>
    public string Left { get; set; } = string.Empty;

    /// <summary>右侧内容。 The right content.</summary>
    public string Right { get; set; } = string.Empty;

    /// <summary>左侧是否有这一行。 Whether the left side has this line.</summary>
    public bool HasLeft { get; set; }

    /// <summary>右侧是否有这一行。 Whether the right side has this line.</summary>
    public bool HasRight { get; set; }

    /// <summary>槽状态（由两侧内容推导）。 The slot status, derived from both sides.</summary>
    public FolderItemStatus Status { get; private set; }

    /// <summary>按当前两侧内容重算状态：两侧都有且相同=相等，都有但不同=不同，只有一侧=单侧。两侧皆空=相等。</summary>
    /// Recomputes the status from both sides' content.
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
    /// <para>把一侧内容设成给定文本并指定该侧是否保留这一行：空行与缺失行必须分开（否则保存会吃掉空行）。</para>
    /// Sets one side's content and whether that side keeps the line: a blank line and a missing line must differ, otherwise
    /// saving would drop blank lines.
    /// </summary>
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
/// <para>文本对比的槽模型：两侧文档等长（一行一槽），槽号即对齐序号。编辑器产生的改动在这里翻译成槽操作——
/// 行内编辑只改该槽该侧的内容；新增行在本侧拆出新槽、对侧自动成为填充槽；删除/合并行则把本侧对应的槽留成
/// 填充槽（对侧内容原样保留）。因此对侧文件永远不会因为本侧的编辑而被改写。</para>
/// Slot model of a text comparison: both documents have one line per slot, so the slot index is the alignment index.
/// Editor changes are translated into slot operations here — an in-line edit only touches that side's slot content, a new
/// line splits a slot and leaves a filler slot on the other side, and deleting or joining lines turns this side's slot into
/// a filler while the other side keeps its content. The counterpart file is therefore never rewritten by edits made on this
/// side.
/// </summary>
internal sealed class CompareTextSlotModel
{
    /// <summary>
    /// <para>填充槽在编辑器里显示的占位文本：用户可以直接在填充槽里打字或粘贴，内容一改就变成真实行。</para>
    /// The placeholder text a filler slot shows in the editor: typing or pasting into a filler turns it into a real line.
    /// </summary>
    internal const string FillerText = "/";

    private readonly List<CompareTextSlot> _slots = [];

    /// <summary>槽列表（只读视图）。 The slots (read-only view).</summary>
    internal IReadOnlyList<CompareTextSlot> Slots => _slots;

    /// <summary>从 DiffPlex 行差异建立槽：两侧行号决定该侧是否有这一行。</summary>
    /// Builds slots from DiffPlex line differences: each side's line number decides whether it has the row.
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

    /// <summary>建立单侧文件的槽（对侧全是填充槽）。</summary>
    /// Builds the slots of a single-side file (the other side is all fillers).
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
    /// <para>生成某一侧在编辑器里的文本：一行一槽，填充槽显示 <c>/</c>。行号因此等于对齐序号。</para>
    /// Projects one side into the editor's text: one line per slot with <c>/</c> for a filler, so line numbers are the
    /// alignment indices.
    /// </summary>
    internal string Project(bool leftSide)
        => string.Join('\n', _slots.Select(slot => leftSide
            ? slot.HasLeft ? slot.Left : FillerText
            : slot.HasRight ? slot.Right : FillerText));

    /// <summary>某一侧的真实行（跳过填充槽），保存时写回文件用。</summary>
    /// One side's real lines with fillers skipped, used when saving the file.
    internal List<string> RealLines(bool leftSide)
    {
        var lines = new List<string>();
        foreach (var slot in _slots)
        {
            if (leftSide ? slot.HasLeft : slot.HasRight)
            {
                lines.Add(leftSide ? slot.Left : slot.Right);
            }
        }

        return lines;
    }

    /// <summary>某一侧需要整行着色的槽号（0 起）：该侧有内容且槽状态不是「相等」。</summary>
    /// The slot indices (0-based) that need whole-line colouring on one side: content on that side whose status is not
    /// "equal".
    internal List<int> HighlightedSlots(bool leftSide)
    {
        var result = new List<int>();
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

    /// <summary>槽状态（供文档汇总与行投影使用）。 The slot statuses (used by the document summary and row projection).</summary>
    internal FolderItemStatus StatusAt(int index) => _slots[index].Status;

    /// <summary>某一侧某槽的内容。 One side's content of a slot.</summary>
    internal string TextAt(int index, bool leftSide)
        => leftSide ? _slots[index].Left : _slots[index].Right;

    /// <summary>某一侧某槽是否有内容。 Whether a side has content in a slot.</summary>
    internal bool HasAt(int index, bool leftSide) => leftSide ? _slots[index].HasLeft : _slots[index].HasRight;

    /// <summary>
    /// <para>行内编辑：把某一侧第 <paramref name="index"/> 槽的内容换成新文本。填充槽被写进内容即变成真实行；
    /// 该侧内容被清空则退回填充槽（这一行在本侧消失）。</para>
    /// In-line edit: replaces one side's slot content. Writing into a filler turns it into a real line, and clearing the
    /// content turns it back into a filler (the line disappears from that side).
    /// </summary>
    /// <param name="index"><para>槽号（编辑器行号 - 1）。</para>The slot index (editor line number - 1).</param>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side is edited.</param>
    /// <param name="text"><para>新的行文本；等于占位符 <c>/</c> 时按填充槽处理。</para>The new line text; <c>/</c> counts as a filler.</param>
    internal void SetLine(int index, bool leftSide, string text)
    {
        if (index < 0 || index >= _slots.Count)
        {
            return;
        }

        // 之前就是填充槽且内容仍是占位符 → 维持填充；否则这一行在本侧是真实的（含空行）。
        // A slot that was a filler and still holds the placeholder stays a filler; otherwise the line is real (including a
        // blank line).
        var slot = _slots[index];
        var wasFiller = leftSide ? !slot.HasLeft : !slot.HasRight;
        var isFiller = wasFiller && string.Equals(text, FillerText, StringComparison.Ordinal);
        slot.SetSide(leftSide, isFiller ? string.Empty : text, present: !isFiller);
    }

    /// <summary>
    /// <para>在某槽的某一侧插入若干行（Enter 断行、多行粘贴）：本侧插入真实行，对侧插入等量填充槽，
    /// 因此两侧行数始终保持一致。</para>
    /// Inserts lines into one side of a slot (Enter or multi-line paste): this side gets real lines and the other side gets
    /// the same number of filler slots, so both documents stay the same length.
    /// </summary>
    /// <param name="index"><para>插入到的槽号（新行插在该槽之后）。</para>The slot index to insert after.</param>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side is edited.</param>
    /// <param name="lines"><para>插入的行。</para>The lines to insert.</param>
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
    /// <para>把某一侧指定范围的行替换成给定文本行（删除所选行、粘贴覆盖、从另一侧复制）：替换后该侧这些槽的内容
    /// 为新行，多出的槽退回填充；新行多于原槽时补插新槽（对侧为填充槽）。</para>
    /// Replaces a range of one side's lines with the given text lines (delete selection, paste over, copy from the other
    /// side): the side's slots take the new lines, surplus slots fall back to fillers and extra lines insert new slots
    /// (fillers on the other side).
    /// </summary>
    /// <param name="startIndex"><para>起始槽号。</para>The first slot index.</param>
    /// <param name="count"><para>被替换的槽数。</para>The number of slots replaced.</param>
    /// <param name="leftSide"><para>是否为左侧。</para>Whether the left side is edited.</param>
    /// <param name="lines"><para>新文本行。</para>The new text lines.</param>
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

        // 新行少于原槽：多出来的槽在该侧退回填充。
        // Fewer new lines than slots: the surplus slots fall back to fillers on this side.
        for (var offset = shared; offset < count; offset++)
        {
            _slots[startIndex + offset].SetSide(leftSide, string.Empty, present: false);
        }

        // 新行多于原槽：在本侧补插新槽（对侧为填充）。
        // More new lines than slots: insert extra slots on this side (fillers on the other side).
        if (lines.Count > count)
        {
            InsertLines(startIndex + count - 1, leftSide, [.. lines.Skip(count)]);
        }
    }

    /// <summary>合并相邻两槽在本侧的内容（行尾 Delete / 行首 Backspace）：本侧前槽变成两行拼接，后槽退回填充。</summary>
    /// Joins two neighbouring slots on one side (Delete at the end of a line or Backspace at the start): the first slot takes
    /// both lines' text and the second falls back to a filler.
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

    /// <summary>丢弃两侧都没有内容的槽（可选收拢，保持「空槽不占位」）。</summary>
    /// Removes slots that have no content on either side.
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

    /// <summary>一个槽的状态快照（撤销/重做用；保存显式的存在性，空行与填充槽不会混淆）。</summary>
    /// A slot's state snapshot (used by undo/redo; the explicit presence flag keeps blank lines and fillers apart).
    internal readonly record struct SlotState(string Left, string Right, bool HasLeft, bool HasRight);

    /// <summary>捕获全部槽状态（撤销点）。 Captures every slot's state (an undo point).</summary>
    internal List<SlotState> CaptureState()
        => _slots.Select(slot => new SlotState(slot.Left, slot.Right, slot.HasLeft, slot.HasRight)).ToList();

    /// <summary>用快照恢复槽状态（撤销/重做）。 Restores the slots from a snapshot (undo/redo).</summary>
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
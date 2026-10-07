using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using UotanStudio.Services;
using UotanStudio.ViewModels.Compare;

namespace UotanStudio.Views.Compare;

// 本分部承载 CompareView 的一组同类成员；类与其余分部合并后等价于原实现。
public partial class CompareView
{
    // ===== 文本对比编辑器（槽模型 ↔ 两侧编辑器） =====

    /// <summary>同步编辑器文档时置位，避免把程序写入当成用户编辑。</summary>
    private bool _syncingEditors;

    /// <summary>每个对比编辑器的高亮着色器（按行着色整行差异）。</summary>
    private readonly Dictionary<TextEditor, CompareLineHighlighter> _editors = [];

    /// <summary>最后一次右键命中的编辑器（右键菜单项据此定位编辑器）。</summary>
    private TextEditor? _contextEditor;

    /// <summary>编辑器滚动镜像中置位（防回环）。 Set while mirroring editor scrolling (guards against loops).</summary>
    private bool _syncingEditorScroll;

    /// <summary>
    /// <para>两个对比编辑器的滚动镜像：两侧文档等长（行=对齐槽），所以直接按像素镜像偏移就能保持同行对应，
    /// 不需要按行号换算。</para>
    /// Scroll mirroring between the two compare editors: both documents have the same length (one line per aligned slot), so
    /// mirroring the pixel offset keeps the rows aligned without any row-index conversion.
    /// </summary>
    private void OnCompareEditorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingEditorScroll || sender is not ScrollViewer source || !IsLoaded)
        {
            return;
        }

        var editors = EnumerateCompareEditors();
        var sourceEditor = editors.FirstOrDefault(pair => ReferenceEquals(ResolveEditorScrollViewer(pair.Editor), source));
        if (sourceEditor.Editor is null)
        {
            return;
        }

        var target = editors.FirstOrDefault(pair => pair.LeftSide != sourceEditor.LeftSide).Editor is { } targetEditor
            ? ResolveEditorScrollViewer(targetEditor)
            : null;
        if (target is null || Math.Abs(target.Offset.Y - source.Offset.Y) < 0.5)
        {
            return;
        }

        _syncingEditorScroll = true;
        try
        {
            target.Offset = new Vector(target.Offset.X, source.Offset.Y);
        }
        finally
        {
            _syncingEditorScroll = false;
        }
    }

    /// <summary>取编辑器内部的滚动容器（AvaloniaEdit 未公开该部件，按可视树解析）。</summary>
    /// Resolves the editor's inner scroll container (AvaloniaEdit does not expose the part, so the visual tree is used).
    private static ScrollViewer? ResolveEditorScrollViewer(TextEditor editor)
        => editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>
    /// <para>把两个对比编辑器同步成槽模型的投影（行号=对齐序号，填充槽显示斜杠），并刷新差异行着色与选中范围。</para>
    /// Synchronises both compare editors with the slot model's projection (line numbers are alignment indices, fillers show a
    /// slash) and refreshes the diff-line colouring and the selected slot range.
    /// </summary>
    private void SyncCompareEditors(TextEditor? skip = null)
    {
        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        // 同步过程本身也不能抛：任何编辑器异常都转成错误条 + 日志。
        // The synchronisation itself must not throw either: any editor failure becomes an error banner entry plus a log line.
        try
        {
            SyncCompareEditorsCore(vm, skip);
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>同步实现：写入两侧投影并刷新差异着色。 The synchronisation itself: writes both projections and refreshes the diff colouring.</summary>
    private void SyncCompareEditorsCore(CompareViewModel vm, TextEditor? skip)
    {
        _syncingEditors = true;
        try
        {
            foreach (var (editor, leftSide) in EnumerateCompareEditors())
            {
                // 编辑侧保持用户刚输入的内容与光标（模型已按同样的语义更新），只同步另一侧。
                // The edited side keeps the user's text and caret (the model was updated with the same semantics); only the other
                // side is synchronised.
                if (!ReferenceEquals(editor, skip))
                {
                    var text = vm.ProjectText(leftSide);
                    if (!string.Equals(editor.Text, text, StringComparison.Ordinal))
                    {
                        editor.Text = text;
                    }
                }

                // 差异行整行着色（不同=红、单侧=橙/蓝），与行列表期间的规则一致。
                // Whole-line diff colouring (different=red, single-side=orange/blue), matching the row list's rule.
                if (_editors.TryGetValue(editor, out var highlighter))
                {
                    highlighter.SetLines(vm.HighlightedSlots(leftSide).Select(index => index + 1));
                    editor.TextArea.TextView.InvalidateVisual();
                }
            }
        }
        finally
        {
            _syncingEditors = false;
        }
    }

    /// <summary>枚举当前对比中的编辑器及其所属侧。 Enumerates the compare editors with the side they show.</summary>
    private List<(TextEditor Editor, bool LeftSide)> EnumerateCompareEditors()
    {
        List<(TextEditor, bool)> result = [];
        foreach (var visuals in _columnVisuals)
        {
            if (visuals.CompareEditor is { } editor && visuals.Column.IsCompareSide)
            {
                result.Add((editor, !visuals.Column.UsesRightCells));
            }
        }

        return result;
    }

    /// <summary>在列容器里取文本对比编辑器（按名字查找）。 Finds the compare text editor inside a column container.</summary>
    private static TextEditor? ResolveCompareEditor(ContentControl container)
        => ResolveEditor(container, "CompareTextEditor");

    /// <summary>按名字在列容器里查找编辑器。 Finds an editor by name inside a column container.</summary>
    private static TextEditor? ResolveEditor(ContentControl container, string name)
    {
        foreach (var editor in container.GetVisualDescendants().OfType<TextEditor>())
        {
            if (string.Equals(editor.Name, name, StringComparison.Ordinal))
            {
                return editor;
            }
        }

        return null;
    }

    /// <summary>
    /// <para>接线每一列的编辑器：浏览文本文件用编辑器（内容随列属性双向同步），文本对比用两侧编辑器（改动翻译成槽操作）。</para>
    /// Wires each column's editors: a browsed text file uses the editor (content synchronised both ways with the column's
    /// property) and a text comparison uses both sides' editors (changes translated into slot operations).
    /// </summary>
    private void AttachColumnEditors()
    {
        if (!IsLoaded)
        {
            return;
        }

        foreach (var visuals in _columnVisuals)
        {
            WireBrowseEditor(visuals);
        }

        AttachCompareEditors();
        SyncCompareEditors();
    }

    /// <summary>接线浏览文本编辑器：把列里的文本推给编辑器，并把用户的编辑写回列（标记已修改）。</summary>
    /// Wires the browsing text editor: the column's text is pushed into the editor and the user's edits are written back into
    /// the column (marking it modified).
    private void WireBrowseEditor(ColumnVisuals visuals)
    {
        var editor = ResolveEditor(visuals.Container, "BrowseTextEditor");
        if (editor is null)
        {
            return;
        }

        visuals.BrowseEditor = editor;
        if (visuals.BrowseWired)
        {
            return;
        }

        visuals.BrowseWired = true;
        visuals.ColumnHandler = (_, args) =>
        {
            if (args.PropertyName is not (nameof(CompareColumnViewModel.EditorText)
                or nameof(CompareColumnViewModel.IsTextEditorVisible)))
            {
                return;
            }

            _syncingEditors = true;
            try
            {
                if (!string.Equals(editor.Document.Text, visuals.Column.EditorText, StringComparison.Ordinal))
                {
                    // 写入延后到本次属性通知之后：在编辑器自身的变更通知里回写文档容易触发内部状态冲突。
                    // The write is deferred past the current notification: writing the document back from inside the editor's own
                    // change notification can trip internal state.
                    var text = visuals.Column.EditorText;
                    Dispatcher.UIThread.Post(
                        () =>
                        {
                            _syncingEditors = true;
                            try
                            {
                                // 写回整篇文档会把光标重置到文件开头，先记住光标位置再恢复。
                                // Replacing the whole document resets the caret to the top of the file, so it is captured and restored.
                                var caret = editor.CaretOffset;
                                editor.Document.Text = text;
                                editor.CaretOffset = Math.Clamp(caret, 0, editor.Document.TextLength);
                            }
                            finally
                            {
                                _syncingEditors = false;
                            }
                        },
                        DispatcherPriority.Background);
                }
            }
            finally
            {
                _syncingEditors = false;
            }
        };
        visuals.Column.PropertyChanged += visuals.ColumnHandler;
        editor.AddHandler(ContextRequestedEvent, OnEditorContextRequested, RoutingStrategies.Tunnel);
        editor.Document.Changed += (_, _) =>
        {
            if (_syncingEditors)
            {
                return;
            }

            // 编辑失败也不能闪退：异常写进错误区与日志。
            // An editing failure must not crash the app either: exceptions go to the error banner and the log.
            try
            {
                visuals.Column.EditorText = editor.Document.Text;
            }
            catch (Exception ex)
            {
                if (DataContext is CompareViewModel viewModel)
                {
                    ReportEditorFailure(viewModel, ex);
                }
                else
                {
                    LogService.Instance.Write(LogLevel.Error, "Compare", ex.ToString());
                }
            }
        };
        editor.Document.Text = visuals.Column.EditorText;
    }

    // ===== 文本编辑器的右键菜单 =====

    /// <summary>记录右键命中的编辑器（菜单项在弹出层里，取不到编辑器本身）。</summary>
    /// Records the editor a context request hit (menu items live in a popup and cannot resolve the editor themselves).
    private void OnEditorContextRequested(object? sender, ContextRequestedEventArgs e)
        => _contextEditor = sender as TextEditor;

    /// <summary>菜单项对应的编辑器：优先右键记录，回退到当前聚焦的编辑器。</summary>
    /// The editor a menu item applies to: the recorded context editor, falling back to the focused one.
    private TextEditor? ResolveMenuEditor()
        => _contextEditor ?? EnumerateCompareEditors().Select(pair => pair.Editor).FirstOrDefault(editor => editor.IsFocused)
            ?? _columnVisuals.Select(visuals => visuals.BrowseEditor).FirstOrDefault(editor => editor?.IsFocused == true);

    /// <summary>编辑器右键菜单：剪切。</summary>
    private void OnEditorCut(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuEditor() is { } editor)
        {
            editor.Cut();
        }
    }

    /// <summary>编辑器右键菜单：复制。</summary>
    private void OnEditorCopy(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuEditor() is { } editor)
        {
            editor.Copy();
        }
    }

    /// <summary>编辑器右键菜单：粘贴。</summary>
    private void OnEditorPaste(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuEditor() is { } editor)
        {
            editor.Paste();
        }
    }

    /// <summary>编辑器右键菜单：全选。</summary>
    private void OnEditorSelectAll(object? sender, RoutedEventArgs e)
    {
        if (ResolveMenuEditor() is { } editor)
        {
            editor.SelectAll();
        }
    }

    /// <summary>
    /// <para>文本对比编辑器右键菜单「复制到另一侧」：整行选中时复制这些行到对侧相同槽位；只在单行内选中一段文字时，
    /// 用选中文字替换对侧同一行的相同列范围。</para>
    /// The text-comparison editor's "copy to the other side": a whole-line selection copies those lines to the same slots on the other
    /// side, while a partial selection inside one line replaces the counterpart's same column range with the selected text.
    /// </summary>
    private void OnCopyEditorSelectionToOtherSide(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CompareViewModel vm || !vm.IsTextEditorMode || ResolveMenuEditor() is not { } editor)
        {
            return;
        }

        var (owner, leftSide) = FindCompareEditorSide(editor.Document);
        if (owner is null)
        {
            return;
        }

        try
        {
            var selection = editor.TextArea.Selection;
            var startLine = Math.Min(selection.StartPosition.Line, selection.EndPosition.Line) - 1;
            var endLine = Math.Max(selection.StartPosition.Line, selection.EndPosition.Line) - 1;
            var selectedText = selection.IsEmpty ? null : editor.SelectedText;

            // 只有「单行内、且没有整行覆盖」时才按列范围替换，其余情况按整行复制。
            // Only a single-line selection that does not cover whole lines is handled as a column range; everything else copies whole lines.
            var partial = selectedText is not null && startLine == endLine
                && selection.StartPosition.Column > 1;
            vm.CopyEditorSelection(
                fromLeftToRight: leftSide,
                startSlot: Math.Max(0, startLine),
                endSlot: Math.Max(0, endLine),
                selectedText: partial ? selectedText : null,
                startColumn: Math.Max(0, selection.StartPosition.Column - 1),
                endColumn: Math.Max(0, selection.EndPosition.Column - 1));
            SyncCompareEditors();
            ClearEditorUndo(editor);
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>
    /// <para>挂接对比编辑器：载入槽模型投影、拦截会改变行数的编辑（翻译成槽操作后重新投影两侧），
    /// 把行内编辑写回槽模型，并把编辑器选区同步成行选择（供顶部复制按钮使用）。</para>
    /// Attaches a compare editor: loads the slot model's projection, intercepts line-count-changing edits (translated into slot
    /// operations with both sides re-projected afterwards), writes in-line edits back into the slot model and mirrors the
    /// editor's selection into the row selection used by the toolbar's copy buttons.
    /// </summary>
    private void AttachCompareEditors()
    {
        if (!IsLoaded || DataContext is not CompareViewModel vm || !vm.IsTextEditorMode)
        {
            return;
        }

        foreach (var visuals in _columnVisuals)
        {
            var editor = ResolveCompareEditor(visuals.Container);
            visuals.CompareEditor = editor;
            if (editor is null)
            {
                continue;
            }

            if (_editors.ContainsKey(editor))
            {
                continue;
            }

            var highlighter = new CompareLineHighlighter(ResolveDiffBrush());
            editor.TextArea.TextView.BackgroundRenderers.Add(highlighter);
            editor.Document.Changed += OnCompareEditorChanged;
            editor.TextArea.SelectionChanged += OnCompareEditorSelectionChanged;
            // 右键菜单项在弹出层里取不到编辑器，与浏览编辑器一样在隧道阶段记录右键目标。
            // Menu items live in a popup and cannot resolve the editor, so record the context target in the tunnel phase
            // just like the browsing editor does.
            editor.AddHandler(ContextRequestedEvent, OnEditorContextRequested, RoutingStrategies.Tunnel);
            // Ctrl+Z / Ctrl+Y 由两侧共享的撤销历史处理（隧道阶段先拦截）。
            // Ctrl+Z / Ctrl+Y are handled by the history both panes share (intercepted in the tunnel phase).
            editor.AddHandler(KeyDownEvent, OnCompareEditorKeyDown, RoutingStrategies.Tunnel);
            // 两侧文档等长、行高相同，因此编辑器滚动直接按像素镜像即可保持同行对应。
            // Both documents have the same length and row height, so mirroring the pixel offsets keeps the rows aligned.
            if (ResolveEditorScrollViewer(editor) is { } scrollViewer)
            {
                scrollViewer.ScrollChanged += OnCompareEditorScrollChanged;
            }

            _editors[editor] = highlighter;
        }

        SyncCompareEditors();
    }

    /// <summary>取差异行底色（主题画刷优先，取不到时用半透明红）。 Resolves the diff-line brush (theme first, translucent red as fallback).</summary>
    private Avalonia.Media.IBrush ResolveDiffBrush()
        => this.TryFindResource("DiffBackgroundBrush", out var brush) && brush is Avalonia.Media.IBrush found
            ? found
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x40, 0xC4, 0x2B, 0x1C));

    /// <summary>
    /// <para>编辑器改动前置处理：行数会变化的编辑（Enter、删除换行、多行粘贴、剪切整行）在这里取消，
    /// 改由槽模型执行并重新投影两侧，保证两侧文档始终等长；行数不变的行内编辑直接放行。</para>
    /// Pre-change handling: an edit that changes the line count (Enter, deleting a line break, multi-line paste, cutting a
    /// whole line) is cancelled and executed through the slot model with both sides re-projected so the documents stay the
    /// same length; an in-line edit that keeps the line count passes through.
    /// </summary>
    private void OnCompareEditorChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_syncingEditors || sender is not TextDocument document
            || DataContext is not CompareViewModel vm || !vm.IsTextEditorMode)
        {
            return;
        }

        // 任何异常都不能让应用闪退（UI 事件里的未捕获异常会直接终止进程）：出错写进错误区与日志，保持视图可用。
        // No exception may crash the app (an uncaught exception in a UI event kills the process): failures go to the error banner and
        // the log, and the view stays usable.
        try
        {
            var (editor, leftSide) = FindCompareEditorSide(document);
            if (editor is null)
            {
                return;
            }

            // 编辑器的改动整体交给槽模型翻译（行内编辑只改本侧内容；行数变化则在对侧补/收填充槽），
            // 然后只把另一侧同步成新的投影，编辑侧保留用户输入与光标。
            // The editor change is translated by the slot model as a whole (an in-line edit only touches this side's content, a
            // count change adds/removes fillers on the other side) and only the other side is synchronised afterwards, so the
            // edited side keeps the user's text and caret.
            var (startLine, removedLines, lines, _) = DescribeChange(document, e);
            // 先记撤销点（两侧共享），再落到槽模型：一次结构编辑会连同对侧的填充槽一起回退。
            // Record the undo point (shared by both panes) before applying to the slot model, so a structural edit rolls back
            // together with the counterpart's filler.
            vm.RecordTextUndoPoint(
                structural: removedLines != lines.Count,
                leftSlot: CaretSlot(FindEditor(leftSide: true)),
                rightSlot: CaretSlot(FindEditor(leftSide: false)));
            vm.ApplyEditorLines(leftSide, startLine, removedLines, lines);
            // 变更通知里只更新模型：另一侧文档的写入与撤销栈清理全部延后到通知结束之后。
            // 在编辑器自身的变更通知里改文档（哪怕改的是另一侧）或清理撤销栈，都会触发编辑器内部状态冲突并导致闪退。
            // Inside a change notification only the model is updated: writing the other side's document and clearing undo stacks are
            // deferred until the notification has finished. Touching documents (even the other side) or undo stacks from inside a change
            // notification trips internal state in the editor and crashes the app.
            Dispatcher.UIThread.Post(
                () =>
                {
                    try
                    {
                        SyncCompareEditors(editor);
                        ClearEditorUndo(editor);
                    }
                    catch (Exception ex)
                    {
                        ReportEditorFailure(vm, ex);
                    }
                },
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>取某一侧的对比编辑器。 Returns a side's compare editor.</summary>
    private TextEditor? FindEditor(bool leftSide)
        => EnumerateCompareEditors().Where(pair => pair.LeftSide == leftSide).Select(pair => pair.Editor).FirstOrDefault();

    /// <summary>
    /// <para>编辑器处理失败时把原因写进错误区与日志：宁可显示一条错误，也不能让未处理异常结束进程。</para>
    /// Records an editor failure in the error banner and the log: showing an error beats letting an unhandled exception kill the
    /// process.
    /// </summary>
    private void ReportEditorFailure(CompareViewModel vm, Exception ex)
    {
        LogService.Instance.Write(LogLevel.Error, "Compare", ex.ToString());
        vm.ReportError(global::UotanStudio.Services.UserError.Describe(ex));
    }

    /// <summary>取编辑器光标所在槽号（无光标时为 0）。 Returns the editor's caret slot (0 when there is no caret).</summary>
    private static int CaretSlot(TextEditor? editor) => editor is null ? 0 : Math.Max(0, editor.TextArea.Caret.Line - 1);

    /// <summary>清空编辑器自带的撤销栈（撤销由两侧共享的历史负责）。 Clears the editor's own undo stack (the shared history owns undo).</summary>
    private static void ClearEditorUndo(TextEditor editor) => editor.Document.UndoStack.ClearAll();

    /// <summary>
    /// <para>对比编辑器上的 Ctrl+Z / Ctrl+Y（含 Ctrl+Shift+Z）：回退两侧共享的编辑历史，结构编辑连同对侧填充槽一起回退，
    /// 因此两侧永远保持同行对应。</para>
    /// Ctrl+Z / Ctrl+Y (and Ctrl+Shift+Z) inside a compare editor: rolls the shared history back and forth, so a structural edit is
    /// undone together with the counterpart's filler and the rows stay aligned.
    /// </summary>
    private void OnCompareEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CompareViewModel vm || !vm.IsTextEditorMode
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is not (Key.Z or Key.Y))
        {
            return;
        }

        var redo = e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.Handled = true;
        try
        {
            var caret = redo ? vm.RedoText() : vm.UndoText();
            if (caret is not { } target)
            {
                return;
            }

            SyncCompareEditors();
            foreach (var (editor, leftSide) in EnumerateCompareEditors())
            {
                RestoreEditorCaret(editor, leftSide ? target.LeftSlot : target.RightSlot);
                ClearEditorUndo(editor);
            }
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>把光标放回指定槽所在行的末尾。 Places the caret at the end of the given slot's line.</summary>
    private static void RestoreEditorCaret(TextEditor editor, int slot)
    {
        var document = editor.Document;
        if (document.LineCount == 0)
        {
            return;
        }

        var number = Math.Clamp(slot + 1, 1, document.LineCount);
        var line = document.GetLineByNumber(number);
        editor.CaretOffset = line.Offset + line.Length;
        editor.ScrollToLine(number);
    }

    /// <summary>编辑器选区变化 → 同步成行选择，顶部复制按钮据此复制到另一侧。</summary>
    /// Selection changes mirror into the row selection so the toolbar's copy buttons know which lines to copy.
    private void OnCompareEditorSelectionChanged(object? sender, EventArgs e)
    {
        if (_syncingEditors || DataContext is not CompareViewModel vm || !vm.IsTextEditorMode
            || sender is not TextArea textArea)
        {
            return;
        }

        try
        {
            var (editor, _) = FindCompareEditorSide(textArea.Document);
            if (editor is null)
            {
                return;
            }

            var selection = textArea.Selection;
            var start = Math.Min(selection.StartPosition.Line, selection.EndPosition.Line) - 1;
            var end = Math.Max(selection.StartPosition.Line, selection.EndPosition.Line) - 1;
            List<int> indices = [];
            for (var line = Math.Max(start, 0); line <= Math.Max(end, 0); line++)
            {
                indices.Add(line);
            }

            vm.UpdateRowSelection(indices);
        }
        catch (Exception ex)
        {
            ReportEditorFailure(vm, ex);
        }
    }

    /// <summary>
    /// <para>把一次文档改动换算成「受影响的整行范围 + 该范围替换后的行 + 被替换掉的旧行数」。</para>
    /// <para>注意：<c>Changed</c> 事件触发时文档**已经是改动后**的状态，所以新行直接从文档里读，
    /// 不能拿改动前的区域再套一次插入（那样会把同一个字符应用两遍，表现为输入一个字符出现两个、删除多删）。
    /// 旧行数由「新行数 − 本次插入的换行数 + 本次删除的换行数」还原。</para>
    /// Converts one document change into "the affected whole-line range, its replacement lines and how many old lines it replaced".
    /// Note that when <c>Changed</c> fires the document already holds the post-change state, so the new lines are read straight from the
    /// document — applying the insertion to a pre-change region again would insert the same character twice (typing one character shows
    /// two, deletions remove extra characters). The old line count is recovered as
    /// "new line count − inserted newlines + removed newlines".
    /// </summary>
    private static (int StartLine, int RemovedLines, List<string> Lines, int CaretColumn) DescribeChange(
        TextDocument document,
        DocumentChangeEventArgs e)
    {
        var startLine = document.GetLineByOffset(e.Offset).LineNumber - 1;
        var endOffset = Math.Min(e.Offset + e.InsertionLength, document.TextLength);
        var endLine = document.GetLineByOffset(endOffset).LineNumber - 1;
        var firstLine = document.GetLineByNumber(startLine + 1);
        var lastLine = document.GetLineByNumber(endLine + 1);
        var regionEnd = lastLine.Offset + lastLine.Length;
        var region = document.GetText(firstLine.Offset, Math.Max(0, regionEnd - firstLine.Offset));
        var lines = region.Split('\n').ToList();

        // 旧行数 = 新行数 − 插入的换行数 + 删除的换行数（换行只在受影响的整行范围内）。
        // Old line count = new line count − inserted newlines + removed newlines (newlines only occur inside the affected range).
        var netNewLines = CountNewlines(e.InsertedText.Text) - CountNewlines(e.RemovedText.Text);
        var removedLines = Math.Max(1, lines.Count - netNewLines);
        var caretColumn = lines.Count == 0 ? 0 : lines[^1].Length;
        return (startLine, removedLines, lines, caretColumn);
    }

    /// <summary>数一段文本里的换行符个数。 Counts the newline characters in a piece of text.</summary>
    private static int CountNewlines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>取某个文档所属的对比编辑器与它显示的侧（左/右）。 Finds the compare editor and side owning a document.</summary>
    private (TextEditor? Editor, bool LeftSide) FindCompareEditorSide(TextDocument? document)
    {
        if (document is null)
        {
            return (null, true);
        }

        foreach (var (editor, leftSide) in EnumerateCompareEditors())
        {
            if (ReferenceEquals(editor.Document, document))
            {
                return (editor, leftSide);
            }
        }

        return (null, true);
    }

    /// <summary>结构变化后把光标放回目标行与列。 Restores the caret to the given line and column after a structural change.</summary>
    /// <summary>
    /// <para>差异行整行背景着色器：按行号集合画半透明色块（与行列表期间的整行变色一致）。</para>
    /// Whole-line diff background renderer: paints translucent bands for the given line numbers, matching the row list's
    /// whole-row colouring.
    /// </summary>
    private sealed class CompareLineHighlighter(Avalonia.Media.IBrush brush) : IBackgroundRenderer
    {
        private HashSet<int> _lines = [];

        public KnownLayer Layer => KnownLayer.Background;

        public void SetLines(IEnumerable<int> lines) => _lines = [.. lines];

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_lines.Count == 0 || !textView.VisualLinesValid)
            {
                return;
            }

            // 渲染里的异常会直接崩掉应用：着色失败就放弃本次绘制。
            // An exception during rendering crashes the app, so a failed paint is simply skipped.
            try
            {
                using (drawingContext.PushClip(new Rect(textView.Bounds.Size)))
                {
                    foreach (var line in textView.VisualLines)
                    {
                        if (!_lines.Contains(line.FirstDocumentLine.LineNumber))
                        {
                            continue;
                        }

                        var y = line.VisualTop - textView.ScrollOffset.Y;
                        drawingContext.FillRectangle(brush, new Rect(0, y, textView.Bounds.Width, line.Height));
                    }
                }
            }
            catch (Exception)
            {
                // 忽略本次着色。
                // Skip this paint.
            }
        }
    }
}
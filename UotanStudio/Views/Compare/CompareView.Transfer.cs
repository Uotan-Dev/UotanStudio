using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Compare;

namespace UotanStudio.Views.Compare;

// 本分部承载 CompareView 的一组同类成员；类与其余分部合并后等价于原实现。
public partial class CompareView
{
    // ===== 从行拖出到另一个目录（拖拽复制） =====

    /// <summary>按下时记录的拖拽候选（触发事件 + 行 + 起始点），移动超过阈值后才真正开始拖拽。</summary>
    private PointerPressedEventArgs? _dragTrigger;
    private CompareRowViewModel? _dragRow;
    private ListBox? _dragList;
    private Point _dragOrigin;

    /// <summary>跨行框选的起点（在某一行上按下）。</summary>
    private ListBox? _rangeList;
    private int _rangeAnchor = -1;

    /// <summary>正文拖拽的负载路径（拖到文件夹行时用它复制，避免依赖平台拖拽数据的格式差异）。</summary>
    private string? _draggingPath;

    /// <summary>拖拽来源列：跨列复制的兜底目标需要排除它。</summary>
    private CompareColumnViewModel? _dragSourceColumn;

    /// <summary>右键菜单当前作用的行与列（在行上收到 ContextRequested 时记录）。</summary>
    private CompareRowViewModel? _contextRow;
    private CompareColumnViewModel? _contextColumn;

    /// <summary>记录拖拽候选：只有文件/文件夹行（非文本编辑行）可以拖出。</summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ClearDragCandidate();
        _rangeList = null;
        _rangeAnchor = -1;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is not Control { DataContext: CompareRowViewModel row } source
            || FindListFor(source) is not { } list)
        {
            return;
        }

        var index = IndexOfRow(list, row);
        if (index < 0)
        {
            return;
        }

        // 输入框内按下：先按「文字选择」处理，但记录行锚点——指针移动到别的行时就升级为跨行选中，
        // 与 Beyond Compare 里「拖动经过多行即选多行」的手感一致。
        // A press inside an editor starts as text selection, but the row anchor is recorded: once the pointer reaches
        // another row it turns into a cross-row selection, matching dragging over lines in Beyond Compare.
        _rangeList = list;
        _rangeAnchor = index;

        // Multiple 模式下列表的点击是切换语义：点新行不会清掉旧选中，高亮会一路残留。
        // 普通左键按下（无 Ctrl/Shift）在隧道阶段先清空旧选择，列表随后的处理只会选中按下的这一行。
        // A Multiple-mode list toggles on click: pressing a new row keeps the old rows highlighted. A plain left
        // press (no Ctrl/Shift) clears the old selection in the tunnel phase so the list's own handling selects only
        // the pressed row.
        if (list.Selection is { } selection
            && (list.SelectionMode & SelectionMode.Multiple) != 0
            && !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            selection.Clear();
            selection.Select(index);
        }

        // 双击：目录行展开/折叠（对比中两侧同步），文件行在文件夹对比里开新标签的文件对比。
        // 直接按 ClickCount 判定，不依赖手势事件的路由，避免列表项消费事件导致双击失效。
        // Double-click: a folder row expands/collapses (both sides in a folder comparison) and a file row opens its
        // comparison in a new tab while comparing. It is detected from ClickCount so no gesture routing can swallow it.
        if (e.ClickCount == 2)
        {
            ActivateRow(row, list);
            return;
        }

        if (source is TextBox)
        {
            return;
        }

        // 只有文件/文件夹行可以拖出到另一个目录。
        // Only file/folder rows can be dragged out into another directory.
        if (row.TreeNode is null)
        {
            return;
        }

        _dragTrigger = e;
        _dragRow = row;
        _dragList = list;
        _dragOrigin = e.GetPosition(this);
        _dragSourceColumn = FindColumnFor(source);
    }

    /// <summary>
    /// <para>移动超过阈值时开始拖拽：把该行的完整路径放进拖拽数据（文本格式），
    /// 落到另一个文件夹行上即复制进该目录。</para>
    /// Starts a drag once the pointer moved past the threshold: the row's full path becomes the drag payload (text format),
    /// and dropping it on another folder row copies it into that folder.
    /// </summary>
    private async void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        ExtendRangeSelection(e);

        var trigger = _dragTrigger;
        var row = _dragRow;
        var list = _dragList;
        if (trigger is null || row is null || list is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ClearDragCandidate();
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragOrigin.X) < DragThreshold
            && Math.Abs(position.Y - _dragOrigin.Y) < DragThreshold)
        {
            return;
        }

        ClearDragCandidate();
        if (ResolveRowPath(row, list) is not { } path)
        {
            return;
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(path));
        // 应用内拖拽额外记录路径，作为拖放数据格式差异的兜底。
        // An in-view drag also records the path, covering platform differences in the payload's text format.
        _draggingPath = path;
        await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Copy);
    }

    /// <summary>
    /// <para>按住左键在某一行按下后拖过其它行 → 选中整段（跨行框选）；松开或移到行外即结束。</para>
    /// Dragging over other rows while the left button is held selects the whole range (cross-row selection); releasing or
    /// leaving the rows ends it.
    /// </summary>
    private void ExtendRangeSelection(PointerEventArgs e)
    {
        if (_rangeList is not { } list || _rangeAnchor < 0
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 指针下的元素给出当前行；行内的输入框/文本块都带行上下文，列表空白处则忽略。
        // The element under the pointer gives the current row: editors and text blocks inside a row carry the row context,
        // while empty list space is ignored.
        if (e.Source is not Control { DataContext: CompareRowViewModel row })
        {
            return;
        }

        var index = IndexOfRow(list, row);
        if (index < 0 || index == _rangeAnchor)
        {
            return;
        }

        var start = Math.Min(_rangeAnchor, index);
        var end = Math.Max(_rangeAnchor, index);
        var selection = list.Selection;
        if (selection is null)
        {
            return;
        }

        selection.Clear();
        selection.SelectRange(start, end);
    }

    /// <summary>取行在列表中的索引（列表的 ItemsSource 就是文档的行列表）。</summary>
    private static int IndexOfRow(ListBox list, CompareRowViewModel row)
    {
        if (list.ItemsSource is not IReadOnlyList<CompareRowViewModel> rows)
        {
            return -1;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            if (ReferenceEquals(rows[index], row))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>清空当前拖拽候选。</summary>
    private void ClearDragCandidate()
    {
        _dragTrigger = null;
        _dragRow = null;
        _dragList = null;
    }

    /// <summary>解析树行对应的绝对路径（浏览行自带路径，对比行用所在侧的根目录加相对路径）。</summary>
    private string? ResolveRowPath(CompareRowViewModel row, ListBox list)
    {
        if (row.TreeNode is not { } node)
        {
            return null;
        }

        if (node.FullPath.Length > 0)
        {
            return node.FullPath;
        }

        if (DataContext is not CompareViewModel vm || FindColumnFor(list) is not { } column)
        {
            return null;
        }

        var root = column.Side == CompareColumnSide.Right && vm.IsComparing ? vm.RightPath : column.Path;
        return root.Length == 0
            ? null
            : Path.Combine(root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    // ===== 右键菜单（条目操作与文本复制） =====

    /// <summary>
    /// <para>文本对比：把选中行的文本复制到同一行的另一侧（右侧模板传入 Tag=Right）。</para>
    /// Text comparison: copies the selected rows' text to the same rows on the other side (the right template passes
    /// Tag=Right).
    /// </summary>
    private void OnCopyTextToOtherSide(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CompareViewModel vm)
        {
            vm.CopySelectedTextRows(fromLeftToRight: !ContextIsRightSide(sender));
        }
    }

    /// <summary>目录对比：把该行条目复制到另一边。</summary>
    private async void OnCopyToOtherSide(object? sender, RoutedEventArgs e)
        => await TransferComparedEntryAsync(sender, move: false);

    /// <summary>目录对比：把该行条目移动到另一边。</summary>
    private async void OnMoveToOtherSide(object? sender, RoutedEventArgs e)
        => await TransferComparedEntryAsync(sender, move: true);

    /// <summary>
    /// <para>浏览状态：把该行条目复制/移动到相邻列的目录（Tag 给出方向）。</para>
    /// Browsing: copies or moves the row's entry into the neighbouring column's folder (the tag gives the direction).
    /// </summary>
    private async void OnCopyToNeighbourColumn(object? sender, RoutedEventArgs e)
        => await TransferToNeighbourAsync(sender, move: false);

    /// <summary>浏览状态：把该行条目移动到相邻列的目录。</summary>
    private async void OnMoveToNeighbourColumn(object? sender, RoutedEventArgs e)
        => await TransferToNeighbourAsync(sender, move: true);

    /// <summary>删除该行条目（目录递归删除），确认后执行并刷新视图。</summary>
    private async void OnDeleteEntry(object? sender, RoutedEventArgs e)
    {
        var (path, isDirectory, column) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        var owner = DialogService.GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (!await DialogService.ConfirmAsync(owner,
                LanguageService.Instance.GetString("Dialog_ConfirmDelete"),
                string.Format(LanguageService.Instance.GetString("Dialog_ConfirmDeleteMessage"), name),
                LanguageService.Instance.GetString("Menu_Delete")))
        {
            return;
        }

        try
        {
            if (isDirectory)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }

            await ReloadAfterEntryChangeAsync(column);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(ex);
        }
    }

    /// <summary>重命名该行条目（文件或目录），成功后刷新视图。</summary>
    private async void OnRenameEntry(object? sender, RoutedEventArgs e)
    {
        var (path, isDirectory, column) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        var owner = DialogService.GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        var oldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var newName = await DialogService.InputAsync(owner, LanguageService.Instance.GetString("Dialog_Rename"), oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName
            || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return;
        }

        var target = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, newName);
        try
        {
            if (isDirectory)
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            await ReloadAfterEntryChangeAsync(column);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(ex);
        }
    }

    /// <summary>在该行所在目录（目录行则为它本身）新建文件夹，成功后刷新视图。</summary>
    private async void OnCreateFolder(object? sender, RoutedEventArgs e)
    {
        var (path, isDirectory, column) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        var owner = DialogService.GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        var directory = isDirectory ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        if (await ExplorerItemCreationService.CreateAsync(owner, directory, isDirectory: true) is not null)
        {
            await ReloadAfterEntryChangeAsync(column);
        }
    }

    /// <summary>复制该行条目的完整路径到剪贴板。</summary>
    private async void OnCopyEntryPath(object? sender, RoutedEventArgs e)
    {
        var (path, _, _) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(path);
        }
    }

    /// <summary>在系统文件资源管理器中定位该行条目。</summary>
    /// <summary>
    /// <para>浏览状态下「进入此文件夹」：把该列重新根到双击的目录（路径框同步为新路径）。</para>
    /// "Enter this folder" while browsing: re-roots the column at the chosen directory (the path box follows).
    /// </summary>
    private async void OnEnterFolder(object? sender, RoutedEventArgs e)
    {
        if (_contextRow?.TreeNode is not { IsDirectory: true } node || _contextColumn is not { } column)
        {
            return;
        }

        await column.EnterDirectoryAsync(node);
    }

    private void OnOpenEntryInExplorer(object? sender, RoutedEventArgs e)
    {
        var (path, _, _) = ResolveEntry(sender);
        if (path is null)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(path))
            {
                // Windows 上定位到文件本身；其他平台（或目录）直接打开所在目录。
                // On Windows select the file itself; other platforms (or directories) open the containing folder.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }

            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    /// <summary>目录对比中把某侧条目复制/移动到另一侧（同一相对路径）。</summary>
    private async Task TransferComparedEntryAsync(object? sender, bool move)
    {
        var (path, _, column) = ResolveEntry(sender);
        if (DataContext is not CompareViewModel vm || path is null)
        {
            return;
        }

        var sourceIsLeft = !ContextIsRightSide(sender);
        var targetColumn = sourceIsLeft
            ? vm.Columns.FirstOrDefault(candidate => candidate.Side == CompareColumnSide.Right)
            : vm.Columns.FirstOrDefault(candidate => candidate.Side == CompareColumnSide.Left);
        var target = targetColumn?.Path;
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
        {
            vm.ReportError(LanguageService.Instance.GetString("Compare_NoNeighbourFolder"));
            return;
        }

        await TransferAsync(vm, column ?? _contextColumn, path, target, move);
    }

    /// <summary>浏览状态中把条目复制/移动到相邻列的目录。</summary>
    private async Task TransferToNeighbourAsync(object? sender, bool move)
    {
        var (path, _, column) = ResolveEntry(sender);
        if (DataContext is not CompareViewModel vm || path is null || column is null)
        {
            return;
        }

        var index = vm.Columns.IndexOf(column);
        var neighbourIndex = index + (ContextIsRightSide(sender) ? 1 : -1);
        var target = neighbourIndex >= 0 && neighbourIndex < vm.Columns.Count
            ? vm.Columns[neighbourIndex].Path
            : string.Empty;
        if (target.Length == 0 || !Directory.Exists(target))
        {
            vm.ReportError(LanguageService.Instance.GetString("Compare_NoNeighbourFolder"));
            return;
        }

        await TransferAsync(vm, column, path, target, move);
    }

    /// <summary>
    /// <para>执行一次不覆盖的复制/移动（复用资源管理器的传输实现），成功后刷新相关视图。</para>
    /// Runs one non-overwriting copy/move (reusing the explorer's transfer implementation) and refreshes the affected
    /// views afterwards.
    /// </summary>
    private async Task TransferAsync(
        CompareViewModel vm,
        CompareColumnViewModel? column,
        string source,
        string targetDirectory,
        bool move)
    {
        try
        {
            ExplorerFileOperations.Transfer(source, targetDirectory, move);
            await ReloadAfterEntryChangeAsync(column);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(ex);
        }
    }

    /// <summary>
    /// <para>条目变化（复制/移动/删除/重命名/新建文件夹）后刷新视图：对比中重新比对两侧，
    /// 浏览中重新加载该列。</para>
    /// Refreshes the view after an entry changed (copy/move/delete/rename/new folder): comparing re-runs the
    /// comparison and browsing reloads that column.
    /// </summary>
    private async Task ReloadAfterEntryChangeAsync(CompareColumnViewModel? column)
    {
        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        if (vm.IsComparing)
        {
            await vm.RefreshCompareAsync();
            return;
        }

        if (column is not null)
        {
            await column.ReloadContentAsync();
            return;
        }

        // 拿不到具体列（落点在工具栏/列间隔等）时刷新所有列，保证复制进去的文件一定出现在列表里。
        // When no specific column could be resolved (the drop landed on the toolbar or a divider) every column is reloaded so the copied
        // file always shows up.
        foreach (var visuals in _columnVisuals.ToList())
        {
            await visuals.Column.ReloadContentAsync();
        }
    }

    /// <summary>把操作失败写入对比视图的错误区（并留下日志）。</summary>
    private void ReportOperationFailure(Exception ex)
    {
        LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        if (DataContext is CompareViewModel vm)
        {
            vm.ReportError(global::UotanStudio.Services.UserError.Describe(ex));
        }
    }

    /// <summary>
    /// <para>解析菜单/拖拽目标对应的条目：返回绝对路径、是否为目录与所在列。
    /// 浏览行的节点自带绝对路径；对比行用点击那一侧的对比根目录加相对路径还原。</para>
    /// Resolves the entry behind a menu click or drop: absolute path, whether it is a directory and the owning column.
    /// A browsing row's node carries the absolute path; a compared row rebuilds it from the clicked side's comparison
    /// root plus the relative path.
    /// </summary>
    private (string? Path, bool IsDirectory, CompareColumnViewModel? Column) ResolveEntry(object? sender)
    {
        // 优先使用右键时记录的上下文行（菜单项本身在弹出层里，取不到列）；直接调用（如拖放）时回退到 sender。
        // The row recorded by the context request wins (menu items live in a popup and cannot resolve a column); a direct
        // call (drops) falls back to the sender.
        var row = _contextRow
            ?? (sender as Control)?.DataContext as CompareRowViewModel;
        if (DataContext is not CompareViewModel vm || row?.TreeNode is not { } node)
        {
            return (null, false, null);
        }

        var column = _contextColumn
            ?? ((sender as Control) is { } control ? FindColumnFor(control) : null);
        if (node.FullPath.Length > 0)
        {
            return (node.FullPath, node.IsDirectory, column);
        }

        if (column is null)
        {
            return (null, node.IsDirectory, null);
        }

        var sourceIsLeft = !IsRightTagged(sender);
        var rootColumn = vm.IsComparing
            ? vm.Columns.FirstOrDefault(candidate => candidate.Side == (sourceIsLeft ? CompareColumnSide.Left : CompareColumnSide.Right)) ?? column
            : column;
        var root = rootColumn.Path;
        if (root.Length == 0)
        {
            return (null, node.IsDirectory, column);
        }

        var relative = node.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        return (Path.Combine(root, relative), node.IsDirectory, column);
    }

    /// <summary>读取菜单项的 Tag 判断它是否来自右侧模板。</summary>
    private static bool IsRightTagged(object? sender)
        => sender is MenuItem { Tag: string tag } && string.Equals(tag, "Right", StringComparison.Ordinal);

    /// <summary>判断右键目标是否位于右侧栏：优先用右键时记录的列，回退到菜单项 Tag。</summary>
    private bool ContextIsRightSide(object? sender)
        => _contextColumn is { UsesRightCells: true } || IsRightTagged(sender);

    /// <summary>
    /// <para>右键请求：行没有提供菜单（十六进制/元数据行、浏览状态下的文本行）时吞掉请求，
    /// 避免弹出空菜单；其余情况交给行上挂的 <c>ContextMenu</c>。</para>
    /// Context request: when the row offers no menu (hex/metadata rows and text rows while browsing) the request is
    /// swallowed so no empty popup appears; otherwise the row's <c>ContextMenu</c> opens as usual.
    /// </summary>
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control { DataContext: CompareRowViewModel row } control)
        {
            return;
        }

        // 菜单项在弹出层里，取不到行列表祖先；这里记录右键目标（行 + 列），菜单命令据此定位文件。
        // Menu items live in a popup and cannot find the row list through their ancestors, so the context request records
        // the target (row + column) and the menu commands resolve the file from it.
        _contextRow = row;
        _contextColumn = FindColumnFor(control);
        if (row.Menu == CompareRowMenuKind.None)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// <para>拖拽悬停事件：仅接受文件/文件夹路径的复制拖放（Avalonia 12 的
    /// <c>DataTransfer</c> 数据载体与 <c>DataFormat.File</c> 格式）。
    /// OS 拖拽（如 Windows 资源管理器）的文件格式可能以平台实例暴露，
    /// <c>Contains(DataFormat.File)</c> 不命中，故再以 <c>TryGetFiles</c> 直接探测，
    /// 保证光标与 Drop 的实际消费结果一致。</para>
    /// Drag-over event: accepts copy drops of file/folder paths only (Avalonia 12's
    /// <c>DataTransfer</c> payload with the <c>DataFormat.File</c> format).
    /// OS-originated drags (e.g. from Windows Explorer) may expose the file format as a
    /// platform instance that <c>Contains(DataFormat.File)</c> misses, so the handler also
    /// probes <c>TryGetFiles</c> directly, keeping the cursor in sync with what Drop consumes.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFilePayload(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// <para>判定拖放数据是否携带文件/文件夹路径：先检查通用文件格式（应用内/进程内拖拽），
    /// 再以 <c>TryGetFiles</c> 直接探测（与 Drop 消费一致），最后扫描格式列表中的系统文件格式名。</para>
    /// Determines whether the drag payload carries file/folder paths: checks the universal file format
    /// first (in-process drags), then probes <c>TryGetFiles</c> directly (matching what Drop consumes),
    /// and finally scans the format list for the OS file-format name.
    /// </summary>
    private bool HasFilePayload(IDataTransfer transfer)
        => UotanStudio.Views.Controls.FileDropPaths.HasFilePayload(transfer, "CompareView")
            || LooksLikeExistingPath(transfer)
            || _draggingPath is not null;

    /// <summary>
    /// <para>拖放事件：把拖入的文件/文件夹路径交给视图模型，每个路径占用一个文件列
    /// （含 OS 拖拽的平台格式提取）。立即标记 <c>Handled</c> 并消费数据，再异步加载——避免异步
    /// 异常导致拖放状态丢失。</para>
    /// Drop event: hands the dropped file/folder paths to the view model, where each path takes one file
    /// column (including platform-format extraction for OS drags). Marks <c>Handled</c> and consumes the
    /// payload immediately, then loads asynchronously — so an async failure cannot leave the drop
    /// unhandled.
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not CompareViewModel vm)
        {
            return;
        }

        var paths = ExtractDropPaths(e.DataTransfer);
        var inViewDrag = _draggingPath is not null;
        _draggingPath = null;
        if (paths.Count == 0)
        {
            return;
        }

        // 拖到某个文件夹行上 → 复制进该目录（从资源管理器或应用内拖一个文件到另一个目录即可完成复制）。
        // Dropping onto a folder row copies into that folder (drag a file from the explorer or from the app onto another
        // directory to copy it).
        // 视图内拖拽（跨列复制）再按指针位置兜底：落在列表空白、列头、列间隔或工具栏上时同样能定位目标列，
        // 否则「拖到另一列」会因为落点不在行上而毫无反应。
        // An in-view drag (cross-column copy) also falls back to the pointer position so dropping on empty list space, a column header,
        // a divider or the toolbar still resolves the target column instead of doing nothing.
        var dropTarget = ResolveDropTarget(e.Source, inViewDrag)
            ?? (inViewDrag ? ResolveInViewDropFolder(e.GetPosition(this)) : null);
        if (dropTarget is not null)
        {
            var copied = false;
            foreach (var path in paths)
            {
                try
                {
                    ExplorerFileOperations.Transfer(path, dropTarget, move: false);
                    copied = true;
                }
                catch (Exception ex)
                {
                    ReportOperationFailure(ex);
                }
            }

            // 复制成功后自动刷新目标列（对比中重新比对、浏览中重新枚举目录内容），不需要再手动点刷新。
            // After a successful copy the target column refreshes itself (re-compares while comparing, re-enumerates the folder while
            // browsing), so no manual refresh is needed.
            if (copied)
            {
                var column = ResolveEntry(e.Source) is var (_, _, resolved) ? resolved : null;
                column ??= _columnVisuals
                    .Select(visuals => visuals.Column)
                    .FirstOrDefault(candidate => string.Equals(candidate.Path, dropTarget, StringComparison.OrdinalIgnoreCase));
                await ReloadAfterEntryChangeAsync(column);
            }

            return;
        }

        if (paths.Count > 0)
        {
            await vm.TryAddPathsAsync(paths);
        }
    }

    /// <summary>
    /// <para>解析拖拽落点：落在目录树行（目录）上时返回该目录的绝对路径，否则返回 <c>null</c>（按「加载为新列」处理）。</para>
    /// Resolves a drop target: a directory tree row yields that directory's absolute path, anything else returns
    /// <c>null</c> (handled as "load into columns").
    /// </summary>
    /// <param name="source"><para>拖放命中的元素。</para>The element hit by the drop.</param>
    /// <param name="inViewDrag"><para>是否来自视图内的行拖拽（可放宽落点：同一列里的任何位置都算落到该列目录）。</para>Whether the drag started inside the view (its target may fall back to the column's own folder).</param>
    /// <summary>
    /// <para>视图内拖拽按指针位置兜底定位目标目录：先用指针所在列的目录，再退一步取「不是拖拽来源列」的第一个目录列。</para>
    /// Position-based fallback for an in-view drag: first the folder of the column under the pointer, then the first folder column that
    /// is not the drag source.
    /// </summary>
    /// <param name="position"><para>指针在视图内的坐标。</para>The pointer position inside the view.</param>
    private string? ResolveInViewDropFolder(Point position)
    {
        var column = ResolveColumnAt(position);
        if (column is not null && Directory.Exists(column.Path))
        {
            return column.Path;
        }

        foreach (var visuals in _columnVisuals)
        {
            if (!ReferenceEquals(visuals.Column, _dragSourceColumn) && Directory.Exists(visuals.Column.Path))
            {
                return visuals.Column.Path;
            }
        }

        return null;
    }

    /// <summary>取指针所在（或最接近）的列。 Returns the column under the pointer, or the closest one.</summary>
    private CompareColumnViewModel? ResolveColumnAt(Point position)
    {
        CompareColumnViewModel? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var visuals in _columnVisuals)
        {
            var bounds = visuals.Container.Bounds;
            if (bounds.Contains(position))
            {
                return visuals.Column;
            }

            var distance = Math.Min(Math.Abs(position.X - bounds.Left), Math.Abs(position.X - bounds.Right));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = visuals.Column;
            }
        }

        return nearest;
    }

    private string? ResolveDropTarget(object? source, bool inViewDrag)
    {
        if (source is not Control { DataContext: CompareRowViewModel row } control
            || row.TreeNode is not { IsDirectory: true } node)
        {
            // 视图内拖拽落在非目录行上时，退化为「落到该行所在列的目录」。
            // An in-view drag dropped on a non-folder row falls back to the folder of that row's column.
            if (inViewDrag && source is Control controlOnRow
                && FindColumnFor(controlOnRow) is { } fallbackColumn
                && Directory.Exists(fallbackColumn.Path))
            {
                return fallbackColumn.Path;
            }

            return null;
        }

        if (node.FullPath.Length > 0)
        {
            return node.FullPath;
        }

        var column = FindColumnFor(control);
        if (column is null)
        {
            return null;
        }

        var root = column.Side == CompareColumnSide.Right && DataContext is CompareViewModel comparing && comparing.IsComparing
            ? comparing.RightPath
            : column.Path;
        return root.Length == 0
            ? null
            : Path.Combine(root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// <para>从拖放数据提取文件/文件夹路径：先按通用文件格式取条目，失败时再按平台文件
    /// 格式（<c>FileDrop</c>）取条目数据（宽容枚举 <see cref="IStorageItem"/>）。</para>
    /// Extracts file/folder paths from the drag payload: first via the universal file format, then
    /// via the platform file format (<c>FileDrop</c>) reading the item data as an enumerable of
    /// <see cref="IStorageItem"/>.
    /// </summary>
    private static IReadOnlyList<string> TryExtractPaths(IDataTransfer transfer)
        => UotanStudio.Views.Controls.FileDropPaths.TryExtractPaths(transfer, "CompareView");

    /// <summary>
    /// <para>从拖放数据取路径：优先平台文件格式（资源管理器等外部拖拽），其次文本格式
    /// （视图内从行拖出的负载，必须是真实存在的路径）。</para>
    /// Extracts paths from a drag payload: the platform file formats first (external drags such as Explorer), then the
    /// text format (an in-view row drag, which must name an existing path).
    /// </summary>
    private IReadOnlyList<string> ExtractDropPaths(IDataTransfer transfer)
    {
        var paths = TryExtractPaths(transfer);
        if (paths.Count > 0)
        {
            return paths;
        }

        if (TryGetDragText(transfer) is { } text && (File.Exists(text) || Directory.Exists(text)))
        {
            return [text];
        }

        // 应用内拖拽：拖放数据的文本格式取不到时用拖出时记录的路径。
        // In-view drags: when the payload's text format cannot be read, use the path recorded when the drag started.
        if (_draggingPath is { } recorded && (File.Exists(recorded) || Directory.Exists(recorded)))
        {
            return [recorded];
        }

        return paths;
    }

    /// <summary>判断拖放数据是否是「视图内拖出的行路径」（用于拖拽光标反馈）。</summary>
    private static bool LooksLikeExistingPath(IDataTransfer transfer)
        => TryGetDragText(transfer) is { } text && (File.Exists(text) || Directory.Exists(text));

    /// <summary>读取拖放数据中的文本负载（内建拖拽用文本格式携带路径）；异常与空文本返回 <c>null</c>。</summary>
    private static string? TryGetDragText(IDataTransfer transfer)
    {
        try
        {
            var text = transfer.TryGetText();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
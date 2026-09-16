using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services.Compare;
using UotanStudio.ViewModels.Compare;
using Xunit;

namespace UotanStudio.Tests.Compare;

/// <summary>
/// <para>对比文档的文件列契约（对齐 Beyond Compare 的行为）：默认两列、可继续追加（浏览用）、导入目录后
/// 保持折叠；文件夹对比把两侧压平成同一份对齐行列表——同名条目同一行、单侧条目在另一侧留空、差异整行变色、
/// 两侧共享展开状态；双击文件在新标签打开文件对比；对比中的刷新与返回上一级同时作用于两侧。</para>
/// File-column contract of the compare document (aligned with Beyond Compare): two columns by default, freely
/// appendable for browsing, an imported folder stays collapsed; a folder comparison flattens both sides into one
/// aligned row list — same-name entries share a row, single-side entries stay blank on the other side, differing
/// rows are coloured as a whole and both sides share one expansion state; double-clicking a file opens its
/// comparison in a new tab; refresh and go-up act on both sides while comparing.
/// </summary>
public sealed class CompareDocumentColumnsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"uotan-compare-columns-{Guid.NewGuid():N}");
    private readonly CompareViewModel _document = new(ComparisonService.CreateDefault());

    public CompareDocumentColumnsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Constructor_StartsWithTwoEmptyColumns()
    {
        Assert.Equal(2, _document.Columns.Count);
        Assert.All(_document.Columns, column => Assert.False(column.HasPath));
        Assert.True(_document.IsEmptyDocument);
        Assert.All(_document.Columns, column => Assert.True(column.ShowHint));
    }

    [Fact]
    public async Task AddComparisonPath_ImportedFolder_ListsItsContents()
    {
        var folder = Path.Combine(_root, "left");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));

        await _document.AddComparisonPathAsync(folder);

        var column = _document.Columns[0];
        var root = column.TreeRoot;
        Assert.NotNull(root);
        Assert.False(root.IsExpanded);
        Assert.Null(root.Status);
        Assert.Equal(folder, root.FullPath);

        // 导入文件夹直接列出它里面的内容：根节点本身不作为一行，子项就是顶层行（子文件夹保持折叠）。
        // Importing a folder lists its contents straight away: the root itself is not a row and its children are the top-level rows (a
        // subfolder stays collapsed).
        var row = Assert.Single(column.Rows!);
        Assert.True(row.IsTreeRow);
        Assert.True(row.TreeNode!.IsExpandable);
        Assert.Equal(0, row.Depth);
        Assert.Equal("nested", row.LeftText);
        Assert.False(row.TreeNode.IsExpanded);
        Assert.Equal(folder, _document.LeftPath);
        Assert.False(_document.IsEmptyDocument);
    }

    [Fact]
    public async Task ExpandedFolder_ListsDirectChildrenWithDirectoriesFirst()
    {
        var folder = Path.Combine(_root, "tree");
        WriteFile("tree/zdir/inner.txt", "i");
        WriteFile("tree/afile.txt", "a");

        await _document.AddComparisonPathAsync(folder);
        var column = _document.Columns[0];

        // 顶层行就是导入文件夹的内容（目录优先），子文件夹保持折叠。
        // The top-level rows are the imported folder's contents (directories first) with subfolders collapsed.
        Assert.Equal(["zdir", "afile.txt"], column.Rows!.Select(row => row.LeftText));
        Assert.Equal([0, 0], column.Rows!.Select(row => row.Depth));

        // 双击子文件夹展开：孩子插在原位并缩进一级；再双击收起。
        // Double-clicking a subfolder expands it (children appear in place, indented one level) and double-clicking again collapses it.
        var subfolder = column.Rows![0].TreeNode!;
        await column.ToggleTreeExpansionAsync(subfolder);
        Assert.True(subfolder.IsExpanded);
        Assert.Equal(["zdir", "inner.txt", "afile.txt"], column.Rows!.Select(row => row.LeftText));
        Assert.Equal([0, 1, 0], column.Rows!.Select(row => row.Depth));

        await column.ToggleTreeExpansionAsync(subfolder);
        Assert.False(subfolder.IsExpanded);
        Assert.Equal(["zdir", "afile.txt"], column.Rows!.Select(row => row.LeftText));
    }

    [Fact]
    public async Task AddedPaths_TakeOneColumnEachWithoutStartingAComparison()
    {
        var paths = new[]
        {
            WriteFile("a.txt", "a"),
            WriteFile("b.txt", "b"),
            WriteFile("c.txt", "c"),
        };

        Assert.True(await _document.TryAddPathsAsync(paths));

        Assert.Equal(3, _document.Columns.Count);
        Assert.All(_document.Columns, column => Assert.True(column.HasPath));
        Assert.False(_document.IsComparing);
        Assert.All(_document.Columns, column => Assert.True(column.IsBrowsing));
    }

    [Fact]
    public async Task AddedPaths_WhileComparing_AreRejected()
    {
        await _document.AddComparisonPathAsync(WriteFile("left.txt", "one\n"));
        await _document.AddComparisonPathAsync(WriteFile("right.txt", "two\n"));
        await _document.ToggleCompareCommand.ExecuteAsync(null);
        Assert.True(_document.IsComparing);

        Assert.False(await _document.TryAddPathsAsync([WriteFile("d.txt", "d")]));
        Assert.True(_document.HasError);
        Assert.Equal(2, _document.Columns.Count);
    }

    [Fact]
    public async Task ToggleCompare_WithoutExactlyTwoLoadedColumns_ReportsAnError()
    {
        await _document.AddComparisonPathAsync(WriteFile("only.txt", "only"));

        await _document.ToggleCompareCommand.ExecuteAsync(null);
        Assert.True(_document.HasError);
        Assert.False(_document.IsComparing);

        await _document.AddComparisonPathAsync(WriteFile("second.txt", "second"));
        await _document.AddComparisonPathAsync(WriteFile("third.txt", "third"));
        await _document.ToggleCompareCommand.ExecuteAsync(null);
        Assert.True(_document.HasError);
        Assert.False(_document.IsComparing);
    }

    [Fact]
    public async Task ToggleCompare_TwoFolders_AlignsRowsAndColoursDifferences()
    {
        CreateCompareFixture();
        await _document.AddComparisonPathAsync(Path.Combine(_root, "left"));
        await _document.AddComparisonPathAsync(Path.Combine(_root, "right"));

        await _document.ToggleCompareCommand.ExecuteAsync(null);

        Assert.True(_document.IsComparing);
        Assert.Equal(CompareMode.Folder, _document.Mode);
        Assert.Equal(2, _document.EqualCount);
        Assert.Equal(2, _document.DifferentCount);
        Assert.Equal(1, _document.LeftOnlyCount);
        Assert.Equal(1, _document.RightOnlyCount);
        Assert.NotEmpty(_document.StatsText);

        var left = _document.Columns[0];
        var right = _document.Columns[1];
        Assert.Equal(CompareColumnSide.Left, left.Side);
        Assert.Equal(CompareColumnSide.Right, right.Side);

        // 两栏共享同一份对齐行列表：同名条目落在同一行，单侧条目在另一侧自动留空。
        // Both panes share one aligned row list: same-name entries share a row and single-side entries stay blank.
        Assert.NotNull(_document.Rows);
        Assert.Same(_document.Rows, left.Rows);
        Assert.Same(_document.Rows, right.Rows);

        // 对比一开始就展开最初始的文件夹：视图直接显示目录内的条目，根行按子树聚合为「不同」→ 两侧整行变色。
        // The initial folder is expanded from the start so the directory's entries show immediately; the root row is aggregated to
        // "different" from its subtree and is coloured on both sides.
        var rootRow = _document.Rows![0];
        Assert.True(rootRow.IsTreeRow);
        Assert.Equal("left", rootRow.LeftText);
        Assert.Equal("right", rootRow.RightText);
        Assert.True(rootRow.IsLeftDiff);
        Assert.True(rootRow.IsRightDiff);

        var rows = _document.Rows!;
        Assert.Equal(
            ["left", "sub", "diff.txt", "leftonly.txt", "rightonly.txt", "same.txt"],
            rows.Select(row => string.IsNullOrEmpty(row.LeftText) ? row.RightText : row.LeftText));
        Assert.Equal([0, 1, 1, 1, 1, 1], rows.Select(row => row.Depth));

        // 收起最初始的文件夹：只剩根行；再展开又列出内容（两侧共享同一节点，同步展开/收起）。
        // Collapsing the initial folder leaves only the root row; expanding it lists the contents again (both sides share one node).
        await _document.ToggleTreeExpansionAsync(rootRow.TreeNode!);
        Assert.Single(_document.Rows!);
        await _document.ToggleTreeExpansionAsync(rootRow.TreeNode!);
        Assert.Equal(6, _document.Rows!.Count);

        var sub = rows[1];
        Assert.True(sub.IsLeftDiff);
        Assert.True(sub.IsRightDiff);

        var different = rows[2];
        Assert.Equal("diff.txt", different.LeftText);
        Assert.Equal("diff.txt", different.RightText);
        Assert.True(different.IsLeftDiff);
        Assert.True(different.IsRightDiff);

        var leftOnly = rows[3];
        Assert.Equal("leftonly.txt", leftOnly.LeftText);
        Assert.True(leftOnly.IsRightEmpty);
        Assert.NotNull(leftOnly.LeftTintBrush);
        Assert.Null(leftOnly.RightTintBrush);

        var rightOnly = rows[4];
        Assert.True(rightOnly.IsLeftEmpty);
        Assert.Equal("rightonly.txt", rightOnly.RightText);
        Assert.NotNull(rightOnly.RightTintBrush);
        Assert.Null(rightOnly.LeftTintBrush);

        var equal = rows[5];
        Assert.False(equal.IsLeftDiff);
        Assert.False(equal.IsRightDiff);
        Assert.Null(equal.LeftTintBrush);
        Assert.Null(equal.RightTintBrush);

        // 结束对比回到普通浏览：列出文件夹内容、没有状态色、子文件夹保持折叠。
        // Ending the comparison returns to plain browsing: the folder's contents are listed, no status colours and subfolders stay collapsed.
        await _document.ToggleCompareCommand.ExecuteAsync(null);
        Assert.False(_document.IsComparing);
        Assert.Equal(CompareMode.None, _document.Mode);
        Assert.Equal(
            ["sub", "diff.txt", "leftonly.txt", "same.txt"],
            left.Rows!.Select(row => row.LeftText));
        Assert.All(left.Rows!, row => Assert.True(row.IsTreeRow));
        Assert.All(left.Rows!, row => Assert.False(row.IsLeftDiff));
        Assert.All(left.Rows!, row => Assert.Null(row.TreeNode!.Status));
    }

    [Fact]
    public async Task OpenFileComparisonInNewTab_SendsBothSidesAndKeepsTheFolderComparison()
    {
        CreateCompareFixture();
        await _document.AddComparisonPathAsync(Path.Combine(_root, "left"));
        await _document.AddComparisonPathAsync(Path.Combine(_root, "right"));
        await _document.ToggleCompareCommand.ExecuteAsync(null);
        // 对比一开始就展开最初始的文件夹，内容直接可见（无需再展开）。
        // The initial folder starts expanded, so its entries are visible without expanding anything.
        var rootRow = _document.Rows![0];

        var received = new List<OpenFileCompareInNewTabRequestMessage>();
        void Handler(object recipient, OpenFileCompareInNewTabRequestMessage message) => received.Add(message);
        WeakReferenceMessenger.Default.Register<OpenFileCompareInNewTabRequestMessage>(this, Handler);
        try
        {
            var different = _document.Rows!.First(row => row.LeftText == "diff.txt");
            _document.OpenFileComparisonInNewTab(different.TreeNode!);

            var request = Assert.Single(received);
            Assert.Equal(Path.Combine(_root, "left", "diff.txt"), request.LeftPath);
            Assert.Equal(Path.Combine(_root, "right", "diff.txt"), request.RightPath);

            // 单侧文件：另一侧留空，当前视图仍是原来的目录对比。
            // A single-side file leaves the other side empty, and the current view stays the folder comparison.
            var leftOnly = _document.Rows!.First(row => row.LeftText == "leftonly.txt");
            _document.OpenFileComparisonInNewTab(leftOnly.TreeNode!);

            Assert.Equal(2, received.Count);
            Assert.Equal(Path.Combine(_root, "left", "leftonly.txt"), received[1].LeftPath);
            Assert.Empty(received[1].RightPath);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<OpenFileCompareInNewTabRequestMessage>(this);
        }

        Assert.True(_document.IsComparing);
        Assert.Equal(CompareMode.Folder, _document.Mode);
        Assert.NotNull(_document.Rows);
    }

    [Fact]
    public async Task GoUp_WhileComparing_MovesBothSides()
    {
        CreateCompareFixture();
        var leftRoot = Path.Combine(_root, "left");
        var rightRoot = Path.Combine(_root, "right");
        await _document.AddComparisonPathAsync(leftRoot);
        await _document.AddComparisonPathAsync(rightRoot);
        await _document.ToggleCompareCommand.ExecuteAsync(null);

        await _document.GoUpCompareAsync();

        Assert.Equal(_root, _document.Columns[0].Path);
        Assert.Equal(_root, _document.Columns[1].Path);
        Assert.Equal(_root, _document.LeftPath);
        Assert.True(_document.IsComparing);
    }

    [Fact]
    public async Task ToggleCompare_TwoTextFiles_AlignsRowsThenReturnsToBrowsing()
    {
        await _document.AddComparisonPathAsync(WriteFile("left.txt", "one\ntwo\n"));
        await _document.AddComparisonPathAsync(WriteFile("right.txt", "one\ntwo changed\n"));

        await _document.ToggleCompareCommand.ExecuteAsync(null);

        Assert.True(_document.IsComparing);
        Assert.Equal(CompareMode.Text, _document.Mode);
        Assert.False(_document.HasError);

        var left = _document.Columns[0];
        var right = _document.Columns[1];
        Assert.Equal(CompareColumnSide.Left, left.Side);
        Assert.Equal(CompareColumnSide.Right, right.Side);
        Assert.NotNull(_document.Rows);
        Assert.Same(_document.Rows, left.Rows);
        Assert.Same(_document.Rows, right.Rows);
        Assert.Contains(_document.Rows!, row => row.IsLeftDiff || row.IsRightDiff);
        // 文本对比改用项目文本编辑器：行列表让位给编辑器，行选择仍驱动复制命令。
        // A text comparison uses the project's text editors: the row list yields to the editors while the row selection keeps
        // driving the copy commands.
        Assert.True(_document.IsTextEditorMode);
        Assert.True(left.IsCompareTextEditorVisible);
        Assert.True(right.IsCompareTextEditorVisible);
        Assert.False(left.ShowLeftCells);
        Assert.False(right.ShowRightCells);

        await _document.ToggleCompareCommand.ExecuteAsync(null);

        Assert.False(_document.IsComparing);
        Assert.Equal(CompareMode.None, _document.Mode);
        // 结束后两列回到浏览状态：文本文件用文本编辑器打开，各自显示自己的内容。
        // Ending returns both columns to browsing: a text file opens in the text editor with its own content.
        Assert.Null(left.Rows);
        Assert.Null(right.Rows);
        Assert.True(left.IsTextEditorVisible);
        Assert.True(right.IsTextEditorVisible);
        Assert.NotEqual(left.EditorText, right.EditorText);
    }

    [Fact]
    public async Task OpenCompareFiles_WithOneEmptySide_LeavesThatColumnBlank()
    {
        var file = WriteFile("only.txt", "content\n");

        await _document.OpenCompareFilesAsync(file, string.Empty);
        Assert.Equal(CompareMode.Text, _document.Mode);
        Assert.True(_document.IsTextEditorMode);
        Assert.All(_document.Rows!, row => Assert.True(row.IsRightEmpty));
        Assert.All(_document.Rows!, row => Assert.True(row.IsLeftDiff));
        // 单侧对比中另一侧的全是填充槽（编辑器里显示斜杠）。
        // In a single-side comparison the other side is entirely filler slots (shown as slashes in the editor).
        Assert.Equal("/", _document.ProjectText(leftSide: false));
        Assert.False(_document.CanShowHexView);

        // 文件在右侧（新标签里「对侧没有对应文件」的另一种方向）：左侧整列留空。
        // The file on the right (the other direction of "no counterpart"): the left column stays blank.
        await _document.OpenCompareFilesAsync(string.Empty, file);
        Assert.Equal(CompareMode.Text, _document.Mode);
        Assert.All(_document.Rows!, row => Assert.True(row.IsLeftEmpty));
        Assert.All(_document.Rows!, row => Assert.True(row.IsRightDiff));
        Assert.Equal("/", _document.ProjectText(leftSide: true));
        Assert.Equal(CompareColumnSide.Right, _document.Columns[1].Side);
    }

    [Fact]
    public async Task BrowsingTextFile_OpensTheTextEditorAndSaves()
    {
        var path = Path.Combine(_root, "browse-edit.txt");
        File.WriteAllText(path, "one\r\ntwo\r\n");
        await _document.AddComparisonPathAsync(path);

        var column = _document.Columns[0];
        Assert.True(column.IsBrowsing);
        // 浏览文本文件改用项目文本编辑器：真正的多行编辑（对比状态仍是逐行对齐的行列表）。
        // Browsing a text file now uses the project's text editor (the comparison state keeps the aligned row list).
        Assert.True(column.IsTextEditorVisible);
        Assert.Null(column.Rows);
        Assert.Equal("one\ntwo", column.EditorText);
        Assert.False(column.IsDirty);

        column.EditorText = "one edited\nthree";
        Assert.True(column.IsDirty);

        await column.SaveCommand.ExecuteAsync(null);

        Assert.False(column.IsDirty);
        Assert.Equal("one edited\r\nthree\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task ComparisonRows_MissingSide_CarrySlashFiller()
    {
        // 文本对比：单侧行用编辑框占位符显示斜杠占位，提示另一侧没有对应内容。
        // A text comparison marks a single-side row with a slash placeholder in the editor.
        await _document.OpenCompareFilesAsync(WriteFile("filler-left.txt", "only-left\n"), string.Empty);

        var row = Assert.Single(_document.Rows!);
        Assert.True(row.IsComparedRow);
        Assert.True(row.IsRightEmpty);
        // 编辑器投影里对侧是填充槽（斜杠），用户可以直接在该行打字或粘贴来补齐。
        // In the editor projection the other side is a filler slot (a slash), which the user may fill by typing or pasting.
        Assert.Equal("/", _document.ProjectText(leftSide: false));
        Assert.Equal("only-left", _document.ProjectText(leftSide: true));

        WriteFile("filler-tree-left/only.txt", "x\n");
        Directory.CreateDirectory(Path.Combine(_root, "filler-tree-right"));

        // 目录对比：另一侧缺失的条目同样带斜杠占位（用独立文档，避免与上面的单侧文件对比互相干扰）。
        // A folder comparison carries the same placeholder for an entry missing on the other side (a separate document
        // keeps it independent from the single-side file comparison above).
        var folders = new CompareViewModel(ComparisonService.CreateDefault());
        try
        {
            await folders.AddComparisonPathAsync(Path.Combine(_root, "filler-tree-left"));
            await folders.AddComparisonPathAsync(Path.Combine(_root, "filler-tree-right"));
            await folders.ToggleCompareCommand.ExecuteAsync(null);

            // 初始化即展开，条目直接可见。
            // Expanded from the start, so the entries are visible right away.
            var rootRow = folders.Rows![0];
            var onlyRow = folders.Rows!.First(candidate => candidate.LeftText == "only.txt");
            Assert.True(onlyRow.ShowRightFiller);
            Assert.False(onlyRow.ShowLeftFiller);
        }
        finally
        {
            folders.Cleanup();
        }
    }

    [Fact]
    public async Task TextComparison_EditCopyToOtherSideAndSave_WritesTheTargetFile()
    {
        // CRLF + 空行：保存必须保留换行风格与空行（空行由「该侧有这一行」标记，而不是文本非空）。
        // CRLF plus a blank line: saving must keep the line ending and the blank line (a blank line is tracked by the
        // "this side has the row" flag rather than by non-empty text).
        var leftPath = Path.Combine(_root, "edit-left.txt");
        var rightPath = Path.Combine(_root, "edit-right.txt");
        File.WriteAllText(leftPath, "one\r\ntwo\r\n\r\nfour\r\n");
        File.WriteAllText(rightPath, "one\r\nTWO\r\n\r\nfour\r\n");

        await _document.OpenCompareFilesAsync(leftPath, rightPath);
        Assert.Equal(CompareMode.Text, _document.Mode);

        var rows = _document.Rows!;
        Assert.Equal(4, rows.Count);
        Assert.False(rows[2].IsLeftEmpty);
        Assert.False(rows[2].IsRightEmpty);
        Assert.Equal(string.Empty, rows[2].LeftText);
        Assert.True(rows[1].IsLeftDiff);

        // 行内编辑（编辑器改动翻译到槽模型）：第 1 行右侧改成 "one edited"，标记右侧已修改。
        // An in-line edit (an editor change translated into the slot model): the first right line becomes "one edited" and the
        // right side is marked modified.
        _document.ApplyEditorLines(leftSide: false, startSlot: 0, removedSlots: 1, ["one edited"]);
        Assert.True(_document.IsFileDirty(CompareColumnSide.Right));

        // 选中第 2 槽后从左复制到右：右侧文本与左侧一致。
        // Selecting the second slot and copying left to right makes the right text match the left.
        _document.UpdateRowSelection([1]);
        _document.CopySelectedTextRows(fromLeftToRight: true);
        Assert.Equal("two", _document.Rows![1].RightText);
        Assert.False(_document.Rows![1].IsLeftDiff);
        Assert.False(_document.Rows![1].IsRightDiff);
        Assert.False(_document.IsFileDirty(CompareColumnSide.Left));
        Assert.True(_document.IsFileDirty(CompareColumnSide.Right));

        await _document.SaveSideAsync(_document.Columns[1]);

        Assert.False(_document.IsFileDirty(CompareColumnSide.Right));
        var saved = File.ReadAllText(rightPath);
        Assert.Equal("one edited\r\ntwo\r\n\r\nfour\r\n", saved);
        // 左侧文件未被保存动作改动。
        // The left file is untouched by saving the right side.
        Assert.Equal("one\r\ntwo\r\n\r\nfour\r\n", File.ReadAllText(leftPath));
    }

    [Fact]
    public async Task TextComparison_LineEditing_SplitsJoinsAndDeletesRows()
    {
        var leftPath = Path.Combine(_root, "lines-left.txt");
        var rightPath = Path.Combine(_root, "lines-right.txt");
        File.WriteAllText(leftPath, "alpha\nbeta\ngamma\n");
        File.WriteAllText(rightPath, "alpha\nbeta\ngamma\n");
        await _document.OpenCompareFilesAsync(leftPath, rightPath);

        Assert.Equal(3, _document.Rows!.Count);

        // 断行（编辑器里按回车）：光标后的文本成为新的一行，对侧同位置补一个填充槽，两侧文档仍然等长。
        // Splitting (Enter in the editor): the text after the caret becomes a new line and the other side gains a filler slot at
        // the same position, so both documents stay the same length.
        _document.ApplyEditorLines(leftSide: true, startSlot: 1, removedSlots: 1, ["be", "ta"]);
        Assert.Equal(4, _document.Rows!.Count);
        Assert.Equal("be", _document.Rows![1].LeftText);
        Assert.Equal("beta", _document.Rows![1].RightText);
        Assert.True(_document.Rows![1].IsLeftDiff);
        Assert.Equal("ta", _document.Rows![2].LeftText);
        Assert.True(_document.Rows![2].IsRightEmpty);
        Assert.Equal(FolderItemStatus.LeftOnly, _document.Rows![2].Status);
        // 左侧投影（编辑器文本）反映断行，右侧同位置是填充槽（斜杠）。
        // The left projection (the editor text) shows the split while the right side holds a filler slash at the same position.
        Assert.Equal("alpha\nbe\nta", string.Join('\n', _document.ProjectText(leftSide: true).Split('\n').Take(3)));
        Assert.Equal("/", _document.ProjectText(leftSide: false).Split('\n')[2]);
        Assert.True(_document.IsFileDirty(CompareColumnSide.Left));
        Assert.False(_document.IsFileDirty(CompareColumnSide.Right));

        // 合并（行尾 Delete）：两槽并回一行，两侧重新相同，多出来的全空槽被收掉。
        // Joining (Delete at the end of a line): the two slots merge back, both sides match again and the now-empty slot is dropped.
        _document.ApplyEditorLines(leftSide: true, startSlot: 1, removedSlots: 2, ["beta"]);
        Assert.Equal(3, _document.Rows!.Count);
        Assert.Equal("beta", _document.Rows![1].LeftText);
        Assert.Equal("beta", _document.Rows![1].RightText);
        Assert.False(_document.Rows![1].IsLeftDiff);

        // 删除所选行（本侧）：本侧该槽退回填充、对侧内容保留，变成单侧行。
        // Deleting selected lines (this side): the slot falls back to a filler here while the other side keeps its content, so the
        // row becomes single-side.
        _document.UpdateRowSelection([1]);
        _document.DeleteSelectedTextRows(leftSide: true);
        Assert.Equal(3, _document.Rows!.Count);
        Assert.True(_document.Rows![1].IsLeftEmpty);
        Assert.Equal("beta", _document.Rows![1].RightText);
        Assert.Equal(FolderItemStatus.RightOnly, _document.Rows![1].Status);

        // 对侧也删掉后两侧都空，整槽消失。
        // Deleting the other side too leaves the slot empty on both sides, so it disappears.
        _document.DeleteSelectedTextRows(leftSide: false);
        Assert.Equal(2, _document.Rows!.Count);
        Assert.Equal(["alpha", "gamma"], _document.Rows!.Select(row => row.LeftText));
        Assert.Equal(["alpha", "gamma"], _document.Rows!.Select(row => row.RightText));
    }

    [Fact]
    public async Task TextComparison_Undo_RollsBothSidesBackTogether()
    {
        var leftPath = Path.Combine(_root, "undo-left.txt");
        var rightPath = Path.Combine(_root, "undo-right.txt");
        File.WriteAllText(leftPath, "alpha\nbeta\ngamma\n");
        File.WriteAllText(rightPath, "alpha\nbeta\ngamma\n");
        await _document.OpenCompareFilesAsync(leftPath, rightPath);
        Assert.Equal(3, _document.Rows!.Count);

        // 结构编辑（断行）：本侧多出一行，对侧同位置补填充槽。
        // A structural edit (split): this side gains a line and the other side gains a filler slot at the same position.
        _document.RecordTextUndoPoint(structural: true, leftSlot: 1, rightSlot: 1);
        _document.ApplyEditorLines(leftSide: true, startSlot: 1, removedSlots: 1, ["be", "ta"]);
        Assert.Equal(4, _document.Rows!.Count);
        Assert.Equal("/", _document.ProjectText(leftSide: false).Split('\n')[2]);
        Assert.True(_document.IsFileDirty(CompareColumnSide.Left));

        // 一次撤销：两侧一起回到断行前（对侧的填充槽同时收回），这正是「成对撤销」要保证的。
        // One undo: both sides return to the state before the split (the counterpart's filler is removed too), which is exactly
        // what paired undo guarantees.
        Assert.True(_document.CanUndoText);
        var caret = _document.UndoText();
        Assert.NotNull(caret);
        Assert.Equal(1, caret!.Value.LeftSlot);
        Assert.Equal(3, _document.Rows!.Count);
        Assert.Equal("alpha\nbeta\ngamma", _document.ProjectText(leftSide: true));
        Assert.Equal("alpha\nbeta\ngamma", _document.ProjectText(leftSide: false));

        // 重做：又回到断行后的状态。
        // Redo returns to the state after the split.
        Assert.True(_document.CanRedoText);
        Assert.NotNull(_document.RedoText());
        Assert.Equal(4, _document.Rows!.Count);
        Assert.Equal("/", _document.ProjectText(leftSide: false).Split('\n')[2]);
    }

    [Fact]
    public async Task ToggleCompare_MixedFileAndFolder_ReportsAnError()
    {
        await _document.AddComparisonPathAsync(WriteFile("file.txt", "text"));
        var folder = Path.Combine(_root, "mixed");
        Directory.CreateDirectory(folder);
        await _document.AddComparisonPathAsync(folder);

        await _document.ToggleCompareCommand.ExecuteAsync(null);

        Assert.True(_document.HasError);
        Assert.False(_document.IsComparing);
    }

    [Fact]
    public async Task RemoveColumn_KeepsAtLeastOneColumnAndEndsTheComparison()
    {
        await _document.AddComparisonPathAsync(WriteFile("left.txt", "one\n"));
        await _document.AddComparisonPathAsync(WriteFile("right.txt", "two\n"));
        await _document.ToggleCompareCommand.ExecuteAsync(null);
        Assert.True(_document.IsComparing);

        var removed = _document.Columns[1];
        await removed.CloseCommand.ExecuteAsync(null);

        Assert.Single(_document.Columns);
        Assert.False(_document.IsComparing);
        Assert.True(_document.Columns[0].IsBrowsing);
    }

    public void Dispose()
    {
        _document.Cleanup();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// <para>建立一对包含相同/不同/仅左/仅右与同名子目录的对比目录（子目录本身两侧都存在，只有其中的文件
    /// 不同，因此子目录的状态只能靠子树聚合得出）。</para>
    /// Creates a pair of directories holding equal/different/left-only/right-only entries plus a same-named
    /// subdirectory (the subdirectory itself exists on both sides and only its file differs, so its status can only
    /// come from subtree aggregation).
    /// </summary>
    private void CreateCompareFixture()
    {
        WriteFile("left/same.txt", "same\n");
        WriteFile("left/diff.txt", "left\n");
        WriteFile("left/leftonly.txt", "left only\n");
        WriteFile("left/sub/diff.txt", "left\n");

        WriteFile("right/same.txt", "same\n");
        WriteFile("right/diff.txt", "right\n");
        WriteFile("right/rightonly.txt", "right only\n");
        WriteFile("right/sub/diff.txt", "right\n");
    }
}
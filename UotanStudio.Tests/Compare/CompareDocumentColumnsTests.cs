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
    public async Task AddComparisonPath_ImportedFolder_StaysCollapsedUntilExpanded()
    {
        var folder = Path.Combine(_root, "left");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));

        await _document.AddComparisonPathAsync(folder);

        var column = _document.Columns[0];
        var root = column.TreeRoot;
        Assert.NotNull(root);
        Assert.False(root.IsExpanded);
        Assert.Null(root.Status);

        // 折叠状态下只显示根节点一行（目录树行），展开箭头可用。
        // While collapsed only the root row is shown (a tree row) and the expander is available.
        var row = Assert.Single(column.Rows!);
        Assert.True(row.IsTreeRow);
        Assert.True(row.TreeNode!.IsExpandable);
        Assert.Equal(0, row.Depth);
        Assert.Equal("left", row.LeftText);
        Assert.Equal(folder, _document.LeftPath);
        Assert.False(_document.IsEmptyDocument);
    }

    [Fact]
    public async Task ExpandedFolder_ListsDirectChildrenWithDirectoriesFirst()
    {
        var folder = Path.Combine(_root, "tree");
        Directory.CreateDirectory(Path.Combine(folder, "zdir"));
        WriteFile("tree/afile.txt", "a");

        await _document.AddComparisonPathAsync(folder);
        var column = _document.Columns[0];
        var root = column.TreeRoot!;
        await column.ToggleTreeExpansionAsync(root);

        Assert.True(root.IsExpanded);
        Assert.Equal(["tree", "zdir", "afile.txt"], column.Rows!.Select(row => row.LeftText));
        Assert.Equal([0, 1, 1], column.Rows!.Select(row => row.Depth));

        await column.ToggleTreeExpansionAsync(root);
        Assert.False(root.IsExpanded);
        Assert.Single(column.Rows!);
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

        // 初始只展开到最初始的文件夹一行，且该行按子树聚合为「不同」→ 两侧整行变色。
        // Initially only the initial folder row is visible, aggregated to "different" from its subtree, so the row
        // is coloured on both sides.
        var rootRow = Assert.Single(_document.Rows!);
        Assert.True(rootRow.IsTreeRow);
        Assert.Equal("left", rootRow.LeftText);
        Assert.Equal("right", rootRow.RightText);
        Assert.True(rootRow.IsLeftDiff);
        Assert.True(rootRow.IsRightDiff);

        // 展开最初始的文件夹：两侧同步展开（同一节点），子项按目录优先、同级按名称排序。
        // Expanding the initial folder expands both sides (one node) with directories first, then names.
        await _document.ToggleTreeExpansionAsync(rootRow.TreeNode!);

        var rows = _document.Rows!;
        Assert.Equal(
            ["left", "sub", "diff.txt", "leftonly.txt", "rightonly.txt", "same.txt"],
            rows.Select(row => string.IsNullOrEmpty(row.LeftText) ? row.RightText : row.LeftText));
        Assert.Equal([0, 1, 1, 1, 1, 1], rows.Select(row => row.Depth));

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

        // 结束对比回到普通浏览树：不再有状态色，目录重新折叠。
        // Ending the comparison returns to plain browsing trees without status colours and collapsed folders.
        await _document.ToggleCompareCommand.ExecuteAsync(null);
        Assert.False(_document.IsComparing);
        Assert.Equal(CompareMode.None, _document.Mode);
        var browseRow = Assert.Single(left.Rows!);
        Assert.True(browseRow.IsTreeRow);
        Assert.False(browseRow.IsLeftDiff);
        Assert.Null(browseRow.TreeNode!.Status);
    }

    [Fact]
    public async Task OpenFileComparisonInNewTab_SendsBothSidesAndKeepsTheFolderComparison()
    {
        CreateCompareFixture();
        await _document.AddComparisonPathAsync(Path.Combine(_root, "left"));
        await _document.AddComparisonPathAsync(Path.Combine(_root, "right"));
        await _document.ToggleCompareCommand.ExecuteAsync(null);
        var rootRow = _document.Rows![0];
        await _document.ToggleTreeExpansionAsync(rootRow.TreeNode!);

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
        Assert.True(right.UsesRightCells);
        Assert.False(left.UsesRightCells);
        Assert.True(left.ShowLeftCells);
        Assert.True(right.ShowRightCells);

        await _document.ToggleCompareCommand.ExecuteAsync(null);

        Assert.False(_document.IsComparing);
        Assert.Equal(CompareMode.None, _document.Mode);
        Assert.NotNull(left.Rows);
        Assert.NotNull(right.Rows);
        Assert.NotSame(left.Rows, right.Rows);
    }

    [Fact]
    public async Task OpenCompareFiles_WithOneEmptySide_LeavesThatColumnBlank()
    {
        var file = WriteFile("only.txt", "content\n");

        await _document.OpenCompareFilesAsync(file, string.Empty);
        Assert.Equal(CompareMode.Text, _document.Mode);
        Assert.All(_document.Rows!, row => Assert.True(row.IsRightEmpty));
        Assert.All(_document.Rows!, row => Assert.True(row.IsLeftDiff));
        Assert.All(_document.Rows!, row => Assert.True(row.IsEditable));
        Assert.False(_document.CanShowHexView);

        // 文件在右侧（新标签里「对侧没有对应文件」的另一种方向）：左侧整列留空。
        // The file on the right (the other direction of "no counterpart"): the left column stays blank.
        await _document.OpenCompareFilesAsync(string.Empty, file);
        Assert.Equal(CompareMode.Text, _document.Mode);
        Assert.All(_document.Rows!, row => Assert.True(row.IsLeftEmpty));
        Assert.All(_document.Rows!, row => Assert.True(row.IsRightDiff));
        Assert.Equal(CompareColumnSide.Right, _document.Columns[1].Side);
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

        // 选中第 2 行后从左复制到右：右侧文本与左侧一致，只有右侧被标记为已修改。
        // Selecting row 2 and copying left to right makes the right text match and only the right side dirty.
        _document.UpdateRowSelection([1]);
        _document.CopySelectedTextRows(fromLeftToRight: true);
        Assert.Equal("two", rows[1].RightText);
        Assert.False(rows[1].IsLeftDiff);
        Assert.False(rows[1].IsRightDiff);
        Assert.False(_document.IsFileDirty(CompareColumnSide.Left));
        Assert.True(_document.IsFileDirty(CompareColumnSide.Right));

        // 行内编辑同样标记「已修改」并可写回。
        // Editing a row in place marks it dirty too and can be written back.
        rows[0].RightText = "one edited";
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

        var rows = _document.Rows!;
        Assert.Equal(3, rows.Count);

        // 行内回车断行：光标后的文本成为紧随其后的新行，另一侧内容留在原行，对比状态随之更新。
        // Enter splits the line: the text after the caret becomes the following row, the other side stays with the
        // original row and the comparison state follows.
        var (insertedIndex, insertedCaret) = _document.SplitTextRow(rows[1], leftSide: true, caretIndex: 2);
        Assert.Equal(2, insertedIndex);
        Assert.Equal(0, insertedCaret);
        Assert.Equal(4, rows.Count);
        Assert.Equal("be", rows[1].LeftText);
        Assert.Equal("beta", rows[1].RightText);
        Assert.True(rows[1].IsLeftDiff);
        Assert.Equal("ta", rows[2].LeftText);
        Assert.True(rows[2].IsRightEmpty);
        Assert.Equal(FolderItemStatus.LeftOnly, rows[2].Status);
        Assert.True(_document.IsFileDirty(CompareColumnSide.Left));
        Assert.False(_document.IsFileDirty(CompareColumnSide.Right));

        // 行尾 Delete 并行：下一行接回本行末尾，多出来的行消失，两侧重新相同。
        // Delete at the end joins the next row back, the extra row disappears and both sides match again.
        var (joinIndex, joinCaret) = _document.JoinTextRowWithNext(rows[1], leftSide: true);
        Assert.Equal(1, joinIndex);
        Assert.Equal(2, joinCaret);
        Assert.Equal(3, rows.Count);
        Assert.Equal("beta", rows[1].LeftText);
        Assert.Equal("beta", rows[1].RightText);
        Assert.False(rows[1].IsLeftDiff);

        // 行首 Backspace 并入上一行。
        // Backspace at the start merges into the previous row.
        _document.SplitTextRow(rows[1], leftSide: true, caretIndex: 2);
        var (previousIndex, previousCaret) = _document.JoinTextRowWithPrevious(rows[2], leftSide: true);
        Assert.Equal(1, previousIndex);
        Assert.Equal(2, previousCaret);
        Assert.Equal(3, rows.Count);
        Assert.Equal("beta", rows[1].LeftText);

        // 删除所选行：只清掉该侧的行，另一侧保留时该行变成单侧；两侧都空时整行移除。
        // Deleting selected rows clears that side only (the row becomes single-side when the other side keeps content)
        // and removes the row entirely once both sides are empty.
        _document.UpdateRowSelection([1]);
        _document.DeleteSelectedTextRows(leftSide: true);
        Assert.Equal(3, rows.Count);
        Assert.True(rows[1].IsLeftEmpty);
        Assert.Equal("beta", rows[1].RightText);
        Assert.Equal(FolderItemStatus.RightOnly, rows[1].Status);

        _document.DeleteSelectedTextRows(leftSide: false);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["alpha", "gamma"], rows.Select(row => row.LeftText));
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

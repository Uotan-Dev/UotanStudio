using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services.Compare;
using UotanStudio.ViewModels.Compare;
using Xunit;

namespace UotanStudio.Tests.Compare;

/// <summary>
/// File-column contract of the compare document (aligned with Beyond Compare): two columns by default, freely
/// appendable for browsing, an imported folder stays collapsed; a folder comparison flattens both sides into one
/// aligned row list — same-name entries share a row, single-side entries stay blank on the other side, differing
/// rows are coloured as a whole and both sides share one expansion state; double-clicking a file opens its
/// comparison in a new tab; refresh and go-up act on both sides while comparing.
/// <para>对比文档的文件列契约（对齐 Beyond Compare 的行为）：默认两列、可继续追加（浏览用）、导入目录后
/// 保持折叠；文件夹对比把两侧压平成同一份对齐行列表——同名条目同一行、单侧条目在另一侧留空、差异整行变色、
/// 两侧共享展开状态；双击文件在新标签打开文件对比；对比中的刷新与返回上一级同时作用于两侧。</para>
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

        Assert.Equal(["zdir", "afile.txt"], column.Rows!.Select(row => row.LeftText));
        Assert.Equal([0, 0], column.Rows!.Select(row => row.Depth));

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

        Assert.NotNull(_document.Rows);
        Assert.Same(_document.Rows, left.Rows);
        Assert.Same(_document.Rows, right.Rows);

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
        Assert.True(_document.IsTextEditorMode);
        Assert.True(left.IsCompareTextEditorVisible);
        Assert.True(right.IsCompareTextEditorVisible);
        Assert.False(left.ShowLeftCells);
        Assert.False(right.ShowRightCells);

        await _document.ToggleCompareCommand.ExecuteAsync(null);

        Assert.False(_document.IsComparing);
        Assert.Equal(CompareMode.None, _document.Mode);
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
        Assert.Equal("/", _document.ProjectText(leftSide: false));
        Assert.False(_document.CanShowHexView);

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
        await _document.OpenCompareFilesAsync(WriteFile("filler-left.txt", "only-left\n"), string.Empty);

        var row = Assert.Single(_document.Rows!);
        Assert.True(row.IsComparedRow);
        Assert.True(row.IsRightEmpty);
        Assert.Equal("/", _document.ProjectText(leftSide: false));
        Assert.Equal("only-left", _document.ProjectText(leftSide: true));

        WriteFile("filler-tree-left/only.txt", "x\n");
        Directory.CreateDirectory(Path.Combine(_root, "filler-tree-right"));

        var folders = new CompareViewModel(ComparisonService.CreateDefault());
        try
        {
            await folders.AddComparisonPathAsync(Path.Combine(_root, "filler-tree-left"));
            await folders.AddComparisonPathAsync(Path.Combine(_root, "filler-tree-right"));
            await folders.ToggleCompareCommand.ExecuteAsync(null);

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

        _document.ApplyEditorLines(leftSide: false, startSlot: 0, removedSlots: 1, ["one edited"]);
        Assert.True(_document.IsFileDirty(CompareColumnSide.Right));

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

        _document.ApplyEditorLines(leftSide: true, startSlot: 1, removedSlots: 1, ["be", "ta"]);
        Assert.Equal(4, _document.Rows!.Count);
        Assert.Equal("be", _document.Rows![1].LeftText);
        Assert.Equal("beta", _document.Rows![1].RightText);
        Assert.True(_document.Rows![1].IsLeftDiff);
        Assert.Equal("ta", _document.Rows![2].LeftText);
        Assert.True(_document.Rows![2].IsRightEmpty);
        Assert.Equal(FolderItemStatus.LeftOnly, _document.Rows![2].Status);
        Assert.Equal("alpha\nbe\nta", string.Join('\n', _document.ProjectText(leftSide: true).Split('\n').Take(3)));
        Assert.Equal("/", _document.ProjectText(leftSide: false).Split('\n')[2]);
        Assert.True(_document.IsFileDirty(CompareColumnSide.Left));
        Assert.False(_document.IsFileDirty(CompareColumnSide.Right));

        _document.ApplyEditorLines(leftSide: true, startSlot: 1, removedSlots: 2, ["beta"]);
        Assert.Equal(3, _document.Rows!.Count);
        Assert.Equal("beta", _document.Rows![1].LeftText);
        Assert.Equal("beta", _document.Rows![1].RightText);
        Assert.False(_document.Rows![1].IsLeftDiff);

        _document.UpdateRowSelection([1]);
        _document.DeleteSelectedTextRows(leftSide: true);
        Assert.Equal(3, _document.Rows!.Count);
        Assert.True(_document.Rows![1].IsLeftEmpty);
        Assert.Equal("beta", _document.Rows![1].RightText);
        Assert.Equal(FolderItemStatus.RightOnly, _document.Rows![1].Status);

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

        _document.RecordTextUndoPoint(structural: true, leftSlot: 1, rightSlot: 1);
        _document.ApplyEditorLines(leftSide: true, startSlot: 1, removedSlots: 1, ["be", "ta"]);
        Assert.Equal(4, _document.Rows!.Count);
        Assert.Equal("/", _document.ProjectText(leftSide: false).Split('\n')[2]);
        Assert.True(_document.IsFileDirty(CompareColumnSide.Left));

        Assert.True(_document.CanUndoText);
        var caret = _document.UndoText();
        Assert.NotNull(caret);
        Assert.Equal(1, caret!.Value.LeftSlot);
        Assert.Equal(3, _document.Rows!.Count);
        Assert.Equal("alpha\nbeta\ngamma", _document.ProjectText(leftSide: true));
        Assert.Equal("alpha\nbeta\ngamma", _document.ProjectText(leftSide: false));

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
    /// Creates a pair of directories holding equal/different/left-only/right-only entries plus a same-named
    /// subdirectory (the subdirectory itself exists on both sides and only its file differs, so its status can only
    /// come from subtree aggregation).
    /// <para>建立一对包含相同/不同/仅左/仅右与同名子目录的对比目录（子目录本身两侧都存在，只有其中的文件
    /// 不同，因此子目录的状态只能靠子树聚合得出）。</para>
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
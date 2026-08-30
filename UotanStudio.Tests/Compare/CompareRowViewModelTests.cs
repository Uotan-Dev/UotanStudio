using UotanStudio.Services.Compare;
using UotanStudio.ViewModels.Compare;
using Xunit;

namespace UotanStudio.Tests.Compare;

/// <summary>
/// CompareRowViewModel / CompareViewModel tests: verify the folder-comparison row semantics —
/// left-only items leave the right cell empty (never duplicated across panes) and vice versa,
/// single-side rows carry the tint brush, only Different rows are diff-highlighted, and the
/// per-pane row lists (LeftRows/RightRows) filter out the opposite-side-only items.
/// <para>CompareRowViewModel / CompareViewModel 测试：验证文件夹比较行的语义——仅左侧条目右侧单元格置空
/// （不再跨栏重复显示）反之亦然、仅单侧行携带淡色背景画刷、仅 Different 行差异高亮，
/// 以及左右行列表（LeftRows/RightRows）过滤掉对侧独有条目。</para>
/// </summary>
public sealed class CompareRowViewModelTests
{
    [Fact]
    public void ForFolder_LeftOnly_LeavesRightCellEmpty()
    {
        var row = CompareRowViewModel.ForFolder(new FolderCompareItem
        {
            RelativePath = "left_only.txt",
            Status = FolderItemStatus.LeftOnly,
            IsDirectory = false,
            LeftSize = 208,
            RightSize = -1,
        });

        // 仅左侧条目：左侧有名称与大小，右侧单元格必须置空（不跨栏重复显示）。
        // Left-only item: the left side carries the name and size while the right cell stays empty.
        Assert.False(row.IsLeftEmpty);
        Assert.Equal("left_only.txt", row.LeftText);
        Assert.True(row.IsRightEmpty);
        Assert.True(string.IsNullOrEmpty(row.RightText));
        Assert.True(string.IsNullOrEmpty(row.RightSub));

        // 高亮语义：仅左侧不是差异行，不触发差异高亮，但带左侧橙色调淡色背景。
        // Highlight semantics: left-only is not a diff row (no diff highlight) but carries the
        // left orange tint.
        Assert.False(row.IsLeftDiff);
        Assert.False(row.IsRightDiff);
        Assert.NotNull(row.LeftTintBrush);
        Assert.Null(row.RightTintBrush);
    }

    [Fact]
    public void ForFolder_RightOnly_LeavesLeftCellEmpty()
    {
        var row = CompareRowViewModel.ForFolder(new FolderCompareItem
        {
            RelativePath = "right_only.txt",
            Status = FolderItemStatus.RightOnly,
            IsDirectory = false,
            LeftSize = -1,
            RightSize = 4096,
        });

        // 仅右侧条目：右侧有名称与大小，左侧单元格必须置空。
        // Right-only item: the right side carries the name and size while the left cell stays empty.
        Assert.True(row.IsLeftEmpty);
        Assert.True(string.IsNullOrEmpty(row.LeftText));
        Assert.True(string.IsNullOrEmpty(row.LeftSub));
        Assert.False(row.IsRightEmpty);
        Assert.Equal("right_only.txt", row.RightText);

        Assert.False(row.IsLeftDiff);
        Assert.False(row.IsRightDiff);
        Assert.Null(row.LeftTintBrush);
        Assert.NotNull(row.RightTintBrush);
    }

    [Fact]
    public void ForFolder_Equal_ShowsBothSidesWithoutHighlight()
    {
        var row = CompareRowViewModel.ForFolder(new FolderCompareItem
        {
            RelativePath = "same.txt",
            Status = FolderItemStatus.Equal,
            IsDirectory = false,
            LeftSize = 100,
            RightSize = 100,
        });

        // 相同条目：两侧都显示名称与大小，无高亮、无淡色背景。
        // Equal item: both sides show the name and size with no highlight and no tint.
        Assert.False(row.IsLeftEmpty);
        Assert.False(row.IsRightEmpty);
        Assert.Equal("same.txt", row.LeftText);
        Assert.Equal("same.txt", row.RightText);
        Assert.False(row.IsLeftDiff);
        Assert.False(row.IsRightDiff);
        Assert.Null(row.LeftTintBrush);
        Assert.Null(row.RightTintBrush);
    }

    [Fact]
    public void ForFolder_Different_HighlightsBothSides()
    {
        var row = CompareRowViewModel.ForFolder(new FolderCompareItem
        {
            RelativePath = "diff.txt",
            Status = FolderItemStatus.Different,
            IsDirectory = false,
            LeftSize = 10,
            RightSize = 20,
        });

        // 不同条目：两侧都显示并整行差异高亮，无淡色背景。
        // Different item: both sides are shown and diff-highlighted, with no tint.
        Assert.False(row.IsLeftEmpty);
        Assert.False(row.IsRightEmpty);
        Assert.True(row.IsLeftDiff);
        Assert.True(row.IsRightDiff);
        Assert.Null(row.LeftTintBrush);
        Assert.Null(row.RightTintBrush);
    }

    [Fact]
    public async Task CompareViewModel_LeftRightRows_ShareAlignedRows()
    {
        var root = CreateTempDirectory();
        try
        {
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);

            File.WriteAllText(Path.Combine(left, "same.txt"), "identical content");
            File.WriteAllText(Path.Combine(right, "same.txt"), "identical content");
            File.WriteAllText(Path.Combine(left, "diff.txt"), "left content");
            File.WriteAllText(Path.Combine(right, "diff.txt"), "right content");
            File.WriteAllText(Path.Combine(left, "left_only.txt"), "only on the left");
            File.WriteAllText(Path.Combine(right, "right_only.txt"), "only on the right");

            var vm = new CompareViewModel();
            await vm.OpenCompareFoldersAsync(left, right);

            // 左右两侧共享同一行集合：行数一致、同名条目落在同一索引（对齐），
            // 仅单侧条目在另一侧为空白占位行（单元格置空，不显示名称）。
            // Both panes share the same row list: same count, same-name items land at the same
            // index (aligned); single-side items are blank placeholder rows on the other side.
            Assert.NotNull(vm.LeftRows);
            Assert.NotNull(vm.RightRows);
            Assert.Equal(vm.LeftRows.Count, vm.RightRows.Count);

            var leftOnly = Assert.Single(vm.LeftRows, static r => r.LeftText == "left_only.txt");
            Assert.True(leftOnly.IsLeftEmpty is false);
            Assert.True(leftOnly.IsRightEmpty);
            var rightOnly = Assert.Single(vm.LeftRows, static r => r.RightText == "right_only.txt");
            Assert.True(rightOnly.IsLeftEmpty);
            Assert.True(rightOnly.IsRightEmpty is false);

            // 同名条目索引一致（行对齐）：同一索引上左右侧显示同名文件。
            // Same-name items share the index (row alignment).
            var leftSame = vm.LeftRows.Select((r, i) => (r, i)).First(t => t.r.LeftText == "same.txt").i;
            var rightSame = vm.RightRows.Select((r, i) => (r, i)).First(t => t.r.RightText == "same.txt").i;
            Assert.Equal(leftSame, rightSame);
            var leftDiff = vm.LeftRows.Select((r, i) => (r, i)).First(t => t.r.LeftText == "diff.txt").i;
            var rightDiff = vm.RightRows.Select((r, i) => (r, i)).First(t => t.r.RightText == "diff.txt").i;
            Assert.Equal(leftDiff, rightDiff);

            vm.Cleanup();
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRecursive(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 忽略清理失败（如文件被占用）。
            // Ignore cleanup failures (e.g. files still in use).
        }
    }
}

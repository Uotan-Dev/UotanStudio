using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Compare;
using UotanStudio.ViewModels.Compare;
using Xunit;

namespace UotanStudio.Tests.Compare;

public sealed class DifferenceNavigationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"uotan-diff-navigation-{Guid.NewGuid():N}");
    private readonly CompareViewModel _document = new(ComparisonService.CreateDefault());
    private readonly List<string> _notices = [];

    public DifferenceNavigationTests()
    {
        Directory.CreateDirectory(_root);
        WeakReferenceMessenger.Default.Register<DifferenceNavigationTests, CompareNavigationNoticeMessage>(this,
            static (test, message) =>
            {
                if (ReferenceEquals(test._document, message.Source))
                {
                    test._notices.Add(message.Text);
                }
            });
    }

    [Fact]
    public async Task HexNavigation_SameRowAndCrossRowRangesReachBoundariesWithoutWrapping()
    {
        var right = new byte[96];
        right[2] = right[3] = right[9] = right[31] = right[32] = right[80] = 1;
        await OpenHexAsync(new byte[96], right);
        foreach (var expected in new[] { 2, 9, 31, 80 })
        {
            _document.NextDiffCommand.Execute(null);
            Assert.Equal(expected, _document.HexNavigationOffset);
            Assert.Equal(expected / 16, _document.ScrollToIndex);
        }
        _document.NextDiffCommand.Execute(null);
        Assert.Equal(80, _document.HexNavigationOffset);
        Assert.Equal(LanguageService.Instance.GetString("Compare_LastDifference"), Assert.Single(_notices));
        foreach (var expected in new[] { 31, 9, 2 })
        {
            _document.PrevDiffCommand.Execute(null);
            Assert.Equal(expected, _document.HexNavigationOffset);
        }
        _document.PrevDiffCommand.Execute(null);
        _document.PrevDiffCommand.Execute(null);
        Assert.Equal(2, _document.HexNavigationOffset);
        Assert.Equal(new[] { "Compare_LastDifference", "Compare_FirstDifference", "Compare_FirstDifference" }.Select(LanguageService.Instance.GetString), _notices);
    }

    [Fact]
    public async Task HexNavigation_ManualCaretAndEditedDifferencesUseCurrentPosition()
    {
        var right = new byte[96];
        right[2] = right[9] = right[80] = 1;
        await OpenHexAsync(new byte[96], right);
        _document.SetHexNavigationPosition(40);
        _document.NextDiffCommand.Execute(null);
        Assert.Equal(80, _document.HexNavigationOffset);
        _document.SetHexNavigationPosition(9);
        _document.PrevDiffCommand.Execute(null);
        Assert.Equal(2, _document.HexNavigationOffset);
        var left = _document.Columns[0];
        left.HexBytes![9] = 1;
        left.HexBytesEdited(9, 1);
        _document.NextDiffCommand.Execute(null);
        Assert.Equal(80, _document.HexNavigationOffset);
    }

    [Fact]
    public async Task HexNavigation_SingleTrailingRangeReportsBothBoundaries()
    {
        await OpenHexAsync([0], [0, 1, 2, 3]);
        _document.PrevDiffCommand.Execute(null);
        Assert.Equal(1, _document.HexNavigationOffset);
        _document.NextDiffCommand.Execute(null);
        _document.PrevDiffCommand.Execute(null);
        Assert.Equal(new[] { "Compare_LastDifference", "Compare_FirstDifference" }.Select(LanguageService.Instance.GetString), _notices);
    }

    [Fact]
    public async Task HexNavigation_EqualFilesReportNoDifferences()
    {
        await OpenHexAsync([0, 1, 2], [0, 1, 2]);
        _document.NextDiffCommand.Execute(null);
        _document.PrevDiffCommand.Execute(null);
        Assert.Equal(new[] { "Compare_NoDifferences", "Compare_NoDifferences" }.Select(LanguageService.Instance.GetString), _notices);
        Assert.Equal(-1, _document.SelectedIndex);
    }

    [Fact]
    public async Task TextNavigation_ContiguousChangedLinesMoveAsOneRange()
    {
        var left = Path.Combine(_root, "left.txt");
        var right = Path.Combine(_root, "right.txt");
        await File.WriteAllTextAsync(left, "same\na\nb\nsame2\nc\nend", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(right, "same\nx\ny\nsame2\nz\nend", TestContext.Current.CancellationToken);
        await _document.OpenCompareFilesAsync(left, right);
        _document.NextDiffCommand.Execute(null);
        Assert.Equal(1, _document.ScrollToIndex);
        _document.NextDiffCommand.Execute(null);
        Assert.Equal(4, _document.ScrollToIndex);
        _document.NextDiffCommand.Execute(null);
        Assert.Equal(LanguageService.Instance.GetString("Compare_LastDifference"), Assert.Single(_notices));
        _document.PrevDiffCommand.Execute(null);
        Assert.Equal(1, _document.ScrollToIndex);
        _document.PrevDiffCommand.Execute(null);
        Assert.Equal(LanguageService.Instance.GetString("Compare_FirstDifference"), _notices[1]);
        _document.SelectedIndex = 3;
        _document.NextDiffCommand.Execute(null);
        Assert.Equal(4, _document.ScrollToIndex);
    }

    [Fact]
    public void HexSelection_CacheEvictionRetainsSelectionWithoutLoadingEveryRow()
    {
        using var rows = new CompareHexRowCollection(new byte[16 * 1024], new byte[16 * 1024]);
        rows.SetSelectedIndex(0);
        Assert.True(rows[0].IsSelected);
        _ = rows[512];
        Assert.True(rows[0].IsSelected);
        rows.SetSelectedIndex(900);
        Assert.False(rows[0].IsSelected);
        Assert.True(rows[900].IsSelected);
    }

    [Fact]
    public async Task ReadOnlyHexNavigation_UsesBitmapWithoutReadingRows()
    {
        var path = Path.Combine(_root, "bitmap.bin");
        await File.WriteAllBytesAsync(path, new byte[16 * 1024], TestContext.Current.CancellationToken);
        var bitmap = new byte[128];
        bitmap[0] = 0b00001100;
        bitmap[100] = 1;
        using var rows = new CompareHexRowCollection(path, path, bitmap);
        rows.Dispose();
        Assert.Equal(2, DifferenceNavigation.Find(rows.Count, -1, true, rows.IsRowDifferent));
        Assert.Equal(800, DifferenceNavigation.Find(rows.Count, 2, true, rows.IsRowDifferent));
        Assert.Equal(2, DifferenceNavigation.Find(rows.Count, 800, false, rows.IsRowDifferent));
    }

    [Theory]
    [InlineData(0, -1, true, -1)]
    [InlineData(8, -1, true, 2)]
    [InlineData(8, -1, false, 6)]
    [InlineData(8, 3, true, 6)]
    [InlineData(8, 3, false, -1)]
    [InlineData(8, 5, false, 2)]
    [InlineData(8, 6, true, -1)]
    public void Find_DifferenceRangesRespectDirection(int count, int current, bool forward, int expected)
    {
        Assert.Equal(expected, DifferenceNavigation.Find(count, current, forward, index => index is 2 or 3 or 6));
    }

    private async Task OpenHexAsync(byte[] left, byte[] right)
    {
        var leftPath = Path.Combine(_root, "left.bin");
        var rightPath = Path.Combine(_root, "right.bin");
        await File.WriteAllBytesAsync(leftPath, left, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(rightPath, right, TestContext.Current.CancellationToken);
        await _document.OpenCompareFilesAsync(leftPath, rightPath);
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _document.Cleanup();
        Directory.Delete(_root, recursive: true);
    }
}
using UotanStudio.Services.Compare;
using UotanStudio.ViewModels.Compare;
using Xunit;

namespace UotanStudio.Tests.Compare;

public sealed class BrowseHexEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"uotan-browse-hex-{Guid.NewGuid():N}");
    private readonly CompareViewModel _document = new(ComparisonService.CreateDefault());

    public BrowseHexEditorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task BrowseHex_MultipleColumnsEditAndSaveIndependently()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstPath = await WriteBinaryAsync("first.bin", [0, 1, 2]);
        var secondPath = await WriteBinaryAsync("second.bin", [0, 3, 4]);
        await _document.AddComparisonPathAsync(firstPath);
        await _document.AddComparisonPathAsync(secondPath);
        await _document.AddComparisonPathAsync(firstPath);
        Assert.False(_document.IsComparing);
        Assert.All(_document.Columns, column =>
        {
            Assert.True(column.IsBrowsingHexEditor);
            Assert.True(column.IsHexEditorVisible);
            Assert.False(column.IsCompareHexEditorVisible);
            Assert.False(column.ShowLeftCells);
            Assert.False(column.ShowHint);
            Assert.False(column.ShowDiffNavigation);
            Assert.Null(column.OtherHexBytes);
        });
        var first = _document.Columns[0];
        var second = _document.Columns[1];
        var duplicate = _document.Columns[2];
        Assert.NotSame(first.HexBytes, duplicate.HexBytes);
        first.HexBytes![1] = 9;
        first.HexBytesEdited(1, 1);
        Assert.False(second.IsDirty);
        Assert.False(duplicate.IsDirty);
        Assert.Equal(1, duplicate.HexBytes![1]);
        second.HexBytes![2] = 8;
        second.HexBytesEdited(2, 1);
        await first.SaveCommand.ExecuteAsync(null);
        Assert.False(first.IsDirty);
        Assert.True(second.IsDirty);
        Assert.Equal(new byte[] { 0, 9, 2 }, await File.ReadAllBytesAsync(firstPath, ct));
        Assert.Equal(new byte[] { 0, 3, 4 }, await File.ReadAllBytesAsync(secondPath, ct));
        await second.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new byte[] { 0, 3, 8 }, await File.ReadAllBytesAsync(secondPath, ct));
        Assert.Empty(Directory.GetFiles(_root, "*.save-*"));
    }

    [Fact]
    public async Task BrowseHex_ChangingAddressBeforeSavingWritesTheLoadedFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var originalPath = await WriteBinaryAsync("original.bin", [0, 1]);
        var otherPath = await WriteBinaryAsync("other.bin", [0, 2]);
        await _document.AddComparisonPathAsync(originalPath);
        var column = _document.Columns[0];
        column.HexBytes![1] = 7;
        column.HexBytesEdited(1, 1);
        column.Path = otherPath;
        await column.SaveCommand.ExecuteAsync(null);
        Assert.False(column.IsDirty);
        Assert.Equal(new byte[] { 0, 7 }, await File.ReadAllBytesAsync(originalPath, ct));
        Assert.Equal(new byte[] { 0, 2 }, await File.ReadAllBytesAsync(otherPath, ct));
    }

    [Fact]
    public async Task BrowseHex_SaveFailureKeepsEditsForRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, "file.bin");
        await File.WriteAllBytesAsync(path, [0, 1], ct);
        await _document.AddComparisonPathAsync(path);
        var column = _document.Columns[0];
        column.HexBytes![1] = 7;
        column.HexBytesEdited(1, 1);
        var movedDirectory = Path.Combine(_root, "moved");
        Directory.Move(sourceDirectory, movedDirectory);
        await column.SaveCommand.ExecuteAsync(null);
        Assert.True(column.IsDirty);
        Assert.True(_document.HasError);
        Assert.Equal(7, column.HexBytes[1]);
        Assert.Equal(new byte[] { 0, 1 }, await File.ReadAllBytesAsync(Path.Combine(movedDirectory, "file.bin"), ct));
        Directory.Move(movedDirectory, sourceDirectory);
        await column.SaveCommand.ExecuteAsync(null);
        Assert.False(column.IsDirty);
        Assert.False(_document.HasError);
        Assert.Equal(new byte[] { 0, 7 }, await File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task BrowseHex_LoadingTextAndClearingColumnReleasesBinaryContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var binaryPath = await WriteBinaryAsync("file.bin", [0, 1]);
        var textPath = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(textPath, "hello", ct);
        await _document.AddComparisonPathAsync(binaryPath);
        var column = _document.Columns[0];
        column.Path = textPath;
        await column.ReloadContentAsync();
        Assert.Null(column.HexBytes);
        Assert.True(column.IsBrowsingTextEditor);
        Assert.False(column.IsHexEditorVisible);
        column.Path = binaryPath;
        await column.ReloadContentAsync();
        Assert.True(column.IsBrowsingHexEditor);
        column.ClearContent();
        Assert.Null(column.HexBytes);
        Assert.False(column.IsHexEditorVisible);
        Assert.True(column.ShowHint);
        Assert.False(column.IsDirty);
    }

    [Fact]
    public async Task BrowseHex_ComparisonRoundTripReturnsToIndependentEditors()
    {
        var firstPath = await WriteBinaryAsync("left.bin", [0, 1]);
        var secondPath = await WriteBinaryAsync("right.bin", [0, 2]);
        await _document.AddComparisonPathAsync(firstPath);
        await _document.AddComparisonPathAsync(secondPath);
        var first = _document.Columns[0];
        first.HexBytes![1] = 3;
        first.HexBytesEdited(1, 1);
        await first.SaveCommand.ExecuteAsync(null);
        await _document.ToggleCompareCommand.ExecuteAsync(null);
        Assert.All(_document.Columns, column => Assert.True(column.IsCompareHexEditorVisible));
        Assert.Equal(3, first.HexBytes![1]);
        Assert.NotNull(first.OtherHexBytes);
        await _document.ToggleCompareCommand.ExecuteAsync(null);
        Assert.All(_document.Columns, column =>
        {
            Assert.True(column.IsBrowsingHexEditor);
            Assert.Null(column.OtherHexBytes);
            Assert.False(column.IsDirty);
        });
        var second = _document.Columns[1];
        second.HexBytes![1] = 9;
        second.HexBytesEdited(1, 1);
        Assert.False(first.IsDirty);
        Assert.Equal(3, first.HexBytes[1]);
        _document.Cleanup();
        Assert.Null(first.HexBytes);
        Assert.Null(second.HexBytes);
    }

    [Fact]
    public async Task HexSave_CancelledWritePreservesOriginalAndCleansTemporaryFile()
    {
        var path = await WriteBinaryAsync("cancel.bin", [0, 1]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HexFileEditService.SaveAsync(path, [0, 9], cts.Token));
        Assert.Equal(new byte[] { 0, 1 }, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*.save-*"));
    }

    [Fact]
    public async Task HexRead_ChangedFileLengthRejectsStaleSize()
    {
        var path = await WriteBinaryAsync("changed.bin", [0, 1]);
        await Assert.ThrowsAsync<IOException>(() => HexFileEditService.ReadAsync(path, 1, TestContext.Current.CancellationToken));
    }

    private async Task<string> WriteBinaryAsync(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        return path;
    }

    public void Dispose()
    {
        _document.Cleanup();
        Directory.Delete(_root, recursive: true);
    }
}
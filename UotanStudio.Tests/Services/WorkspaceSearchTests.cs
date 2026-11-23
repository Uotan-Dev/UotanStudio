using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WorkspaceSearchTests
{
    [Fact]
    public void HexSearchText_Build_MapsBytesToAsciiProjection()
    {
        byte[] bytes = [0x00, 0x41, 0x7F, 0x7E, 0xFF];

        var snapshot = HexSearchText.Build(bytes);

        Assert.Equal(".A.~.", snapshot.Text);
        Assert.Equal(0, HexSearchText.ByteOffset(snapshot, 0));
        Assert.Equal(4, HexSearchText.ByteOffset(snapshot, 4));
        Assert.Equal(4, HexSearchText.ByteOffset(snapshot, 99));
        Assert.Equal(0, HexSearchText.ByteOffset(HexSearchText.Build([]), 3));
    }

    [Fact]
    public void TextSearchService_Find_TextBeyondEightMiBReturnsMatch()
    {
        var offset = 8 * 1024 * 1024 + 100;
        var text = new string('a', offset) + "needle";
        var options = new TextSearchOptions("needle", string.Empty, true, false, false, false);

        var matches = TextSearchService.Find(text, options, cancellationToken: TestContext.Current.CancellationToken);

        var match = Assert.Single(matches);
        Assert.Equal(offset, match.Offset);
        Assert.Equal("needle", match.Value);
    }

    [Fact]
    public void TextSearchService_Find_MoreThanTenThousandMatchesReturnsAll()
    {
        var text = new string('a', 10_005);
        var options = new TextSearchOptions("a", string.Empty, true, false, false, false);

        var matches = TextSearchService.Find(text, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(10_005, matches.Count);
        Assert.Equal(0, matches[0].Offset);
        Assert.Equal(10_004, matches[^1].Offset);
    }

    [Fact]
    public void HexEditorViewModel_SearchText_ExposesBytesAndRevealsByteOffset()
    {
        var model = new HexEditorViewModel();
        model.RawBytes = [0x00, 0x41, 0x42, 0x00];
        var searchable = (ISearchableDocument)model;
        int? locatedOffset = null;
        var locatedLength = -1;
        model.SearchMatchLocated += (offset, length) =>
        {
            locatedOffset = offset;
            locatedLength = length;
        };

        Assert.Equal(".AB.", searchable.SearchText);
        Assert.False(searchable.SupportsSearchReplacement);
        Assert.Null(searchable.Editor);
        Assert.Throws<NotSupportedException>(() => searchable.ApplySearchText("changed"));
        Assert.True(((IHexSearchMatchLocator)model).TryRevealSearchMatch(2, 2));
        Assert.Equal(2, locatedOffset);
        Assert.Equal(2, locatedLength);
    }

    [Fact]
    public void FileViewModel_BinarySearchText_ExposesBytesAndInvalidatesAfterEdit()
    {
        var model = new FileViewModel
        {
            IsBinary = true,
            RawBytes = [0x00, 0x41, 0x42, 0x00]
        };
        var searchable = (ISearchableDocument)model;
        int? locatedOffset = null;
        var locatedLength = -1;
        model.HexSearchMatchLocated += (offset, length) =>
        {
            locatedOffset = offset;
            locatedLength = length;
        };

        Assert.Equal(".AB.", searchable.SearchText);
        Assert.False(searchable.SupportsSearchReplacement);
        Assert.True(((IHexSearchMatchLocator)model).TryRevealSearchMatch(1, 2));
        Assert.Equal(1, locatedOffset);
        Assert.Equal(2, locatedLength);

        model.RawBytes![1] = (byte)'Z';
        model.MarkModifiedByHexEdit();

        Assert.Equal(".ZB.", searchable.SearchText);
    }
}
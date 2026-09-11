using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Services.Workspace;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public class TextSearchServiceTests
{
    private static IReadOnlyList<TextSearchMatch> Find(string text, TextSearchOptions options, int start = 0, int? length = null)
        => TextSearchService.Find(text, options, start, length, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public void LiteralSearchHonorsCaseAndEscapesRegex(bool matchCase, int count)
    {
        var matches = Find("a.b A.B a.B axb", new("a.b", MatchCase: matchCase));
        Assert.Equal(count, matches.Count);
    }

    [Fact]
    public void WholeWordUsesUnicodeWordBoundaries()
    {
        var matches = Find("cat cat_ cats scat cat. 猫cat", new("cat", WholeWord: true));
        Assert.Equal(new[] { 0, 19 }, matches.Select(m => m.Offset));
    }

    [Fact]
    public void SelectionRestrictsMatchesButRetainsAbsoluteOffsets()
    {
        var matches = Find("cat cat cat", new("cat", "dog"), 4, 3);
        Assert.Single(matches);
        Assert.Equal(4, matches[0].Offset);
        Assert.Equal("cat dog cat", TextSearchService.Apply("cat cat cat", matches));
    }

    [Fact]
    public void RegexGroupsAndZeroLengthMatchesMakeProgress()
    {
        var matches = Find("a=12\nb=34", new(@"(?<key>\w)=(\d+)", "${key}:$1", UseRegex: true));
        Assert.Equal("a:12\nb:34", TextSearchService.Apply("a=12\nb=34", matches));
        var zero = Find("ab", new("(?=.)", "_", UseRegex: true));
        Assert.Equal("_a_b", TextSearchService.Apply("ab", zero));
    }

    [Theory]
    [InlineData("CAT", "DOG")]
    [InlineData("cat", "dog")]
    [InlineData("Cat", "Dog")]
    [InlineData("cAt", "dog")]
    public void ReplacementPreservesCase(string input, string expected)
    {
        var matches = Find(input, new("cat", "dog", PreserveCase: true));
        Assert.Equal(expected, TextSearchService.Apply(input, matches));
    }

    [Theory]
    [InlineData("123-", "dOg", "dOg")]
    [InlineData("A", "dOg", "DOG")]
    [InlineData("A-b", "1dOG", "1Dog")]
    [InlineData("a-B", "dOg", "dOg")]
    [InlineData("中文", "dOg", "dOg")]
    [InlineData("ǅ", "dOg", "dOg")]
    [InlineData("É-cole", "éCOLE", "École")]
    [InlineData("Áb", "dOG", "Dog")]
    public void PreserveCaseRetainsUnicodeAndNonletterRules(string input, string replacement, string expected)
    {
        var matches = Find(input, new(input, replacement, MatchCase: true, PreserveCase: true));
        Assert.Equal(expected, TextSearchService.Apply(input, matches));
    }

    [Fact]
    public void ProjectTextReadRetainsContentsAcrossBufferBoundaries()
    {
        using var temp = new TempDirectory();
        var text = new string('a', 4095) + "😀\r\n" + new string('中', 5000);
        var path = temp.WriteBytes("large.txt", Encoding.UTF8.GetBytes(text));
        Assert.Equal(text, ProjectTextSearchService.ReadText(path, TestContext.Current.CancellationToken));
        File.AppendAllText(path, "\0");
        Assert.Null(ProjectTextSearchService.ReadText(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void InvalidRegexAndStaleContentAreRejected()
    {
        Assert.Throws<RegexParseException>(() => Find("text", new("[", UseRegex: true)));
        var matches = Find("cat", new("cat", "dog"));
        Assert.Throws<IOException>(() => TextSearchService.Apply("bat", matches));
        Assert.Throws<ArgumentOutOfRangeException>(() => Find("cat", new("cat"), 4));
    }

    [Fact]
    public void SearchIsCancellableAndMatchCountIsBounded()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TextSearchService.Find("abc", new("x"), cancellationToken: cancellation.Token));
        Assert.Throws<IOException>(() => Find(new string('a', 10001), new("a")));
    }

    [Fact]
    public void PathologicalRegexTimesOut()
    {
        Assert.Throws<RegexMatchTimeoutException>(() => Find(new string('a', 10000) + "!", new("^(a+)+$", UseRegex: true)));
    }

    [Fact]
    public void ReplacementSizeIsBounded()
    {
        Assert.Throws<IOException>(() => Find("aa", new("a", new string('x', TextSearchService.MaximumTextLength))));
    }

    [Fact]
    public void ProjectSearchReadsUnicodeAndExcludesBinaryAndOpenSnapshots()
    {
        using var temp = new TempDirectory();
        var utf8 = temp.WriteBytes("config.txt", Encoding.UTF8.GetBytes("hello target"));
        var utf16 = Path.Combine(temp.Path, "config.xml");
        File.WriteAllText(utf16, "target", Encoding.Unicode);
        temp.WriteBytes("firmware.bin", [0, 1, 2, 3]);
        var excluded = temp.WriteBytes("open.txt", Encoding.UTF8.GetBytes("target"));
        Directory.CreateDirectory(Path.Combine(temp.Path, ".uotan"));
        File.WriteAllText(Path.Combine(temp.Path, ".uotan", "session.json"), "target");
        var found = ProjectTextSearchService.Find(temp.Path, new("target"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { excluded }, TestContext.Current.CancellationToken);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, file => file.Path == utf8 && file.Matches[0].Offset == 6);
        Assert.Contains(found, file => file.Path == utf16 && file.Text == "target");
        Assert.Equal("target", File.ReadAllText(excluded));
    }

    [Fact]
    public void ProjectSearchSkipsOversizedFilesAndRejectsCancellation()
    {
        using var temp = new TempDirectory();
        using (var file = File.Create(Path.Combine(temp.Path, "large.img"))) file.SetLength(TextSearchService.MaximumTextLength + 1L);
        Assert.Empty(ProjectTextSearchService.Find(temp.Path, new("x"), new HashSet<string>(), TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectTextSearchService.Find(temp.Path, new("x"), new HashSet<string>(), cancellation.Token));
    }

    [Fact]
    public void InvalidUnicodeIsNotSilentlyDecodedAsReplacementCharacters()
    {
        using var temp = new TempDirectory();
        var utf16 = temp.WriteBytes("invalid.txt", [0xff, 0xfe, 0x00, 0xd8]);
        Assert.Null(ProjectTextSearchService.ReadText(utf16, TestContext.Current.CancellationToken));
        var utf8 = temp.WriteBytes("invalid-utf8.txt", [0xef, 0xbb, 0xbf, 0xff]);
        Assert.Null(ProjectTextSearchService.ReadText(utf8, TestContext.Current.CancellationToken));
    }
}

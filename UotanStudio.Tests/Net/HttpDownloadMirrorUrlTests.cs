using UotanStudio.Net.GitHub;
using UotanStudio.Net.Http;
using Xunit;

namespace UotanStudio.Tests.Net;

public sealed class HttpDownloadMirrorUrlTests
{
    private const string ReleaseUrl = "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1";

    [Theory]
    [InlineData("https://gh-proxy.org/https://github.com")]
    [InlineData("https://ghfast.top/https://github.com/")]
    [InlineData("https://xget.xi-xu.me/gh")]
    [InlineData("https://cors.isteed.cc/github.com")]
    [InlineData("https://g.blfrp.cn/https://github.com")]
    [InlineData("https://down.npee.cn/?https://github.com")]
    [InlineData("https://github.com")]
    public void MirrorUrl_ReleaseAssetAppendsTheGitHubPathOnceWithoutRepeatingTheOrigin(string baseUrl)
    {
        var site = new GitHubMirrorSite { Name = "probe", BaseUrl = baseUrl };

        var mirror = HttpDownload.MirrorUrl(site, new Uri(ReleaseUrl));

        var text = mirror.ToString();
        Assert.Equal(1, CountOccurrences(text, "/Genymobile/scrcpy/releases"));
        Assert.DoesNotContain("https://github.com/https://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://github.com/https://", text, StringComparison.Ordinal);
        Assert.EndsWith("/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1", text, StringComparison.Ordinal);
        Assert.StartsWith(baseUrl.TrimEnd('/'), text, StringComparison.Ordinal);
    }

    [Fact]
    public void MirrorUrl_KnownProxySiteProducesTheCanonicalProxyShape()
    {
        var site = new GitHubMirrorSite { Name = "gh-proxy.org", BaseUrl = "https://gh-proxy.org/https://github.com" };

        var mirror = HttpDownload.MirrorUrl(site, new Uri(ReleaseUrl));

        Assert.Equal("https://gh-proxy.org/https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1",
            mirror.ToString());
    }

    [Fact]
    public void MirrorUrl_InListSites_ResolveToDistinctReachableShapes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sites = new[]
        {
            new GitHubMirrorSite { Name = "a", BaseUrl = "https://gh-proxy.org/https://github.com" },
            new GitHubMirrorSite { Name = "b", BaseUrl = "https://xget.xi-xu.me/gh" },
            new GitHubMirrorSite { Name = "c", BaseUrl = "https://cors.isteed.cc/github.com" },
        };

        foreach (var site in sites) seen.Add(HttpDownload.MirrorUrl(site, new Uri(ReleaseUrl)).ToString());

        Assert.Equal(sites.Length, seen.Count);
    }

    [Fact]
    public void MirrorUrl_RawContentKeepsItsOwnHostUnderAMarkerPrefix()
    {
        var site = new GitHubMirrorSite { Name = "marker", BaseUrl = "https://gh-proxy.org/https://github.com" };
        var raw = new Uri("https://raw.githubusercontent.com/Genymobile/scrcpy/master/README.md");

        var text = HttpDownload.MirrorUrl(site, raw).ToString();

        Assert.Equal("https://gh-proxy.org/https://raw.githubusercontent.com/Genymobile/scrcpy/master/README.md", text);
    }

    [Fact]
    public void MirrorUrl_NonGitHubSiteLeavesTheUrlAloneForOtherHosts()
    {
        var site = new GitHubMirrorSite { Name = "generic", BaseUrl = "https://mirror.example/gh" };
        var other = new Uri("https://example.org/tool.tar.gz");

        Assert.Equal(other, HttpDownload.MirrorUrl(site, other));
    }

    [Fact]
    public void MirrorUrl_ApiOriginUnderAMarkerPrefix_KeepsTheFullUrlInThePath()
    {
        var site = new GitHubMirrorSite { Name = "ghfast", BaseUrl = "https://ghfast.top/https://github.com" };

        var mirror = HttpDownload.MirrorUrl(site, new Uri("https://api.github.com/repos/topjohnwu/Magisk/releases?per_page=1&page=1"));

        Assert.Equal("https://ghfast.top/https://api.github.com/repos/topjohnwu/Magisk/releases?per_page=1&page=1",
            mirror.ToString());
    }

    [Fact]
    public void EnumerateSources_ApiOrigin_ExpandsOntoTheEnabledMirrors()
    {
        var origin = new Uri("https://api.github.com/repos/topjohnwu/Magisk/releases?per_page=1&page=1");

        var sources = HttpDownload.EnumerateSources(origin).ToArray();

        Assert.True(sources.Length > 1);
        Assert.Equal(origin, sources[0]);
        Assert.Contains(sources, source => string.Equals(source.Host, "ghfast.top", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnumerateSources_NonGitHubHost_YieldsOnlyTheOrigin()
    {
        var origin = new Uri("https://example.org/tool.tar.gz");

        Assert.Equal([origin], HttpDownload.EnumerateSources(origin).ToArray());
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
using System.Text.Json;
using UotanStudio.Net.GitHub;
using Xunit;

namespace UotanStudio.Tests.Net;

public sealed class GitHubReleaseListTests
{
    [Fact]
    public void ParseReleases_ReleaseArray_KeepsDocumentOrderAndNormalizesVersions()
    {
        const string json = """
            [
              {"tag_name":"v28.1","assets":[{"name":"Magisk-v28.1.apk","size":12,"browser_download_url":"https://github.com/topjohnwu/Magisk/releases/download/v28.1/Magisk-v28.1.apk","digest":"sha256:AA"}]},
              {"tag_name":"v28.0","assets":[]}
            ]
            """;

        var releases = GitHubReleaseClient.ParseReleases("topjohnwu", "Magisk", json);

        Assert.Equal(2, releases.Count);
        Assert.Equal("28.1", releases[0].Version);
        Assert.Equal("v28.1", releases[0].Tag);
        Assert.Equal("28.0", releases[1].Version);
        Assert.Single(releases[0].Assets);
        Assert.Equal("topjohnwu", releases[0].Owner);
    }

    [Fact]
    public void ParseReleases_DraftEntry_IsRemovedWhilePreReleasesStayListed()
    {
        const string json = """
            [
              {"tag_name":"v3.0.0-rc.1","prerelease":true,"assets":[]},
              {"tag_name":"v2.9.9","draft":true,"assets":[]},
              {"tag_name":"v2.9.8","assets":[]}
            ]
            """;

        var releases = GitHubReleaseClient.ParseReleases("owner", "repo", json);

        Assert.Equal(["3.0.0-rc.1", "2.9.8"], releases.Select(release => release.Version).ToArray());
    }

    [Fact]
    public void ParseReleases_EntryWithoutTag_Refuses()
    {
        const string json = """[{"tag_name":"v1.0.0","assets":[]},{"assets":[]}]""";

        Assert.Throws<InvalidOperationException>(() => GitHubReleaseClient.ParseReleases("owner", "repo", json));
    }

    [Fact]
    public void ParseReleases_AssetWithoutName_Refuses()
    {
        const string json = """[{"tag_name":"v1.0.0","assets":[{"size":1,"browser_download_url":"https://github.com/x"}]}]""";

        Assert.Throws<InvalidOperationException>(() => GitHubReleaseClient.ParseReleases("owner", "repo", json));
    }

    [Fact]
    public void ParseReleases_ObjectInsteadOfArray_ThrowsJsonException()
        => Assert.Throws<JsonException>(() => GitHubReleaseClient.ParseReleases("owner", "repo", "{}"));
}
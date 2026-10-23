using System.Text.Json;
using UotanStudio.Net.GitHub;
using Xunit;

namespace UotanStudio.Tests.Net;

public sealed class GitHubReleaseClientTests
{
    private const string ServerDigest = "764eb6f79811d5211fe9df341120882ba9994c7a61b897d7bf3fb662e53bc536";
    private const string ServerUrl = "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1";

    [Fact]
    public void ParseRelease_ReleaseApiShape_ProjectsTagVersionAndAssets()
    {
        const string json = """
            {"tag_name":"v5.0.1","assets":[
              {"name":"scrcpy-win64-v5.0.1.zip","size":11264634,"browser_download_url":"https://github.com/x","digest":"sha256:b12a2c4e"},
              {"name":"scrcpy-server-v5.0.1","size":733930,"browser_download_url":"https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1","digest":"sha256:764EB6F79811D5211FE9DF341120882BA9994C7A61B897D7BF3FB662E53BC536"}
            ]}
            """;

        var release = GitHubReleaseClient.ParseRelease("Genymobile", "scrcpy", json);

        Assert.Equal("Genymobile", release.Owner);
        Assert.Equal("scrcpy", release.Repository);
        Assert.Equal("v5.0.1", release.Tag);
        Assert.Equal("5.0.1", release.Version);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal("scrcpy-win64-v5.0.1.zip", release.Assets[0].Name);
        Assert.Null(release.Assets[0].Sha256);
        var server = Assert.Single(release.Assets, asset => asset.Name == "scrcpy-server-v5.0.1");
        Assert.Equal(ServerUrl, server.DownloadUrl);
        Assert.Equal(733_930, server.Size);
        Assert.Equal(ServerDigest, server.Sha256);
    }

    [Fact]
    public void ParseRelease_PrereleaseTag_KeepsTheDeterministicVersionSuffix()
    {
        const string json = """{"tag_name":"v2.0.0-rc.1","assets":[]}""";

        var release = GitHubReleaseClient.ParseRelease("owner", "repo", json);

        Assert.Equal("2.0.0-rc.1", release.Version);
        Assert.Empty(release.Assets);
    }

    [Fact]
    public void ParseRelease_AssetWithoutDownloadUrl_Refuses()
    {
        const string json = """{"tag_name":"v1.0.0","assets":[{"name":"tool.zip","size":1,"digest":"sha256:deadbeef"}]}""";

        Assert.Throws<InvalidOperationException>(() => GitHubReleaseClient.ParseRelease("owner", "repo", json));
    }

    [Fact]
    public void ParseRelease_MissingTag_Refuses()
        => Assert.Throws<InvalidOperationException>(() => GitHubReleaseClient.ParseRelease("owner", "repo", "{}"));

    [Fact]
    public void ParseRelease_MalformedDocument_ThrowsJsonException()
        => Assert.Throws<JsonException>(() => GitHubReleaseClient.ParseRelease("owner", "repo", "{ not json"));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(" v1.2.3 ", "1.2.3")]
    [InlineData("v1.2.3-beta.1", "1.2.3-beta.1")]
    [InlineData("release-2026", "release-2026")]
    public void NormalizeVersion_TagShapes_ProduceStableVersionText(string tag, string expected)
        => Assert.Equal(expected, GitHubReleaseClient.NormalizeVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeVersion_BlankTag_Rejects(string tag)
        => Assert.Throws<ArgumentException>(() => GitHubReleaseClient.NormalizeVersion(tag));
}
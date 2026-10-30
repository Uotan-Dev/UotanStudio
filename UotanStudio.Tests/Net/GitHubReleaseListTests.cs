using System.Globalization;
using System.Net;
using System.Text;
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

    [Fact]
    public async Task ListAsync_ResponseAboveTheCap_RetriesWithFewerReleases()
    {
        List<int> counts = [];
        using var client = new HttpClient(new StubHandler(request =>
        {
            var count = QueryInt(request.RequestUri!, "per_page");
            counts.Add(count);
            if (count > 10)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new string('x', GitHubReleaseClient.MaximumResponseBytes + 1))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"tag_name":"v1.0.0","assets":[]}]""", Encoding.UTF8, "application/json")
            };
        }));

        var releases = await GitHubReleaseClient.ListAsync(client, "tiann", "KernelSU", maximum: 20,
            TestContext.Current.CancellationToken);

        Assert.Single(releases);
        Assert.Equal([20, 10], counts);
    }

    [Fact]
    public async Task ListAsync_SingleReleaseAboveTheCap_ThrowsTheDedicatedError()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', GitHubReleaseClient.MaximumResponseBytes + 1))
        }));

        await Assert.ThrowsAsync<GitHubResponseTooLargeException>(() => GitHubReleaseClient.ListAsync(
            client, "owner", "repo", maximum: 4, TestContext.Current.CancellationToken));
    }

    private static int QueryInt(Uri uri, string name)
    {
        foreach (var segment in uri.Query.TrimStart('?').Split('&'))
        {
            if (segment.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return int.Parse(segment[(name.Length + 1)..], CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException($"Missing query value '{name}'.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
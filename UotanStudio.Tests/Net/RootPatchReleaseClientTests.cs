using System.Globalization;
using System.Net;
using System.Text;
using UotanStudio.Net.Components.Root;
using UotanStudio.Net.GitHub;
using Xunit;

namespace UotanStudio.Tests.Net;

public sealed class RootPatchReleaseClientTests
{
    [Theory]
    [InlineData("magisk", "topjohnwu", "Magisk")]
    [InlineData("magisk-alpha", "vvb2060", "Magisk")]
    [InlineData("kernelsu", "tiann", "KernelSU")]
    [InlineData("sukisu", "SukiSU-Ultra", "SukiSU-Ultra")]
    [InlineData("apatch", "bmax121", "APatch")]
    public void Find_KnownKey_ResolvesTheOfficialRepository(string key, string owner, string repository)
    {
        var vendor = RootPatchCatalog.Find(key);

        Assert.NotNull(vendor);
        Assert.Equal(owner, vendor.Owner);
        Assert.Equal(repository, vendor.Repository);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("magisk-beta")]
    public void Find_UnknownKey_ReturnsNull(string key) => Assert.Null(RootPatchCatalog.Find(key));

    [Fact]
    public void Find_KeyComparison_IgnoresCase()
        => Assert.Equal("topjohnwu", RootPatchCatalog.Find(" MAGISK ")!.Owner);

    [Fact]
    public void SelectPackageAsset_ReleaseApkWinsOverDebugApkAndZip()
    {
        GitHubAsset[] assets =
        [
            new("KernelSU_v1.0.5_debug.apk", "https://github.com/x/debug.apk", 3, null),
            new("KernelSU_v1.0.5.zip", "https://github.com/x/sources.zip", 4, null),
            new("KernelSU_v1.0.5_12000-release.apk", "https://github.com/x/release.apk", 2, null),
            new("KernelSU_v1.0.5_12000.apk", "https://github.com/x/plain.apk", 1, null),
        ];

        var selected = RootPatchReleaseClient.SelectPackageAsset(assets);

        Assert.NotNull(selected);
        Assert.Equal("KernelSU_v1.0.5_12000-release.apk", selected.Name);
    }

    [Fact]
    public void SelectPackageAsset_StubAppShell_LosesToTheManagerApk()
    {
        GitHubAsset[] assets =
        [
            new("stub-release.apk", "https://github.com/x/stub.apk", 28_400, null),
            new("Magisk-v24.0.apk", "https://github.com/x/magisk.apk", 1, null),
        ];

        var selected = RootPatchReleaseClient.SelectPackageAsset(assets);

        Assert.NotNull(selected);
        Assert.Equal("Magisk-v24.0.apk", selected.Name);
    }

    [Fact]
    public void SelectPackageAsset_AssetWithoutApkOrZip_ReturnsNull()
    {
        GitHubAsset[] assets = [new("APatch_v1.5.0.apk.sha256", "https://github.com/x/digest", 1, null)];

        Assert.Null(RootPatchReleaseClient.SelectPackageAsset(assets));
    }

    [Fact]
    public void SelectPackageAsset_LegacyBuildFirstInApiOrder_LosesToTheStandardApk()
    {
        GitHubAsset[] assets =
        [
            new("apk-legacy-release.apk", "https://github.com/x/legacy.apk", 2, null),
            new("app-release.apk", "https://github.com/x/app.apk", 1, null),
        ];

        Assert.Equal("app-release.apk", RootPatchReleaseClient.SelectPackageAsset(assets)!.Name);
    }

    [Fact]
    public void SelectPackageAsset_SpoofedBuildFirstInApiOrder_LosesToTheStandardApk()
    {
        GitHubAsset[] assets =
        [
            new("SukiSU_v4.3.0_41100-release-spoofed.apk", "https://github.com/x/spoofed.apk", 2, null),
            new("SukiSU_v4.3.0_41100-release.apk", "https://github.com/x/release.apk", 1, null),
        ];

        Assert.Equal("SukiSU_v4.3.0_41100-release.apk", RootPatchReleaseClient.SelectPackageAsset(assets)!.Name);
    }

    [Fact]
    public void FromRelease_ReleaseApiShape_ProjectsVersionDigestAndUrl()
    {
        const string json = """
            {"tag_name":"v28.1","assets":[
              {"name":"Magisk-v28.1.apk","size":12345,"browser_download_url":"https://github.com/topjohnwu/Magisk/releases/download/v28.1/Magisk-v28.1.apk","digest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}
            ]}
            """;
        var vendor = RootPatchCatalog.Find(RootPatchCatalog.MagiskKey)!;

        var package = RootPatchReleaseClient.FromRelease(vendor,
            GitHubReleaseClient.ParseRelease(vendor.Owner, vendor.Repository, json));

        Assert.NotNull(package);
        Assert.Equal(RootPatchCatalog.MagiskKey, package.Key);
        Assert.Equal("28.1", package.Version);
        Assert.Equal("v28.1", package.Tag);
        Assert.Equal("Magisk-v28.1.apk", package.AssetName);
        Assert.Equal(12_345, package.Length);
        Assert.Equal(new string('a', 64), package.Sha256);
    }

    [Fact]
    public void FromRelease_AssetWithoutDigest_KeepsThePackageUsableFromTheOrigin()
    {
        var vendor = RootPatchCatalog.Find(RootPatchCatalog.APatchKey)!;
        var release = new GitHubRelease(vendor.Owner, vendor.Repository, "v1.5.0", "1.5.0",
            [new GitHubAsset("APatch_v1.5.0.apk", "https://github.com/bmax121/APatch/releases/download/v1.5.0/APatch_v1.5.0.apk", 42, null)]);

        var package = RootPatchReleaseClient.FromRelease(vendor, release);

        Assert.NotNull(package);
        Assert.Null(package.Sha256);
    }

    [Fact]
    public void FromReleases_SkipsReleasesWithoutAnInstallableAsset()
    {
        var vendor = RootPatchCatalog.Find(RootPatchCatalog.KernelSuKey)!;
        GitHubRelease[] releases =
        [
            new(vendor.Owner, vendor.Repository, "v1.0.5", "1.0.5",
                [new GitHubAsset("KernelSU_v1.0.5_release.apk", "https://github.com/x/apk", 1, null)]),
            new(vendor.Owner, vendor.Repository, "v1.0.4", "1.0.4",
                [new GitHubAsset("KernelSU_v1.0.4.tar.gz", "https://github.com/x/sources", 2, null)]),
        ];

        var packages = RootPatchReleaseClient.FromReleases(vendor, releases);

        Assert.Single(packages);
        Assert.Equal("1.0.5", packages[0].Version);
    }

    [Fact]
    public async Task ListAsync_UnknownKey_Rejects()
        => await Assert.ThrowsAsync<ArgumentException>(() => RootPatchReleaseClient.ListAsync(
            new HttpClient(), "unknown-key", cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public async Task ListAsync_DefaultCatalog_RequestsTheRootCatalogCount()
    {
        int? requested = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requested = QueryInt(request.RequestUri!, "per_page");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        }));

        var packages = await RootPatchReleaseClient.ListAsync(client, RootPatchCatalog.KernelSuKey,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(packages);
        Assert.Equal(RootPatchReleaseClient.DefaultCatalogCount, requested);
    }

    [Fact]
    public async Task ListPageAsync_FirstSourceFailing_ServesTheDocumentFromTheNextSource()
    {
        var hosts = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (string.Equals(request.RequestUri.Host, "mirror.test", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("mirror down in this test");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseJson("28.1"), Encoding.UTF8, "application/json")
            };
        }));
        var sources = new[]
        {
            new Uri("https://mirror.test/https://api.github.com/repos/topjohnwu/Magisk/releases?per_page=5&page=1"),
            new Uri("https://api.github.com/repos/topjohnwu/Magisk/releases?per_page=5&page=1"),
        };

        var page = await RootPatchReleaseClient.ListPageAsync(client, RootPatchCatalog.MagiskKey,
            cancellationToken: TestContext.Current.CancellationToken, sources: sources);

        Assert.Single(page.Releases);
        Assert.Equal("28.1", page.Releases[0].Version);
        Assert.Equal(["mirror.test", "api.github.com"], hosts);
    }

    [Fact]
    public async Task ListPageAsync_FirstSourceServing_SkipsTheRemainingSources()
    {
        var hosts = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseJson("28.1"), Encoding.UTF8, "application/json")
            };
        }));
        var sources = new[]
        {
            new Uri("https://mirror.test/https://api.github.com/repos/topjohnwu/Magisk/releases?per_page=5&page=1"),
            new Uri("https://api.github.com/repos/topjohnwu/Magisk/releases?per_page=5&page=1"),
        };

        var page = await RootPatchReleaseClient.ListPageAsync(client, RootPatchCatalog.MagiskKey,
            cancellationToken: TestContext.Current.CancellationToken, sources: sources);

        Assert.Single(page.Releases);
        Assert.Equal(["mirror.test"], hosts);
    }

    [Fact]
    public async Task ListPageAsync_EverySourceFailing_ReportsTheAggregate()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            throw new HttpRequestException("every source is down in this test")));
        var sources = new[]
        {
            new Uri("https://mirror.test/https://api.github.com/repos/topjohnwu/Magisk/releases?per_page=5&page=1"),
            new Uri("https://api.github.com/repos/topjohnwu/Magisk/releases?per_page=5&page=1"),
        };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            RootPatchReleaseClient.ListPageAsync(client, RootPatchCatalog.MagiskKey,
                cancellationToken: TestContext.Current.CancellationToken, sources: sources));

        Assert.Contains("mirror.test", error.Message, StringComparison.Ordinal);
        Assert.Contains("api.github.com", error.Message, StringComparison.Ordinal);
    }

    private static string ReleaseJson(string version)
        => $$"""
            [{"tag_name":"v{{version}}","assets":[
              {"name":"Magisk-v{{version}}.apk","size":1024,"browser_download_url":"https://github.com/topjohnwu/Magisk/releases/download/v{{version}}/Magisk-v{{version}}.apk"}
            ]}]
            """;

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
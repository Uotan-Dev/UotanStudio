using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using UotanStudio.Assets;
using UotanStudio.Net.Components.Scrcpy;
using UotanStudio.Net.GitHub;
using UotanStudio.Services.Scrcpy;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ScrcpyServerReleaseTests : IDisposable
{
    private readonly List<string> _createdRoots = [];
    private const string ServerAsset = "scrcpy-server-v5.0.1";
    private const string ServerUrl = "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/" + ServerAsset;
    private const string ServerDigest = "764eb6f79811d5211fe9df341120882ba9994c7a61b897d7bf3fb662e53bc536";

    [Fact]
    public void FromRelease_ReleaseApiShape_PicksTheServerAssetAndItsDigest()
    {
        const string json = """
            {"tag_name":"v5.0.1","assets":[
              {"name":"scrcpy-win64-v5.0.1.zip","size":11264634,"browser_download_url":"https://github.com/x","digest":"sha256:b12a2c4e"},
              {"name":"scrcpy-server-v5.0.1","size":733930,"browser_download_url":"https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1","digest":"sha256:764EB6F79811D5211FE9DF341120882BA9994C7A61B897D7BF3FB662E53BC536"}
            ]}
            """;

        var release = ScrcpyServerReleaseClient.FromRelease(
            GitHubReleaseClient.ParseRelease("Genymobile", "scrcpy", json));

        Assert.Equal("5.0.1", release.Version);
        Assert.Equal(ServerAsset, release.AssetName);
        Assert.Equal(733_930, release.Length);
        Assert.Equal(ServerDigest, release.Sha256);
        Assert.Equal(ServerUrl, release.DownloadUrl);
    }

    [Fact]
    public void FromRelease_AssetWithoutPublishedDigest_Refuses()
    {
        var release = new GitHubRelease("Genymobile", "scrcpy", "v5.0.1", "5.0.1",
            [new GitHubAsset(ServerAsset, ServerUrl, 733_930, null)]);

        var error = Assert.Throws<InvalidOperationException>(() => ScrcpyServerReleaseClient.FromRelease(release));

        Assert.Contains("no SHA-256 digest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromRelease_ReleaseWithoutServerAsset_Refuses()
    {
        var release = new GitHubRelease("Genymobile", "scrcpy", "v5.0.1", "5.0.1",
            [new GitHubAsset("scrcpy-win64-v5.0.1.zip", "https://github.com/x", 1, new string('a', 64))]);

        var error = Assert.Throws<InvalidOperationException>(() => ScrcpyServerReleaseClient.FromRelease(release));

        Assert.Contains(ScrcpyServerReleaseClient.ServerAssetPrefix, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v5.0.1", "5.0.1")]
    [InlineData("v5.0", "5.0")]
    [InlineData("5.0.1", "5.0.1")]
    public void TrimVersion_StripsOnlyALeadingVForDottedDecimal(string tag, string expected)
        => Assert.Equal(expected, ScrcpyServerReleaseClient.TrimVersion(tag));

    [Theory]
    [InlineData("release-2026")]
    [InlineData("v")]
    [InlineData("5..1")]
    [InlineData("5.0.1-rc1")]
    public void TrimVersion_RejectsTagsWithoutADottedDecimalVersion(string tag)
        => Assert.Null(ScrcpyServerReleaseClient.TrimVersion(tag));

    [Fact]
    public void ParseDigest_RejectsADigestThatIsNotSixtyFourHex()
        => Assert.Null(ScrcpyServerReleaseClient.ParseDigest("sha256:deadbeef"));

    [Fact]
    public void ParseDigest_AcceptsADigestWithoutTheAlgorithmPrefix()
        => Assert.Equal(new string('a', 64), ScrcpyServerReleaseClient.ParseDigest(new string('A', 64)));

    [Fact]
    public void Resolve_EmptyCacheWithoutBundledJar_ReportsTheStoreRoot()
    {
        var service = new ScrcpyServerService(new HttpClient(), cacheRoot: NewStoreRoot());

        Assert.Null(service.ReadInstall());
        var error = Assert.Throws<FileNotFoundException>(service.Resolve);

        Assert.Contains("scrcpy-server.jar", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_CachedJarWhoseBytesDivergedFromTheManifest_IsRejected()
    {
        var root = NewStoreRoot();
        var service = new ScrcpyServerService(new HttpClient(), cacheRoot: root);
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        Directory.CreateDirectory(Path.GetDirectoryName(service.JarPathFor("9.9.9"))!);
        File.WriteAllBytes(service.JarPathFor("9.9.9"), payload);
        WriteInstall(root, new ScrcpyServerInstall("9.9.9", Sha256Hex(payload), ServerAsset, ServerUrl, ServerUrl, 1));

        Assert.NotNull(service.ReadInstall());
        var matched = service.Resolve();

        Assert.Equal("9.9.9", matched.Version);
        Assert.Equal(ScrcpyServerSource.Provisioned, matched.Source);

        File.WriteAllBytes(service.JarPathFor("9.9.9"), new byte[] { 1, 2, 3, 4, 6 });

        Assert.Throws<FileNotFoundException>(service.Resolve);
    }

    [Fact]
    public void ReadInstall_CorruptManifestFile_IsTreatedAsNoInstall()
    {
        var root = NewStoreRoot();
        var service = new ScrcpyServerService(new HttpClient(), cacheRoot: root);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ScrcpyServerService.ManifestFileName), "{ not json");

        Assert.Null(service.ReadInstall());
    }

    [Fact]
    public void JarPathFor_KeepsEachVersionInItsOwnDirectory()
    {
        var root = NewStoreRoot();
        var service = new ScrcpyServerService(new HttpClient(), cacheRoot: root);

        Assert.Equal(Path.Combine(root, "5.0.1", "scrcpy-server.jar"), service.JarPathFor("5.0.1"));
    }

    [Fact]
    public void BundledCatalog_DigestMatchesTheJarShippedWithTheApplication()
    {
        Assert.SkipWhen(!AssetCatalog.ScrcpyServer.IsPresent,
            "UotanStudio.Assets/Assets/scrcpy/scrcpy-server.jar 未随包，随包产物门禁无从校验");

        var entry = AssetCatalog.ScrcpyServer[AssetNames.Scrcpy.ServerJar];

        Assert.Equal(ScrcpyServerService.BundledSha256, entry.Sha256);
    }

    [Fact]
    public async Task EnsureLatest_EmptyCacheWithoutBundledJar_DoesNotResolveBeforeFetching()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        var service = new ScrcpyServerService(client, progress: null, cacheRoot: NewStoreRoot(), useMirrorList: false);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.EnsureLatestAsync(TestContext.Current.CancellationToken));

        Assert.Contains("GitHub release feed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureLatest_EmptyCache_DownloadsAndInstallsTheLatestRelease()
    {
        var payload = new byte[512];
        Random.Shared.NextBytes(payload);
        var digest = Sha256Hex(payload);
        var root = NewStoreRoot();
        var releaseJson = $$"""
            {"tag_name":"v9.9.9","assets":[
              {"name":"{{ServerAsset}}","size":{{payload.Length}},"browser_download_url":"{{ServerUrl}}","digest":"sha256:{{digest}}"}
            ]}
            """;
        using var client = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsoluteUri == ScrcpyServerReleaseClient.LatestReleaseUrl
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, System.Text.Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var service = new ScrcpyServerService(client, progress: null, cacheRoot: root, useMirrorList: false);

        var artifact = await service.EnsureLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScrcpyServerSource.Provisioned, artifact.Source);
        Assert.Equal("9.9.9", artifact.Version);
        Assert.True(File.Exists(artifact.Path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(artifact.Path, TestContext.Current.CancellationToken));
        var install = service.ReadInstall();
        Assert.NotNull(install);
        Assert.Equal(digest, install.Sha256);
    }

    [Fact]
    public async Task EnsureLatest_UnreachableFeed_KeepsTheLocalArtifact()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var root = NewStoreRoot();
        var service = new ScrcpyServerService(
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline"))),
            progress: null, cacheRoot: root, useMirrorList: false);
        Directory.CreateDirectory(Path.GetDirectoryName(service.JarPathFor("9.9.9"))!);
        File.WriteAllBytes(service.JarPathFor("9.9.9"), payload);
        WriteInstall(root, new ScrcpyServerInstall("9.9.9", Sha256Hex(payload), ServerAsset, ServerUrl, ServerUrl, 1));

        var artifact = await service.EnsureLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("9.9.9", artifact.Version);
        Assert.Equal(ScrcpyServerSource.Provisioned, artifact.Source);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static void WriteInstall(string root, ScrcpyServerInstall install)
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, ScrcpyServerService.ManifestFileName),
            JsonSerializer.SerializeToUtf8Bytes(install, ScrcpyServerJsonContext.Default.ScrcpyServerInstall));
    }

    private string NewStoreRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uotan-scrcpy-{Guid.NewGuid():N}");
        _createdRoots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in _createdRoots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Sha256Hex(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
}
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using UotanStudio.Net.Components.Scrcpy;
using UotanStudio.Net.GitHub;
using UotanStudio.Services.Scrcpy;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class OhscrcpyServerTests : IDisposable
{
    private readonly List<string> _createdRoots = [];
    private const string ServerDigest = "a65a99f1222f8fc13c8d7ad33d01390a00ffd227a438840231f3860c69edd0c7";
    private const string ServerUrl = "https://github.com/guoxiucai/ohos-scrcpy-app/releases/download/1.0.3/OHScrcpyServer.hap";

    [Fact]
    public void FromRelease_ReleaseApiShape_PicksTheDeviceServerHap()
    {
        const string json = """
            {"tag_name":"1.0.3","assets":[
              {"name":"HongJing-1.0.3.dmg","size":22209202,"browser_download_url":"https://github.com/x","digest":"sha256:e572c3c8"},
              {"name":"OHScrcpyServer.hap","size":1664409,"browser_download_url":"https://github.com/guoxiucai/ohos-scrcpy-app/releases/download/1.0.3/OHScrcpyServer.hap","digest":"sha256:A65A99F1222F8FC13C8D7AD33D01390A00FFD227A438840231F3860C69EDD0C7"}
            ]}
            """;

        var release = OhscrcpyServerReleaseClient.FromRelease(
            GitHubReleaseClient.ParseRelease("guoxiucai", "ohos-scrcpy-app", json));

        Assert.Equal("1.0.3", release.Version);
        Assert.Equal(OhscrcpyServerReleaseClient.ServerAssetName, release.AssetName);
        Assert.Equal(1_664_409, release.Length);
        Assert.Equal(ServerDigest, release.Sha256);
        Assert.Equal(ServerUrl, release.DownloadUrl);
    }

    [Fact]
    public void FromRelease_ReleaseWithoutTheHap_Refuses()
    {
        var release = new GitHubRelease("guoxiucai", "ohos-scrcpy-app", "1.0.3", "1.0.3",
            [new GitHubAsset("HongJing-1.0.3.dmg", "https://github.com/x", 1, new string('a', 64))]);

        var error = Assert.Throws<InvalidOperationException>(() => OhscrcpyServerReleaseClient.FromRelease(release));

        Assert.Contains(OhscrcpyServerReleaseClient.ServerAssetName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromRelease_HapWithoutPublishedDigest_Refuses()
    {
        var release = new GitHubRelease("guoxiucai", "ohos-scrcpy-app", "1.0.3", "1.0.3",
            [new GitHubAsset(OhscrcpyServerReleaseClient.ServerAssetName, ServerUrl, 1, null)]);

        var error = Assert.Throws<InvalidOperationException>(() => OhscrcpyServerReleaseClient.FromRelease(release));

        Assert.Contains("no SHA-256 digest", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.3", "1.0.3")]
    [InlineData("v1.0.3", "1.0.3")]
    public void TrimVersion_AcceptsPlainAndVPrefixedTags(string tag, string expected)
        => Assert.Equal(expected, OhscrcpyServerReleaseClient.TrimVersion(tag));

    [Theory]
    [InlineData("release-2026")]
    [InlineData("v")]
    [InlineData("1..3")]
    [InlineData("1.0.3-rc1")]
    public void TrimVersion_RejectsTagsWithoutADottedDecimalVersion(string tag)
        => Assert.Null(OhscrcpyServerReleaseClient.TrimVersion(tag));

    [Theory]
    [InlineData("OHScrcpyServer.hap", true)]
    [InlineData("OHScrcpyServer-1.0.3.hap", true)]
    [InlineData("OHScrcpyServer.hap.sig", false)]
    [InlineData("HongJing-Setup-1.0.3.exe", false)]
    public void IsServerAsset_AcceptsOnlyTheHapPackage(string name, bool expected)
        => Assert.Equal(expected, OhscrcpyServerReleaseClient.IsServerAsset(name));

    [Fact]
    public void Resolve_EmptyCacheWithoutSideLoadedHap_ReportsTheAssetAndStoreRoot()
    {
        var root = NewStoreRoot();
        var service = new OhscrcpyServerService(new HttpClient(), cacheRoot: root);

        Assert.Null(service.ReadInstall());
        var error = Assert.Throws<FileNotFoundException>(service.Resolve);

        Assert.Contains(OhscrcpyServerReleaseClient.ServerAssetName, error.Message, StringComparison.Ordinal);
        Assert.Contains(root, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_CachedHapWhoseBytesDivergedFromTheManifest_IsRejected()
    {
        var root = NewStoreRoot();
        var service = new OhscrcpyServerService(new HttpClient(), cacheRoot: root);
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        Directory.CreateDirectory(Path.GetDirectoryName(service.HapPathFor("9.9.9"))!);
        File.WriteAllBytes(service.HapPathFor("9.9.9"), payload);
        WriteInstall(root, new OhscrcpyServerInstall("9.9.9", Sha256Hex(payload),
            OhscrcpyServerReleaseClient.ServerAssetName, ServerUrl, ServerUrl, 1));

        var matched = service.Resolve();

        Assert.Equal("9.9.9", matched.Version);
        Assert.Equal(OhscrcpyServerSource.Provisioned, matched.Source);

        File.WriteAllBytes(service.HapPathFor("9.9.9"), new byte[] { 1, 2, 3, 4, 6 });

        Assert.Throws<FileNotFoundException>(service.Resolve);
    }

    [Fact]
    public void ReadInstall_CorruptManifestFile_IsTreatedAsNoInstall()
    {
        var root = NewStoreRoot();
        var service = new OhscrcpyServerService(new HttpClient(), cacheRoot: root);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, OhscrcpyServerService.ManifestFileName), "{ not json");

        Assert.Null(service.ReadInstall());
    }

    [Fact]
    public void HapPathFor_KeepsEachVersionInItsOwnDirectory()
    {
        var root = NewStoreRoot();
        var service = new OhscrcpyServerService(new HttpClient(), cacheRoot: root);

        Assert.Equal(Path.Combine(root, "1.0.3", OhscrcpyServerReleaseClient.ServerAssetName),
            service.HapPathFor("1.0.3"));
    }

    [Fact]
    public async Task EnsureLatest_EmptyCacheAndOfflineFeed_ReportsThatNothingIsAvailable()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        var service = new OhscrcpyServerService(client, progress: null, cacheRoot: NewStoreRoot(), useMirrorList: false);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.EnsureLatestAsync(TestContext.Current.CancellationToken));

        Assert.Contains("GitHub release feed", error.Message, StringComparison.Ordinal);
        Assert.Contains(OhscrcpyServerReleaseClient.ServerAssetName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureLatest_OfflineFeed_KeepsTheVerifiedCache()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var root = NewStoreRoot();
        var service = new OhscrcpyServerService(
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline"))),
            progress: null, cacheRoot: root, useMirrorList: false);
        Directory.CreateDirectory(Path.GetDirectoryName(service.HapPathFor("9.9.9"))!);
        File.WriteAllBytes(service.HapPathFor("9.9.9"), payload);
        WriteInstall(root, new OhscrcpyServerInstall("9.9.9", Sha256Hex(payload),
            OhscrcpyServerReleaseClient.ServerAssetName, ServerUrl, ServerUrl, 1));

        var artifact = await service.EnsureLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("9.9.9", artifact.Version);
        Assert.Equal(OhscrcpyServerSource.Provisioned, artifact.Source);
    }

    [Fact]
    public async Task EnsureLatest_EmptyCache_DownloadsAndInstallsTheLatestHap()
    {
        var payload = new byte[512];
        Random.Shared.NextBytes(payload);
        var digest = Sha256Hex(payload);
        var root = NewStoreRoot();
        var releaseJson = $$"""
            {"tag_name":"9.9.9","assets":[
              {"name":"{{OhscrcpyServerReleaseClient.ServerAssetName}}","size":{{payload.Length}},"browser_download_url":"{{ServerUrl}}","digest":"sha256:{{digest}}"}
            ]}
            """;
        using var client = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsoluteUri == OhscrcpyServerReleaseClient.LatestReleaseUrl
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, System.Text.Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var service = new OhscrcpyServerService(client, progress: null, cacheRoot: root, useMirrorList: false);

        var artifact = await service.EnsureLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OhscrcpyServerSource.Provisioned, artifact.Source);
        Assert.Equal("9.9.9", artifact.Version);
        Assert.True(File.Exists(artifact.Path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(artifact.Path, TestContext.Current.CancellationToken));
        var install = service.ReadInstall();
        Assert.NotNull(install);
        Assert.Equal(digest, install.Sha256);
    }

    [Fact]
    public async Task InstallAsync_DigestMismatch_ThrowsAndLeavesNoManifest()
    {
        var payload = new byte[] { 9, 8, 7 };
        var root = NewStoreRoot();
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var service = new OhscrcpyServerService(client, progress: null, cacheRoot: root, useMirrorList: false);
        var release = new OhscrcpyServerRelease("1.0.3", "1.0.3", OhscrcpyServerReleaseClient.ServerAssetName,
            ServerUrl, payload.Length, new string('a', 64));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InstallAsync(release, TestContext.Current.CancellationToken));

        Assert.Contains("digest mismatch", error.Message, StringComparison.Ordinal);
        Assert.Null(service.ReadInstall());
        Assert.Empty(Directory.GetFiles(root, "*.part-*", SearchOption.TopDirectoryOnly));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static void WriteInstall(string root, OhscrcpyServerInstall install)
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, OhscrcpyServerService.ManifestFileName),
            JsonSerializer.SerializeToUtf8Bytes(install, OhscrcpyServerJsonContext.Default.OhscrcpyServerInstall));
    }

    private string NewStoreRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uotan-ohscrcpy-{Guid.NewGuid():N}");
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
using System.Security.Cryptography;
using System.Text.Json;
using UotanStudio.Services.Scrcpy;
using UotanStudio.Tests.Firmware.Architecture;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ScrcpyServerReleaseTests : IDisposable
{
    private readonly List<string> _createdRoots = [];
    private const string ServerAsset = "scrcpy-server-v5.0.1";
    private const string ServerUrl = "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/" + ServerAsset;
    private const string ServerDigest = "764eb6f79811d5211fe9df341120882ba9994c7a61b897d7bf3fb662e53bc536";

    [Fact]
    public void FromDocument_ReleaseApiShape_PicksTheServerAssetAndItsDigest()
    {
        const string json = """
            {"tag_name":"v5.0.1","assets":[
              {"name":"scrcpy-win64-v5.0.1.zip","size":11264634,"browser_download_url":"https://github.com/x","digest":"sha256:b12a2c4e"},
              {"name":"scrcpy-server-v5.0.1","size":733930,"browser_download_url":"https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1","digest":"sha256:764EB6F79811D5211FE9DF341120882BA9994C7A61B897D7BF3FB662E53BC536"}
            ]}
            """;

        var document = JsonSerializer.Deserialize(json, ScrcpyReleaseJsonContext.Default.GitHubReleaseDocument)!;
        var release = ScrcpyServerReleaseClient.FromDocument(document);

        Assert.Equal("5.0.1", release.Version);
        Assert.Equal(ServerAsset, release.AssetName);
        Assert.Equal(733_930, release.Length);
        Assert.Equal(ServerDigest, release.Sha256);
        Assert.Equal(ServerUrl, release.DownloadUrl);
    }

    [Fact]
    public void FromDocument_AssetWithoutPublishedDigest_Refuses()
    {
        var document = new GitHubReleaseDocument("v5.0.1", [new GitHubAssetDocument(ServerAsset, 733_930, ServerUrl, null)]);

        var error = Assert.Throws<InvalidOperationException>(() => ScrcpyServerReleaseClient.FromDocument(document));

        Assert.Contains("no SHA-256 digest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromDocument_ReleaseWithoutServerAsset_Refuses()
    {
        var document = new GitHubReleaseDocument("v5.0.1",
            [new GitHubAssetDocument("scrcpy-win64-v5.0.1.zip", 1, "https://github.com/x", "sha256:" + new string('a', 64))]);

        var error = Assert.Throws<InvalidOperationException>(() => ScrcpyServerReleaseClient.FromDocument(document));

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
        var bundled = ScrcpyServerService.BundledCandidates.FirstOrDefault(File.Exists)
            ?? (FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var repositoryRoot)
                ? Path.Combine(repositoryRoot!, "UotanStudio.Desktop", "Assets", "scrcpy-server.jar")
                : null);
        Assert.SkipWhen(bundled is null || !File.Exists(bundled),
            "Neither the test host output nor a checked-out repository above it exposes "
            + "UotanStudio.Desktop/Assets/scrcpy-server.jar, so the shipped-artifact gate has nothing to hash.");

        using var stream = File.OpenRead(bundled!);

        Assert.Equal(ScrcpyServerService.BundledSha256,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
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
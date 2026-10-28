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
    public void SelectPackageAsset_AssetWithoutApkOrZip_ReturnsNull()
    {
        GitHubAsset[] assets = [new("APatch_v1.5.0.apk.sha256", "https://github.com/x/digest", 1, null)];

        Assert.Null(RootPatchReleaseClient.SelectPackageAsset(assets));
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
}
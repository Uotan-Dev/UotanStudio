using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using UotanStudio.Firmware.BootSecurity.Patcher;
using UotanStudio.Services.PatchPackages;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class RootPatchPackageTests : IDisposable
{
    private const string AssetName = "Magisk-v28.1.apk";
    private const string AssetUrl = "https://github.com/topjohnwu/Magisk/releases/download/v28.1/" + AssetName;

    private readonly List<string> _roots = [];

    [Fact]
    public void CommandFolderFor_EachSource_KeepsItsOwnFolderUnderTheSharedRoot()
    {
        var root = NewRoot();
        var service = NewService(new HttpClient(), root, useMirrorList: false);

        Assert.Equal(Path.Combine(root, "magisk"), service.CommandFolderFor(RootPatchPackageSource.Magisk));
        Assert.Equal(Path.Combine(root, "magisk-alpha"), service.CommandFolderFor(RootPatchPackageSource.MagiskAlpha));
        Assert.Equal(Path.Combine(root, "kernelsu"), service.CommandFolderFor(RootPatchPackageSource.KernelSu));
        Assert.Equal(Path.Combine(root, "sukisu"), service.CommandFolderFor(RootPatchPackageSource.SukiSu));
        Assert.Equal(Path.Combine(root, "apatch"), service.CommandFolderFor(RootPatchPackageSource.APatch));
    }

    [Fact]
    public void PackagePathFor_VersionAndAsset_LandInsideTheCommandVersionFolder()
    {
        var root = NewRoot();
        var service = NewService(new HttpClient(), root, useMirrorList: false);

        var path = service.PackagePathFor(Package(RootPatchPackageSource.Magisk, "28.1", 12, null));

        Assert.Equal(Path.Combine(root, "magisk", "28.1", AssetName), path);
    }

    [Fact]
    public void PackagePathFor_PrereleaseWithSeparators_IsSanitizedIntoOneFolderSegment()
    {
        var root = NewRoot();
        var service = NewService(new HttpClient(), root, useMirrorList: false);

        var path = service.PackagePathFor(Package(RootPatchPackageSource.KernelSu, "1.0.0/rc:1", 12, null));

        var directory = Path.GetFileName(Path.GetDirectoryName(path)!);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, directory);
        Assert.Equal("1.0.0_rc_1", directory);
    }

    [Theory]
    [InlineData(RootPatchPackageSource.Magisk, BootPatchKind.Magisk)]
    [InlineData(RootPatchPackageSource.MagiskAlpha, BootPatchKind.Magisk)]
    [InlineData(RootPatchPackageSource.KernelSu, BootPatchKind.KernelSu)]
    [InlineData(RootPatchPackageSource.SukiSu, BootPatchKind.KernelSu)]
    [InlineData(RootPatchPackageSource.APatch, BootPatchKind.APatch)]
    public void ExpectedKind_ForkFamilies_MapOntoThePatchPathTheyUse(RootPatchPackageSource source, BootPatchKind expected)
        => Assert.Equal(expected, source.ExpectedKind());

    [Fact]
    public void FromIndex_PresentationOrder_MatchesTheWindowList()
    {
        Assert.Equal(RootPatchPackageSource.Magisk, RootPatchSources.FromIndex(0));
        Assert.Equal(RootPatchPackageSource.MagiskAlpha, RootPatchSources.FromIndex(1));
        Assert.Equal(RootPatchPackageSource.KernelSu, RootPatchSources.FromIndex(2));
        Assert.Equal(RootPatchPackageSource.SukiSu, RootPatchSources.FromIndex(3));
        Assert.Equal(RootPatchPackageSource.APatch, RootPatchSources.FromIndex(4));
        Assert.Null(RootPatchSources.FromIndex(5));
    }

    [Fact]
    public async Task EnsurePackageAsync_CachedVerbatimFile_IsReturnedWithoutNetwork()
    {
        var root = NewRoot();
        var payload = new byte[] { 7, 7, 7, 7 };
        var package = Package(RootPatchPackageSource.Magisk, "28.1", payload.Length, Sha256Hex(payload));
        var service = NewService(new HttpClient(new StubHandler(_ =>
            throw new HttpRequestException("the network must not be touched"))), root, useMirrorList: false);
        var path = service.PackagePathFor(package);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, payload, TestContext.Current.CancellationToken);

        var artifact = await service.EnsurePackageAsync(package, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(artifact.FromCache);
        Assert.Equal(path, artifact.Path);
        Assert.Equal(payload, await File.ReadAllBytesAsync(artifact.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsurePackageAsync_MissingFile_DownloadsAndVerifiesBeforePublishing()
    {
        var root = NewRoot();
        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);
        var package = Package(RootPatchPackageSource.Magisk, "28.1", payload.Length, Sha256Hex(payload));
        var calls = 0;
        var service = NewService(new HttpClient(new StubHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        })), root, useMirrorList: false);
        var path = service.PackagePathFor(package);

        var artifact = await service.EnsurePackageAsync(package, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(artifact.FromCache);
        Assert.Equal(path, artifact.Path);
        Assert.True(calls > 0);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.part-*"));
    }

    [Fact]
    public async Task EnsurePackageAsync_DigestMismatch_CommitsNothingAndDeletesTheStagedFile()
    {
        var root = NewRoot();
        var payload = new byte[256];
        Random.Shared.NextBytes(payload);
        var package = Package(RootPatchPackageSource.Magisk, "28.1", payload.Length, new string('a', 64));
        var service = NewService(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })), root, useMirrorList: false);
        var path = service.PackagePathFor(package);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.EnsurePackageAsync(package, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("digest mismatch", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.part-*"));
    }

    [Fact]
    public async Task ValidateAsync_UnreadableFile_ReportsUnsupported()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "package.apk");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "not a zip container", TestContext.Current.CancellationToken);
        var service = NewService(new HttpClient(), root, useMirrorList: false);

        var validation = await service.ValidateAsync(path, RootPatchPackageSource.Magisk,
            TestContext.Current.CancellationToken);

        Assert.False(validation.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(validation.Problem));
    }

    [Fact]
    public async Task ValidateAsync_MissingFile_ReportsUnsupported()
    {
        var root = NewRoot();
        var service = NewService(new HttpClient(), root, useMirrorList: false);

        var validation = await service.ValidateAsync(Path.Combine(root, "absent.apk"), null,
            TestContext.Current.CancellationToken);

        Assert.False(validation.IsSupported);
        Assert.Contains("does not exist", validation.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_MagiskPackageForTheApatchSource_ReportsTheFamilyMismatch()
    {
        var root = NewRoot();
        var path = WriteMagiskPackage(root);
        var service = NewService(new HttpClient(), root, useMirrorList: false);

        var validation = await service.ValidateAsync(path, RootPatchPackageSource.APatch,
            TestContext.Current.CancellationToken);

        Assert.False(validation.IsSupported);
        Assert.Equal(BootPatchKind.Magisk, validation.Detection!.Kind);
        Assert.Contains("apatch", validation.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_MagiskPackageWithoutExpectedSource_IsAcceptedWithItsAbi()
    {
        var root = NewRoot();
        var path = WriteMagiskPackage(root);
        var service = NewService(new HttpClient(), root, useMirrorList: false);

        var validation = await service.ValidateAsync(path, null, TestContext.Current.CancellationToken);

        Assert.True(validation.IsSupported);
        Assert.Null(validation.Problem);
        Assert.Equal(BootPatchKind.Magisk, validation.Detection!.Kind);
        Assert.Contains("arm64", validation.Detection.Architectures, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeSourcesAsync_OnlyTheOriginResponding_PinsItForTheDownload()
    {
        var root = NewRoot();
        var payload = new byte[2048];
        Random.Shared.NextBytes(payload);
        var hosts = new ConcurrentBag<string>();
        var service = NewService(new HttpClient(new StubHandler(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (!string.Equals(request.RequestUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("mirror unavailable in this test");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        })), root, useMirrorList: true);
        var package = Package(RootPatchPackageSource.Magisk, "28.1", payload.Length, Sha256Hex(payload));

        var fastest = await service.ProbeSourcesAsync(new Uri(AssetUrl), TestContext.Current.CancellationToken);

        Assert.Equal("https://github.com", fastest);
        Assert.Equal("https://github.com", service.PreferredSourceAuthority);

        var artifact = await service.EnsurePackageAsync(package, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(artifact.FromCache);
        Assert.Equal(payload, await File.ReadAllBytesAsync(artifact.Path, TestContext.Current.CancellationToken));
        Assert.Contains("github.com", hosts);
        Assert.Equal(payload.Length, new FileInfo(artifact.Path).Length);
    }

    [Fact]
    public async Task HasStoredPackage_MatchingLength_IsReportedAsDownloaded()
    {
        var root = NewRoot();
        var payload = new byte[64];
        var package = Package(RootPatchPackageSource.Magisk, "28.1", payload.Length, Sha256Hex(payload));
        var service = NewService(new HttpClient(), root, useMirrorList: false);
        var path = service.PackagePathFor(package);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, payload, TestContext.Current.CancellationToken);

        Assert.True(service.HasStoredPackage(package));
    }

    [Fact]
    public async Task HasStoredPackage_LengthMismatch_IsReportedAsNotDownloaded()
    {
        var root = NewRoot();
        var package = Package(RootPatchPackageSource.Magisk, "28.1", 64, null);
        var service = NewService(new HttpClient(), root, useMirrorList: false);
        var path = service.PackagePathFor(package);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[32], TestContext.Current.CancellationToken);

        Assert.False(service.HasStoredPackage(package));
    }

    [Fact]
    public void HasStoredPackage_AbsentFile_IsReportedAsNotDownloaded()
    {
        var service = NewService(new HttpClient(), NewRoot(), useMirrorList: false);

        Assert.False(service.HasStoredPackage(Package(RootPatchPackageSource.APatch, "1.5.0", 64, null)));
    }

    [Fact]
    public void PackagePathFor_TraversalInTheAssetName_StaysInsideTheCommandFolder()
    {
        var root = NewRoot();
        var service = NewService(new HttpClient(), root, useMirrorList: false);
        var hostile = new RootPatchPackage(RootPatchPackageSource.SukiSu, "3.1.8", "v3.1.8", "../../evil.apk",
            AssetUrl, 12, null);

        var path = service.PackagePathFor(hostile);

        Assert.Equal(Path.Combine(root, "sukisu", "3.1.8", "evil.apk"), path);
        Assert.StartsWith(service.CommandFolderFor(RootPatchPackageSource.SukiSu), path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListPackagesAsync_RepeatedCalls_ReadTheReleaseFeedOnce()
    {
        var root = NewRoot();
        var calls = 0;
        var service = NewService(new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.Host != "api.github.com")
            {
                throw new HttpRequestException("only the release feed may be read here");
            }

            Interlocked.Increment(ref calls);
            return Json(ReleasesJson("topjohnwu", "Magisk", "28.1"));
        })), root, useMirrorList: false);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await service.ListPackagesAsync(RootPatchPackageSource.Magisk, cancellationToken);
        var second = await service.ListPackagesAsync(RootPatchPackageSource.Magisk, cancellationToken);

        Assert.Single(first);
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
        Assert.True(service.CatalogLoaded(RootPatchPackageSource.Magisk));
        Assert.Null(service.TryGetCatalog(RootPatchPackageSource.KernelSu));
        Assert.Equal(first, service.TryGetCatalog(RootPatchPackageSource.Magisk));
    }

    [Fact]
    public async Task WarmUpAsync_EverySource_CachesFiveCatalogsWithoutReadingTheFeedAgain()
    {
        var feedReads = new ConcurrentBag<string>();
        var service = NewService(new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var segments = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var owner = segments[^3];
                var repo = segments[^2];
                feedReads.Add($"{owner}/{repo}");
                return Json(ReleasesJson(owner, repo, "28.1"));
            }

            if (string.Equals(request.RequestUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) };
            }

            throw new HttpRequestException("mirror unavailable in this test");
        })), NewRoot(), useMirrorList: true);
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.WarmUpAsync(cancellationToken);

        Assert.Equal(RootPatchSources.All.Count, feedReads.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(RootPatchSources.All, source => Assert.True(service.CatalogLoaded(source)));
        Assert.Equal("https://github.com", service.PreferredSourceAuthority);

        var before = feedReads.Count;
        await service.ListPackagesAsync(RootPatchPackageSource.APatch, cancellationToken);
        await service.WarmUpAsync(cancellationToken);

        Assert.Equal(before, feedReads.Count);
    }

    [Fact]
    public async Task ProbeSourcesAsync_SecondCall_ReusesThePinnedSourceWithoutProbing()
    {
        var requests = 0;
        var service = NewService(new HttpClient(new StubHandler(request =>
        {
            Interlocked.Increment(ref requests);
            if (!string.Equals(request.RequestUri!.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("mirror unavailable in this test");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) };
        })), NewRoot(), useMirrorList: true);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await service.ProbeSourcesAsync(new Uri(AssetUrl), cancellationToken);
        var afterFirst = requests;
        var second = await service.ProbeSourcesAsync(new Uri(AssetUrl), cancellationToken);

        Assert.Equal("https://github.com", first);
        Assert.Equal(first, second);
        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, requests);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string ReleasesJson(string owner, string repo, string version)
    {
        var asset = $"{repo}-v{version}.apk";
        return $$"""
            [{"tag_name":"v{{version}}","assets":[{"name":"{{asset}}","size":1024,"browser_download_url":"https://github.com/{{owner}}/{{repo}}/releases/download/v{{version}}/{{asset}}"}]}]
            """;
    }

    private RootPatchPackage Package(RootPatchPackageSource source, string version, long length, string? sha256)
        => new(source, version, "v" + version, AssetName, AssetUrl, length, sha256);

    private static RootPatchPackageService NewService(HttpClient client, string root, bool useMirrorList)
        => new(client, root, useMirrorList);

    private static string WriteMagiskPackage(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Magisk-v28.1.apk");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("lib/arm64-v8a/libmagisk64.so");
        using var stream = entry.Open();
        stream.Write([0x7f, (byte)'E', (byte)'L', (byte)'F']);
        return path;
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uotan-rootpatch-{Guid.NewGuid():N}");
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
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

    private static string Sha256Hex(byte[] payload) => Convert.ToHexStringLower(SHA256.HashData(payload));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
using System.Collections.Concurrent;
using System.Globalization;
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

    [Theory]
    [InlineData(RootPatchPackageSource.SukiSu, BootPatchKind.KernelSu, true)]
    [InlineData(RootPatchPackageSource.SukiSu, BootPatchKind.KernelPatch, true)]
    [InlineData(RootPatchPackageSource.SukiSu, BootPatchKind.Magisk, false)]
    [InlineData(RootPatchPackageSource.SukiSu, BootPatchKind.APatch, false)]
    [InlineData(RootPatchPackageSource.KernelSu, BootPatchKind.KernelPatch, false)]
    [InlineData(RootPatchPackageSource.APatch, BootPatchKind.KernelPatch, false)]
    public void Accepts_SourceAndDetectedFamily_MatchesThePatchPath(
        RootPatchPackageSource source, BootPatchKind kind, bool accepted)
        => Assert.Equal(accepted, source.Accepts(kind));

    [Fact]
    public void DefaultSuperKey_OnlyTheSukiSuSource_PrefillsTheKernelPatchConvention()
    {
        Assert.Equal(RootPatchSources.KernelPatchDefaultSuperKey,
            RootPatchPackageSource.SukiSu.DefaultSuperKey());
        Assert.Null(RootPatchPackageSource.KernelSu.DefaultSuperKey());
        Assert.Null(RootPatchPackageSource.APatch.DefaultSuperKey());
    }

    [Fact]
    public async Task ValidateAsync_SukiSuKernelPatchBuild_IsAcceptedOnlyByTheSukiSuSource()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "SukiSU.apk");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("assets/kpm/kpimg");
            archive.CreateEntry("lib/arm64-v8a/libkptools.so");
        }
        var service = NewService(new HttpClient(), root, useMirrorList: false);

        var accepted = await service.ValidateAsync(path, RootPatchPackageSource.SukiSu,
            TestContext.Current.CancellationToken);
        var rejected = await service.ValidateAsync(path, RootPatchPackageSource.KernelSu,
            TestContext.Current.CancellationToken);

        Assert.True(accepted.IsSupported);
        Assert.Equal(BootPatchKind.KernelPatch, accepted.Detection!.Kind);
        Assert.False(rejected.IsSupported);
    }

    [Fact]
    public async Task ValidateAsync_LegacyMagiskZip_ReportsTheLayoutReason()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Magisk-v21.4.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("arm/magiskinit");
            archive.CreateEntry("common/boot_patch.sh");
        }

        var service = NewService(new HttpClient(), root, useMirrorList: false);
        var validation = await service.ValidateAsync(path, RootPatchPackageSource.Magisk,
            TestContext.Current.CancellationToken);

        Assert.False(validation.IsSupported);
        Assert.NotNull(validation.Problem);
        Assert.Contains("v22.0", validation.Problem!, StringComparison.Ordinal);
    }

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
    public async Task ListPackagesAsync_CancelledWaiter_LeavesTheSharedReadRunningForTheNextCaller()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GateHandler(gate.Task);
        var service = NewService(new HttpClient(handler), NewRoot(), useMirrorList: false);
        using var cancelled = new CancellationTokenSource();

        var first = service.ListPackagesAsync(RootPatchPackageSource.Magisk, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(service.CatalogLoaded(RootPatchPackageSource.Magisk));

        gate.SetResult(Json(ReleasesJson("topjohnwu", "Magisk", "28.1")));
        var catalog = await service.ListPackagesAsync(RootPatchPackageSource.Magisk,
            TestContext.Current.CancellationToken);

        Assert.Single(catalog);
        Assert.Equal(1, handler.Calls);
        Assert.True(service.CatalogLoaded(RootPatchPackageSource.Magisk));
    }

    [Fact]
    public async Task LoadMoreAsync_NextPage_AppendsOlderReleasesAndClearsHasMore()
    {
        var pages = new ConcurrentBag<int>();
        var service = NewService(new HttpClient(new StubHandler(request =>
        {
            var page = QueryInt(request.RequestUri!, "page");
            pages.Add(page);
            return Json(ReleasesJson("topjohnwu", "Magisk", page == 1 ? 5 : 2, page));
        })), NewRoot(), useMirrorList: false);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await service.ListPackagesAsync(RootPatchPackageSource.Magisk, cancellationToken);

        Assert.Equal(5, first.Count);
        Assert.True(service.HasMore(RootPatchPackageSource.Magisk));

        var all = await service.LoadMoreAsync(RootPatchPackageSource.Magisk, cancellationToken);

        Assert.Equal(7, all.Count);
        Assert.Equal(7, service.TryGetCatalog(RootPatchPackageSource.Magisk)!.Count);
        Assert.False(service.HasMore(RootPatchPackageSource.Magisk));
        Assert.Contains(2, pages);
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
        Assert.Equal("https://api.github.com", service.PreferredSourceAuthority);

        var before = feedReads.Count;
        await service.ListPackagesAsync(RootPatchPackageSource.APatch, cancellationToken);
        await service.WarmUpAsync(cancellationToken);

        Assert.Equal(before, feedReads.Count);
    }

    [Fact]
    public async Task WarmUpAsync_MirrorServingOnlyTheProbe_KeepsCatalogsOnTheOrigin()
    {
        var catalogReads = new ConcurrentBag<string>();
        var service = NewService(new HttpClient(new StubHandler(request =>
        {
            var host = request.RequestUri!.Host;
            if (string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                if (QueryInt(request.RequestUri, "per_page") == 1)
                {
                    throw new HttpRequestException("the origin does not answer the speed probe here");
                }

                catalogReads.Add(host);
                var segments = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return Json(ReleasesJson(segments[^3], segments[^2], "28.1"));
            }

            if (string.Equals(host, "ghfast.top", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024]) };
            }

            throw new HttpRequestException($"source unavailable in this test: {host}");
        })), NewRoot(), useMirrorList: true);
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.WarmUpAsync(cancellationToken);

        Assert.Equal("https://ghfast.top", service.PreferredSourceAuthority);
        Assert.Equal(RootPatchSources.All.Count, catalogReads.Count);
        Assert.All(RootPatchSources.All, source =>
        {
            var catalog = service.TryGetCatalog(source)!;
            Assert.Single(catalog);
            Assert.Equal("28.1", catalog[0].Version);
        });
    }

    [Fact]
    public async Task EnsurePackageAsync_OriginDigestMismatch_FallsBackToTheMirror()
    {
        var root = NewRoot();
        var expected = new byte[2048];
        Random.Shared.NextBytes(expected);
        var corrupted = (byte[])expected.Clone();
        corrupted[0] ^= 0xFF;
        var hosts = new ConcurrentBag<string>();
        var service = NewService(new HttpClient(new StubHandler(request =>
        {
            var host = request.RequestUri!.Host;
            hosts.Add(host);
            var payload = string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
                ? corrupted
                : expected;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        })), root, useMirrorList: true);
        var package = Package(RootPatchPackageSource.Magisk, "28.1", expected.Length, Sha256Hex(expected));

        var artifact = await service.EnsurePackageAsync(package, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(artifact.FromCache);
        Assert.NotEqual("github.com", artifact.Source.Host);
        Assert.Contains(hosts, host => !string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, await File.ReadAllBytesAsync(artifact.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WarmUpAsync_MirrorListDisabled_KeepsCatalogReadsOnTheOrigin()
    {
        var catalogReads = new ConcurrentBag<string>();
        var service = NewService(new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.Host != "api.github.com")
            {
                throw new HttpRequestException("mirror unavailable in this test");
            }

            var segments = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var owner = segments[^3];
            var repo = segments[^2];
            catalogReads.Add($"{owner}/{repo}");
            return Json(ReleasesJson(owner, repo, "28.1"));
        })), NewRoot(), useMirrorList: false);
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.WarmUpAsync(cancellationToken);

        Assert.All(RootPatchSources.All, source => Assert.True(service.CatalogLoaded(source)));
        Assert.Equal(RootPatchSources.All.Count, catalogReads.Count);
    }

    [Fact]
    public void DescribeLogDetail_CjkStopWithAppendedAsciiPeriod_TrimsTheDuplicate()
    {
        Assert.Equal("Unable to read data from the transport connection: 远程主机强迫关闭了一个现有的连接。",
            RootPatchPackageService.DescribeLogDetail(
                "Unable to read data from the transport connection: 远程主机强迫关闭了一个现有的连接。."));
        Assert.Equal("mirror unavailable.", RootPatchPackageService.DescribeLogDetail("mirror unavailable."));
        Assert.Equal("远程主机强迫关闭了一个现有的连接。",
            RootPatchPackageService.DescribeLogDetail("远程主机强迫关闭了一个现有的连接。"));
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

    private static string ReleasesJson(string owner, string repo, int count, int page)
    {
        var releases = Enumerable.Range(0, count).Select(index =>
        {
            var version = $"{page}.{index}";
            var asset = $"{repo}-v{version}.apk";
            return $$"""{"tag_name":"v{{version}}","assets":[{"name":"{{asset}}","size":1024,"browser_download_url":"https://github.com/{{owner}}/{{repo}}/releases/download/v{{version}}/{{asset}}"}]}""";
        });
        return "[" + string.Join(",", releases) + "]";
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

    private sealed class GateHandler(Task<HttpResponseMessage> response) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return await response.WaitAsync(cancellationToken);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
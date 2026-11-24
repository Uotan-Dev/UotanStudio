using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using UotanStudio.Pipeline.Delivery;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class Net11RecoveryToolTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task PreparePlatformsAsync_RecoveryOnlyZstWithLocalTool_StagesLinuxFastbootAndZstd()
    {
        byte[] fastboot = [4, 5, 6];
        var archive = PlatformToolsArchive(fastboot);
        var digest = Convert.ToHexString(SHA256.HashData(archive));
        using var http = new HttpClient(new ArchiveHandler(archive));
        var stage = Path.Combine(_temp.Path, "stage");
        var localZstd = _temp.Write("portable-zstd", "portable");
        var service = new DeliveryToolService(http, Path.Combine(_temp.Path, "cache"));
        (string Name, DeliveryToolPackage Package)[] platforms =
        [
            ("Linux", DeliveryToolService.Linux with { Url = "https://example.org/linux.zip", Sha256 = digest })
        ];
        var plan = new DeliveryPlan
        {
            Recovery = true,
            Files = [new DeliveryFile { FileName = "boot.zst" }],
            LocalZstd = new() { ["Linux"] = localZstd }
        };
        var progress = new SyncProgress<DeliveryProgress>();

        await service.PreparePlatformsAsync(platforms, stage, plan, progress, TestContext.Current.CancellationToken);

        Assert.Equal(fastboot, File.ReadAllBytes(Path.Combine(stage, "bin", "Linux", "fastboot")));
        Assert.Equal("portable", File.ReadAllText(Path.Combine(stage, "bin", "Linux", "zstd")));
        Assert.False(File.Exists(Path.Combine(stage, "bin", "Linux", "adb")));
        Assert.Contains(progress.Reports, report => report.Detail == "Linux");
    }

    [Fact]
    public async Task PrepareAsync_RecoveryOnlyZstAlreadyCancelled_ThrowsBeforeDownload()
    {
        var handler = new ArchiveHandler([]);
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new DeliveryToolService(http, Path.Combine(_temp.Path, "cache"));
        var plan = new DeliveryPlan
        {
            Recovery = true,
            Files = [new DeliveryFile { FileName = "boot.ZST" }]
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PrepareAsync(plan, Path.Combine(_temp.Path, "stage"), null, cancellation.Token));

        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "stage")));
    }

    private static byte[] PlatformToolsArchive(byte[] fastboot)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "platform-tools/fastboot", fastboot);
            AddEntry(zip, "platform-tools/adb", [1]);
        }
        return buffer.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(content);
    }

    private sealed class ArchiveHandler(byte[] archive) : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }
    }
}
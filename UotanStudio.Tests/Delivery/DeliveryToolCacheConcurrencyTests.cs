using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using UotanStudio.Delivery;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class DeliveryToolCacheConcurrencyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string Root => _temp.Path;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AcquireAsync_TwoPackagesConcurrently_BothPublish()
    {
        byte[] alpha = [1, 2, 3, 4];
        byte[] beta = [5, 6, 7, 8, 9];
        var handler = new ConcurrentHandler(url => url.EndsWith("alpha.zip", StringComparison.Ordinal) ? alpha : beta);
        using var http = new HttpClient(handler);
        var cache = Path.Combine(Root, "cache");
        var service = new DeliveryToolService(http, cache);
        var packages = new DeliveryToolPackage[]
        {
            new("alpha", "https://example.org/alpha.zip", Convert.ToHexString(SHA256.HashData(alpha)), ""),
            new("beta", "https://example.org/beta.zip", Convert.ToHexString(SHA256.HashData(beta)), ""),
        };

        var paths = await Task.WhenAll(packages.Select(package => service.AcquireAsync(package, null, Token)));

        Assert.Equal(2, handler.Requests);
        Assert.Equal(2, handler.PeakConcurrentRequests);
        Assert.Equal(alpha, File.ReadAllBytes(paths[0]));
        Assert.Equal(beta, File.ReadAllBytes(paths[1]));
        Assert.Empty(Directory.GetFiles(cache, "*.download-*"));
    }

    [Fact]
    public async Task PrepareAsync_TwoPlatforms_ExtractsPerDirectory()
    {
        byte[] linuxFastboot = [4, 5, 6];
        byte[] windowsFastboot = [7, 8, 9];
        var archive = PlatformToolsArchive(linuxFastboot, windowsFastboot);
        var digest = Convert.ToHexString(SHA256.HashData(archive));
        var handler = new ConcurrentHandler(_ => archive);
        using var http = new HttpClient(handler);
        var stage = Path.Combine(Root, "stage");
        var service = new DeliveryToolService(http, Path.Combine(Root, "cache"));
        (string Name, DeliveryToolPackage Package)[] platforms =
        [
            ("Windows", DeliveryToolService.Windows with { Url = "https://example.org/win.zip", Sha256 = digest }),
            ("Linux", DeliveryToolService.Linux with { Url = "https://example.org/linux.zip", Sha256 = digest }),
        ];
        var reported = new ConcurrentBag<string>();
        var progress = new InlineProgress<DeliveryProgress>(report =>
        {
            if (report.Stage == "Delivery_ProgressTools")
            {
                reported.Add(report.Detail ?? "");
            }
        });

        await service.PreparePlatformsAsync(platforms, stage, new DeliveryPlan(), progress, Token);

        Assert.Equal(linuxFastboot, File.ReadAllBytes(Path.Combine(stage, "bin", "Linux", "fastboot")));
        Assert.Equal(windowsFastboot, File.ReadAllBytes(Path.Combine(stage, "bin", "Windows", "fastboot.exe")));
        Assert.False(File.Exists(Path.Combine(stage, "bin", "Linux", "adb")));
        Assert.False(File.Exists(Path.Combine(stage, "bin", "Windows", "adb.exe")));
        Assert.Equal(2, handler.Requests);
        Assert.Equal(2, handler.PeakConcurrentRequests);
        Assert.Equal(2, reported.Count);
        Assert.Contains("Windows", reported);
        Assert.Contains("Linux", reported);
    }

    private byte[] PlatformToolsArchive(byte[] linuxFastboot, byte[] windowsFastboot)
    {
        var path = Path.Combine(Root, "platform-tools.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(zip, "platform-tools/fastboot", linuxFastboot);
            AddEntry(zip, "platform-tools/fastboot.exe", windowsFastboot);
            AddEntry(zip, "platform-tools/adb", [1]);
            AddEntry(zip, "platform-tools/adb.exe", [2]);
        }

        return File.ReadAllBytes(path);
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(content);
    }

    private sealed class ConcurrentHandler(Func<string, byte[]> payloadFor) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _secondArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _inFlight;
        private int _peak;
        private int _requests;

        internal int Requests => Volatile.Read(ref _requests);

        internal int PeakConcurrentRequests => Volatile.Read(ref _peak);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            RaisePeak(Interlocked.Increment(ref _inFlight));
            var arrival = Interlocked.Increment(ref _arrived);
            if (arrival >= 2)
            {
                _secondArrived.TrySetResult();
            }

            try
            {
                if (arrival == 1)
                {
                    await Task.WhenAny(_secondArrived.Task, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payloadFor(request.RequestUri!.ToString())),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void RaisePeak(int observed)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _peak);
                if (observed <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _peak, observed, current) != current);
        }
    }
}
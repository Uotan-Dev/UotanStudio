using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Services.Firmware;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class RemoteRomServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_UnknownRemoteFileKeepsDirectDownloadMode()
    {
        using var http = new HttpClient(new StaticHandler("plain rom"u8.ToArray()));
        var result = await new RemoteRomService(http).AnalyzeAsync("https://example.test/firmware.bin", TestContext.Current.CancellationToken);
        Assert.Equal(ContainerKind.None, result.Kind);
        Assert.False(result.SupportsPartitionSelection);
        Assert.Equal("firmware.bin", result.FileName);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task DownloadAsync_ReportsBoundedByteProgressAndWritesFile()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new StaticHandler("remote content"u8.ToArray()));
        var samples = new List<(long Processed, long Total)>();
        var destination = Path.Combine(temp.Path, "rom.bin");
        await new RemoteRomService(http).DownloadAsync(new Uri("https://example.test/rom.bin"), destination,
            new InlineProgress<(long Processed, long Total)>(samples.Add), TestContext.Current.CancellationToken);
        Assert.Equal("remote content", File.ReadAllText(destination));
        Assert.NotEmpty(samples);
        Assert.Equal(samples[^1].Total, samples[^1].Processed);
    }

    [Fact]
    public async Task DownloadAsync_UsesBoundedParallelRangesAndBrowserHeaders()
    {
        using var temp = new TempDirectory();
        var data = Enumerable.Range(0, 20 * 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
        using var handler = new RangeHandler(data);
        using var http = new HttpClient(handler);
        var destination = Path.Combine(temp.Path, "rom.bin");

        await new RemoteRomService(http).DownloadAsync(new Uri("https://example.test/rom.bin"), destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(data, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.True(handler.MaximumConcurrentRequests > 1);
        Assert.True(handler.RangedRequestCount > 1);
        Assert.Contains(handler.UserAgents, userAgent => userAgent.Contains("Mozilla/5.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergeIncrementalPayloadAsync_TwoVerifiedPartitions_PublishesTogether()
    {
        using var temp = new TempDirectory();
        var systemSource = CreateImage(0x10);
        var vendorSource = CreateImage(0x40);
        var systemTarget = ChangeBlocks(systemSource, 1, 5);
        var vendorTarget = ChangeBlocks(vendorSource, 2, 7);
        var systemSourcePath = temp.WriteBytes("base/system.img", systemSource);
        var vendorSourcePath = temp.WriteBytes("base/vendor.img", vendorSource);
        var payload = BuildIncrementalPayload(temp, ("system", systemSourcePath, systemTarget),
            ("vendor", vendorSourcePath, vendorTarget));
        var output = Path.Combine(temp.Path, "project");
        temp.WriteBytes("project/system.img", systemSource);
        temp.WriteBytes("project/vendor.img", vendorSource);
        using var http = new HttpClient(new RangeHandler(payload));

        var result = await new RemoteRomService(http).MergeIncrementalPayloadAsync(
            new Uri("https://example.test/payload.bin"),
            ["system", "vendor"],
            new Dictionary<string, string>
            {
                ["system"] = systemSourcePath,
                ["vendor"] = vendorSourcePath
            },
            output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["system", "vendor"], result.PartitionNames);
        Assert.Equal(systemTarget, await File.ReadAllBytesAsync(Path.Combine(output, "system.img"), TestContext.Current.CancellationToken));
        Assert.Equal(vendorTarget, await File.ReadAllBytesAsync(Path.Combine(output, "vendor.img"), TestContext.Current.CancellationToken));
        Assert.All(result.OutputPaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task MergeIncrementalPayloadAsync_LaterPartitionHasWrongBase_PreservesEveryDestination()
    {
        using var temp = new TempDirectory();
        var systemSource = CreateImage(0x10);
        var vendorSource = CreateImage(0x40);
        var systemSourcePath = temp.WriteBytes("base/system.img", systemSource);
        var wrongVendorSourcePath = temp.WriteBytes("base/vendor-wrong.img", CreateImage(0x70));
        var expectedVendorSourcePath = temp.WriteBytes("base/vendor-expected.img", vendorSource);
        var payload = BuildIncrementalPayload(temp, ("system", systemSourcePath, ChangeBlocks(systemSource, 1)),
            ("vendor", expectedVendorSourcePath, ChangeBlocks(vendorSource, 2)));
        var oldSystem = "existing system"u8.ToArray();
        var oldVendor = "existing vendor"u8.ToArray();
        var output = Path.Combine(temp.Path, "project");
        temp.WriteBytes("project/system.img", oldSystem);
        temp.WriteBytes("project/vendor.img", oldVendor);
        using var http = new HttpClient(new RangeHandler(payload));

        await Assert.ThrowsAsync<PartitionExtractionException>(() => new RemoteRomService(http).MergeIncrementalPayloadAsync(
            new Uri("https://example.test/payload.bin"),
            ["system", "vendor"],
            new Dictionary<string, string>
            {
                ["system"] = systemSourcePath,
                ["vendor"] = wrongVendorSourcePath
            },
            output,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(oldSystem, await File.ReadAllBytesAsync(Path.Combine(output, "system.img"), TestContext.Current.CancellationToken));
        Assert.Equal(oldVendor, await File.ReadAllBytesAsync(Path.Combine(output, "vendor.img"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergeZipIncrementalPayloadAsync_StoredPayload_PublishesVerifiedPartition()
    {
        using var temp = new TempDirectory();
        var source = CreateImage(0x20);
        var target = ChangeBlocks(source, 3, 8);
        var sourcePath = temp.WriteBytes("base/system.img", source);
        var payload = BuildIncrementalPayload(temp, ("system", sourcePath, target));
        var zip = WrapStoredPayload(payload);
        var output = Path.Combine(temp.Path, "project");
        temp.WriteBytes("project/system.img", source);
        using var http = new HttpClient(new RangeHandler(zip));

        await new RemoteRomService(http).MergeZipIncrementalPayloadAsync(
            new Uri("https://example.test/ota.zip"),
            ["system"],
            new Dictionary<string, string> { ["system"] = sourcePath },
            output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(target, await File.ReadAllBytesAsync(Path.Combine(output, "system.img"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergeIncrementalPayloadAsync_PreCancelled_PreservesDestination()
    {
        using var temp = new TempDirectory();
        var source = CreateImage(0x20);
        var sourcePath = temp.WriteBytes("base/system.img", source);
        var payload = BuildIncrementalPayload(temp, ("system", sourcePath, ChangeBlocks(source, 1)));
        var existing = "existing"u8.ToArray();
        var output = Path.Combine(temp.Path, "project");
        temp.WriteBytes("project/system.img", existing);
        using var http = new HttpClient(new RangeHandler(payload));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemoteRomService(http).MergeIncrementalPayloadAsync(
            new Uri("https://example.test/payload.bin"),
            ["system"],
            new Dictionary<string, string> { ["system"] = sourcePath },
            output,
            cancellationToken: cancellation.Token));

        Assert.Equal(existing, await File.ReadAllBytesAsync(Path.Combine(output, "system.img"), TestContext.Current.CancellationToken));
    }

    private static byte[] BuildIncrementalPayload(TempDirectory temp, params ReadOnlySpan<(string Name, string Source, byte[] Target)> partitions)
    {
        var payloadPath = Path.Combine(temp.Path, $"payload-{Guid.NewGuid():N}.bin");
        using (var builder = new PayloadBuilder().WithBlockSize(16))
        {
            foreach (var (name, source, target) in partitions)
            {
                var targetPath = temp.WriteBytes($"targets/{name}-{Guid.NewGuid():N}.img", target);
                builder.AddPartitionIncremental(name, targetPath, source);
            }
            builder.Build(payloadPath);
        }
        return File.ReadAllBytes(payloadPath);
    }

    private static byte[] CreateImage(byte seed)
        => Enumerable.Range(0, 16 * 12).Select(index => (byte)(seed + index)).ToArray();

    private static byte[] ChangeBlocks(byte[] source, params ReadOnlySpan<int> blocks)
    {
        var target = source.ToArray();
        foreach (var block in blocks)
        {
            target.AsSpan(block * 16, 16).Fill((byte)(0xA0 + block));
        }
        return target;
    }

    private static byte[] WrapStoredPayload(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("payload.bin", CompressionLevel.NoCompression);
            using var content = entry.Open();
            content.Write(payload);
        }
        return output.ToArray();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class StaticHandler(byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(request.Headers.Range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data)
            };
            response.Content.Headers.ContentLength = data.Length;
            response.Content.Headers.ContentRange = request.Headers.Range is null
                ? null
                : new ContentRangeHeaderValue(0, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class RangeHandler(byte[] data) : HttpMessageHandler
    {
        private int _active;
        private int _maximumConcurrent;
        private int _rangedRequestCount;
        public int MaximumConcurrentRequests => _maximumConcurrent;
        public int RangedRequestCount => _rangedRequestCount;
        public ConcurrentBag<string> UserAgents { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            var active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumConcurrent);
                if (active <= observed) break;
            } while (Interlocked.CompareExchange(ref _maximumConcurrent, active, observed) != observed);

            try
            {
                await Task.Delay(20, cancellationToken);
                if (request.Headers.Range is not { } range)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

                Interlocked.Increment(ref _rangedRequestCount);
                var from = checked((int)(range.Ranges.First().From ?? 0));
                var to = checked((int)(range.Ranges.First().To ?? (data.Length - 1)));
                var bytes = data.AsSpan(from, to - from + 1).ToArray();
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(bytes)
                };
                response.Content.Headers.ContentLength = bytes.Length;
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
                return response;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
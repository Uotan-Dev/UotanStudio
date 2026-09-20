using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using UotanStudio.Firmware.SharedKernel.Containers;
using UotanStudio.Services.Firmware;
using UotanStudio.Tests.Comm.TestDoubles;
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
        // Report inline instead of via Progress<T>, whose thread-pool callbacks can lag the await.
        // 使用同步内联报告而非 Progress<T>，后者在线程池回调可能滞后于 await。
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
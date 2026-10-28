using System.Collections.Concurrent;
using System.Net;
using UotanStudio.Net.Http;
using Xunit;

namespace UotanStudio.Tests.Net;

public sealed class HttpDownloadSourceOrderTests
{
    [Fact]
    public async Task DownloadAsync_ExplicitSources_SkipsTheMirrorSpeedProbe()
    {
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        var origin = new Uri("https://github.com/topjohnwu/Magisk/releases/download/v28.1/Magisk-v28.1.apk");
        var hosts = new ConcurrentBag<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var directory = Path.Combine(Path.GetTempPath(), $"uotan-download-{Guid.NewGuid():N}");
        var destination = Path.Combine(directory, "Magisk-v28.1.apk");
        Directory.CreateDirectory(directory);
        try
        {
            var used = await HttpDownload.DownloadAsync(client, origin, destination, sources: [origin],
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(origin, used);
            Assert.All(hosts, host => Assert.Equal("github.com", host, ignoreCase: true));
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
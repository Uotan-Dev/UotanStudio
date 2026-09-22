using System.Net;
using System.Net.Http.Headers;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class NetworkNodeTests
{
    [Fact]
    public async Task Download_CustomUserAgent_StreamsToPathAndReportsSource()
    {
        var handler = new DownloadHandler("node payload"u8.ToArray());
        using var client = new HttpClient(handler);
        var node = NetworkNodeViewModel.CreateDownloadNode(client);
        var output = Path.Combine(Path.GetTempPath(), $"uotanstudio-network-{Guid.NewGuid():N}.bin");
        try
        {
            SetInput(node, "URL", "https://downloads.example.test/firmware.bin");
            SetInput(node, "Output Path", output);
            SetInput(node, "User Agent", "UotanStudio-Node-Test/2.0");

            await node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken));

            Assert.Equal("node payload", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
            Assert.All(handler.UserAgents, value => Assert.Equal("UotanStudio-Node-Test/2.0", value));
            Assert.Equal(Path.GetFullPath(output), GetOutput(node, "Path"));
            Assert.Equal("https://downloads.example.test/firmware.bin", GetOutput(node, "Source URL"));
            Assert.Equal(12d, GetOutput(node, "Bytes"));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Download_RequestFails_PreservesExistingDestination()
    {
        using var client = new HttpClient(new DownloadHandler("replacement"u8.ToArray(), HttpStatusCode.BadGateway));
        var node = NetworkNodeViewModel.CreateDownloadNode(client);
        var output = Path.Combine(Path.GetTempPath(), $"uotanstudio-network-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(output, "existing", TestContext.Current.CancellationToken);
        try
        {
            SetInput(node, "URL", "https://downloads.example.test/firmware.bin");
            SetInput(node, "Output Path", output);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken)));

            Assert.Equal("existing", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".part-*"));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task GitHubDownload_NonGitHubUrl_RejectsBeforeNetworkAccess()
    {
        var handler = new DownloadHandler([]);
        using var client = new HttpClient(handler);
        var node = NetworkNodeViewModel.CreateGitHubDownloadNode(client);
        SetInput(node, "URL", "https://example.test/firmware.bin");
        SetInput(node, "Output Path", Path.Combine(Path.GetTempPath(), "unused.bin"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken)));

        Assert.Contains("only accepts GitHub", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.UserAgents);
    }

    [Fact]
    public async Task Download_UserAgentContainsNewline_RejectsWithoutPublishing()
    {
        var handler = new DownloadHandler("payload"u8.ToArray());
        using var client = new HttpClient(handler);
        var node = NetworkNodeViewModel.CreateDownloadNode(client);
        var output = Path.Combine(Path.GetTempPath(), $"uotanstudio-network-{Guid.NewGuid():N}.bin");
        SetInput(node, "URL", "https://downloads.example.test/firmware.bin");
        SetInput(node, "Output Path", output);
        SetInput(node, "User Agent", "valid\r\nInjected: value");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken)));

        Assert.False(File.Exists(output));
        Assert.Empty(handler.UserAgents);
    }

    private static void SetInput(FlowNodeViewModel node, string title, object value)
        => Assert.Single(node.Input, port => port.Title == title).Data = value;

    private static object? GetOutput(FlowNodeViewModel node, string title)
        => Assert.Single(node.Output, port => port.Title == title).Data;

    private sealed class DownloadHandler(byte[] payload, HttpStatusCode statusCode = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public List<string?> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            var response = new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentLength = payload.Length;
            if (request.Headers.Range is RangeHeaderValue)
            {
                response.Headers.AcceptRanges.Add("bytes");
            }
            return Task.FromResult(response);
        }
    }

    private sealed class TestExecutionContext(CancellationToken cancellationToken) : INodeExecutionContext, IUnifiedLog
    {
        public IUnifiedLog Log => this;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public void ReportProgress(double progress) { }
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}
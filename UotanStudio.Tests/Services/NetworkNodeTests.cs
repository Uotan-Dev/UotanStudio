using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using UotanStudio.Firmware.Infrastructure.Downloads;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class NetworkNodeTests
{
    [Fact]
    public async Task Download_CustomRequestHeaders_StreamsToPathAndReportsSource()
    {
        var handler = new DownloadHandler("node payload"u8.ToArray());
        using var client = new HttpClient(handler);
        var node = NetworkNodeViewModel.CreateDownloadNode(client);
        var output = Path.Combine(Path.GetTempPath(), $"uotanstudio-network-{Guid.NewGuid():N}.bin");
        try
        {
            SetInput(node, "URL", "https://downloads.example.test/firmware.bin");
            SetInput(node, "Output Path", output);
            SetInput(node, "Request Headers", "User-Agent: UotanStudio-Node-Test/2.0\nX-Download-Mode: test");

            await node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken));

            Assert.Equal("node payload", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
            Assert.All(handler.UserAgents, value => Assert.Equal("UotanStudio-Node-Test/2.0", value));
            Assert.All(handler.DownloadModes, value => Assert.Equal("test", value));
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
    public void Download_RequestHeaders_DefaultToBrowserUserAgent()
    {
        var node = NetworkNodeViewModel.CreateDownloadNode();
        var headers = Assert.Single(node.Input, port => port.PortId == "user-agent");

        Assert.Equal("Request Headers", headers.Title);
        Assert.Equal("User-Agent: " + HttpDownload.DefaultBrowserUserAgent, headers.InitialValue);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Download_ExpectedSha256_OnlyPublishesMatchingContent(bool matches)
    {
        var payload = "verified payload"u8.ToArray();
        using var client = new HttpClient(new DownloadHandler(payload));
        var node = NetworkNodeViewModel.CreateDownloadNode(client);
        var output = Path.Combine(Path.GetTempPath(), $"uotanstudio-network-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(output, "existing", TestContext.Current.CancellationToken);
        try
        {
            SetInput(node, "URL", "https://downloads.example.test/firmware.bin");
            SetInput(node, "Output Path", output);
            var hash = SHA256.HashData(matches ? payload : "wrong"u8.ToArray());
            SetInput(node, "Expected SHA256", Convert.ToHexString(hash));

            if (matches)
            {
                await node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken));
                Assert.Equal(payload, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken)));
                Assert.Equal("existing", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
            }
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".verify-*"));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Download_InvalidTimeout_RejectsBeforeNetworkAccess()
    {
        var handler = new DownloadHandler("payload"u8.ToArray());
        using var client = new HttpClient(handler);
        var node = NetworkNodeViewModel.CreateDownloadNode(client);
        SetInput(node, "URL", "https://downloads.example.test/firmware.bin");
        SetInput(node, "Output Path", Path.Combine(Path.GetTempPath(), "unused.bin"));
        SetInput(node, "Timeout Seconds", -1d);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken)));

        Assert.Empty(handler.UserAgents);
    }

    [Fact]
    public async Task Download_Timeout_FailsWorkflowAndPreservesExistingDestination()
    {
        using var client = new HttpClient(new WaitingHandler());
        var node = NetworkNodeViewModel.CreateDownloadNode(client);
        var output = Path.Combine(Path.GetTempPath(), $"uotanstudio-timeout-{Guid.NewGuid():N}.bin");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(output, "existing", token);
        try
        {
            node.Input.Single(port => port.Title == "URL").InitialValue = "https://downloads.example.test/firmware.bin";
            node.Input.Single(port => port.Title == "Output Path").InitialValue = output;
            node.Input.Single(port => port.Title == "Timeout Seconds").InitialValue = 1d;

            var result = await new NodeExecutionEngine(new TestExecutionContext(token)).ExecuteAsync(
                [node], [], token);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Equal(NodeExecutionStatus.Failed, node.ExecutionStatus);
            Assert.Contains(LanguageService.Instance.GetString("Node_Network_Timeout"), node.ExecutionError);
            Assert.Equal("existing", await File.ReadAllTextAsync(output, token));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Download_GitHubMirrorSource_NonGitHubUrl_RejectsBeforeNetworkAccess()
    {
        var handler = new DownloadHandler([]);
        using var client = new HttpClient(handler);
        var node = NetworkNodeViewModel.CreateDownloadNode(client);
        SetInput(node, "Source", "GitHub Mirror");
        SetInput(node, "URL", "https://example.test/firmware.bin");
        SetInput(node, "Output Path", Path.Combine(Path.GetTempPath(), "unused.bin"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken)));

        Assert.Contains("only accepts GitHub", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.UserAgents);
    }

    [Fact]
    public async Task Download_MalformedRequestHeader_RejectsWithoutPublishing()
    {
        var handler = new DownloadHandler("payload"u8.ToArray());
        using var client = new HttpClient(handler);
        var node = NetworkNodeViewModel.CreateDownloadNode(client);
        var output = Path.Combine(Path.GetTempPath(), $"uotanstudio-network-{Guid.NewGuid():N}.bin");
        SetInput(node, "URL", "https://downloads.example.test/firmware.bin");
        SetInput(node, "Output Path", output);
        SetInput(node, "Request Headers", "Malformed header");

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
        public List<string?> DownloadModes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            DownloadModes.Add(request.Headers.TryGetValues("X-Download-Mode", out var values) ? Assert.Single(values) : null);
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

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The request should have been cancelled.");
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
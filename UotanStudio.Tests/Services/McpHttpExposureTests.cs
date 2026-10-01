using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Http;
using UotanStudio.Services.Mcp;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// End-to-end coverage for the desktop MCP HTTP exposure: a real Streamable HTTP MCP client connects
/// to the loopback listener, lists and calls tools, lists and reads resources, and the settings-driven
/// service starts and releases the port.
/// <para>桌面 MCP HTTP 暴露的端到端覆盖：真实 Streamable HTTP MCP 客户端连接回环监听并通过它列举与调用工具、
/// 列举与读取资源；由设置驱动的服务负责启动与释放端口。</para>
/// </summary>
public sealed class McpHttpExposureTests
{
    [Fact]
    public async Task HttpHost_StreamableHttpClientRoundTrip_ListsAndCallsTools()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var file = Path.Combine(Path.GetTempPath(), "uotan-mcp-update-" + Guid.NewGuid().ToString("N") + ".app");
        await File.WriteAllBytesAsync(file, [0x55, 0xAA, 0x5A, 0xA5], cancellationToken);

        try
        {
            await using var host = CreateHost();
            host.Start(0);

            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{host.Port}/"),
                TransportMode = HttpTransportMode.StreamableHttp
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            Assert.Contains(tools, tool => tool.Name == "list_formats");
            Assert.Contains(tools, tool => tool.Name == "detect_format");

            var formats = await client.CallToolAsync("list_formats", null, null, null, cancellationToken);
            var formatsText = TextOf(formats);
            Assert.True(formats.IsError is not true, formatsText);
            Assert.Contains("format handler(s) registered", formatsText, StringComparison.Ordinal);

            var detected = await client.CallToolAsync(
                "detect_format",
                new Dictionary<string, object?> { ["path"] = file },
                null,
                null,
                cancellationToken);
            var detectedText = TextOf(detected);
            Assert.True(detected.IsError is not true, detectedText);
            Assert.Contains("\"success\":true", detectedText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task HttpHost_NonLoopbackOrigin_ReturnsForbidden()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = CreateHost();
        host.Start(0);

        const string body = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"1.0"}}}""";
        var head = $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\nOrigin: ";

        var foreign = await SendRawAsync(host.Port, head + "http://evil.example\r\n\r\n" + body, cancellationToken);
        Assert.StartsWith("HTTP/1.1 403", foreign, StringComparison.Ordinal);

        var loopback = await SendRawAsync(host.Port, head + $"http://127.0.0.1:{host.Port}\r\n\r\n" + body, cancellationToken);
        Assert.StartsWith("HTTP/1.1 200", loopback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExposureService_Apply_StartsAndReleasesLoopbackPort()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var port = ReserveLoopbackPort();
        var service = McpExposureService.Instance;

        try
        {
            service.Apply(true, port);
            await WaitForAsync(() => service.IsRunning && service.Port == port, cancellationToken);
            Assert.Null(service.LastError);

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            }

            service.Apply(false, port);
            await WaitForAsync(() => !service.IsRunning, cancellationToken);

            using var rejected = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(async () => await rejected.ConnectAsync(IPAddress.Loopback, port, cancellationToken));
        }
        finally
        {
            service.Stop();
        }
    }

    [Fact]
    public async Task HttpHost_SandboxedContainerResource_ListsEntriesWithoutExtraction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "uotan-mcp-http-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "rom.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(
            zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var entryStream = archive.CreateEntry("images/boot.img").Open();
            entryStream.Write([0x55, 0xAA, 0x5A, 0xA5]);
        }

        try
        {
            await using var host = CreateHost(new PathSandbox(root));
            host.Start(0);

            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{host.Port}/"),
                TransportMode = HttpTransportMode.StreamableHttp
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

            var resources = await client.ListResourcesAsync(cancellationToken: cancellationToken);
            Assert.Contains(resources, resource => resource.Name == "sandbox-overview");

            var templates = await client.ListResourceTemplatesAsync(cancellationToken: cancellationToken);
            Assert.Contains(templates, template => template.Name == "container-entries");
            Assert.Contains(templates, template => template.Name == "filesystem-entries");

            var read = await client.ReadResourceAsync(
                "uotan://container/rom.zip/entries", null, cancellationToken);
            var text = string.Concat(read.Contents.OfType<TextResourceContents>().Select(content => content.Text));
            Assert.NotEmpty(text);
            Assert.Contains("images/boot.img", text, StringComparison.Ordinal);
            Assert.DoesNotContain(root, text, StringComparison.Ordinal);

            var escaped = await Record.ExceptionAsync(async () =>
                await client.ReadResourceAsync(
                    "uotan://container/%2e%2e%2f%2e%2e%2fwindows.ini/entries", null, cancellationToken));
            Assert.NotNull(escaped);
            Assert.Contains("sandbox", escaped!.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static McpHttpServerHost CreateHost()
        => CreateHost(PathSandbox.Unrestricted);

    private static McpHttpServerHost CreateHost(PathSandbox sandbox)
    {
        var serverHost = new McpServerHost(sandbox, allowFlash: false);
        return new McpHttpServerHost(serverHost.BuildTools(), serverHost.BuildResources());
    }

    private static string TextOf(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> SendRawAsync(int port, string request, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(25, cancellationToken);
        }

        Assert.True(condition(), "The MCP exposure state did not change in time.");
    }
}
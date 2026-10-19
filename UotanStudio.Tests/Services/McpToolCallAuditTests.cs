using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Http;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Coverage for the call-tool audit filter: the shared server options carry exactly one filter when a sink
/// is supplied, and a real Streamable HTTP round trip records the tool name, the arguments as the client
/// sent them, the summary of a successful envelope and the cause of a refused call. A sink that throws must
/// not change what the client receives.
/// <para>工具调用审计过滤器的覆盖：给出接收端时共用的服务器选项恰好带一个过滤器；真实 Streamable HTTP 往返记录工具名、
/// 客户端原样传入的参数、成功信封的摘要与被拒绝调用的原因。抛出异常的接收端不得改变客户端收到的结果。</para>
/// </summary>
[Collection("MCP tool generation")]
public sealed class McpToolCallAuditTests
{
    [Fact]
    public void CreateOptions_WithoutAudit_LeavesCallToolPipelineUnfiltered()
    {
        var options = McpServerHost.CreateOptions([], null, audit: null);

        Assert.Empty(options.Filters.Request.CallToolFilters);
    }

    [Fact]
    public void CreateOptions_WithAudit_AppendsExactlyOneCallToolFilter()
    {
        var options = McpServerHost.CreateOptions([], null, new CollectingAudit());

        Assert.Single(options.Filters.Request.CallToolFilters);
    }

    [Fact]
    public async Task HttpHost_AuditedToolCall_RecordsNameArgumentsAndSummary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "uotan-mcp-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "probe.app");
        await File.WriteAllBytesAsync(file, [0x55, 0xAA, 0x5A, 0xA5], cancellationToken);

        var audit = new CollectingAudit();
        try
        {
            var serverHost = new McpServerHost(new PathSandbox(root), allowFlash: false);
            await using var host = new McpHttpServerHost(
                serverHost.BuildTools(), serverHost.BuildResources(),
                loggerFactory: null, audit);
            host.Start(0);
            await using var client = await CreateClientAsync(host.Port, cancellationToken);

            var detected = await client.CallToolAsync(
                "detect_format",
                new Dictionary<string, object?> { ["path"] = file },
                null,
                null,
                cancellationToken);
            Assert.True(detected.IsError is not true, TextOf(detected));

            var record = Assert.Single(audit.Records);
            Assert.Equal("detect_format", record.ToolName);
            Assert.True(record.Success);
            Assert.False(string.IsNullOrWhiteSpace(record.Summary));
            Assert.True(record.Duration >= TimeSpan.Zero);
            using var arguments = JsonDocument.Parse(record.Arguments);
            Assert.Equal(file, arguments.RootElement.GetProperty("path").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HttpHost_AuditedToolCallOutsideSandbox_RecordsFailureWithCause()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "uotan-mcp-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var audit = new CollectingAudit();
        try
        {
            var serverHost = new McpServerHost(new PathSandbox(root), allowFlash: false);
            await using var host = new McpHttpServerHost(
                serverHost.BuildTools(), serverHost.BuildResources(),
                loggerFactory: null, audit);
            host.Start(0);
            await using var client = await CreateClientAsync(host.Port, cancellationToken);

            var outside = Path.Combine(Path.GetTempPath(), "uotan-mcp-audit-elsewhere.img");
            var detected = await client.CallToolAsync(
                "detect_format",
                new Dictionary<string, object?> { ["path"] = outside },
                null,
                null,
                cancellationToken);
            Assert.True(detected.IsError is true, TextOf(detected));

            var record = Assert.Single(audit.Records);
            Assert.False(record.Success);
            Assert.Contains(nameof(UnauthorizedAccessException), record.Summary, StringComparison.Ordinal);
            Assert.Contains("outside the MCP sandbox root", record.Summary, StringComparison.Ordinal);
            using var arguments = JsonDocument.Parse(record.Arguments);
            Assert.Equal(outside, arguments.RootElement.GetProperty("path").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HttpHost_AuditedToolCallWithoutArguments_RecordsEmptyObject()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var audit = new CollectingAudit();
        var serverHost = new McpServerHost(PathSandbox.Unrestricted, allowFlash: false);
        await using var host = new McpHttpServerHost(
            serverHost.BuildTools(), serverHost.BuildResources(),
            loggerFactory: null, audit);
        host.Start(0);
        await using var client = await CreateClientAsync(host.Port, cancellationToken);

        var formats = await client.CallToolAsync("list_formats", null, null, null, cancellationToken);

        Assert.True(formats.IsError is not true, TextOf(formats));
        var record = Assert.Single(audit.Records);
        Assert.Equal("list_formats", record.ToolName);
        Assert.Equal("{}", record.Arguments);
        Assert.True(record.Success);
    }

    [Fact]
    public async Task HttpHost_AuditSinkThrows_ToolResultStillReachesClient()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverHost = new McpServerHost(PathSandbox.Unrestricted, allowFlash: false);
        await using var host = new McpHttpServerHost(
            serverHost.BuildTools(), serverHost.BuildResources(),
            loggerFactory: null, new ThrowingAudit());
        host.Start(0);
        await using var client = await CreateClientAsync(host.Port, cancellationToken);

        var formats = await client.CallToolAsync("list_formats", null, null, null, cancellationToken);

        Assert.True(formats.IsError is not true, TextOf(formats));
        Assert.Contains("format handler(s) registered", TextOf(formats), StringComparison.Ordinal);
    }

    private static async Task<McpClient> CreateClientAsync(int port, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/"),
            TransportMode = HttpTransportMode.StreamableHttp
        });
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    private static string TextOf(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private sealed class CollectingAudit : IMcpToolCallAudit
    {
        private readonly Lock _sync = new();
        private readonly List<McpToolCallRecord> _records = [];

        internal IReadOnlyList<McpToolCallRecord> Records
        {
            get
            {
                lock (_sync)
                {
                    return [.. _records];
                }
            }
        }

        public void RecordToolCall(McpToolCallRecord record)
        {
            lock (_sync)
            {
                _records.Add(record);
            }
        }
    }

    private sealed class ThrowingAudit : IMcpToolCallAudit
    {
        public void RecordToolCall(McpToolCallRecord record)
            => throw new InvalidOperationException("The audit sink failed on purpose.");
    }
}
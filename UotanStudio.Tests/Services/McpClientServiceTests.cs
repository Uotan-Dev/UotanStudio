using UotanStudio.Mcp;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Tests for the MCP client service config resolution: name lookup, disabled servers, and unknown
/// servers fail fast with clear hints before any transport is opened. Real stdio/HTTP round-trips
/// need an external MCP server process and are out of scope here.
/// <para>MCP 客户端服务配置解析测试：名称查找、禁用服务器与未知服务器在任何传输建立之前
/// 即快速失败并给出明确提示。真实 stdio/HTTP 回环需要外部 MCP 服务器进程，不在本测试范围。</para>
/// </summary>
public sealed class McpClientServiceTests
{
    [Fact]
    public async Task ListToolsAsync_UnknownServer_ThrowsWithHint()
    {
        await McpClientService.Instance.SetServersAsync([]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpClientService.Instance.ListToolsAsync("missing", TestContext.Current.CancellationToken));
        Assert.Contains("'missing'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListToolsAsync_DisabledServer_ThrowsWithHint()
    {
        await McpClientService.Instance.SetServersAsync(
        [
            new McpClientServerConfig { Name = "disabled", Enabled = false, Transport = McpTransportKind.Stdio, Command = "noop" },
        ]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpClientService.Instance.ListToolsAsync("disabled", TestContext.Current.CancellationToken));
        Assert.Contains("disabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_UnknownServer_ThrowsWithHint()
    {
        await McpClientService.Instance.SetServersAsync([]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpClientService.Instance.CallToolAsync("missing", "tool", null, TestContext.Current.CancellationToken));
        Assert.Contains("'missing'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetServersAsync_ReplacesPreviousConfigs()
    {
        await McpClientService.Instance.SetServersAsync(
        [
            new McpClientServerConfig { Name = "old" },
        ]);
        await McpClientService.Instance.SetServersAsync(
        [
            new McpClientServerConfig { Name = "new", Enabled = false },
        ]);

        var oldEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpClientService.Instance.ListToolsAsync("old", TestContext.Current.CancellationToken));
        var newEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpClientService.Instance.ListToolsAsync("new", TestContext.Current.CancellationToken));
        Assert.Contains("'old'", oldEx.Message, StringComparison.Ordinal);
        Assert.Contains("disabled", newEx.Message, StringComparison.Ordinal);

        // 恢复空配置，避免污染其他测试共享的单例。
        // Restore the empty config to keep the shared singleton clean for other tests.
        await McpClientService.Instance.SetServersAsync([]);
    }
}

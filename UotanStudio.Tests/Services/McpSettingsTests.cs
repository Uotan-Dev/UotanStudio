using System.Text.Json;
using UotanStudio.Mcp;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Verifies the MCP settings surface persisted by the settings page.</summary>
/// <para>验证设置页持久化的 MCP 设置面。</para>
public sealed class McpSettingsTests
{
    [Fact]
    public void Settings_LoadCopyAndRoundTripMcpServerList()
    {
        const string json = """
            {"mcpExposeEnabled":true,"mcpExposePort":19001,
             "mcpServers":[{"name":"local","transport":"Stdio","command":"npx","arguments":"-y server","enabled":false},
                           {"name":"remote","transport":"Http","url":"http://localhost:8080/mcp"}]}
            """;

        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;

        Assert.True(settings.McpExposeEnabled);
        Assert.Equal(19001, settings.McpExposePort);
        Assert.Equal(2, settings.McpServers.Count);
        Assert.Equal(McpTransportKind.Stdio, settings.McpServers[0].Transport);
        Assert.False(settings.McpServers[0].Enabled);
        Assert.Equal("http://localhost:8080/mcp", settings.McpServers[1].Url);
        Assert.True(settings.McpServers[1].Enabled);

        var copy = settings.Copy();
        copy.McpServers.RemoveAt(0);
        Assert.Equal(2, settings.McpServers.Count);

        var saved = JsonSerializer.Serialize(copy, AppSettingsJsonContext.Default.AppSettings);
        var reloaded = JsonSerializer.Deserialize(saved, AppSettingsJsonContext.Default.AppSettings)!;
        var server = Assert.Single(reloaded.McpServers);
        Assert.Equal("remote", server.Name);
        Assert.Equal(McpTransportKind.Http, server.Transport);
    }

    [Fact]
    public void Settings_DefaultExposePortAndEmptyServerList()
    {
        var settings = JsonSerializer.Deserialize("{}", AppSettingsJsonContext.Default.AppSettings)!;

        Assert.False(settings.McpExposeEnabled);
        Assert.Equal(18321, settings.McpExposePort);
        Assert.Empty(settings.McpServers);
    }
}
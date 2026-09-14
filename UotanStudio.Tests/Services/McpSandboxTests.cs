using System.Text.Json;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Verifies the MCP path sandbox containment and flash-tool gating.
/// <para>验证 MCP 路径沙箱包含关系与刷写工具门控。</para>
/// </summary>
public class McpSandboxTests : IDisposable
{
    private readonly string _root;

    public McpSandboxTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotanstudio-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Resolve_InsideRoot_ReturnsFullPath()
    {
        var sandbox = new PathSandbox(_root);
        var resolved = sandbox.Resolve("file.bin");
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_TraversalEscape_Throws()
    {
        var sandbox = new PathSandbox(_root);
        Assert.Throws<UnauthorizedAccessException>(() => sandbox.Resolve(@"..\outside.bin"));
    }

    [Fact]
    public void Resolve_AbsoluteOutside_Throws()
    {
        var sandbox = new PathSandbox(_root);
        Assert.Throws<UnauthorizedAccessException>(() => sandbox.Resolve(Path.GetTempPath()));
    }

    [Fact]
    public void Resolve_SiblingRootPrefix_Throws()
    {
        var sandbox = new PathSandbox(_root);
        // "root2" shares the "root" prefix but is a different directory.
        // "root2" 与 "root" 共享前缀但属于不同目录。
        var sibling = _root + "2";
        Assert.Throws<UnauthorizedAccessException>(() => sandbox.Resolve(sibling));
    }

    [Fact]
    public void Resolve_MissingRequiredFile_Throws()
    {
        var sandbox = new PathSandbox(_root);
        Assert.Throws<FileNotFoundException>(() => sandbox.Resolve("missing.bin", mustExist: true));
    }

    [Fact]
    public void BuildTools_DefaultConfig_ExcludesFlashTools()
    {
        var config = new McpServerConfig { RootDirectory = _root, AllowFlash = false };
        var tools = new McpServerHost(config).BuildTools();
        var names = tools.Select(t => t.ProtocolTool.Name).ToList();
        Assert.Contains("list_formats", names);
        Assert.Contains("compress_file", names);
        Assert.Contains("delivery_validate", names);
        Assert.DoesNotContain("fastboot_flash", names);
        Assert.DoesNotContain("fastboot_erase", names);
        Assert.DoesNotContain("adb_reboot", names);
    }

    [Fact]
    public void BuildTools_AllowFlashConfig_IncludesFlashTools()
    {
        var config = new McpServerConfig { RootDirectory = _root, AllowFlash = true };
        var tools = new McpServerHost(config).BuildTools();
        var names = tools.Select(t => t.ProtocolTool.Name).ToList();
        Assert.Contains("fastboot_flash", names);
        Assert.Contains("fastboot_erase", names);
        Assert.Contains("adb_reboot", names);
    }

    [Fact]
    public async Task ListFormats_ReportsRegisteredHandlers()
    {
        var sandbox = new PathSandbox(_root);
        var json = await FirmwareToolHandlers.ListFormatsAsync();
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.True(document.RootElement.GetProperty("data").GetArrayLength() > 0);
    }

    [Fact]
    public async Task CompressionRoundTrip_ProducesIdenticalBytes()
    {
        var sandbox = new PathSandbox(_root);
        var input = Path.Combine(_root, "payload.bin");
        var compressed = Path.Combine(_root, "payload.bin.zst");
        var restored = Path.Combine(_root, "payload.restored.bin");
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(input, payload);

        var compressJson = await CompressionToolHandlers.Compress(sandbox, input, compressed, "zstd", null, null);
        Assert.True(JsonDocument.Parse(compressJson).RootElement.GetProperty("success").GetBoolean(),
            compressJson);

        var decompressJson = await CompressionToolHandlers.Decompress(sandbox, compressed, restored, null, 1024 * 1024);
        Assert.True(JsonDocument.Parse(decompressJson).RootElement.GetProperty("success").GetBoolean(),
            decompressJson);

        Assert.True(payload.AsSpan().SequenceEqual(File.ReadAllBytes(restored)));
    }

    [Fact]
    public async Task UnpackRejectsPathsOutsideSandbox()
    {
        var sandbox = new PathSandbox(_root);
        // The handler resolves before detection, so containment must reject the call outright.
        // 处理器先做路径解析，包含关系检查必须直接拒绝调用。
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => FirmwareToolHandlers.UnpackAsync(sandbox, @"..\evil.bin", "out", 1024 * 1024));
    }

    [Fact]
    public void Parse_AcceptsRootAndFlashFlags_IgnoresUnknown()
    {
        var config = McpServerConfig.Parse(["--mcp-headless", "--mcp-root", _root, "--allow-flash", "--startui"]);
        Assert.True(config.AllowFlash);
        Assert.Equal(Path.GetFullPath(_root), config.RootDirectory);
        Assert.False(McpLauncher.IsHeadlessRequested(["--mcp"]));
        Assert.True(McpLauncher.IsHeadlessRequested(["--mcp-headless"]));
        Assert.True(McpLauncher.IsCompanionRequested(["--mcp"]));
        Assert.False(McpLauncher.IsCompanionRequested(["--mcp-headless"]));
    }
}
using System.Text.Json;
using ModelContextProtocol.Server;
using UotanStudio.Mcp;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Pins the MCP tool catalog emitted through the protocol surface: every registered tool must expose
/// a client-passable <c>inputSchema</c> that never carries the ambient parameters the SDK binds itself.
/// <para>固化协议层暴露的 MCP 工具目录：每个已注册工具的 <c>inputSchema</c> 都必须给出客户端可传的参数，
/// 且不得包含由 SDK 自行绑定的环境参数。</para>
/// </summary>
public sealed class McpToolCatalogTests : IDisposable
{
    private static readonly HashSet<string> AmbientParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cancellationToken",
        "token",
        "progress",
        "progressToken",
        "context",
        "sessionId"
    };

    private readonly string _root;

    public McpToolCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotanstudio-mcp-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly string[] BaseToolNames =
    [
        "list_formats",
        "detect_format",
        "read_metadata",
        "unpack_firmware",
        "pack_firmware",
        "extract_container_entries",
        "list_compression_algorithms",
        "compress_file",
        "decompress_file",
        "list_devices",
        "adb_devices",
        "adb_shell",
        "adb_pull",
        "adb_push",
        "fastboot_variables",
        "delivery_validate",
        "delivery_list_outputs",
        "delivery_build",
        "ota_zip_verify",
        "ota_zip_patch",
        "ota_zip_rebuild",
        "super_payload_convert",
        "delivery_prepare_tools",
        "solution_read",
        "solution_write",
        "firmware_project_read",
        "dex_disassemble",
        "dex_decompile",
        "dex_assemble",
        "dex_info",
        "axml_parse",
        "arsc_parse",
        "apk_strip_signatures",
        "get_env"
    ];

    private static readonly string[] FlashToolNames =
    [
        "adb_reboot",
        "fastboot_erase",
        "fastboot_flash"
    ];

    [Fact]
    public void BuildTools_DefaultConfig_ExposesExactlyBaseToolSet()
    {
        var names = ToolNames(allowFlash: false);

        Assert.Equal(
            BaseToolNames.OrderBy(name => name, StringComparer.Ordinal),
            names.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void BuildTools_AllowFlash_AddsExactlyFlashGatedTools()
    {
        var baseNames = ToolNames(allowFlash: false);
        var flashNames = ToolNames(allowFlash: true);

        Assert.Equal(BaseToolNames.Length + FlashToolNames.Length, flashNames.Count);
        Assert.Equal(
            FlashToolNames.OrderBy(name => name, StringComparer.Ordinal),
            flashNames.Except(baseNames, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void BuildTools_ToolNamesAreUniqueSnakeCase()
    {
        var names = ToolNames(allowFlash: true);

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Matches("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", name));
    }

    [Fact]
    public void BuildTools_EveryTool_DescribesItselfForClients()
    {
        foreach (var tool in AllTools())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(tool.ProtocolTool.Description),
                $"{tool.ProtocolTool.Name} is registered without a description.");
        }
    }

    [Fact]
    public void BuildTools_EveryTool_HasNoAmbientParameterInInputSchema()
    {
        var tools = AllTools();
        Assert.NotEmpty(tools);

        foreach (var tool in tools)
        {
            var name = tool.ProtocolTool.Name;
            Assert.False(string.IsNullOrWhiteSpace(name), "A registered tool has no protocol tool name.");

            var schema = tool.ProtocolTool.InputSchema;
            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            Assert.True(schema.TryGetProperty("properties", out var properties),
                $"{name} inputSchema has no properties object.");
            Assert.Equal(JsonValueKind.Object, properties.ValueKind);

            AssertNoAmbientNames(
                properties.EnumerateObject().Select(property => property.Name),
                name,
                "inputSchema properties");

            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                AssertNoAmbientNames(
                    required.EnumerateArray().Select(entry => entry.GetString() ?? string.Empty),
                    name,
                    "inputSchema required");
            }
        }
    }

    private List<string> ToolNames(bool allowFlash) =>
        BuildHost(allowFlash).BuildTools().Select(tool => tool.ProtocolTool.Name).ToList();

    private McpServerHost BuildHost(bool allowFlash) => new(new PathSandbox(_root), allowFlash);

    private IReadOnlyList<McpServerTool> AllTools() => BuildHost(allowFlash: true).BuildTools();

    private static void AssertNoAmbientNames(IEnumerable<string> candidateNames, string toolName, string section)
    {
        var leaked = candidateNames
            .Where(AmbientParameterNames.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(leaked.Count == 0,
            $"{toolName} {section} exposes ambient SDK-bound parameter(s): {string.Join(", ", leaked)}.");
    }
}
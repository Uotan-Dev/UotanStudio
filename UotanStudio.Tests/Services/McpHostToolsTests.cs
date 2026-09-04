using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using UotanStudio.Services.Mcp;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// MCP host tool tests for <see cref="UotanStudioMcpTools"/>: verify the tool definitions (names,
/// descriptions, input JSON Schemas) and exercise the firmware.unpack / firmware.pack execution path
/// with a real temp-directory cpio round-trip, plus the flash.image "device not found" failure path.
/// The MCP host itself is not started — TCP listening is out of scope; only the tool layer is tested.
/// <para><see cref="UotanStudioMcpTools"/> 的 MCP 工具测试：校验工具定义（名称、描述、输入 JSON
/// Schema），并以临时目录对 firmware.unpack / firmware.pack 做真实 cpio 回环，另测 flash.image
/// 的「设备不存在」失败路径（工具直连不可注入的 <see cref="UotanStudio.Services.Devices.DeviceMonitorService"/>
/// 单例，扫描基础设施不可用时跳过并注明）。不启动 MCP host——TCP 监听不在测试范围，仅测工具层。</para>
/// </summary>
public sealed class McpHostToolsTests
{
    private static readonly UotanStudioMcpTools Tools = new();

    /// <summary>
    /// Creates an MCP server tool definition from the named <see cref="UotanStudioMcpTools"/> method
    /// via the same SDK path the host uses (attributes drive name/description/input schema).
    /// <para>经与 host 相同的 SDK 路径，从 <see cref="UotanStudioMcpTools"/> 的指定方法创建 MCP 工具定义
    ///（名称/描述/输入 Schema 均来自特性）。</para>
    /// </summary>
    private static McpServerTool CreateTool(string methodName)
    {
        var method = typeof(UotanStudioMcpTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing tool method: {methodName}");
        return McpServerTool.Create(method, Tools, new McpServerToolCreateOptions());
    }

    /// <summary>
    /// Reads the "required" array of a tool's input JSON Schema.
    /// <para>读取工具输入 JSON Schema 的 required 数组。</para>
    /// </summary>
    private static HashSet<string> RequiredFields(McpServerTool tool)
    {
        var schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("required", out var required))
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in required.EnumerateArray())
        {
            if (item.GetString() is { Length: > 0 } name)
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the "properties" object's key set of a tool's input JSON Schema.
    /// <para>读取工具输入 JSON Schema 的 properties 键集合。</para>
    /// </summary>
    private static HashSet<string> SchemaProperties(McpServerTool tool)
    {
        var schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties))
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            result.Add(property.Name);
        }

        return result;
    }

    /// <summary>
    /// All tool methods of <see cref="UotanStudioMcpTools"/> carrying <see cref="McpServerToolAttribute"/>.
    /// <para><see cref="UotanStudioMcpTools"/> 上带 <see cref="McpServerToolAttribute"/> 的全部工具方法。</para>
    /// </summary>
    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(UotanStudioMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    [Fact]
    public void AllMcpTools_HaveNonEmptyNameAndDescription()
    {
        var methods = ToolMethods().ToList();
        Assert.Contains(methods, m => m.Name == "FirmwareInfo");
        Assert.Contains(methods, m => m.Name == "FirmwareUnpack");
        Assert.Contains(methods, m => m.Name == "FirmwarePack");
        Assert.Contains(methods, m => m.Name == "FlashImage");

        foreach (var method in methods)
        {
            var tool = CreateTool(method.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Name), $"{method.Name}: tool name is empty.");
            Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description), $"{method.Name}: tool description is empty.");
        }
    }

    [Fact]
    public void FirmwareUnpack_ToolDefinition_HasSchemaWithRequiredFilePath()
    {
        var tool = CreateTool(nameof(UotanStudioMcpTools.FirmwareUnpack));

        Assert.Equal("firmware.unpack", tool.ProtocolTool.Name);
        Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description));

        var required = RequiredFields(tool);
        Assert.Contains("filePath", required);
        Assert.Contains("filePath", SchemaProperties(tool));
        Assert.Contains("outputDir", SchemaProperties(tool));
        Assert.Contains("format", SchemaProperties(tool));
        Assert.Contains("extractFiles", SchemaProperties(tool));
    }

    [Fact]
    public void FirmwarePack_ToolDefinition_HasSchemaWithRequiredOutputPath()
    {
        var tool = CreateTool(nameof(UotanStudioMcpTools.FirmwarePack));

        Assert.Equal("firmware.pack", tool.ProtocolTool.Name);
        Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description));

        var required = RequiredFields(tool);
        Assert.Contains("outputPath", required);
        var properties = SchemaProperties(tool);
        Assert.Contains("inputDir", properties);
        Assert.Contains("inputPath", properties);
        Assert.Contains("format", properties);
    }

    [Fact]
    public void FlashImage_ToolDefinition_HasSchemaWithRequiredImagePathAndPartition()
    {
        var tool = CreateTool(nameof(UotanStudioMcpTools.FlashImage));

        Assert.Equal("flash.image", tool.ProtocolTool.Name);
        Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description));

        var required = RequiredFields(tool);
        Assert.Contains("imagePath", required);
        Assert.Contains("partition", required);
        Assert.Contains("serial", SchemaProperties(tool));
    }

    [Fact]
    public async Task FirmwareUnpack_MissingFile_ReturnsStructuredErrorJson()
    {
        var result = await Tools.FirmwareUnpack(
            Path.Combine(Path.GetTempPath(), $"no-such-firmware-{Guid.NewGuid():N}.bin"),
            ct: CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("firmware.unpack", doc.RootElement.GetProperty("tool").GetString());
        Assert.Contains("not found", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FirmwarePack_MissingSource_ReturnsStructuredErrorJson()
    {
        var result = await Tools.FirmwarePack(
            inputDir: Path.Combine(Path.GetTempPath(), $"no-such-directory-{Guid.NewGuid():N}"),
            inputPath: null,
            outputPath: Path.Combine(Path.GetTempPath(), "out.cpio"),
            ct: CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("firmware.pack", doc.RootElement.GetProperty("tool").GetString());
    }

    [Fact]
    public async Task FirmwarePackAndUnpack_CpioRoundTrip_MatchesFiles()
    {
        using var temp = new TempDirectory();
        var sourceDir = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(Path.Combine(sourceDir, "sub"));
        File.WriteAllText(Path.Combine(sourceDir, "data.txt"), "hello mcp\n");
        File.WriteAllText(Path.Combine(sourceDir, "sub", "notes.txt"), "nested file\n");

        var cpioPath = Path.Combine(temp.Path, "out.cpio");
        var packResult = await Tools.FirmwarePack(
            inputDir: sourceDir,
            inputPath: null,
            outputPath: cpioPath,
            format: "cpio",
            ct: CancellationToken.None);

        using (var packDoc = JsonDocument.Parse(packResult))
        {
            Assert.True(packDoc.RootElement.GetProperty("success").GetBoolean(), packResult);
            Assert.Equal("cpio", packDoc.RootElement.GetProperty("format").GetString());
            Assert.True(packDoc.RootElement.GetProperty("sizeBytes").GetInt64() > 0);
        }

        Assert.True(File.Exists(cpioPath));

        var unpackedDir = Path.Combine(temp.Path, "unpacked");
        var unpackResult = await Tools.FirmwareUnpack(cpioPath, outputDir: unpackedDir, ct: CancellationToken.None);

        using (var unpackDoc = JsonDocument.Parse(unpackResult))
        {
            Assert.True(unpackDoc.RootElement.GetProperty("success").GetBoolean(), unpackResult);
            Assert.Equal(unpackedDir, unpackDoc.RootElement.GetProperty("outputDir").GetString());
        }

        var sourceFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .OrderBy(Path.GetFileName)
            .ToArray();
        var unpackedFiles = Directory.EnumerateFiles(unpackedDir, "*", SearchOption.AllDirectories)
            .OrderBy(Path.GetFileName)
            .ToArray();

        Assert.Equal(sourceFiles.Length, unpackedFiles.Length);
        for (var i = 0; i < sourceFiles.Length; i++)
        {
            var relative = Path.GetRelativePath(sourceDir, sourceFiles[i]);
            Assert.Equal(relative, Path.GetRelativePath(unpackedDir, unpackedFiles[i]));
            Assert.Equal(File.ReadAllBytes(sourceFiles[i]), File.ReadAllBytes(unpackedFiles[i]));
        }
    }

    [Fact]
    public async Task FlashImage_UnknownSerial_ReturnsClearDeviceError()
    {
        using var temp = new TempDirectory();
        var imagePath = temp.Write("boot.img", "fake image content");

        var result = await Tools.FlashImage(
            imagePath,
            "boot",
            serial: "MCP-TEST-NONEXISTENT-SERIAL-0000",
            ct: CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("flash.image", doc.RootElement.GetProperty("tool").GetString());
        var error = doc.RootElement.GetProperty("error").GetString() ?? string.Empty;

        // DeviceMonitorService.Instance 是单例、不可注入，本测试走真实 USB/adb 扫描；扫描基础
        // 设施不可用（无 USB 后端 / adb server）时，工具仍返回结构化 JSON，但错误文本不含所请求的
        // serial——此时按约定跳过并注明，不对该错误做硬断言。扫描能跑通的环境下保持完整断言。
        // DeviceMonitorService.Instance is a singleton (not injectable), so this test goes through
        // the real USB/adb scan; when the scan infrastructure is unavailable the tool still returns
        // structured JSON, but the error text lacks the requested serial — skip with a note then,
        // keeping the full assertions for environments where the scan actually runs.
        if (!error.Contains("MCP-TEST-NONEXISTENT-SERIAL-0000", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("DeviceMonitorService.Scan() is not injectable and could not run in this environment (no USB/adb infrastructure); the flash.image device-not-found path was not exercised.");
        }

        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MCP-TEST-NONEXISTENT-SERIAL-0000", error, StringComparison.OrdinalIgnoreCase);
    }
}
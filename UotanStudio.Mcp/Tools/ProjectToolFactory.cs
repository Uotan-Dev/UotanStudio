using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using UotanStudio.Services.Projects;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the project/solution MCP tools backed by <see cref="SolutionPersistenceService"/>.
/// <para>基于 <see cref="SolutionPersistenceService"/> 构建项目/解决方案域 MCP 工具。</para>
/// Full project session management (creation, sub-projects, save-as) stays in the desktop host's
/// operation gate; the MCP surface is limited to lossless snapshot read/write so an agent can
/// inspect and edit solution structure without bypassing host rules.
/// <para>完整的项目会话管理（新建、子项目、另存为）仍由桌面宿主的项目操作门负责；MCP 层仅提供
/// 无损快照读写，让代理能检查与编辑解决方案结构而不绕过宿主规则。</para>
/// </summary>
internal static class ProjectToolFactory
{
    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox)
    {
        var read = McpServerTool.Create(
            ([Description("Solution file path (.uotan) inside the sandbox.")] string path => ProjectToolHandlers.ReadSolutionAsync(sandbox, path),
            new McpServerToolCreateOptions
            {
                Name = "solution_read",
                Description = "Read a .uotan solution file: solution name, node project reference, "
                    + "sub-projects and the raw extension state section (e.g. the delivery plan).",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

        var write = McpServerTool.Create(
            ([Description("Solution file path (.uotan) inside the sandbox.")] string path,
             [Description("Complete SolutionSnapshot JSON to write. Existing state not included in the JSON is lost.")] string snapshotJson => ProjectToolHandlers.WriteSolutionAsync(sandbox, path, snapshotJson),
            new McpServerToolCreateOptions
            {
                Name = "solution_write",
                Description = "Write a complete SolutionSnapshot JSON to a .uotan solution file. "
                    + "Read the existing solution first and modify it; this replaces the whole file.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = true
            });

        var firmwareProject = McpServerTool.Create(
            ([Description("Firmware project file path (.uotanfw) inside the sandbox.")] string path => ProjectToolHandlers.ReadFirmwareProjectAsync(sandbox, path),
            new McpServerToolCreateOptions
            {
                Name = "firmware_project_read",
                Description = "Read a .uotanfw firmware project snapshot (images, groups, metadata).",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

        return [read, write, firmwareProject];
    }
}

/// <summary>
/// Implementations for the project tool delegates; kept separate for direct unit testing.
/// <para>项目工具委托的实现；独立存放便于直接单元测试。</para>
/// </summary>
internal static class ProjectToolHandlers
{
    public static async Task<string> ReadSolutionAsync(PathSandbox sandbox, string path)
    {
        var resolved = sandbox.Resolve(path, mustExist: true);
        var snapshot = await SolutionPersistenceService.LoadSolutionAsync(resolved).ConfigureAwait(false);
        return snapshot is null
            ? ToolJson.Serialize(new ToolResult(false, $"Not a valid solution file: {resolved}"))
            : ToolJson.Serialize(new ToolResult(true, $"Solution '{snapshot.SolutionName}'.",
                JsonSerializer.SerializeToElement(snapshot)));
    }

    public static async Task<string> WriteSolutionAsync(PathSandbox sandbox, string path, string snapshotJson)
    {
        var resolved = sandbox.ResolveOutput(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        var snapshot = JsonSerializer.Deserialize(snapshotJson, McpJsonContext.Default.SolutionSnapshot)
            ?? throw new InvalidDataException("Solution snapshot JSON could not be parsed.");
        var written = await SolutionPersistenceService.SaveSolutionAsync(resolved, snapshot).ConfigureAwait(false);
        return written
            ? ToolJson.Serialize(new ToolResult(true, $"Solution written: {resolved}", new { path = resolved }))
            : ToolJson.Serialize(new ToolResult(false, $"Writing the solution failed: {resolved}"));
    }

    public static async Task<string> ReadFirmwareProjectAsync(PathSandbox sandbox, string path)
    {
        var resolved = sandbox.Resolve(path, mustExist: true);
        var snapshot = await SolutionPersistenceService.LoadFirmwareProjectAsync(resolved).ConfigureAwait(false);
        return snapshot is null
            ? ToolJson.Serialize(new ToolResult(false, $"Not a valid firmware project file: {resolved}"))
            : ToolJson.Serialize(new ToolResult(true, $"Firmware project loaded.",
                JsonSerializer.SerializeToElement(snapshot)));
    }
}

/// <summary>
/// Source-generation-friendly JSON context for the solution DTOs exposed on the MCP boundary.
/// <para>MCP 边界上解决方案 DTO 的 source-gen JSON 上下文。</para>
/// </summary>
[JsonSerializable(typeof(SolutionSnapshot))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
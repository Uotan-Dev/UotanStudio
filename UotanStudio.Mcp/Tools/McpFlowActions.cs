using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Registers internal flow/macro/artifact helper actions. No new resident MCP schema; exposed via meta-tools.
/// <para>注册内部流程/宏/工件辅助动作。不增加常驻 MCP schema；通过元工具暴露。</para>
/// </summary>
internal static class McpFlowActions
{
    internal static void AddActions(List<McpActionDescriptor> actions, PluginToolContext context)
    {
        AddArtifactActions(actions, context);
        AddMacros(actions);
    }

    private static void AddArtifactActions(List<McpActionDescriptor> actions, PluginToolContext context)
    {
        actions.Add(Create("artifact_read", "misc",
            "Read a sandbox artifact by id. Returns preview by default; set full=true for full text (use carefully).",
            true, false, false, null,
            McpServerTool.Create(
                ([Description("Artifact id returned by a previous action.")] string artifactId,
                 [Description("Return full text content instead of preview.")] bool? full,
                 CancellationToken cancellationToken) => ToolGuard.RunAsync(_ =>
                {
                    if (!context.Artifacts.TryRead(artifactId, out var artifact, out var fullPath) || artifact is null || fullPath is null)
                    {
                        return Task.FromResult(new ToolResult(false, $"Artifact '{artifactId}' not found."));
                    }

                    if (full == true)
                    {
                        var text = File.ReadAllText(fullPath);
                        if (Encoding.UTF8.GetByteCount(text) > McpArtifactStore.DefaultThresholdBytes)
                        {
                            var preview = text.Length <= 240 ? text[..240] + "..." : text;
                            return Task.FromResult(new ToolResult(true, $"Artifact '{artifactId}' full content.", ToolJson.Data(new ArtifactEnvelopePayload("artifact_read", artifact with { Preview = preview }, false))));
                        }

                        return Task.FromResult(new ToolResult(true, $"Artifact '{artifactId}' content.", ToolJson.Data(new ArtifactContentPayload(artifact, text))));
                    }

                    return Task.FromResult(new ToolResult(true, $"Artifact '{artifactId}' metadata.", ToolJson.Data(artifact)));
                }, cancellationToken),
                new McpServerToolCreateOptions { Name = "artifact_read", Description = "Read a sandbox artifact by id.", ReadOnly = true, Destructive = false })));

        actions.Add(Create("artifact_list", "misc",
            "List sandbox-stored artifacts created by large MCP outputs.",
            true, false, false, null,
            McpServerTool.Create(
                (CancellationToken cancellationToken) => ToolGuard.RunAsync(_ =>
                {
                    var items = context.Artifacts.List();
                    return Task.FromResult(new ToolResult(true, $"Listed {items.Count} artifact(s).", ToolJson.Data(new ArtifactListPayload(items))));
                }, cancellationToken),
                new McpServerToolCreateOptions { Name = "artifact_list", Description = "List sandbox-stored artifacts.", ReadOnly = true, Destructive = false })));
    }

    private static void AddMacros(List<McpActionDescriptor> actions)
    {
        var macros = new[]
        {
            new MacroDef("macro.firmware.analyze", "flow", "Analyze firmware: detect format, read metadata/GPT/entries as needed, return summary.", false, false, false,
                "skills/mcp-firmware-analysis/SKILL.md",
                ["detect_format","read_metadata","read_gpt","extract_container_entries"]),
            new MacroDef("macro.firmware.unpack_modify_repack", "flow", "Unpack, modify via allowed actions, repack, and verify outputs.", false, false, false,
                "skills/mcp-modify-repack/SKILL.md",
                ["unpack_firmware","pack_firmware","detect_format","read_metadata"]),
            new MacroDef("macro.device.standard_flash", "flash", "Standard flash flow: preview plan, confirm, execute, report.", false, true, true,
                "skills/mcp-standard-flash/SKILL.md",
                ["preview_flash_plan","execute_flash_plan","get_flash_report"]),
        };

        actions.Add(Create("macro_list", "flow", "List available high-level MCP macros/SOP workflows.", true, false, false, null,
            McpServerTool.Create(
                (CancellationToken cancellationToken) => ToolGuard.RunAsync(_ =>
                    Task.FromResult(new ToolResult(true, $"Listed {macros.Length} macro(s).", ToolJson.Data(new MacroListPayload(macros.Select(m => m.ToInfo()).ToList())))), cancellationToken),
                new McpServerToolCreateOptions { Name = "macro_list", Description = "List available MCP macros.", ReadOnly = true, Destructive = false })));

        actions.Add(Create("macro_describe", "flow", "Describe a macro/SOP workflow and its recommended action sequence.", true, false, false, null,
            McpServerTool.Create(
                ([Description("Macro name from macro_list.")] string name,
                 CancellationToken cancellationToken) => ToolGuard.RunAsync(_ =>
                {
                    var m = macros.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    return Task.FromResult(m is null
                        ? new ToolResult(false, $"Unknown macro '{name}'.")
                        : new ToolResult(true, $"Macro '{m.Name}'.", ToolJson.Data(m.ToInfo())));
                }, cancellationToken),
                new McpServerToolCreateOptions { Name = "macro_describe", Description = "Describe an MCP macro.", ReadOnly = true, Destructive = false })));

        actions.Add(Create("macro_invoke", "flow", "Resolve a macro invocation plan. Returns SOP reference and recommended execute_action calls; does not execute destructive steps.", true, false, false, null,
            McpServerTool.Create(
                ([Description("Macro name from macro_list.")] string name,
                 [Description("Optional input object for the macro.")] JsonElement? input,
                 CancellationToken cancellationToken) => ToolGuard.RunAsync(_ =>
                {
                    var m = macros.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (m is null) return Task.FromResult(new ToolResult(false, $"Unknown macro '{name}'."));
                    var plan = new MacroInvocationPayload(m.Name, m.SkillPath, m.Steps, input?.GetRawText() ?? "{}", "Macro invocation returns a plan only; execute steps via execute_action.");
                    return Task.FromResult(new ToolResult(true, $"Macro '{m.Name}' resolved.", ToolJson.Data(plan)));
                }, cancellationToken),
                new McpServerToolCreateOptions { Name = "macro_invoke", Description = "Resolve an MCP macro into an execution plan.", ReadOnly = true, Destructive = false })));
    }

    private static McpActionDescriptor Create(string name, string domain, string summary, bool readOnly, bool destructive, bool flashGated, string? nodeTypeKey, McpServerTool tool)
    {
        var schema = tool.ProtocolTool.InputSchema.ValueKind == JsonValueKind.Undefined ? ToolJson.EmptyObjectSchema : tool.ProtocolTool.InputSchema.Clone();
        return new McpActionDescriptor(name, domain, summary, schema, readOnly, destructive, flashGated, nodeTypeKey, tool);
    }

    private sealed record MacroDef(string Name, string Domain, string Summary, bool ReadOnly, bool Destructive, bool FlashGated, string SkillPath, string[] Steps)
    {
        public MacroInfoPayload ToInfo() => new(Name, Domain, Summary, ReadOnly, Destructive, FlashGated, SkillPath, Steps);
    }
}
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Registers high-level flash plan actions; execution remains gated by AllowFlash and explicit confirmation.
/// <para>注册高层刷写计划动作；执行仍由 AllowFlash 与显式确认门控。</para>
/// </summary>
internal static class FlashPlanActionFactory
{
    private const string PlanDir = ".uotanstudio-mcp/flash-plans";

    internal static void AddActions(List<McpActionDescriptor> actions, PluginToolContext context)
    {
        actions.Add(CreatePreview(context));
        actions.Add(CreateExecute(context));
        actions.Add(CreateReport(context));
    }

    private static McpActionDescriptor CreatePreview(PluginToolContext context)
    {
        var schema = JsonDocument.Parse("""
        {"type":"object","properties":{"target":{"type":"string","description":"Target device serial or identifier."},"plan":{"type":"object","description":"Flash plan object; stored as-is in sandbox plan artifact."},"confirmToken":{"type":"string","description":"Optional caller token echoed into plan."}},"required":["plan"],"additionalProperties":false}
        """).RootElement.Clone();
        var tool = McpServerTool.Create(
            ([Description("Target device serial or identifier.")] string? target,
             [Description("Flash plan object.")] JsonElement plan,
             [Description("Optional caller confirmation token.")] string? confirmToken,
             CancellationToken cancellationToken) => ToolGuard.RunAsync(_ =>
            {
                var planId = $"plan-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..40];
                var requires = context.AllowFlash;
                var status = "previewed";
                var warnings = new List<string>();
                if (!context.AllowFlash)
                {
                    warnings.Add("AllowFlash is disabled; execute_flash_plan will refuse this plan.");
                }
                warnings.Add("No device write performed. Review plan artifact before execution.");

                var planObj = new FlashPlanRecord(planId, status, target, DateTimeOffset.UtcNow, null, context.AllowFlash, requires, confirmToken, plan.GetRawText(), null);
                var artifact = context.Artifacts.CreateJsonArtifact("flash-plan", planObj, $"flash plan {planId} target={target ?? "<none>"}");
                SavePlan(context, planObj with { }, artifact);
                var payload = new FlashPlanPayload(planId, status, "preview", target, requires, context.AllowFlash, artifact, null, warnings);
                return Task.FromResult(new ToolResult(true, $"Flash plan {planId} previewed.", ToolJson.Data(payload)));
            }, cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "preview_flash_plan",
                Description = "Preview a flash plan without performing device writes. Stores a sandbox plan artifact and returns planId. Execution requires AllowFlash and explicit confirmation.",
                ReadOnly = true,
                Destructive = false
            });
        return new McpActionDescriptor("preview_flash_plan", "flash", tool.ProtocolTool.Description!, schema, true, false, false, null, tool);
    }

    private static McpActionDescriptor CreateExecute(PluginToolContext context)
    {
        var schema = JsonDocument.Parse("""
        {"type":"object","properties":{"planId":{"type":"string"},"confirm":{"type":"boolean","description":"Must be true to execute."},"confirmationText":{"type":"string","description":"Must equal EXECUTE to execute."}},"required":["planId","confirm","confirmationText"],"additionalProperties":false}
        """).RootElement.Clone();
        var tool = McpServerTool.Create(
            ([Description("Plan id returned by preview_flash_plan.")] string planId,
             [Description("Explicit confirmation flag.")] bool confirm,
             [Description("Type EXECUTE to confirm destructive execution.")] string confirmationText,
             CancellationToken cancellationToken) => ToolGuard.RunAsync(_ =>
            {
                if (!context.AllowFlash)
                {
                    return Task.FromResult(new ToolResult(false, "Flash execution is disabled. Start MCP with --allow-flash and preview a plan first."));
                }
                if (!confirm || !string.Equals(confirmationText, "EXECUTE", StringComparison.Ordinal))
                {
                    return Task.FromResult(new ToolResult(false, "Flash execution requires confirm=true and confirmationText='EXECUTE'."));
                }
                if (!TryLoadPlan(context, planId, out var record, out var planArtifact) || record is null || planArtifact is null)
                {
                    return Task.FromResult(new ToolResult(false, $"Flash plan '{planId}' was not found in sandbox artifacts."));
                }

                // P3 bounded implementation: records an execution report for the approved plan.
                // Actual device-mutating operations remain internal flash-gated actions; direct flash actions are blocked by execute_action.
                var executedAt = DateTimeOffset.UtcNow;
                var report = new FlashReportPayload(planId, "executed", true,
                    "Plan approved and execution recorded. Destructive device operations must be performed by approved flash executors bound to this plan.",
                    record.CreatedAt, executedAt, null,
                    ["Validated AllowFlash and explicit confirmation.", "Loaded sandbox plan artifact.", "Recorded execution report artifact."]);
                var reportArtifact = context.Artifacts.CreateJsonArtifact("flash-report", report, $"flash report {planId} executed");
                SavePlan(context, record with { Status = "executed", ExecutedAt = executedAt, ReportArtifactId = reportArtifact.Id }, planArtifact);
                var payload = new FlashReportPayload(planId, report.Status, report.Success, report.Summary, report.CreatedAt, report.ExecutedAt, reportArtifact, report.Steps);
                return Task.FromResult(new ToolResult(true, $"Flash plan {planId} execution recorded.", ToolJson.Data(payload)));
            }, cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "execute_flash_plan",
                Description = "Execute a previously previewed flash plan. Requires AllowFlash, confirm=true, and confirmationText='EXECUTE'. Writes a sandbox report artifact.",
                ReadOnly = false,
                Destructive = true
            });
        return new McpActionDescriptor("execute_flash_plan", "flash", tool.ProtocolTool.Description!, schema, false, true, true, null, tool);
    }

    private static McpActionDescriptor CreateReport(PluginToolContext context)
    {
        var schema = JsonDocument.Parse("""
        {"type":"object","properties":{"planId":{"type":"string"}},"required":["planId"],"additionalProperties":false}
        """).RootElement.Clone();
        var tool = McpServerTool.Create(
            ([Description("Plan id returned by preview_flash_plan.")] string planId,
             CancellationToken cancellationToken) => ToolGuard.RunAsync(_ =>
            {
                if (!TryLoadPlan(context, planId, out var record, out var planArtifact) || record is null)
                {
                    return Task.FromResult(new ToolResult(false, $"Flash plan '{planId}' was not found in sandbox artifacts."));
                }
                ArtifactRef? reportArtifact = null;
                if (!string.IsNullOrWhiteSpace(record.ReportArtifactId) && context.Artifacts.TryRead(record.ReportArtifactId!, out var ra, out var _))
                {
                    reportArtifact = ra;
                }
                var payload = new FlashReportPayload(record.PlanId, record.Status, string.Equals(record.Status, "executed", StringComparison.OrdinalIgnoreCase),
                    reportArtifact is null ? "Plan has no execution report yet." : "Execution report available.",
                    record.CreatedAt, record.ExecutedAt, reportArtifact, reportArtifact is null ? ["Previewed only."] : ["Execution report stored as artifact."]);
                return Task.FromResult(new ToolResult(true, $"Flash report for {planId}.", ToolJson.Data(payload)));
            }, cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "get_flash_report",
                Description = "Get structured status/report for a flash plan by planId.",
                ReadOnly = true,
                Destructive = false
            });
        return new McpActionDescriptor("get_flash_report", "flash", tool.ProtocolTool.Description!, schema, true, false, false, null, tool);
    }

    private static void SavePlan(PluginToolContext context, FlashPlanRecord record, ArtifactRef artifact)
    {
        var root = context.Sandbox.Root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio", "mcp-sandbox");
        var dir = Path.Combine(root, PlanDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Sanitize(record.PlanId) + ".json");
        var index = new FlashPlanIndex(record.PlanId, record.Status, artifact.Id, record.ReportArtifactId, record.CreatedAt, record.ExecutedAt);
        File.WriteAllText(path, JsonSerializer.Serialize(index, ToolJson.TypeInfo<FlashPlanIndex>()));
    }

    private static bool TryLoadPlan(PluginToolContext context, string planId, out FlashPlanRecord? record, out ArtifactRef? artifact)
    {
        record = null; artifact = null;
        var root = context.Sandbox.Root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio", "mcp-sandbox");
        var path = Path.Combine(root, PlanDir.Replace('/', Path.DirectorySeparatorChar), Sanitize(planId) + ".json");
        if (!File.Exists(path)) return false;
        try
        {
            var index = JsonSerializer.Deserialize(File.ReadAllText(path), ToolJson.TypeInfo<FlashPlanIndex>());
            if (index is null) return false;
            if (!context.Artifacts.TryRead(index.ArtifactId, out artifact, out var artifactPath) || artifact is null || artifactPath is null) return false;
            var json = File.ReadAllText(artifactPath);
            record = JsonSerializer.Deserialize(json, ToolJson.TypeInfo<FlashPlanRecord>());
            if (record is null) return false;
            record = record with { Status = index.Status, ExecutedAt = index.ExecutedAt, ReportArtifactId = index.ReportArtifactId };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }

    internal sealed record FlashPlanRecord(string PlanId, string Status, string? Target, DateTimeOffset CreatedAt, DateTimeOffset? ExecutedAt, bool AllowFlash, bool RequiresConfirmation, string? ConfirmToken, string PlanJson, string? ReportArtifactId);
    internal sealed record FlashPlanIndex(string PlanId, string Status, string ArtifactId, string? ReportArtifactId, DateTimeOffset CreatedAt, DateTimeOffset? ExecutedAt);
}
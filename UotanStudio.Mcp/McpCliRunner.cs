using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UotanStudio.Mcp.Tools;

namespace UotanStudio.Mcp;

/// <summary>
/// Minimal CLI prototype for MCP action execution comparison (P4).
/// <para>用于 MCP 动作执行对比的最小 CLI 原型（P4）。</para>
/// </summary>
public static class McpCliRunner
{
    /// <summary>
    /// Returns true when CLI mode is requested.
    /// <para>判断是否请求 CLI 模式。</para>
    /// </summary>
    public static bool IsRequested(string[] args) => args.Any(a => string.Equals(a, "--mcp-cli", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs a single MCP action through the CLI prototype and writes JSON to stdout.
    /// <para>通过 CLI 原型运行单个 MCP 动作并将 JSON 写入 stdout。</para>
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            var options = Parse(args);
            UotanStudio.Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
            var config = McpServerConfig.Parse(args);
            var sandbox = new PathSandbox(config.RootDirectory);
            var host = new McpServerHost(new McpServerComposition
            {
                Sandbox = sandbox,
                AllowFlash = config.AllowFlash,
                DeviceManager = new HeadlessDeviceManagerGateway(),
                DeliveryTools = McpLauncher.CreateDesktopDeliveryTools()
            });
            var registry = host.BuildActionRegistry();

            if (options.ListActions)
            {
                var actions = new JsonArray();
                foreach (var a in registry.Actions)
                {
                    actions.Add((JsonNode)new JsonObject
                    {
                        ["action"] = a.Action,
                        ["domain"] = a.Domain,
                        ["summary"] = a.Summary,
                        ["readOnly"] = a.ReadOnly,
                        ["destructive"] = a.Destructive,
                        ["flashGated"] = a.FlashGated
                    });
                }

                WriteNode(new JsonObject { ["ok"] = true, ["command"] = "list-actions", ["count"] = actions.Count, ["actions"] = actions });
                return 0;
            }

            if (string.IsNullOrWhiteSpace(options.Action))
            {
                WriteNode(new JsonObject { ["ok"] = false, ["error"] = "Missing --action. Use --list-actions or --action <name> --params <json>." });
                return 2;
            }

            if (!registry.TryGet(options.Action, out var descriptor))
            {
                var suggestions = new JsonArray();
                foreach (var s in registry.Actions.Select(a => a.Action).Where(a => a.Contains(options.Action, StringComparison.OrdinalIgnoreCase)).Take(10))
                {
                    suggestions.Add((JsonNode)JsonValue.Create(s)!);
                }

                WriteNode(new JsonObject { ["ok"] = false, ["error"] = $"Unknown action '{options.Action}'.", ["suggestions"] = suggestions });
                return 2;
            }

            if (descriptor.Destructive && !options.DryRun && !options.Confirm)
            {
                WriteNode(new JsonObject { ["ok"] = false, ["error"] = $"Action '{descriptor.Action}' is destructive. Re-run with --dry-run or --confirm.", ["action"] = descriptor.Action, ["destructive"] = true });
                return 3;
            }

            if (descriptor.FlashGated && !config.AllowFlash)
            {
                WriteNode(new JsonObject { ["ok"] = false, ["error"] = $"Action '{descriptor.Action}' requires --allow-flash.", ["action"] = descriptor.Action, ["flashGated"] = true });
                return 3;
            }

            Console.Error.WriteLine($"[mcp-cli] action={descriptor.Action} domain={descriptor.Domain} destructive={descriptor.Destructive} flashGated={descriptor.FlashGated} dryRun={options.DryRun}");

            var @params = ParseParams(options.ParamsJson);
            if (options.DryRun)
            {
                var paramNode = ParamsToNode(@params);
                WriteNode(new JsonObject { ["ok"] = true, ["command"] = "dry-run", ["action"] = descriptor.Action, ["domain"] = descriptor.Domain, ["destructive"] = descriptor.Destructive, ["flashGated"] = descriptor.FlashGated, ["params"] = paramNode });
                return 0;
            }

            var result = await ExecuteAsync(host, registry, descriptor.Action, @params, cancellationToken).ConfigureAwait(false);
            var text = result.Content?.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text).FirstOrDefault() ?? "";
            JsonElement? structured = result.StructuredContent;
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(text); } catch { parsed = JsonValue.Create(text); }
            JsonNode? structuredNode = structured.HasValue ? JsonNode.Parse(structured.Value.GetRawText()) : null;
            WriteNode(new JsonObject
            {
                ["ok"] = result.IsError != true,
                ["action"] = descriptor.Action,
                ["isError"] = result.IsError == true,
                ["result"] = parsed,
                ["structuredContent"] = structuredNode
            });
            return result.IsError == true ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            WriteNode(new JsonObject { ["ok"] = false, ["cancelled"] = true });
            return 130;
        }
        catch (Exception ex)
        {
            WriteNode(new JsonObject { ["ok"] = false, ["error"] = ex.GetType().Name, ["message"] = ex.Message });
            return 1;
        }
    }

    private static async Task<ModelContextProtocol.Protocol.CallToolResult> ExecuteAsync(McpServerHost host, McpActionRegistry registry, string action, Dictionary<string, JsonElement>? @params, CancellationToken cancellationToken)
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        var transport = new ModelContextProtocol.Server.StreamServerTransport(input, output, "uotanstudio-cli");
        var options = McpServerHost.CreateOptions(McpServerHost.BuildMetaTools(registry), host.BuildResources(), (IMcpToolCallAudit?)null);
        var server = ModelContextProtocol.Server.McpServer.Create(transport, options);

        var meta = McpMetaToolFactory.CreateExecuteAction(registry, registry.Artifacts);
        var paramsObj = new JsonObject();
        if (@params is not null)
        {
            foreach (var kv in @params)
            {
                paramsObj[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
            }
        }

        var args = new Dictionary<string, JsonElement>
        {
            ["action"] = JsonElement.Parse(JsonValue.Create(action)!.ToJsonString()),
            ["params"] = JsonElement.Parse(paramsObj.ToJsonString())
        };
        var jsonRpc = new ModelContextProtocol.Protocol.JsonRpcRequest { Method = "tools/call", Id = new ModelContextProtocol.Protocol.RequestId(1) };
        var requestParams = new ModelContextProtocol.Protocol.CallToolRequestParams { Name = "execute_action", Arguments = args };
        var ctx = new ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams>(server, jsonRpc, requestParams);
        return await meta.InvokeAsync(ctx, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, JsonElement>? ParseParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("--params must be a JSON object.");
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        return dict;
    }

    private static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (Eq(a, "--list-actions")) o.ListActions = true;
            else if (Eq(a, "--dry-run")) o.DryRun = true;
            else if (Eq(a, "--confirm")) o.Confirm = true;
            else if (Eq(a, "--action") && i + 1 < args.Length) o.Action = args[++i];
            else if (Eq(a, "--params") && i + 1 < args.Length) o.ParamsJson = args[++i];
        }
        return o;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void WriteNode(JsonNode node) => Console.Out.WriteLine(node.ToJsonString());

    private static JsonNode ParamsToNode(Dictionary<string, JsonElement>? @params)
    {
        if (@params is null) return new JsonObject();
        var obj = new JsonObject();
        foreach (var kv in @params) obj[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
        return obj;
    }

    private sealed class CliOptions
    {
        public bool ListActions; public bool DryRun; public bool Confirm; public string? Action; public string? ParamsJson;
    }
}
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds and searches the internal MCP action registry from the catalog.
/// <para>从工具目录构建并检索内部 MCP 动作注册表。</para>
/// </summary>
internal sealed class McpActionRegistry
{
    private readonly IReadOnlyList<McpActionDescriptor> _actions;
    private readonly Dictionary<string, McpActionDescriptor> _byName;

    /// <summary>
    /// Creates a registry for one host context.
    /// <para>为单个宿主上下文创建注册表。</para>
    /// </summary>
    public McpActionRegistry(PluginToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Artifacts = context.Artifacts;
        var actions = new List<McpActionDescriptor>(PluginToolCatalog.All.Count + 16);
        foreach (var descriptor in PluginToolCatalog.All)
        {
            if (descriptor.FlashGated && !context.AllowFlash)
            {
                continue;
            }

            var tool = descriptor.Create(context);
            var protocol = tool.ProtocolTool;
            var summary = protocol.Description ?? descriptor.Name;
            var schema = protocol.InputSchema.ValueKind == JsonValueKind.Undefined
                ? ToolJson.EmptyObjectSchema
                : protocol.InputSchema.Clone();
            actions.Add(new McpActionDescriptor(
                descriptor.Name,
                descriptor.Domain,
                summary,
                schema,
                descriptor.ReadOnly,
                descriptor.Destructive,
                descriptor.FlashGated,
                descriptor.NodeTypeKey,
                tool));
        }

        AddBuiltInActions(actions, context);
        _actions = actions;
        _byName = actions.ToDictionary(a => a.Action, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets all registered actions. <para>获取全部已注册动作。</para></summary>
    public IReadOnlyList<McpActionDescriptor> Actions => _actions;

    /// <summary>Gets the artifact store for this registry. <para>获取此注册表的工件存储。</para></summary>
    internal McpArtifactStore Artifacts { get; }

    /// <summary>Tries to resolve an action by name. <para>按名称解析动作。</para></summary>
    public bool TryGet(string action, out McpActionDescriptor descriptor)
        => _byName.TryGetValue(action, out descriptor!);

    private static void AddBuiltInActions(List<McpActionDescriptor> actions, PluginToolContext context)
    {
        McpFlowActions.AddActions(actions, context);
        FlashPlanActionFactory.AddActions(actions, context);
    }
}
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Mcp;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>MCP 工具节点视图模型，提供对设置页配置的 MCP 服务器的工具列举与调用
/// （TypeKey：<c>MCP.ListTools</c> 列举工具、<c>MCP.CallTool</c> 调用工具并返回结果文本）。</para>
/// MCP tool node view model, providing tool listing and calling on the MCP servers configured on the
/// settings page (TypeKeys: <c>MCP.ListTools</c> lists tools, <c>MCP.CallTool</c> calls a tool and
/// returns the result text).
/// </summary>
public class McpNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// <para>创建具有指定标题的 MCP 工具节点。</para>
    /// Creates an MCP tool node with the specified title.
    /// </summary>
    /// <param name="title"><para>节点的显示标题。</para>The display title of the node.</param>
    public McpNodeViewModel(string title) : base(title, NodeCategory.Mcp)
    {
    }

    /// <summary>
    /// <para>创建默认的 MCP 工具列举节点。</para>
    /// Creates a default MCP tool-listing node.
    /// </summary>
    public static McpNodeViewModel CreateListToolsNode()
    {
        var node = new McpNodeViewModel("List MCP Tools")
        {
            Location = new Point(100, 100),
            TypeKey = "MCP.ListTools"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Server Name", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Tools", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>创建默认的 MCP 工具调用节点。</para>
    /// Creates a default MCP tool-calling node.
    /// </summary>
    public static McpNodeViewModel CreateCallToolNode()
    {
        var node = new McpNodeViewModel("Call MCP Tool")
        {
            Location = new Point(100, 100),
            TypeKey = "MCP.CallTool"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Server Name", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Tool Name", PortType = NodePortType.Text });
        node.Input.Add(new NodeConnectorViewModel { Title = "Arguments", PortType = NodePortType.Text });
        node.Output.Add(new NodeConnectorViewModel { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>
    /// <para>执行与节点 TypeKey 对应的 MCP 逻辑：MCP.ListTools 列举服务器工具，
    /// MCP.CallTool 以 JSON 实参调用工具并把结果文本写入输出端口。</para>
    /// Executes the MCP logic matching the node TypeKey: MCP.ListTools lists the server tools, and
    /// MCP.CallTool calls a tool with JSON arguments and writes the result text to the output port.
    /// </summary>
    /// <param name="context"><para>执行上下文。</para>The execution context.</param>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        try
        {
            switch (TypeKey)
            {
                case "MCP.ListTools":
                    await ExecuteListToolsAsync(context).ConfigureAwait(false);
                    break;
                case "MCP.CallTool":
                    await ExecuteCallToolAsync(context).ConfigureAwait(false);
                    break;
                default:
                    await base.ExecuteAsync(context).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            context.Log.Write(LogLevel.Error, Title, ex.Message);
            WriteOutput("Tools", ex.Message);
            WriteOutput("Result", ex.Message);
        }
    }

    /// <summary>
    /// <para>列举服务器工具并写入输出端口。</para>
    /// Lists the server tools and writes them to the output port.
    /// </summary>
    private async Task ExecuteListToolsAsync(INodeExecutionContext context)
    {
        var serverName = ReadInput("Server Name")?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(serverName))
        {
            WriteOutput("Tools", "Server Name is required. 需要填写服务器名称。");
            return;
        }

        var tools = await McpClientService.Instance.ListToolsAsync(serverName, context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Tools", string.Join("\n", tools));
    }

    /// <summary>
    /// <para>以 JSON 实参调用服务器工具并把结果文本写入输出端口。</para>
    /// Calls a server tool with JSON arguments and writes the result text to the output port.
    /// </summary>
    private async Task ExecuteCallToolAsync(INodeExecutionContext context)
    {
        var serverName = ReadInput("Server Name")?.ToString() ?? string.Empty;
        var toolName = ReadInput("Tool Name")?.ToString() ?? string.Empty;
        var arguments = ReadInput("Arguments")?.ToString();

        if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(toolName))
        {
            WriteOutput("Result", "Server Name and Tool Name are required. 需要填写服务器名称与工具名称。");
            return;
        }

        var result = await McpClientService.Instance.CallToolAsync(serverName, toolName, arguments, context.CancellationToken).ConfigureAwait(false);
        WriteOutput("Result", result);
    }
}

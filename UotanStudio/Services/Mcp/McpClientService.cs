using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace UotanStudio.Services.Mcp;

/// <summary>
/// <para>MCP 客户端服务：按 <see cref="McpServerConfig"/> 懒建立连接（stdio/HTTP），
/// 供工具箱 MCP 节点列出并调用外部 MCP server 的工具。AOT 兼容——工具实参以原始 JSON 字符串
/// 传入，由 <see cref="JsonDocument"/> 解析为字典，不引入反射。</para>
/// MCP client service: lazily establishes connections (stdio/HTTP) per
/// <see cref="McpServerConfig"/>, letting the toolbox MCP nodes list and call tools on external MCP
/// servers. AOT-safe — tool arguments arrive as raw JSON strings and are parsed via
/// <see cref="JsonDocument"/>, without reflection.
/// </summary>
public sealed class McpClientService
{
    /// <summary><para>全局单例。</para>The global singleton.</summary>
    public static McpClientService Instance { get; } = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients = new();

    private McpClientService()
    {
    }

    /// <summary>
    /// <para>清空全部缓存连接（设置页修改服务器列表后调用）。</para>
    /// Clears all cached connections (call after the settings page edits the server list).
    /// </summary>
    public void Reset()
    {
        foreach (var lazy in _clients.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                var client = lazy.Value.GetAwaiter().GetResult();
                if (client is not null)
                {
                    _ = client.DisposeAsync();
                }
            }
        }

        _clients.Clear();
    }

    /// <summary>
    /// <para>列出指定服务器的全部工具（"名称 — 描述" 形式）。</para>
    /// Lists all tools of the named server ("name — description" form).
    /// </summary>
    /// <param name="serverName">服务器名称。<para>Server name.</para></param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    public async Task<IReadOnlyList<string>> ListToolsAsync(string serverName, CancellationToken ct)
    {
        var config = FindConfig(serverName);
        var client = await GetClientAsync(config, ct).ConfigureAwait(false);
        var tools = await client.ListToolsAsync((ModelContextProtocol.Protocol.ListToolsRequestParams?)null, ct).ConfigureAwait(false);
        var names = new List<string>(tools.Tools.Count);
        foreach (var t in tools.Tools)
        {
            names.Add(string.IsNullOrWhiteSpace(t.Description) ? t.Name : $"{t.Name} — {t.Description}");
        }
        return names;
    }

    /// <summary>
    /// <para>调用指定服务器的工具并返回文本形式的结果内容。</para>
    /// Calls a tool on the named server and returns the text form of its result content.
    /// </summary>
    /// <param name="serverName">服务器名称。<para>Server name.</para></param>
    /// <param name="toolName">工具名称。<para>Tool name.</para></param>
    /// <param name="argumentsJson">工具实参 JSON（可为 <c>null</c> 表示无参数）。<para>Tool arguments JSON (may be <c>null</c> for none).</para></param>
    /// <param name="ct"><para>取消令牌。</para>Cancellation token.</param>
    public async Task<string> CallToolAsync(string serverName, string toolName, string? argumentsJson, CancellationToken ct)
    {
        var config = FindConfig(serverName);
        var client = await GetClientAsync(config, ct).ConfigureAwait(false);

        IReadOnlyDictionary<string, object?>? arguments = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            arguments = doc.RootElement.EnumerateObject()
                .ToDictionary(static p => p.Name, static p => (object?)p.Value.Clone());
        }

        var result = await client.CallToolAsync(toolName, arguments, progress: null, options: null, ct).ConfigureAwait(false);
        var text = ExtractContentText(result.Content);
        return text.Length > 0 ? text : "(no text content)";
    }

    /// <summary>
    /// <para>从结果内容块中提取文本（递归处理文本块与嵌套的工具结果块）。</para>
    /// Extracts text from result content blocks (recursively handling text blocks and nested
    /// tool-result blocks).
    /// </summary>
    private static string ExtractContentText(IEnumerable<ContentBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextContentBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                    sb.AppendLine(textBlock.Text);
                    break;
                case ToolResultContentBlock toolResult:
                    sb.Append(ExtractContentText(toolResult.Content));
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// <para>按名称查找启用的服务器配置；缺失或已禁用时抛出带明确提示的异常。</para>
    /// Finds the enabled server config by name; throws with a clear hint when missing or disabled.
    /// </summary>
    private static McpServerConfig FindConfig(string serverName)
    {
        var settings = SettingsPersistenceService.Instance.LoadSettings();
        var config = settings.McpServers.FirstOrDefault(s =>
            string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (config is null)
        {
            throw new InvalidOperationException($"MCP server '{serverName}' is not configured.");
        }

        if (!config.Enabled)
        {
            throw new InvalidOperationException($"MCP server '{serverName}' is disabled.");
        }

        return config;
    }

    /// <summary>
    /// <para>获取（或创建）指定服务器的客户端连接，按名称缓存。</para>
    /// Gets (or creates) the client connection for the named server, cached by name.
    /// </summary>
    private Task<McpClient> GetClientAsync(McpServerConfig config, CancellationToken ct)
    {
        return _clients.GetOrAdd(config.Name, _ => new Lazy<Task<McpClient>>(
            () => CreateClientAsync(config, ct), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// <para>按配置创建 MCP 客户端：stdio 启动本地进程，HTTP 连接远程端点。</para>
    /// Creates an MCP client per the config: stdio launches a local process, HTTP connects to a remote
    /// endpoint.
    /// </summary>
    private static async Task<McpClient> CreateClientAsync(McpServerConfig config, CancellationToken ct)
    {
        IClientTransport transport = config.Transport switch
        {
            McpTransportKind.Stdio => new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = config.Command,
                Arguments = string.IsNullOrWhiteSpace(config.Arguments)
                    ? null
                    : config.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            }),
            _ => new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url)
            })
        };

        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new() { Name = "UotanStudio", Version = "1.0.0" } },
            loggerFactory: null,
            ct).ConfigureAwait(false);
    }
}

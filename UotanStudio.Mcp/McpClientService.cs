using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace UotanStudio.Mcp;

/// <summary>
/// MCP client service: lazily establishes connections (stdio/HTTP) to external MCP servers from an
/// injected config list, letting callers list and call tools on those servers. AOT-safe — tool
/// arguments arrive as raw JSON strings and are parsed via <see cref="JsonDocument"/>, without
/// reflection.
/// <para>MCP 客户端服务：按注入的配置列表懒建立与外部 MCP 服务器的连接（stdio/HTTP），
/// 供调用方列出并调用其工具。AOT 兼容——工具实参以原始 JSON 字符串传入，
/// 由 <see cref="JsonDocument"/> 解析为字典，不引入反射。</para>
/// </summary>
public sealed class McpClientService
{
    /// <summary><para>全局单例。</para>The global singleton.</summary>
    /// <value>The shared client service holding every lazily opened connection to external MCP servers.
    /// <para>共享的客户端服务，持有到外部 MCP 服务器的全部懒建立连接。</para></value>
    public static McpClientService Instance { get; } = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients = new();
    private volatile IReadOnlyList<McpClientServerConfig> _configs = [];

    private McpClientService()
    {
    }

    /// <summary>
    /// Replaces the server config list and drops all cached connections.
    /// <para>替换服务器配置列表并清空全部缓存连接。</para>
    /// </summary>
    /// <param name="configs">
    /// Replacement set of server configs, read by name afterwards; the list is kept as supplied so the caller must not mutate it.
    /// <para>替换的服务器配置集合，后续按名称读取；列表按原样保存，调用方不得再修改。</para>
    /// </param>
    /// <returns>A task that completes once every cached connection is disposed and the new list is active.
    /// <para>在全部缓存连接释放且新列表生效之后完成的任务。</para></returns>
    public async Task SetServersAsync(IReadOnlyList<McpClientServerConfig> configs)
    {
        await ResetAsync().ConfigureAwait(false);
        _configs = configs;
    }

    /// <summary>
    /// Disposes all cached connections.
    /// <para>释放全部缓存连接。</para>
    /// </summary>
    /// <returns>
    /// A task that completes after every successfully opened client is disposed and the cache is emptied; connections still being established are abandoned without waiting.
    /// <para>在所有成功建立的客户端被释放且缓存清空之后完成的任务；仍在建立中的连接会被直接丢弃而不等待。</para>
    /// </returns>
    public async Task ResetAsync()
    {
        foreach (var lazy in _clients.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                var client = await lazy.Value.ConfigureAwait(false);
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        _clients.Clear();
    }

    /// <summary>
    /// Lists all tools of the named server as "name — description" entries.
    /// <para>列出指定服务器的全部工具（"名称 — 描述" 形式）。</para>
    /// </summary>
    /// <param name="serverName">Config name matched case-insensitively; the connection is opened on first use.
    /// <para>按不区分大小写匹配的配置名称；连接在首次使用时建立。</para></param>
    /// <param name="cancellationToken">Cancels connection setup and the list round trip.
    /// <para>取消连接建立与列举往返。</para></param>
    /// <returns>One entry per tool, using the bare name when the server supplied no description.
    /// <para>每个工具一项；服务器未提供描述时仅使用名称。</para></returns>
    /// <exception cref="InvalidOperationException">No config carries that name, or the matching server is disabled.
    /// <para>没有该名称的配置，或对应服务器已被禁用。</para></exception>
    public async Task<IReadOnlyList<string>> ListToolsAsync(string serverName, CancellationToken cancellationToken)
    {
        var config = FindConfig(serverName);
        var client = await GetClientAsync(config, cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var names = new List<string>(tools.Count);
        foreach (var tool in tools)
        {
            names.Add(string.IsNullOrWhiteSpace(tool.Description) ? tool.Name : $"{tool.Name} — {tool.Description}");
        }

        return names;
    }

    /// <summary>
    /// Calls a tool on the named server and returns the text form of its result content.
    /// <para>调用指定服务器的工具并返回文本形式的结果内容。</para>
    /// </summary>
    /// <example><code>
    /// string text = await McpClientService.Instance.CallToolAsync(
    ///     "docs", "search", """{"query":"payload"}""", CancellationToken.None);
    /// </code></example>
    /// <param name="serverName"><para>服务器名称。</para>Server name.</param>
    /// <param name="toolName"><para>工具名称。</para>Tool name.</param>
    /// <param name="argumentsJson"><para>工具实参 JSON（可为 <c>null</c> 表示无参数）。</para>Tool arguments JSON (may be <c>null</c> for none).</param>
    /// <param name="cancellationToken"><para>取消令牌。</para>Cancellation token.</param>
    /// <returns>The concatenated text of text blocks and embedded text resources, trimmed, or <c>"(no text content)"</c> when the result carried none.
    /// <para>文本块与内嵌文本资源拼接并去除尾端空白后的结果；结果不含文本时返回 <c>"(no text content)"</c>。</para></returns>
    /// <exception cref="InvalidOperationException">No config carries that name, the server is disabled, or <paramref name="argumentsJson"/> is a JSON value that is not an object.
    /// <para>没有该名称的配置、服务器已被禁用，或 <paramref name="argumentsJson"/> 不是 JSON 对象。</para></exception>
    /// <exception cref="JsonException"><paramref name="argumentsJson"/> is not valid JSON.
    /// <para><paramref name="argumentsJson"/> 不是合法 JSON。</para></exception>
    public async Task<string> CallToolAsync(string serverName, string toolName, string? argumentsJson, CancellationToken cancellationToken)
    {
        var config = FindConfig(serverName);
        var client = await GetClientAsync(config, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, object?>? arguments = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            using var document = JsonDocument.Parse(argumentsJson);
            arguments = document.RootElement.EnumerateObject()
                .ToDictionary(static p => p.Name, static p => (object?)p.Value.Clone());
        }

        var result = await client.CallToolAsync(toolName, arguments, progress: null, options: null, cancellationToken).ConfigureAwait(false);
        var text = ExtractContentText(result.Content);
        return text.Length > 0 ? text : "(no text content)";
    }

    /// <summary>
    /// Extracts text from result content blocks: text blocks and embedded text resources; binary
    /// blocks carry no text and are skipped.
    /// <para>从结果内容块中提取文本：读取 text 块与内嵌文本资源块；
    /// 二进制块（blob/图片/音频）不携带文本故跳过。</para>
    /// </summary>
    private static string ExtractContentText(IEnumerable<ContentBlock> blocks)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextContentBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                    builder.AppendLine(textBlock.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource } when !string.IsNullOrWhiteSpace(resource.Text):
                    builder.AppendLine(resource.Text);
                    break;
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Finds the enabled server config by name; throws with a clear hint when missing or disabled.
    /// <para>按名称查找启用的服务器配置；缺失或已禁用时抛出带明确提示的异常。</para>
    /// </summary>
    private McpClientServerConfig FindConfig(string serverName)
    {
        var config = _configs.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, serverName, StringComparison.OrdinalIgnoreCase));
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
    /// Gets (or creates) the client connection for the named server, cached by name.
    /// <para>获取（或创建）指定服务器的客户端连接，按名称缓存。</para>
    /// </summary>
    private Task<McpClient> GetClientAsync(McpClientServerConfig config, CancellationToken cancellationToken)
    {
        return _clients.GetOrAdd(config.Name, _ => new Lazy<Task<McpClient>>(
            () => CreateClientAsync(config, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// Creates an MCP client per the config: stdio launches a local process, HTTP connects to a
    /// remote endpoint.
    /// <para>按配置创建 MCP 客户端：stdio 启动本地进程，HTTP 连接远程端点。</para>
    /// </summary>
    private static async Task<McpClient> CreateClientAsync(McpClientServerConfig config, CancellationToken cancellationToken)
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
            cancellationToken).ConfigureAwait(false);
    }
}
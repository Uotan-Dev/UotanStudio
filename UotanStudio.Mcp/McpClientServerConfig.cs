namespace UotanStudio.Mcp;

/// <summary>
/// MCP server connection transport kind.
/// <para>MCP 服务器连接传输类型。</para>
/// </summary>
public enum McpTransportKind
{
    /// <summary><para>标准输入输出传输（本地进程）。</para>Stdio transport (local process).</summary>
    Stdio,

    /// <summary><para>HTTP(S) 传输（远程端点）。</para>HTTP(S) transport (remote endpoint).</summary>
    Http
}

/// <summary>
/// Connection config for one external MCP server, selected by name when listing or calling tools.
/// <para>外部 MCP 服务器的连接配置，列出/调用工具时按名称选择。</para>
/// </summary>
public sealed class McpClientServerConfig
{
    /// <summary><para>服务器名称（稳定标识）。</para>Server name (stable identifier).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><para>传输类型：stdio（本地命令）或 HTTP（远程地址）。</para>Transport kind: stdio (local command) or HTTP (remote endpoint).</summary>
    public McpTransportKind Transport { get; set; } = McpTransportKind.Http;

    /// <summary><para>stdio 传输的可执行命令。</para>Executable command for the stdio transport.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary><para>stdio 传输的命令行参数（以空格分隔；含空格参数请用引号包裹）。</para>Command-line arguments for the stdio transport (space-separated; quote arguments containing spaces).</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary><para>HTTP 传输的端点地址（如 <c>http://localhost:8080/mcp</c>）。</para>Endpoint URL for the HTTP transport (e.g. <c>http://localhost:8080/mcp</c>).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary><para>是否启用该服务器（禁用时调用直接失败并提示）。</para>Whether this server is enabled (calls fail with a hint while disabled).</summary>
    public bool Enabled { get; set; } = true;
}

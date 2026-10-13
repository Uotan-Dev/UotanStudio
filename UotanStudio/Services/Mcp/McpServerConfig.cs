namespace UotanStudio.Services.Mcp;

/// <summary>
/// MCP server connection transport kind.
/// <para>MCP 服务器连接传输类型。</para>
/// </summary>
public enum McpTransportKind
{
    /// <summary>Stdio transport (local process). <para>标准输入输出传输（本地进程）。</para></summary>
    Stdio,

    /// <summary>HTTP(S) transport (remote endpoint). <para>HTTP(S) 传输（远程端点）。</para></summary>
    Http
}

/// <summary>
/// MCP server connection settings for one server entry: name, transport, stdio command and HTTP endpoint.
/// <para>一个 MCP 服务器条目的连接配置：名称、传输类型、stdio 命令与 HTTP 端点。</para>
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>
    /// Server name.
    /// <para>服务器名称。</para>
    /// </summary>
    /// <value>The stable identifier of this server entry. <para>该服务器条目的稳定标识。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Transport kind: stdio (local command) or HTTP (remote endpoint).
    /// <para>传输类型：stdio（本地命令）或 HTTP（远程地址）。</para>
    /// </summary>
    /// <value>The transport used to reach this server. <para>访问该服务器所用的传输方式。</para></value>
    public McpTransportKind Transport { get; set; } = McpTransportKind.Http;

    /// <summary>
    /// Executable path for the stdio transport.
    /// <para>stdio 传输的可执行文件路径。</para>
    /// </summary>
    /// <value>The program launched for a stdio connection. <para>stdio 连接时启动的程序。</para></value>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Command-line arguments for the stdio transport (space-separated; quote arguments containing spaces).
    /// <para>stdio 传输的命令行参数（以空格分隔；含空格参数请用引号包裹）。</para>
    /// </summary>
    /// <value>The argument string passed to <see cref="Command"/>. <para>传给 <see cref="Command"/> 的参数串。</para></value>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// Endpoint URL for the HTTP transport.
    /// <para>HTTP 传输的端点地址。</para>
    /// </summary>
    /// <value>The HTTP(S) address of a remote server. <para>远程服务器的 HTTP(S) 地址。</para></value>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Whether this server is enabled.
    /// <para>该服务器是否启用。</para>
    /// </summary>
    /// <value>True when this server entry is enabled. <para>为真时该服务器条目处于启用状态。</para></value>
    public bool Enabled { get; set; } = true;
}
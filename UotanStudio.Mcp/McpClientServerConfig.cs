namespace UotanStudio.Mcp;

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
/// Connection config for one external MCP server, selected by name when listing or calling tools.
/// <para>外部 MCP 服务器的连接配置，列出/调用工具时按名称选择。</para>
/// </summary>
public sealed class McpClientServerConfig
{
    /// <summary>Server name (stable identifier). <para>服务器名称（稳定标识）。</para></summary>
    /// <value>The identifier matched case-insensitively when tools are listed or called.
    /// <para>列出或调用工具时以不区分大小写方式匹配的标识符。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Transport kind: stdio (local command) or HTTP (remote endpoint). <para>传输类型：stdio（本地命令）或 HTTP（远程地址）。</para></summary>
    /// <value>Selects which connection fields are read: <c>Command</c>/<c>Arguments</c> for stdio, <c>Url</c> for HTTP.
    /// <para>决定读取哪组连接字段：stdio 使用 <c>Command</c>/<c>Arguments</c>，HTTP 使用 <c>Url</c>。</para></value>
    public McpTransportKind Transport { get; set; } = McpTransportKind.Http;

    /// <summary>Executable command for the stdio transport. <para>stdio 传输的可执行命令。</para></summary>
    /// <value>Program launched as a child process that speaks MCP over its standard streams; ignored by the HTTP transport.
    /// <para>作为子进程启动、通过标准输入输出讲 MCP 的程序；HTTP 传输忽略该值。</para></value>
    public string Command { get; set; } = string.Empty;

    /// <summary>Command-line arguments for the stdio transport (space-separated; quote arguments containing spaces). <para>stdio 传输的命令行参数（以空格分隔；含空格参数请用引号包裹）。</para></summary>
    /// <value>The raw argument string split on whitespace when the process starts; empty means no arguments.
    /// <para>启动进程时按空白拆分的原始参数字符串；为空表示无参数。</para></value>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Endpoint URL for the HTTP transport (e.g. <c>http://localhost:8080/mcp</c>). <para>HTTP 传输的端点地址（如 <c>http://localhost:8080/mcp</c>）。</para></summary>
    /// <value>Absolute URI parsed into the HTTP client endpoint; ignored by the stdio transport.
    /// <para>会被解析为 HTTP 客户端端点的绝对 URI；stdio 传输忽略该值。</para></value>
    public string Url { get; set; } = string.Empty;

    /// <summary>Whether this server is enabled (calls fail with a hint while disabled). <para>是否启用该服务器（禁用时调用直接失败并提示）。</para></summary>
    /// <value><see langword="true"/> to allow listing and calling tools; <see langword="false"/> makes every lookup for this name fail with a disabled hint.
    /// <para>为 <see langword="true"/> 时允许列出并调用工具；为 <see langword="false"/> 时该名称的每次查找都以“已禁用”提示失败。</para></value>
    public bool Enabled { get; set; } = true;
}
using UotanStudio.Mcp.Tools;

namespace UotanStudio.Services.Mcp;

/// <summary>
/// <para>Writes every MCP tool call served inside this process into the unified log: the protocol name,
/// the arguments exactly as the client sent them, the outcome with elapsed time, and the summary the tool
/// returned. Failures are raised one level above successes so a refused or faulted call stands out while
/// read-only traffic stays readable.</para>
/// <para>把本进程内处理的每次 MCP 工具调用写入统一日志：协议名、客户端原样传入的参数、耗时与结果，以及工具返回的摘要。
/// 失败级别高于成功，使被拒绝或出错的调用可以被一眼看出，只读流量仍保持可读。</para>
/// <para>Arguments are recorded verbatim, including pairing passwords and key paths a client passes in;
/// anything that can read the global log can read those values.</para>
/// <para>参数按原样记录，包含客户端传入的配对密码与密钥路径；能读取全局日志者即能读到这些值。</para>
/// </summary>
public sealed class McpToolCallLog : IMcpToolCallAudit
{
    /// <summary>
    /// <para>The shared instance; the log service is a process singleton, so one sink serves every transport.</para>
    /// <para>共享实例；统一日志服务是进程单例，一个接收端即可服务各传输。</para>
    /// </summary>
    /// <value>The instance wired into the companion stdio server and the embedded HTTP listener.
    /// <para>装配到伴随式 stdio 服务器与内嵌 HTTP 监听的实例。</para></value>
    public static McpToolCallLog Instance { get; } = new();

    private const string Source = "McpTool";

    private McpToolCallLog()
    {
    }

    /// <summary>
    /// <para>Appends one tool call to the unified log, at warning level when the tool reported failure.</para>
    /// <para>向统一日志追加一条工具调用；工具报告失败时使用警告级别。</para>
    /// </summary>
    /// <param name="record">The observed call, whose arguments are logged verbatim.
    /// <para>被观察到的调用，其参数按原样写入日志。</para></param>
    public void RecordToolCall(McpToolCallRecord record)
    {
        LogService.Instance.Write(
            record.Success ? LogLevel.Info : LogLevel.Warn,
            Source,
            $"{record.ToolName} {record.Arguments} -> {(record.Success ? "ok" : "failed")} "
            + $"in {record.Duration.TotalMilliseconds:F0} ms: {record.Summary}");
    }
}
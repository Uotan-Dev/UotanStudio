using ModelContextProtocol.Protocol;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Single execution wrapper every MCP tool delegate runs through, so cancellation is observed,
/// failures are reported on the protocol error channel, and the specific cause survives.
/// <para>所有 MCP 工具委托统一经过的执行包装，使取消被观察到、失败进入协议错误通道、具体原因不被吞掉。</para>
/// The SDK replaces the message of any exception type other than <see cref="ModelContextProtocol.McpException"/>
/// with a generic error text, which would make every handler failure indistinguishable on the wire.
/// Catching here and converting to <see cref="ToolResult"/> keeps the exception type and message
/// visible while still setting <see cref="CallToolResult.IsError"/>.
/// <para>SDK 会把非 <see cref="ModelContextProtocol.McpException"/> 异常的消息替换为通用错误文本，使所有处理器失败在线上无法区分。
/// 在此捕获并转为 <see cref="ToolResult"/>，既保留异常类型与消息，又设置 <see cref="CallToolResult.IsError"/>。</para>
/// </summary>
internal static class ToolGuard
{
    /// <summary>
    /// Runs one tool body, mapping its envelope and any failure into a protocol result.
    /// <para>执行单个工具主体，把其结果信封与任何失败映射为协议结果。</para>
    /// </summary>
    /// <param name="body">Tool implementation receiving the request's cancellation token.
    /// <para>接收本次请求取消令牌的工具实现。</para></param>
    /// <param name="cancellationToken">Token bound by the MCP server from the in-flight request; cancelling it
    /// surfaces as a cancellation result rather than a failure.
    /// <para>由 MCP 服务器从进行中的请求绑定的令牌；取消后表现为取消结果而非失败。</para></param>
    /// <returns>The protocol result carrying the text envelope, the error flag, and structured data.
    /// <para>携带文本信封、错误标志与结构化数据的协议结果。</para></returns>
    internal static async Task<CallToolResult> RunAsync(
        Func<CancellationToken, Task<ToolResult>> body,
        CancellationToken cancellationToken)
    {
        ToolResult result;
        try
        {
            result = await body(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new ToolResult(false, "The operation was canceled by the client.");
        }
        catch (Exception ex)
        {
            result = ToolResult.FromException(ex);
        }

        return result.ToResult();
    }
}
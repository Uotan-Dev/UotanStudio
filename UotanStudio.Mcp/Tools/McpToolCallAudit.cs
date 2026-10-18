using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// <para>One observed MCP tool call: the invoked protocol name, the client arguments, whether the tool reported success, the human-readable summary it returned, and how long the call took. The shared call-tool pipeline hands the same shape to <see cref="IMcpToolCallAudit"/> for every transport.</para>
/// <para>一次被观察到的 MCP 工具调用：被调用的协议名、客户端参数、工具是否报告成功、返回的可读摘要与调用耗时。共用的工具调用流水线把同一形状交给 <see cref="IMcpToolCallAudit"/>，各传输一致。</para>
/// </summary>
/// <param name="ToolName">Protocol name of the invoked tool. <para>被调用工具的协议名称。</para></param>
/// <param name="Arguments">The client arguments as one JSON object, recorded verbatim; <c>{}</c> when the request carried none. <para>以单个 JSON 对象原样记录的客户端参数；请求未带参数时为 <c>{}</c>。</para></param>
/// <param name="Success"><see langword="true"/> unless the result carried the protocol error flag.
/// <para>除非结果带有协议错误标志，否则为 <see langword="true"/>。</para></param>
/// <param name="Summary">The envelope summary text for a tool result, or the plain content text for a result the tool pipeline did not produce.
/// <para>工具结果取信封摘要；非工具流水线产生的结果取纯文本内容。</para></param>
/// <param name="Duration">Elapsed time between entering and leaving the tool pipeline. <para>进入与离开工具流水线之间的耗时。</para></param>
public sealed record McpToolCallRecord(
    string ToolName,
    string Arguments,
    bool Success,
    string Summary,
    TimeSpan Duration);

/// <summary>
/// <para>Sink for every MCP tool call the server serves. The desktop host installs an implementation so
/// tool calls leave a trace in the application log; a transport built without a sink records nothing, so
/// observation is opt-in per server instance rather than process-wide.</para>
/// <para>MCP 工具调用的接收端。桌面宿主提供实现，使工具调用在应用日志中留下痕迹；未给出接收端的传输不记录任何内容，
/// 因此观察按服务器实例启用，而不是进程级默认开启。</para>
/// </summary>
public interface IMcpToolCallAudit
{
    /// <summary>
    /// <para>Reports one completed tool call. Implementations must not throw: the call result is returned
    /// to the client regardless of how the sink behaves, and a throwing sink is absorbed by the pipeline.</para>
    /// <para>上报一次已完成的工具调用。实现不得抛出异常：调用结果始终返回给客户端，抛出异常的接收端会被流水线吸收。</para>
    /// </summary>
    /// <param name="record">The observed call. <para>被观察到的调用。</para></param>
    void RecordToolCall(McpToolCallRecord record);
}

/// <summary>
/// <para>Turns <see cref="IMcpToolCallAudit"/> into a call-tool pipeline filter, the single place where
/// every transport sees the tool name, the client arguments and the produced
/// <see cref="CallToolResult"/> together. Handlers already convert their own failures into error results in
/// <see cref="ToolGuard"/>, so this filter only observes the returned result.</para>
/// <para>把 <see cref="IMcpToolCallAudit"/> 变成工具调用流水线过滤器，这是各传输唯一同时看到工具名、客户端参数与
/// 所得 <see cref="CallToolResult"/> 的位置。处理器已在 <see cref="ToolGuard"/> 中把自身失败转为错误结果，
/// 因此本过滤器只观察返回的结果。</para>
/// </summary>
internal static class McpToolCallAuditFilter
{
    /// <summary>
    /// <para>Builds the pipeline filter that records one call per tool invocation.</para>
    /// <para>构造按每次工具调用记录一条流水的流水线过滤器。</para>
    /// </summary>
    /// <param name="audit">Sink receiving every observed call. <para>接收每次被观察调用的接收端。</para></param>
    /// <returns>A filter to add to <see cref="McpRequestFilters.CallToolFilters"/>.
    /// <para>加入 <see cref="McpRequestFilters.CallToolFilters"/> 的过滤器。</para></returns>
    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(
        IMcpToolCallAudit audit) => next => async (request, cancellationToken) =>
    {
        var started = Stopwatch.GetTimestamp();
        var result = await next(request, cancellationToken).ConfigureAwait(false);
        var record = new McpToolCallRecord(
            request.Params?.Name ?? string.Empty,
            FormatArguments(request.Params?.Arguments),
            result.IsError is not true,
            ReadSummary(result),
            Stopwatch.GetElapsedTime(started));
        try
        {
            audit.RecordToolCall(record);
        }
        catch (Exception)
        {
        }

        return result;
    };

    /// <summary>
    /// <para>Writes the argument map back out as JSON without reflection, keeping every value byte-for-byte
    /// what the client sent.</para>
    /// <para>不经反射把参数表重新写成 JSON，使每个取值与客户端发来的内容逐字节一致。</para>
    /// </summary>
    /// <param name="arguments">Arguments from the request, or <see langword="null"/> when the client sent none.
    /// <para>请求中的参数；客户端未带参数时为 <see langword="null"/>。</para></param>
    /// <returns>The JSON object text; <c>{}</c> for an absent or empty map.
    /// <para>JSON 对象文本；缺省或空表时为 <c>{}</c>。</para></returns>
    private static string FormatArguments(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is not { Count: > 0 })
        {
            return "{}";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// <para>Reads the summary field of the tool envelope. Results the tool pipeline did not produce — an
    /// unknown tool name, for instance — carry no envelope, so their text content is reported instead.</para>
    /// <para>读取工具信封的摘要字段。并非工具流水产生的结果（例如未知的工具名）不带信封，因此改为上报其文本内容。</para>
    /// </summary>
    /// <param name="result">The result returned to the client. <para>返回给客户端的结果。</para></param>
    /// <returns>The summary or, failing that, the concatenated text blocks. <para>摘要；取不到时为拼接后的文本块。</para></returns>
    private static string ReadSummary(CallToolResult result)
    {
        var text = result.Content is { } blocks
            ? string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text))
            : string.Empty;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("summary", out var summary)
                && summary.ValueKind == JsonValueKind.String)
            {
                return summary.GetString() ?? text;
            }
        }
        catch (JsonException)
        {
        }

        return text;
    }
}
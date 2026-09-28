using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Shared JSON serialization and result shaping for all MCP tool factories.
/// <para>所有 MCP 工具工厂共用的 JSON 序列化与结果整形。</para>
/// The desktop host is NativeAOT published, so the reflection resolver is unavailable;
/// payloads are concrete records registered in <see cref="McpToolJsonContext"/>.
/// <para>桌面宿主按 NativeAOT 发布，反射解析器不可用；负载是注册进
/// <see cref="McpToolJsonContext"/> 的具体记录类型。</para>
/// </summary>
internal static class ToolJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateFrozenOptions();

    private static readonly IJsonTypeInfoResolver Resolver = McpToolJsonContext.Default;

    private static readonly JsonTypeInfo<ToolResult> ResultTypeInfo =
        RequireTypeInfo<ToolResult>(ResolverResolve(typeof(ToolResult)));

    private static JsonSerializerOptions CreateFrozenOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = McpToolJsonContext.Default
        };
        options.MakeReadOnly();
        return options;
    }

    private static JsonTypeInfo? ResolverResolve(Type type) =>
        Resolver.GetTypeInfo(type, SerializerOptions);

    /// <summary>
    /// Serializes a result payload as the tool's JSON text output.
    /// <para>把结果负载序列化为工具的 JSON 文本输出。</para>
    /// </summary>
    public static string Serialize(ToolResult result) => JsonSerializer.Serialize(result, ResultTypeInfo);

    /// <summary>
    /// Converts a concrete payload into the envelope's JSON data without reflection.
    /// <para>不经反射把具体负载转换为信封的 JSON 数据。</para>
    /// </summary>
    public static JsonElement Data<T>(T value) =>
        JsonSerializer.SerializeToElement(value, RequireTypeInfo<T>(ResolverResolve(typeof(T))));

    private static JsonTypeInfo<T> RequireTypeInfo<T>(JsonTypeInfo? typeInfo) =>
        (JsonTypeInfo<T>?)typeInfo
        ?? throw new InvalidOperationException(
            $"Type {typeof(T).Name} is not registered in {nameof(McpToolJsonContext)}.");
}

/// <summary>
/// Compact tool result envelope: success flag plus a human-readable summary and structured data.
/// <para>紧凑的工具结果信封：成功标志、可读摘要与结构化数据。</para>
/// </summary>
/// <remarks>
/// <see cref="ToResult"/> publishes the envelope twice: the JSON text stays for clients that already
/// parse it, while <see cref="ModelContextProtocol.Protocol.CallToolResult.IsError"/> and
/// <see cref="ModelContextProtocol.Protocol.CallToolResult.StructuredContent"/> give conforming clients
/// an authoritative failure flag and typed data without string parsing.
/// <para><see cref="ToResult"/> 双写该信封：JSON 文本保留给已按文本解析的客户端，而
/// <see cref="ModelContextProtocol.Protocol.CallToolResult.IsError"/> 与
/// <see cref="ModelContextProtocol.Protocol.CallToolResult.StructuredContent"/> 让合规客户端获得权威的失败标志
/// 与无需解析字符串的结构化数据。</para>
/// </remarks>
internal sealed record ToolResult(bool Success, string Summary, JsonElement? Data = null)
{
    /// <summary><para>把工具结果映射为协议结果。</para>Maps the envelope onto the protocol tool result.</summary>
    /// <returns><para>带文本信封、错误标志与结构化数据的协议结果。</para>A protocol result carrying the text
    /// envelope, the error flag, and the structured data payload.</returns>
    internal CallToolResult ToResult() => new()
    {
        Content = [new TextContentBlock { Text = ToolJson.Serialize(this) }],
        IsError = !Success,
        StructuredContent = Data
    };

    /// <summary>
    /// <para>把异常的类型与消息保留为失败结果；SDK 会吞掉非 <see cref="ModelContextProtocol.McpException"/> 的消息。</para>
    /// Preserves the exception type and message as a failure result; the SDK discards the message of any
    /// exception type other than <see cref="ModelContextProtocol.McpException"/>.
    /// </summary>
    /// <param name="exception"><para>处理过程中抛出的异常。</para>The exception raised while running the tool.</param>
    /// <returns><para>摘要为「类型: 消息」的失败结果。</para>A failure result whose summary is the type and message.</returns>
    internal static ToolResult FromException(Exception exception) =>
        new(false, $"{exception.GetType().Name}: {exception.Message}");

    /// <summary>Maps a <see cref="FirmwareOpResult"/> into the envelope, preserving entries/metadata. <para>把 <see cref="FirmwareOpResult"/> 映射进信封，保留条目与元数据。</para></summary>
    public static ToolResult From(FirmwareOpResult op) => new(op.Success, op.Summary,
        op.Entries.Count == 0 && op.Metadata.Count == 0 && op.Components.Count == 0
            ? null
            : ToolJson.Data(new FirmwareData(op.Entries, op.Metadata, op.Components)));
}
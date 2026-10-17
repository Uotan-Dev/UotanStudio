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

    private static readonly JsonTypeInfo<ToolResult> ResultTypeInfo =
        (JsonTypeInfo<ToolResult>)SerializerOptions.GetTypeInfo(typeof(ToolResult));

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

    /// <summary>
    /// Serializes a result payload as the tool's JSON text output.
    /// <para>把结果负载序列化为工具的 JSON 文本输出。</para>
    /// </summary>
    /// <param name="result">
    /// Envelope written with camelCase names and without null properties; the optional
    /// <see cref="ToolResult.Data"/> is omitted when it is <see langword="null"/>.
    /// <para>以 camelCase 属性名写出并省略 null 属性的信封；可选的 <see cref="ToolResult.Data"/> 为 <see langword="null"/> 时不输出。</para>
    /// </param>
    public static string Serialize(ToolResult result) => JsonSerializer.Serialize(result, ResultTypeInfo);

    /// <summary>
    /// Converts a concrete payload into the envelope's JSON data without reflection.
    /// <para>不经反射把具体负载转换为信封的 JSON 数据。</para>
    /// </summary>
    /// <param name="value">Payload record registered in <see cref="McpToolJsonContext"/>.
    /// <para>已注册进 <see cref="McpToolJsonContext"/> 的负载记录。</para></param>
    /// <typeparam name="T">Concrete payload type; its metadata must exist in the source-generated context.
    /// <para>具体负载类型；其元数据必须存在于源生成上下文中。</para></typeparam>
    /// <returns>The payload as a detached JSON element. <para>作为独立 JSON 元素的负载。</para></returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> has no generated metadata.
    /// <para><typeparamref name="T"/> 没有源生成元数据。</para></exception>
    public static JsonElement Data<T>(T value)
    {
        if (!SerializerOptions.TryGetTypeInfo(typeof(T), out JsonTypeInfo? info)
            || info is not JsonTypeInfo<T> typeInfo)
        {
            throw new InvalidOperationException(
                $"Type {typeof(T).Name} is not registered in {nameof(McpToolJsonContext)}.");
        }

        return JsonSerializer.SerializeToElement(value, typeInfo);
    }
}

/// <summary>
/// Compact tool result envelope: success flag plus a human-readable summary and structured data.
/// <para>紧凑的工具结果信封：成功标志、可读摘要与结构化数据。</para>
/// </summary>
/// <remarks>
/// <see cref="ToResult"/> publishes the envelope twice: the JSON text stays for clients that already
/// parse it, while <see cref="CallToolResult.IsError"/> and
/// <see cref="CallToolResult.StructuredContent"/> give conforming clients
/// an authoritative failure flag and typed data without string parsing.
/// <para><see cref="ToResult"/> 双写该信封：JSON 文本保留给已按文本解析的客户端，而
/// <see cref="CallToolResult.IsError"/> 与
/// <see cref="CallToolResult.StructuredContent"/> 让合规客户端获得权威的失败标志
/// 与无需解析字符串的结构化数据。</para>
/// </remarks>
internal sealed record ToolResult(bool Success, string Summary, JsonElement? Data = null)
{
    /// <summary>Maps the envelope onto the protocol tool result. <para>把工具结果映射为协议结果。</para></summary>
    /// <returns><para>带文本信封、错误标志与结构化数据的协议结果。</para>A protocol result carrying the text
    /// envelope, the error flag, and the structured data payload.</returns>
    internal CallToolResult ToResult() => new()
    {
        Content = [new TextContentBlock { Text = ToolJson.Serialize(this) }],
        IsError = !Success,
        StructuredContent = Data
    };

    /// <summary>
    /// Preserves the exception type and message as a failure result; the SDK discards the message of any
    /// exception type other than <see cref="ModelContextProtocol.McpException"/>.
    /// <para>把异常的类型与消息保留为失败结果；SDK 会吞掉非 <see cref="ModelContextProtocol.McpException"/> 的消息。</para>
    /// </summary>
    /// <param name="exception">The exception raised while running the tool. <para>处理过程中抛出的异常。</para></param>
    /// <returns>A failure result whose summary is the type and message. <para>摘要为「类型: 消息」的失败结果。</para></returns>
    internal static ToolResult FromException(Exception exception) =>
        new(false, $"{exception.GetType().Name}: {exception.Message}");

    /// <summary>Maps a <see cref="FirmwareOpResult"/> into the envelope, preserving entries/metadata. <para>把 <see cref="FirmwareOpResult"/> 映射进信封，保留条目与元数据。</para></summary>
    /// <param name="op">
    /// Operation result whose success flag and summary become the envelope fields; entries,
    /// metadata and components are attached as structured data only when at least one of them
    /// is non-empty.
    /// <para>操作结果：其成功标志与摘要写入信封字段；只有条目、元数据、组件中至少一项非空时才附带结构化数据。</para>
    /// </param>
    public static ToolResult From(FirmwareOpResult op) => new(op.Success, op.Summary,
        op.Entries.Count == 0 && op.Metadata.Count == 0 && op.Components.Count == 0
            ? null
            : ToolJson.Data(new FirmwareData(op.Entries, op.Metadata, op.Components)));
}
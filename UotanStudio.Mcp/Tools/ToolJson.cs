using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UotanStudio.Firmware.SharedKernel;

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
internal sealed record ToolResult(bool Success, string Summary, JsonElement? Data = null)
{
    /// <summary>Maps a <see cref="FirmwareOpResult"/> into the envelope, preserving entries/metadata. <para>把 <see cref="FirmwareOpResult"/> 映射进信封，保留条目与元数据。</para></summary>
    public static ToolResult From(FirmwareOpResult op) => new(op.Success, op.Summary,
        op.Entries.Count == 0 && op.Metadata.Count == 0 && op.Components.Count == 0
            ? null
            : ToolJson.Data(new FirmwareData(op.Entries, op.Metadata, op.Components)));
}

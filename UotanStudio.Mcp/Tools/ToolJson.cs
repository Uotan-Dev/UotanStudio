using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Shared JSON serialization and result shaping for all MCP tool factories.
/// <para>所有 MCP 工具工厂共用的 JSON 序列化与结果整形。</para>
/// The desktop host runs with <c>JsonSerializer.IsReflectionEnabledByDefault=false</c> (the
/// <c>PublishAot</c> runtime config), so the resolver must be configured explicitly; tool payloads are
/// runtime-shaped anonymous objects that no source-generated context can describe.
/// <para>桌面宿主带有 <c>PublishAot</c> 的运行时配置
/// （<c>JsonSerializer.IsReflectionEnabledByDefault=false</c>），因此必须显式配置解析器；
/// 工具负载是运行时构造的匿名对象，源生成上下文无法描述。</para>
/// </summary>
internal static class ToolJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    /// Serializes a result payload as the tool's JSON text output.
    /// <para>把结果负载序列化为工具的 JSON 文本输出。</para>
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MCP results intentionally carry runtime-shaped anonymous payloads; the MCP assembly is not a native-AOT boundary.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MCP results intentionally carry runtime-shaped anonymous payloads; the MCP assembly is not a native-AOT boundary.")]
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>
/// Compact tool result envelope: success flag plus a human-readable summary and structured data.
/// <para>紧凑的工具结果信封：成功标志、可读摘要与结构化数据。</para>
/// </summary>
internal sealed record ToolResult(bool Success, string Summary, object? Data = null)
{
    /// <summary>Maps a <see cref="FirmwareOpResult"/> into the envelope, preserving entries/metadata. <para>把 <see cref="FirmwareOpResult"/> 映射进信封，保留条目与元数据。</para></summary>
    public static ToolResult From(FirmwareOpResult op) => new(op.Success, op.Summary,
        op.Entries.Count == 0 && op.Metadata.Count == 0 && op.Components.Count == 0
            ? null
            : new FirmwareData(op.Entries, op.Metadata, op.Components));
}

internal sealed record FirmwareData(
    IReadOnlyList<string> Entries,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<FirmwareComponent> Components);
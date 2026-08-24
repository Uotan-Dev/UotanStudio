using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Services;

namespace UotanStudio.Core.Flow;

/// <summary>
/// <para>.uotflow 流程文档的 JSON 序列化服务，AOT 兼容。</para>
/// JSON serialization service for .uotflow flow documents; AOT compatible.
/// </summary>
public static class FlowSerializer
{
    /// <summary>
    /// <para>将流程文档序列化为 JSON 字符串。</para>
    /// Serializes a flow document into a JSON string.
    /// </summary>
    /// <param name="document">
    /// <para>要序列化的流程文档。</para>
    /// The flow document to serialize.
    /// </param>
    /// <returns>
    /// <para>序列化后的 JSON 文本。</para>
    /// The serialized JSON text.
    /// </returns>
    public static string Serialize(FlowDocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, FlowJsonContext.Default.FlowDocumentDto);
    }

    /// <summary>
    /// <para>从 JSON 文本反序列化流程文档；输入无效时返回 null。</para>
    /// Deserializes a flow document from JSON text; returns null for invalid input.
    /// </summary>
    /// <param name="json">
    /// <para>JSON 文本。</para>
    /// The JSON text.
    /// </param>
    /// <returns>
    /// <para>反序列化的流程文档，失败时为 null。</para>
    /// The deserialized flow document, or null on failure.
    /// </returns>
    public static FlowDocumentDto? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, FlowJsonContext.Default.FlowDocumentDto);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "FlowSerializer", $"Deserialize flow failed: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// <para><see cref="FlowDocumentDto"/> 的 JSON 序列化上下文，AOT 兼容。</para>
/// JSON serialization context for <see cref="FlowDocumentDto"/>; AOT compatible.
/// </summary>
[JsonSerializable(typeof(FlowDocumentDto))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class FlowJsonContext : JsonSerializerContext;
using System.Text.Json;
using UotanStudio.Services.Delivery;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// JSON entry for delivery plans on the MCP boundary, reusing the delivery library's
/// AOT source-generated context so wire format and host persistence stay identical.
/// <para>MCP 边界上交付计划的 JSON 入口，复用交付库的 AOT source-gen 上下文，
/// 保证线上格式与宿主持久化一致。</para>
/// </summary>
internal static class DeliveryPlanJson
{
    /// <summary>
    /// Deserializes a delivery plan; returns null for structurally invalid JSON.
    /// <para>反序列化交付计划；结构非法的 JSON 返回 null。</para>
    /// </summary>
    public static DeliveryPlan? Deserialize(string json)
        => JsonSerializer.Deserialize(json, DeliveryJsonContext.Default.DeliveryPlan);
}
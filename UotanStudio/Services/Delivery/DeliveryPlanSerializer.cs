using System.Text.Json;
using UotanStudio.Delivery;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// Serializes a delivery plan onto the facade JSON contract through a source-generated context,
/// avoiding reflection-based serialization that breaks under trimming.
/// <para>经源生成上下文把交付计划序列化到门面 JSON 契约，避免裁剪下会失效的反射序列化。</para>
/// </summary>
internal static class DeliveryPlanSerializer
{
    /// <summary>
    /// Serializes a delivery plan into the JSON text the facade parses.
    /// <para>序列化交付计划为门面可解析的 JSON 文本。</para>
    /// </summary>
    /// <param name="plan">The delivery plan to serialize. <para>要序列化的交付计划。</para></param>
    /// <returns>The serialized plan JSON. <para>序列化后的计划 JSON。</para></returns>
    internal static string ToJson(DeliveryPlan plan)
        => JsonSerializer.Serialize(plan, DeliveryJsonContext.Default.DeliveryPlan);
}
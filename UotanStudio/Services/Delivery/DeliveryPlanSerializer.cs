using System.Text.Json;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// <para>把界面采集的交付计划序列化回门面 JSON 契约，使用交付子项目的源生成上下文，避免反射序列化在
/// 裁剪与 NativeAOT 下失效。集中这一处调用，界面与无头入口共用同一契约。</para>
/// Serializes the UI-captured delivery plan back onto the facade JSON contract through the delivery
/// project's source-generated context, avoiding reflection-based serialization that breaks under trimming
/// and NativeAOT. This single call site is shared by the UI and the headless entries.</summary>
internal static class DeliveryPlanSerializer
{
    /// <summary>
    /// <para>序列化交付计划为门面可解析的 JSON 文本。</para>
    /// Serializes a delivery plan into the JSON text the facade parses.
    /// </summary>
    /// <param name="plan"><para>界面捕获的交付计划。</para>The delivery plan captured from the UI.</param>
    /// <returns><para>序列化后的计划 JSON。</para>The serialized plan JSON.</returns>
    internal static string ToJson(DeliveryPlan plan)
        => JsonSerializer.Serialize(plan, DeliveryJsonContext.Default.DeliveryPlan);
}
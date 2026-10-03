using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Renders a firehose rejection as user-facing text, preferring the evidence-backed error
/// classification over the bare response value.
/// <para>把 firehose 的拒绝响应渲染成面向用户的文本，优先使用有证据支撑的错误分类，而不是裸响应值。</para>
/// </summary>
internal static class EdlFailureText
{
    /// <summary>
    /// Gets the display text for one rejected response.
    /// <para>获取一次拒绝的展示文本。</para>
    /// </summary>
    /// <param name="response">The parsed response. <para>已解析的响应。</para></param>
    /// <returns>The classified description together with its kind and observed evidence, the raw value
    /// when nothing was classified, or a placeholder when the response carries no text at all.
    /// <para>分类描述连同其类别与观测到的取值；未分类时返回原始值，响应不含文本时返回占位文本。</para></returns>
    internal static string Describe(FirehoseResponse response) =>
        response.Error is { } error
            ? $"{error.Description} ({error})"
            : response.RawValue ?? "no response value reported";
}
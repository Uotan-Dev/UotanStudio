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
    /// <returns>The classified description with its kind and evidence, the raw value, or a placeholder.
    /// <para>分类描述连同其类别与观测取值；未分类时为原始值，无文本时为占位文本。</para></returns>
    internal static string Describe(FirehoseResponse response) =>
        response.Error is { } error
            ? $"{error.Description} ({error})"
            : response.RawValue ?? "no response value reported";

    /// <summary>
    /// Gets the localized user-facing text for a typed failure kind; kinds without a dedicated phrase
    /// return <see langword="null"/> so the caller keeps its own exception rendering.
    /// <para>获取类型化失败种类的面向用户本地化文本；没有专用短语的种类返回
    /// <see langword="null"/>，由调用方保留自身的异常渲染。</para>
    /// </summary>
    /// <param name="kind">The classified failure kind. <para>已分类的失败种类。</para></param>
    /// <returns>The localized phrase, or <see langword="null"/> when the kind has none.
    /// <para>本地化短语；该种类没有时为 <see langword="null"/>。</para></returns>
    internal static string? Describe(EdlFailureKind kind) => kind switch
    {
        EdlFailureKind.ProgrammerImageMismatch => LanguageService.Instance.GetString("Flash_EdlProgrammerImageMismatch"),
        _ => null
    };
}
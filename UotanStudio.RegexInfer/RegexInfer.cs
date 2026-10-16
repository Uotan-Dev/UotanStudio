using UotanStudio.RegexInfer.PatternTrace;

namespace UotanStudio.RegexInfer;

/// <summary>
/// Deterministic regular-expression inference from input/output examples, following the Flash Fill
/// family of algorithms: every example pair is reduced to a small set of partition traces, the traces are
/// merged into one fragment trie, and the generalised best path through the trie is the inferred pattern.
/// <para>从输入/输出样例确定性推导正则表达式，沿用 Flash Fill 系列算法：先把每对样例归约为少量划分轨迹，
/// 再把轨迹合并成一棵片段字典树，字典树上泛化出的最优路径即推导出的正则。</para>
/// </summary>
/// <remarks>
/// <para>
/// Only patterns that were validated against the .NET regex engine before being returned are emitted, and
/// only candidates that matched every positive example exactly are kept. A candidate that still matches a
/// supplied negative example is reported with a non-zero <see cref="InferredRegex.NegativeLeaks"/> instead
/// of being silently dropped, so callers can judge whether the residual ambiguity is acceptable.
/// </para>
/// <para>
/// 本库只输出在返回前已通过 .NET 正则引擎验证、且精确命中全部正例的候选。仍然匹配所提供负例的候选会带上
/// 非零的 <see cref="InferredRegex.NegativeLeaks"/> 报告出来，而不是被静默丢弃，由调用方判断残留歧义是否可接受。
/// </para>
/// </remarks>
public static class RegexInfer
{
    /// <summary>
    /// Infers a regular expression that extracts the expected outputs from the source text.
    /// <para>推导一个能从源文本中提取出全部期望输出的正则表达式。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="expectedOutputs">
    /// The strings that must be matched exactly, each of which occurs somewhere in <paramref name="sourceText"/>.
    /// <para>必须被精确匹配的字符串，每个都应出现在 <paramref name="sourceText"/> 中某处。</para>
    /// </param>
    /// <param name="options">
    /// Tuning knobs; pass a populated <see cref="RegexInferenceOptions"/> to override the defaults.
    /// <para>可调参数；传入填写好的 <see cref="RegexInferenceOptions"/> 即可覆盖默认值。</para>
    /// </param>
    /// <returns>
    /// Candidates ordered by descending confidence, at most <see cref="RegexInferenceOptions.TopResults"/>
    /// of them. The list is empty when no candidate matched every positive example exactly.
    /// <para>按置信度降序排列的候选，最多 <see cref="RegexInferenceOptions.TopResults"/> 个。没有任何候选精确命中全部正例时为空列表。</para>
    /// </returns>
    public static IReadOnlyList<InferredRegex> Infer(
        string sourceText,
        IReadOnlyList<string> expectedOutputs,
        RegexInferenceOptions options)
    {
        return InferCore(sourceText, expectedOutputs, Array.Empty<string>(), options);
    }

    /// <summary>
    /// Infers a regular expression that extracts the expected outputs while rejecting the negatives.
    /// <para>推导一个既能提取全部期望输出、又能拒绝全部负例的正则表达式。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="expectedOutputs">The strings that must be matched exactly. <para>必须被精确匹配的字符串。</para></param>
    /// <param name="negatives">
    /// The strings the pattern must not match. They steer the ranking, and a candidate that matches one of
    /// them is still returned with a non-zero <see cref="InferredRegex.NegativeLeaks"/>.
    /// <para>正则不得匹配的字符串。它们参与排序；仍然匹配其中之一的候选仍会被返回，并带上非零的
    /// <see cref="InferredRegex.NegativeLeaks"/>。</para>
    /// </param>
    /// <param name="options">
    /// Tuning knobs; pass a populated <see cref="RegexInferenceOptions"/> to override the defaults.
    /// <para>可调参数；传入填写好的 <see cref="RegexInferenceOptions"/> 即可覆盖默认值。</para>
    /// </param>
    /// <returns>
    /// Candidates ordered by descending confidence, at most <see cref="RegexInferenceOptions.TopResults"/>
    /// of them. The list is empty when no candidate matched every positive example exactly.
    /// <para>按置信度降序排列的候选，最多 <see cref="RegexInferenceOptions.TopResults"/> 个。没有任何候选精确命中全部正例时为空列表。</para>
    /// </returns>
    public static IReadOnlyList<InferredRegex> Infer(
        string sourceText,
        IReadOnlyList<string> expectedOutputs,
        IReadOnlyList<string> negatives,
        RegexInferenceOptions options)
    {
        return InferCore(sourceText, expectedOutputs, negatives, options);
    }

    /// <summary>
    /// Runs inference and also reports how many traces the search analysed.
    /// <para>执行推理，并额外报告搜索分析过的轨迹数量。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="expectedOutputs">The strings that must be matched exactly. <para>必须被精确匹配的字符串。</para></param>
    /// <param name="negatives">The strings the pattern must not match. <para>正则不得匹配的字符串。</para></param>
    /// <param name="options">Tuning knobs; pass a populated <see cref="RegexInferenceOptions"/> to override the defaults. <para>可调参数；传入填写好的 <see cref="RegexInferenceOptions"/> 即可覆盖默认值。</para></param>
    /// <returns>The ranked candidates plus the trace count. <para>排序后的候选与轨迹计数。</para></returns>
    public static RegexInferenceResult InferDetailed(
        string sourceText,
        IReadOnlyList<string> expectedOutputs,
        IReadOnlyList<string> negatives,
        RegexInferenceOptions options)
    {
        return InferCoreDetailed(sourceText, expectedOutputs, negatives, options);
    }

    /// <summary>
    /// Shared pipeline: derive traces, render the generalisation, validate, project.
    /// <para>共用流水线：推导轨迹、渲染泛化结果、验证、投影。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="expectedOutputs">The strings that must be matched exactly. <para>必须被精确匹配的字符串。</para></param>
    /// <param name="negatives">The strings the pattern must not match. <para>正则不得匹配的字符串。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>The ranked candidates. <para>排序后的候选。</para></returns>
    private static IReadOnlyList<InferredRegex> InferCore(
        string sourceText,
        IReadOnlyList<string> expectedOutputs,
        IReadOnlyList<string> negatives,
        RegexInferenceOptions options)
    {
        return InferCoreDetailed(sourceText, expectedOutputs, negatives, options).Candidates;
    }

    /// <summary>
    /// Trace derivation, generalisation and validation with the full result payload.
    /// <para>轨迹推导、泛化与验证，返回完整结果载荷。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="expectedOutputs">The strings that must be matched exactly. <para>必须被精确匹配的字符串。</para></param>
    /// <param name="negatives">The strings the pattern must not match. <para>正则不得匹配的字符串。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>The ranked candidates plus the trace count. <para>排序后的候选与轨迹计数。</para></returns>
    private static RegexInferenceResult InferCoreDetailed(
        string sourceText,
        IReadOnlyList<string> expectedOutputs,
        IReadOnlyList<string> negatives,
        RegexInferenceOptions options)
    {
        var traces = TraceBuilder.Build(sourceText, expectedOutputs, options);
        var ranked = PatternTrie.Ranker.Rank(sourceText, traces, expectedOutputs, negatives, options);

        return new RegexInferenceResult(expectedOutputs.Count, ranked, traces.Count);
    }
}
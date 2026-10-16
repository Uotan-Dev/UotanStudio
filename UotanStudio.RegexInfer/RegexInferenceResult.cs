namespace UotanStudio.RegexInfer;

/// <summary>
/// Full result of one inference run, exposing both the ranked candidates and the raw evidence counts.
/// <para>一次推理运行的完整结果，既暴露排序后的候选，也暴露原始证据计数。</para>
/// </summary>
public sealed class RegexInferenceResult
{
    /// <param name="positiveTotal">How many positive examples were supplied. <para>提供的正例数量。</para></param>
    /// <param name="candidates">The ranked candidates. <para>排序后的候选列表。</para></param>
    /// <param name="traceCount">How many traces the search analysed. <para>搜索分析过的轨迹数量。</para></param>
    internal RegexInferenceResult(int positiveTotal, IReadOnlyList<InferredRegex> candidates, int traceCount)
    {
        PositiveTotal = positiveTotal;
        Candidates = candidates;
        TraceCount = traceCount;
    }

    /// <summary>How many positive examples were supplied. <para>提供的正例数量。</para></summary>
    /// <value>The number of expected outputs handed to the engine. <para>交给引擎的期望输出数量。</para></value>
    public int PositiveTotal { get; }

    /// <summary>How many traces the search analysed. <para>搜索分析过的轨迹数量。</para></summary>
    /// <value>
    /// A trace is one way of carving each positive example out of the source text; a large value means
    /// the inputs were ambiguous and many carvings had to be ranked.
    /// <para>轨迹指把每个正例从源文本中切分出来的一种方式；数值越大说明输入越歧义、需要排序的切法越多。</para>
    /// </value>
    public int TraceCount { get; }

    /// <summary>The ranked candidates. <para>排序后的候选列表。</para></summary>
    /// <value>
    /// Ordered by descending <see cref="InferredRegex.Confidence"/>, truncated to the requested
    /// <see cref="RegexInferenceOptions.TopResults"/>; empty when no candidate matched every positive example.
    /// <para>按 <see cref="InferredRegex.Confidence"/> 降序排列，截断到请求的 <see cref="RegexInferenceOptions.TopResults"/>；
    /// 没有任何候选命中全部正例时为空。</para>
    /// </value>
    public IReadOnlyList<InferredRegex> Candidates { get; }
}
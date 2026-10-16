using System.Text.RegularExpressions;

namespace UotanStudio.RegexInfer;

/// <summary>
/// One candidate regular expression produced by <see cref="RegexInfer"/>, together with the evidence that
/// ranked it.
/// <para><see cref="RegexInfer"/> 产出的一个候选正则表达式，附带其排序依据的证据。</para>
/// </summary>
public sealed class InferredRegex
{
    /// <param name="pattern">The inferred .NET pattern. <para>推导出的 .NET 正则表达式。</para></param>
    /// <param name="positiveMatches">How many positive examples the pattern matched exactly. <para>该正则精确匹配了多少个正例。</para></param>
    /// <param name="positiveTotal">How many positive examples existed. <para>正例总数。</para></param>
    /// <param name="confidence">The rank score in [0, 1]. <para>[0, 1] 区间内的排序得分。</para></param>
    /// <param name="negativeLeaks">How many negatives the pattern still matched. <para>该正则仍然匹配的负例数量。</para></param>
    /// <param name="traceCount">How many traces supported this candidate. <para>支撑该候选的轨迹数量。</para></param>
    internal InferredRegex(string pattern, int positiveMatches, int positiveTotal, double confidence, int negativeLeaks, int traceCount)
    {
        Pattern = pattern;
        PositiveMatches = positiveMatches;
        PositiveTotal = positiveTotal;
        Confidence = confidence;
        NegativeLeaks = negativeLeaks;
        TraceCount = traceCount;
    }

    /// <summary>The inferred .NET regular-expression pattern. <para>推导出的 .NET 正则表达式。</para></summary>
    /// <value>A non-empty pattern. <para>非空的正则表达式。</para></value>
    public string Pattern { get; }

    /// <summary>
    /// How many positive examples the pattern extracted as its first capturing group over the source text.
    /// <para>该正则在源文本上将其作为第一个捕获组提取出来的正例数量。</para>
    /// </summary>
    /// <value>
    /// A count between 0 and <see cref="PositiveTotal"/>. A candidate is only returned when it equals
    /// <see cref="PositiveTotal"/>, so a returned candidate always extracted every positive example. The
    /// pattern is a locator plus extractor run against the source text, so a value counts as extracted when
    /// some match of the pattern yields it as <c>Groups[1]</c>, not when the pattern matches the value on
    /// its own.
    /// <para>0 到 <see cref="PositiveTotal"/> 之间的计数。候选只有在等于 <see cref="PositiveTotal"/> 时才会被返回，
    /// 因此返回的候选必定提取出了全部正例。该正则是作用在源文本上的定位器加提取器，因此只要正则的某次匹配把该值
    /// 作为 <c>Groups[1]</c> 产出即计入命中，而不要求正则该值单独也能匹配。</para></value>
    public int PositiveMatches { get; }

    /// <summary>How many positive examples existed. <para>正例总数。</para></summary>
    /// <value>The number of expected outputs supplied to the engine. <para>交给引擎的期望输出数量。</para></value>
    public int PositiveTotal { get; }

    /// <summary>The rank score of this candidate. <para>该候选的排序得分。</para></summary>
    /// <value>
    /// A value in [0, 1] combining the share of extracted positives, the absence of negative leaks, the
    /// trace support and a structural-preference term; higher ranks first.
    /// <para>[0, 1] 区间内的数值，综合正例命中比例、无负例泄漏、轨迹支撑度与结构偏好项；数值越大排序越靠前。</para>
    /// </value>
    public double Confidence { get; }

    /// <summary>How many negatives the pattern still matched. <para>该正则仍然匹配的负例数量。</para></summary>
    /// <value>
    /// Zero for a fully discriminating candidate; any non-zero value means the pattern is not specific
    /// enough and should not be trusted blindly.
    /// <para>完全区分正负例的候选为 0；非零表示该正则区分度不足，不应盲目采信。</para>
    /// </value>
    public int NegativeLeaks { get; }

    /// <summary>How many traces supported this candidate. <para>支撑该候选的轨迹数量。</para></summary>
    /// <value>
    /// A count of the input traces whose derived candidate equals <see cref="Pattern"/>; a larger value
    /// means the pattern was reached from more derivations.
    /// <para>其推导候选等于 <see cref="Pattern"/> 的输入轨迹数量；数值越大说明该正则由更多推导路径得出。</para>
    /// </value>
    public int TraceCount { get; }

    /// <summary>
    /// Compiles <see cref="Pattern"/> and reports whether the compilation succeeded.
    /// <para>编译 <see cref="Pattern"/> 并报告编译是否成功。</para>
    /// </summary>
    /// <param name="compiled">The compiled expression on success, <see langword="null"/> on failure. <para>成功时为编译后的表达式，失败时为 <see langword="null"/>。</para></param>
    /// <returns>
    /// <see langword="true"/> when the pattern compiled, in which case <paramref name="compiled"/> is usable
    /// for matching. Every pattern the engine emits is validated before it is returned, so a
    /// <see langword="false"/> result indicates a host regex engine that rejected a pattern this library
    /// considers valid.
    /// <para>编译成功时为 <see langword="true"/>，此时 <paramref name="compiled"/> 可直接用于匹配。引擎输出的每个正则
    /// 在返回前都已通过验证，因此得到 <see langword="false"/> 意味着宿主正则引擎拒绝了本库认为合法的模式。</para>
    /// </returns>
    public bool TryCreateCompiled(out Regex? compiled)
    {
        try
        {
            compiled = new Regex(Pattern);
            return true;
        }
        catch (ArgumentException)
        {
            compiled = null;
            return false;
        }
    }
}
namespace UotanStudio.RegexInfer.PatternTrace;

/// <summary>
/// Derives the partition traces for each positive example. For every occurrence of the expected output the
/// builder enumerates the candidate leading and trailing delimiters, scores each partition, and keeps the
/// cheapest ones as traces.
/// <para>为每个正例推导划分轨迹。对期望输出的每一次出现，构建器枚举候选的首部与尾部分隔符，
/// 为每种划分打分，并把最便宜的划分保留为轨迹。</para>
/// </summary>
internal static class TraceBuilder
{
    /// <summary>
    /// Builds the traces for all positive examples.
    /// <para>为全部正例构建轨迹。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="expectedOutputs">The strings that must be matched exactly. <para>必须被精确匹配的字符串。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>
    /// Every trace in example order; an example that could not be located contributes no trace, and an
    /// example with no usable delimiter contributes exactly one whole-run trace.
    /// <para>按样例顺序排列的全部轨迹；无法定位的样例不贡献轨迹，没有可用分隔符的样例恰好贡献一条整段轨迹。</para>
    /// </returns>
    public static List<Trace> Build(string sourceText, IReadOnlyList<string> expectedOutputs, RegexInferenceOptions options)
    {
        var result = new List<Trace>();

        for (var positiveIndex = 0; positiveIndex < expectedOutputs.Count; positiveIndex++)
        {
            var expected = expectedOutputs[positiveIndex];
            result.AddRange(BuildForOne(sourceText, expected, positiveIndex, options));
        }

        return result;
    }

    /// <summary>
    /// Builds the traces for a single positive example.
    /// <para>为单个正例构建轨迹。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="expected">The expected output. <para>期望输出。</para></param>
    /// <param name="positiveIndex">Index of the positive example. <para>正例下标。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>The traces of this example, cheapest first. <para>该样例的轨迹，代价最低者在前。</para></returns>
    private static List<Trace> BuildForOne(string sourceText, string expected, int positiveIndex, RegexInferenceOptions options)
    {
        if (expected.Length == 0)
        {
            return new List<Trace>();
        }

        var candidates = new List<(List<Delimiter> Delimiters, int Cost)>();
        var bestCost = int.MaxValue;
        var start = sourceText.IndexOf(expected, StringComparison.Ordinal);

        while (start >= 0)
        {
            var end = start + expected.Length;
            foreach (var delimiter in EnumerateLeadingDelimiters(sourceText, start))
            {
                foreach (var trailing in EnumerateTrailingDelimiters(sourceText, end))
                {
                    var leadingRun = delimiter.ExpectedRunLength;
                    var trailingRun = trailing.ExpectedRunLength;
                    if (leadingRun + trailingRun >= expected.Length)
                    {
                        continue;
                    }

                    var delimiters = new List<Delimiter>();
                    if (delimiter.Text.Length > 0)
                    {
                        delimiters.Add(delimiter);
                    }

                    if (trailing.Text.Length > 0)
                    {
                        delimiters.Add(trailing);
                    }

                    var cost = PartitionCost(delimiters, expected.Length - leadingRun - trailingRun);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        candidates.Clear();
                    }

                    if (cost == bestCost)
                    {
                        candidates.Add((delimiters, cost));
                    }
                }
            }

            start = sourceText.IndexOf(expected, start + 1, StringComparison.Ordinal);
        }

        if (candidates.Count == 0)
        {
            var whole = new List<Delimiter>();
            var fallbackCost = PartitionCost(whole, expected.Length);
            if (fallbackCost < bestCost)
            {
                bestCost = fallbackCost;
            }

            candidates.Add((whole, fallbackCost));
        }

        var traces = new List<Trace>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (delimiters, _) in candidates)
        {
            var trace = Materialise(delimiters, expected, positiveIndex);
            if (seen.Add(Signature(trace)))
            {
                traces.Add(trace);
            }
        }

        return traces;
    }

    /// <summary>
    /// Enumerates candidate leading delimiters: the maximal run of non-output characters immediately before
    /// the output, extended by one output character at a time so shorter output fragments are also covered.
    /// <para>枚举候选首部分隔符：紧贴输出之前的极长非输出字符段，每次再向输出方向扩进一个输出字符，
    /// 从而也覆盖更短的输出片段。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="start">Start position of the output occurrence. <para>输出出现位置的起点。</para></param>
    /// <returns>The candidate delimiters, shortest first. <para>候选分隔符，短者在前。</para></returns>
    private static List<Delimiter> EnumerateLeadingDelimiters(string sourceText, int start)
    {
        var candidates = new List<Delimiter>();
        var boundary = start;

        while (boundary > 0 && IsDelimiterCharacter(sourceText[boundary - 1]))
        {
            boundary--;
        }

        candidates.Add(new Delimiter(sourceText[boundary..start], 0, false));

        for (var claimed = 1; claimed < start - boundary + 1 && claimed < start; claimed++)
        {
            var delimiterStart = start - claimed;
            candidates.Add(new Delimiter(sourceText[delimiterStart..start], claimed, false));
        }

        return candidates;
    }

    /// <summary>
    /// Enumerates candidate trailing delimiters, mirroring <see cref="EnumerateLeadingDelimiters"/>.
    /// <para>枚举候选尾部分隔符，与 <see cref="EnumerateLeadingDelimiters"/> 对称。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="end">End position of the output occurrence. <para>输出出现位置的终点。</para></param>
    /// <returns>The candidate delimiters, shortest first. <para>候选分隔符，短者在前。</para></returns>
    private static List<Delimiter> EnumerateTrailingDelimiters(string sourceText, int end)
    {
        var candidates = new List<Delimiter>();
        var boundary = end;

        while (boundary < sourceText.Length && IsDelimiterCharacter(sourceText[boundary]))
        {
            boundary++;
        }

        candidates.Add(new Delimiter(sourceText[end..boundary], 0, true));

        for (var claimed = 1; claimed < boundary - end + 1 && claimed < sourceText.Length - end; claimed++)
        {
            var delimiterEnd = end + claimed;
            candidates.Add(new Delimiter(sourceText[end..delimiterEnd], claimed, true));
        }

        return candidates;
    }

    /// <summary>
    /// Classifies one character as a delimiter candidate: whitespace and punctuation can separate fragments,
    /// letters and digits belong to the output.
    /// <para>把单个字符归类为分隔符候选：空白与标点可以分隔片段，字母与数字属于输出。</para>
    /// </summary>
    /// <param name="value">The character to classify. <para>待分类的字符。</para></param>
    /// <returns><see langword="true"/> when the character can act as a delimiter. <para>该字符可以充当分隔符时为 <see langword="true"/>。</para></returns>
    private static bool IsDelimiterCharacter(char value)
    {
        return !char.IsLetterOrDigit(value);
    }

    /// <summary>
    /// Scores a partition: it prefers few delimiters, long delimiters and long output fragments.
    /// <para>为划分打分：偏好更少的分隔符、更长的分隔符与更长的输出片段。</para>
    /// </summary>
    /// <param name="delimiters">The delimiter pair of the partition. <para>该划分的分隔符组合。</para></param>
    /// <param name="outputLength">Length of the resulting output fragment. <para>所得输出片段的长度。</para></param>
    /// <returns>The cost, lower is better. <para>代价，越小越好。</para></returns>
    private static int PartitionCost(List<Delimiter> delimiters, int outputLength)
    {
        var delimiterLength = 0;
        foreach (var delimiter in delimiters)
        {
            delimiterLength += delimiter.Text.Length;
        }

        return (delimiters.Count * 4) - delimiterLength - outputLength;
    }

    /// <summary>
    /// Turns a delimiter pair into a trace of leading, output and trailing fragments.
    /// <para>把一对分隔符转化为由首部、输出与尾部片段组成的轨迹。</para>
    /// </summary>
    /// <param name="delimiters">The delimiter pair. <para>分隔符对。</para></param>
    /// <param name="expected">The expected output. <para>期望输出。</para></param>
    /// <param name="positiveIndex">Index of the positive example. <para>正例下标。</para></param>
    /// <returns>The trace. <para>轨迹。</para></returns>
    private static Trace Materialise(List<Delimiter> delimiters, string expected, int positiveIndex)
    {
        var leading = string.Empty;
        var trailing = string.Empty;
        var leadingClaim = 0;
        var trailingClaim = 0;

        foreach (var delimiter in delimiters.OrderBy(item => item.FacesRight ? 1 : 0))
        {
            if (delimiter.FacesRight)
            {
                trailing += delimiter.Text;
                trailingClaim += delimiter.ExpectedRunLength;
            }
            else
            {
                leading += delimiter.Text;
                leadingClaim += delimiter.ExpectedRunLength;
            }
        }

        var outputStart = Math.Min(leadingClaim, expected.Length);
        var outputEnd = Math.Max(outputStart, expected.Length - trailingClaim);
        var output = expected.Substring(outputStart, outputEnd - outputStart);

        return new Trace(
            new[] { leading, output, trailing },
            new[] { 1 },
            positiveIndex);
    }

    /// <summary>
    /// Produces a stable signature so identical traces from different occurrences collapse.
    /// <para>生成稳定签名，使来自不同出现位置的相同轨迹合并。</para>
    /// </summary>
    /// <param name="trace">The trace. <para>轨迹。</para></param>
    /// <returns>The signature. <para>签名。</para></returns>
    private static string Signature(Trace trace)
    {
        return string.Join('\u0002', trace.Segments);
    }
}
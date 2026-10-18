using System.Text.RegularExpressions;

namespace UotanStudio.RegexInfer.PatternTrie;

/// <summary>
/// Renders the generalised pattern and validates it against every example, turning the evidence into a
/// confidence score. A candidate is only kept when it matched every positive example exactly.
/// <para>渲染泛化后的正则，用全部样例验证它，并把证据转化为置信度得分。只有精确命中全部正例的候选才会被保留。</para>
/// </summary>
internal static class Ranker
{
    /// <summary>Renders, validates and scores the candidate derived from the traces.
    /// <para>渲染、验证并评定从轨迹推导出的候选。</para></summary>
    /// <param name="sourceText">The text the pattern will run against.<para>正则将要作用的文本。</para></param>
    /// <param name="traces">The traces the DAG was built from.<para>构建 DAG 所用的轨迹。</para></param>
    /// <param name="expectedOutputs">The strings that must be matched exactly.
    /// <para>必须被精确匹配的字符串。</para></param>
    /// <param name="negatives">The strings the pattern must not match.<para>正则不得匹配的字符串。</para></param>
    /// <param name="options">Resolved tuning knobs.<para>已解析的可调参数。</para></param>
    /// <returns>The scored candidates, descending by confidence; empty when no candidate matched every positive example exactly.
    /// <para>按置信度降序排列的已评分候选；没有任何候选精确命中全部正例时为空。</para></returns>
    public static IReadOnlyList<InferredRegex> Rank(
        string sourceText,
        IReadOnlyList<PatternTrace.Trace> traces,
        IReadOnlyList<string> expectedOutputs,
        IReadOnlyList<string> negatives,
        RegexInferenceOptions options)
    {
        var body = Render(traces, options);
        if (body.Length == 0)
        {
            return Array.Empty<InferredRegex>();
        }

        var pattern = options.PreferAnchors && CoversWholeInput(traces)
            ? "^" + body + "$"
            : body;

        return Validate(sourceText, pattern, traces.Count, expectedOutputs, negatives, options);
    }

    /// <summary>
    /// Reports whether every trace consumed the whole input, leaving no leading or trailing context. Only
    /// then is an anchored pattern equivalent to the unanchored one, which is what makes preferring anchors
    /// safe.
    /// <para>报告是否每条轨迹都消费了完整输入，没有留下首部或尾部上下文。只有此时加锚点的正则才与不加锚点等价，
    /// 这也正是优先加锚点是安全的前提。</para>
    /// </summary>
    /// <param name="traces">The traces to inspect. <para>待检查的轨迹。</para></param>
    /// <returns><see langword="true"/> when the leading and trailing fragments of every trace are empty. <para>每条轨迹的首尾片段均为空时为 <see langword="true"/>。</para></returns>
    private static bool CoversWholeInput(IReadOnlyList<PatternTrace.Trace> traces)
    {
        foreach (var trace in traces)
        {
            if (trace.Segments.Length != 3)
            {
                return false;
            }

            if (trace.Segments[0].Length != 0 || trace.Segments[^1].Length != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Renders the generalised pattern by aligning the traces position by position. The traces share their
    /// shape because every one was split at the same kind of delimiter, so position <c>i</c> of one trace
    /// corresponds to position <c>i</c> of another. Positions whose texts agree collapse into a literal,
    /// positions that vary collapse into a wildcard, and the varying positions that carry the output are
    /// wrapped in a capturing group.
    /// <para>通过逐位置对齐轨迹来渲染泛化后的正则。各轨迹形状相同，因为它们都在同类分隔符处切分，
    /// 因此一条轨迹的第 <c>i</c> 个位置对应另一条轨迹的第 <c>i</c> 个位置。文本一致的位置折叠为字面量，
    /// 变化的位置折叠为通配，而承载输出的变化位置会被包进捕获组。</para>
    /// </summary>
    /// <param name="traces">The traces to merge. <para>待合并的轨迹。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>The rendered pattern body without anchors. <para>不含锚点的正则主体。</para></returns>
    private static string Render(IReadOnlyList<PatternTrace.Trace> traces, RegexInferenceOptions options)
    {
        if (traces.Count == 0)
        {
            return string.Empty;
        }

        var positions = traces.Max(trace => trace.Segments.Length);
        var builder = new System.Text.StringBuilder();
        var separators = separators_of(traces, positions);

        for (var position = 0; position < positions; position++)
        {
            var texts = new List<string>();
            var isOutput = false;

            foreach (var trace in traces)
            {
                if (position >= trace.Segments.Length)
                {
                    continue;
                }

                texts.Add(trace.Segments[position]);
                if (trace.OutputSegmentIndices.Contains(position))
                {
                    isOutput = true;
                }
            }

            if (texts.Count == 0)
            {
                continue;
            }

            if (isOutput)
            {
                var quantifier = QuantifierFor(texts.Max(text => text.Length), options);
                builder.Append('(').Append(OutputWildcard(texts, options, separators)).Append(quantifier).Append(')');
            }
            else
            {
                var rendered = RenderPosition(texts, options);
                if (rendered.Length > 0)
                {
                    builder.Append(rendered);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Collects the single characters that act as separators around the outputs, taken from the context
    /// positions immediately before and after each output position, so the output wildcard can be prevented
    /// from swallowing them.
    /// <para>收集充当输出前后分隔符的单个字符，取自每个输出位置紧前与紧后的上下文位置，使输出通配不会把它们一起吞掉。</para>
    /// </summary>
    /// <param name="traces">The traces to inspect. <para>待检查的轨迹。</para></param>
    /// <param name="positions">How many positions the traces share. <para>各轨迹共享的位置数。</para></param>
    /// <returns>The separator characters, possibly empty. <para>分隔符字符，可能为空。</para></returns>
    private static HashSet<char> separators_of(IReadOnlyList<PatternTrace.Trace> traces, int positions)
    {
        var separators = new HashSet<char>();

        for (var position = 0; position < positions; position++)
        {
            foreach (var trace in traces)
            {
                if (position >= trace.Segments.Length || trace.OutputSegmentIndices.Contains(position))
                {
                    continue;
                }

                foreach (var value in trace.Segments[position])
                {
                    if (IsSeparatorCharacter(value))
                    {
                        separators.Add(value);
                    }
                }
            }
        }

        return separators;
    }

    /// <summary>
    /// Reports whether the context position at the given index sits before the output of its trace, which
    /// makes it a leading separator rather than a trailing one.
    /// <para>报告给定下标处的上下文位置是否位于其轨迹输出之前，从而属于首部分隔符而非尾部分隔符。</para>
    /// </summary>
    /// <param name="trace">The trace that owns the position. <para>拥有该位置的轨迹。</para></param>
    /// <param name="position">The context position index. <para>上下文位置下标。</para></param>
    /// <returns><see langword="true"/> when the position precedes the output. <para>该位置位于输出之前时为 <see langword="true"/>。</para></returns>
    private static bool precedes_output(PatternTrace.Trace trace, int position)
    {
        foreach (var index in trace.OutputSegmentIndices)
        {
            if (index > position)
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="texts">The output texts at this position. <para>该位置上的输出文本。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <param name="separators">Characters the wildcard must not swallow. <para>通配不得吞掉的字符。</para></param>
    /// <returns>The wildcard body without a quantifier. <para>不含量词的通配主体。</para></returns>
    private static string OutputWildcard(List<string> texts, RegexInferenceOptions options, HashSet<char> separators)
    {
        var kind = SharedClassOf(texts, options);
        var wildcard = kind switch
        {
            CharClassKind.Digit => "\\d",
            CharClassKind.Alpha => "[a-zA-Z]",
            CharClassKind.Alphanumeric => "\\w",
            CharClassKind.Whitespace => "\\s",
            _ => "."
        };

        if (wildcard != "." || separators.Count == 0)
        {
            return wildcard;
        }

        return NegatedClassFor(separators);
    }

    /// <summary>
    /// Builds a negated character class that matches any single character except the given separators.
    /// <para>构造一个排除给定分隔符的否定字符类，匹配除这些分隔符之外的任意单个字符。</para>
    /// </summary>
    /// <param name="separators">The characters to exclude. <para>待排除的字符。</para></param>
    /// <returns>The negated class body. <para>否定字符类主体。</para></returns>
    private static string NegatedClassFor(HashSet<char> separators)
    {
        var builder = new System.Text.StringBuilder("[^");
        foreach (var value in separators.Order())
        {
            builder.Append(Regex.Escape(value.ToString()));
        }

        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>
    /// Renders one aligned position. An output position always becomes a capturing group around a wildcard,
    /// because the whole point of the pattern is to extract a value that varies between examples. A context
    /// position becomes the shared literal when every trace agrees, the shared character class when the texts
    /// are built from one class, and otherwise the first text emitted verbatim so the delimiter still
    /// constrains the match.
    /// <para>渲染一个对齐位置。输出位置总是变成包住通配的捕获组，因为该正则的意义正是提取样例间变化的取值。
    /// 上下文位置在所有轨迹一致时输出共享字面量，在文本由同一字符类构成时输出共享字符类，否则原样输出第一个文本，
    /// 使分隔符仍能约束匹配。</para>
    /// </summary>
    /// <param name="texts">The texts at this position across all traces. <para>全部轨迹在该位置上的文本。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>The rendered position. <para>渲染后的位置。</para></returns>
    private static string RenderPosition(List<string> texts, RegexInferenceOptions options)
    {
        var shared = texts[0];
        var agrees = true;

        foreach (var text in texts)
        {
            if (!string.Equals(text, shared, StringComparison.Ordinal))
            {
                agrees = false;
                break;
            }
        }

        if (agrees)
        {
            return Regex.Escape(shared);
        }

        return RenderSeparatorRun(texts);
    }

    /// <summary>
    /// Renders a separator run. When every trace agrees the run is emitted verbatim; when the runs differ but
    /// are built from the same separator characters, each character position becomes its own class so an
    /// ordered run such as <c>,</c> followed by a newline still matches a record whose last field is empty.
    /// <para>渲染一段分隔符。所有轨迹一致时原样输出；各轨迹的分隔符不同但由相同的分隔符字符构成时，
    /// 每个字符位置各自成为一个字符类，使诸如逗号后接换行这样的有序分隔符仍能匹配最后一个字段为空的记录。</para>
    /// </summary>
    /// <param name="texts">The texts at this position across all traces. <para>全部轨迹在该位置上的文本。</para></param>
    /// <returns>The rendered separator run. <para>渲染后的分隔符片段。</para></returns>
    private static string RenderSeparatorRun(List<string> texts)
    {
        var shared = texts[0];
        var agrees = true;

        foreach (var text in texts)
        {
            if (!string.Equals(text, shared, StringComparison.Ordinal))
            {
                agrees = false;
                break;
            }
        }

        if (agrees)
        {
            return Regex.Escape(shared);
        }

        if (!IsSeparatorRun(texts))
        {
            return Regex.Escape(shared);
        }

        var longest = texts.Max(text => text.Length);
        var builder = new System.Text.StringBuilder();

        for (var offset = 0; offset < longest; offset++)
        {
            var characters = new SortedSet<char>();
            var any = false;

            foreach (var text in texts)
            {
                if (offset < text.Length)
                {
                    characters.Add(text[offset]);
                    any = true;
                }
            }

            if (!any)
            {
                continue;
            }

            if (characters.Count == 1)
            {
                builder.Append(Regex.Escape(characters.First().ToString()));
            }
            else
            {
                builder.Append('[');
                foreach (var value in characters)
                {
                    builder.Append(Regex.Escape(value.ToString()));
                }

                builder.Append(']');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reports whether every text at a position consists only of separator characters.
    /// <para>报告某个位置上的所有文本是否仅由分隔符字符组成。</para>
    /// </summary>
    /// <param name="texts">The texts at this position across all traces. <para>全部轨迹在该位置上的文本。</para></param>
    /// <returns><see langword="true"/> when all texts are separator runs. <para>所有文本都是分隔符片段时为 <see langword="true"/>。</para></returns>
    private static bool IsSeparatorRun(List<string> texts)
    {
        foreach (var text in texts)
        {
            foreach (var value in text)
            {
                if (!IsSeparatorCharacter(value))
                {
                    return false;
                }
            }
        }

        return texts.Any(text => text.Length > 0);
    }

    /// <summary>
    /// Reports whether a character can act as a separator between fields of a record.
    /// <para>报告某个字符是否可以充当记录字段之间的分隔符。</para>
    /// </summary>
    /// <param name="value">The character to classify. <para>待分类的字符。</para></param>
    /// <returns><see langword="true"/> for anything that is not a letter, digit or underscore. <para>非字母、数字或下划线的字符为 <see langword="true"/>。</para></returns>
    private static bool IsSeparatorCharacter(char value)
    {
        return !char.IsAsciiLetter(value) && !char.IsAsciiDigit(value) && value != '_';
    }

    /// <summary>
    /// Finds the single character class that every text at a position is built from, or
    /// <see cref="CharClassKind.None"/> when the texts disagree about their class or mix classes.
    /// <para>找出某个位置上的所有文本都由其构成的那一个字符类；当各文本的字符类不一致或混合了多个类时返回
    /// <see cref="CharClassKind.None"/>。</para>
    /// </summary>
    /// <param name="texts">The texts at this position across all traces. <para>全部轨迹在该位置上的文本。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>The shared class, or <see cref="CharClassKind.None"/>. <para>共享的字符类，无共享类时为 <see cref="CharClassKind.None"/>。</para></returns>
    private static CharClassKind SharedClassOf(List<string> texts, RegexInferenceOptions options)
    {
        var shared = ClassesOf(texts[0], options);
        if (shared == CharClassKind.None)
        {
            return CharClassKind.None;
        }

        foreach (var text in texts)
        {
            if (ClassesOf(text, options) != shared)
            {
                return CharClassKind.None;
            }
        }

        return shared;
    }

    /// <summary>
    /// Collects the character classes that a single text is built from, using the option threshold to decide
    /// whether the text is long enough to be treated as a class at all. A text that carries any character
    /// outside every known class classifies as <see cref="CharClassKind.None"/>, because collapsing it would
    /// silently drop that character from the rendered pattern.
    /// <para>收集单个文本由其构成的字符类，并用选项阈值判断该文本是否长到值得按字符类处理。带有任何已知类之外
    /// 字符的文本归类为 <see cref="CharClassKind.None"/>，因为折叠它会把这些字符从渲染出的正则里悄悄漏掉。</para>
    /// </summary>
    /// <param name="text">The text to inspect. <para>待检查的文本。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>The combined class flags, or <see cref="CharClassKind.None"/> for a mixed text. <para>组合后的字符类标志，混合文本为 <see cref="CharClassKind.None"/>。</para></returns>
    private static CharClassKind ClassesOf(string text, RegexInferenceOptions options)
    {
        if (text.Length < Math.Max(options.MinConstantLength, 1))
        {
            return CharClassKind.None;
        }

        var hasDigit = false;
        var hasLetter = false;
        var hasSpace = false;

        foreach (var value in text)
        {
            if (char.IsAsciiDigit(value))
            {
                hasDigit = true;
            }
            else if (char.IsAsciiLetter(value))
            {
                hasLetter = true;
            }
            else if (char.IsWhiteSpace(value))
            {
                hasSpace = true;
            }
            else
            {
                return CharClassKind.None;
            }
        }

        return (hasDigit ? CharClassKind.Digit : CharClassKind.None)
            | (hasLetter ? CharClassKind.Alpha : CharClassKind.None)
            | (hasSpace ? CharClassKind.Whitespace : CharClassKind.None);
    }

    /// <summary>
    /// Picks the quantifier for a wildcard from the longest text at the position and the enabled repetition
    /// options.
    /// <para>根据该位置上最长的文本以及启用的重复选项，为通配挑选量词。</para>
    /// </summary>
    /// <param name="longest">Length of the longest text at this position. <para>该位置上最长文本的长度。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>The quantifier suffix. <para>量词后缀。</para></returns>
    private static string QuantifierFor(int longest, RegexInferenceOptions options)
    {
        if (options.AllowPlus)
        {
            return "+";
        }

        if (options.AllowStar)
        {
            return "*";
        }

        return "+";
    }

    /// <summary>
    /// Compiles the pattern and measures it against the examples, keeping it only when it matched every
    /// positive example exactly.
    /// <para>编译正则并用样例度量，只有精确命中全部正例时才保留。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="pattern">The rendered pattern. <para>渲染出的正则。</para></param>
    /// <param name="traceCount">How many traces supported the candidate. <para>支撑该候选的轨迹数量。</para></param>
    /// <param name="expectedOutputs">The strings that must be matched exactly. <para>必须被精确匹配的字符串。</para></param>
    /// <param name="negatives">The strings the pattern must not match. <para>正则不得匹配的字符串。</para></param>
    /// <param name="options">Resolved tuning knobs. <para>已解析的可调参数。</para></param>
    /// <returns>The scored candidates. <para>已评分的候选。</para></returns>
    private static IReadOnlyList<InferredRegex> Validate(
        string sourceText,
        string pattern,
        int traceCount,
        IReadOnlyList<string> expectedOutputs,
        IReadOnlyList<string> negatives,
        RegexInferenceOptions options)
    {
        Regex compiled;
        try
        {
            compiled = new Regex(pattern);
        }
        catch (ArgumentException)
        {
            return Array.Empty<InferredRegex>();
        }

        var exactMatches = 0;
        foreach (var expected in expectedOutputs)
        {
            if (Captures(sourceText, compiled, expected))
            {
                exactMatches++;
            }
        }

        if (exactMatches != expectedOutputs.Count)
        {
            return Array.Empty<InferredRegex>();
        }

        var negativeLeaks = 0;
        foreach (var negative in negatives)
        {
            if (Captures(sourceText, compiled, negative))
            {
                negativeLeaks++;
            }
        }

        var recall = expectedOutputs.Count == 0 ? 0.0 : (double)exactMatches / expectedOutputs.Count;
        var precision = negatives.Count == 0 ? 1.0 : 1.0 - ((double)negativeLeaks / negatives.Count);
        var support = traceCount == 0 ? 0.0 : Math.Min(1.0, (double)traceCount / Math.Max(1, options.MaxTraceLength));
        var brevity = pattern.Length == 0 ? 0.0 : 1.0 / (1.0 + (pattern.Length / (double)options.MaxOutputLength));

        var confidence = (0.6 * recall) + (0.25 * precision) + (0.1 * support) + (0.05 * brevity);
        var candidate = new InferredRegex(pattern, exactMatches, expectedOutputs.Count, confidence, negativeLeaks, traceCount);

        return new List<InferredRegex> { candidate };
    }

    /// <summary>
    /// Reports whether the compiled pattern extracts the given value somewhere in the source text. A Flash
    /// Fill program runs over the source, so the extracted value is what the capturing group holds: an
    /// unanchored pattern is judged by the group of each of its matches, and an anchored one by the single
    /// whole-input match it can produce.
    /// <para>报告编译后的正则是否在源文本中某处提取出了给定值。Flash Fill 程序作用于源文本，因此被提取的值就是
    /// 捕获组所持有的内容：未加锚点的正则按其每次匹配的捕获组判定，加了锚点的正则按其唯一能产生的整段输入匹配判定。</para>
    /// </summary>
    /// <param name="sourceText">The text the pattern will run against. <para>正则将要作用的文本。</para></param>
    /// <param name="compiled">The compiled pattern. <para>已编译的正则。</para></param>
    /// <param name="value">The value to look for. <para>待查找的值。</para></param>
    /// <returns><see langword="true"/> when some extraction equals the value. <para>存在某次提取结果与该值相等时为 <see langword="true"/>。</para></returns>
    private static bool Captures(string sourceText, Regex compiled, string value)
    {
        foreach (Match match in compiled.Matches(sourceText))
        {
            if (string.Equals(match.Groups[1].Value, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// One node of the fragment trie, carrying how many traces pass through it.
/// <para>片段字典树的一个节点，携带经过它的轨迹数量。</para>
/// </summary>
internal sealed class TrieNode
{
    private readonly Dictionary<string, TrieNode> children = new(StringComparer.Ordinal);

    /// <param name="text">The fragment this node represents. <para>本节点代表的片段。</para></param>
    /// <param name="support">How many traces pass through this node. <para>经过本节点的轨迹数量。</para></param>
    public TrieNode(string text, int support)
    {
        Text = text;
        Support = support;
    }

    /// <summary>The fragment this node represents. <para>本节点代表的片段。</para></summary>
    public string Text { get; }

    /// <summary>How many traces pass through this node. <para>经过本节点的轨迹数量。</para></summary>
    public int Support { get; private set; }

    /// <summary>The child nodes keyed by fragment text. <para>以片段文本为键的子节点。</para></summary>
    public IReadOnlyDictionary<string, TrieNode> Children => children;

    /// <summary>
    /// Returns the child for a fragment, creating and counting it on first use.
    /// <para>返回某一片段对应的子节点，首次使用时创建并计数。</para>
    /// </summary>
    /// <param name="text">The fragment text. <para>片段文本。</para></param>
    /// <returns>The child node. <para>子节点。</para></returns>
    public TrieNode GetOrAdd(string text)
    {
        if (!children.TryGetValue(text, out var child))
        {
            child = new TrieNode(text, 0);
            children.Add(text, child);
        }

        child.Support++;
        return child;
    }
}
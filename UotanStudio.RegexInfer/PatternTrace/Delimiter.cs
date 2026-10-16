namespace UotanStudio.RegexInfer.PatternTrace;

/// <summary>
/// A candidate delimiter between the leading context and the extracted output, or between the output and
/// the trailing context. <see cref="ExpectedRunLength"/> is how many output characters the delimiter claims
/// from the side it faces, which is what makes the output fragment shorter than the whole expected string.
/// <para>首部上下文与提取输出之间、或输出与尾部上下文之间的一个候选分隔符。
/// <see cref="ExpectedRunLength"/> 是该分隔符从其所朝一侧索取的输出字符数，这正是输出片段短于整个期望字符串的原因。</para>
/// </summary>
internal readonly struct Delimiter
{
    /// <param name="text">The literal delimiter text, unescaped. <para>未转义的分隔符字面文本。</para></param>
    /// <param name="expectedRunLength">How many output characters this delimiter claims from the side it faces. <para>该分隔符从其所朝一侧索取的输出字符数。</para></param>
    /// <param name="facesRight"><see langword="true"/> for a trailing delimiter, <see langword="false"/> for a leading one. <para>尾部分隔符为 <see langword="true"/>，首部分隔符为 <see langword="false"/>。</para></param>
    public Delimiter(string text, int expectedRunLength, bool facesRight)
    {
        Text = text;
        ExpectedRunLength = expectedRunLength;
        FacesRight = facesRight;
    }

    /// <summary>The literal delimiter text, unescaped. <para>未转义的分隔符字面文本。</para></summary>
    public string Text { get; }

    /// <summary>How many output characters this delimiter claims from the side it faces. <para>该分隔符从其所朝一侧索取的输出字符数。</para></summary>
    public int ExpectedRunLength { get; }

    /// <summary>Whether this is a trailing delimiter. <para>是否为尾部分隔符。</para></summary>
    public bool FacesRight { get; }
}
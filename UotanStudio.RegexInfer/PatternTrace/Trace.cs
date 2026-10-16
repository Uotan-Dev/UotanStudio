namespace UotanStudio.RegexInfer.PatternTrace;

/// <summary>
/// A derivation of one input/output example as a sequence of alternating fragments. The sequence starts
/// with the leading context (possibly empty), and the output fragments are marked so the renderer can emit
/// the final pattern in <c>prefix(output)suffix</c> shape.
/// <para>一对输入/输出样例的一种推导形式，由交替的片段组成。序列以首部上下文开头（可为空），输出片段带标记，
/// 以便渲染器按 <c>前缀(输出)后缀</c> 的形状输出最终正则。</para>
/// </summary>
internal readonly struct Trace
{
    /// <param name="segments">The alternating fragments. <para>交替片段序列。</para></param>
    /// <param name="outputSegmentIndices">Indices into <paramref name="segments"/> that belong to the output. <paramref name="segments"/> 中属于输出的片段下标。</param>
    /// <param name="positiveIndex">Index of the positive example this trace came from. <para>本轨迹来源正例的下标。</para></param>
    public Trace(string[] segments, int[] outputSegmentIndices, int positiveIndex)
    {
        Segments = segments;
        OutputSegmentIndices = outputSegmentIndices;
        PositiveIndex = positiveIndex;
    }

    /// <summary>The alternating fragments. <para>交替片段序列。</para></summary>
    public string[] Segments { get; }

    /// <summary>Indices into <see cref="Segments"/> that belong to the output. <see cref="Segments"/> 中属于输出的片段下标。</summary>
    public int[] OutputSegmentIndices { get; }

    /// <summary>Index of the positive example this trace came from. <para>本轨迹来源正例的下标。</para></summary>
    public int PositiveIndex { get; }

    /// <summary>The concatenation of the marked output fragments. <para>被标记为输出的片段拼接结果。</para></summary>
    /// <value>The string this trace is expected to produce. <para>本轨迹预期产出的字符串。</para></value>
    public string Output
    {
        get
        {
            var builder = new System.Text.StringBuilder();
            foreach (var index in OutputSegmentIndices)
            {
                builder.Append(Segments[index]);
            }

            return builder.ToString();
        }
    }
}
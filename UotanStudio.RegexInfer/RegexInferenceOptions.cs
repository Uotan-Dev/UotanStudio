namespace UotanStudio.RegexInfer;

/// <summary>
/// Tunable knobs for <see cref="RegexInfer.Infer(string, IReadOnlyList{string}, RegexInferenceOptions)"/>.
/// The defaults reproduce the behaviour of the constructor-less overload.
/// <para><see cref="RegexInfer.Infer(string, IReadOnlyList{string}, RegexInferenceOptions)"/> 的可调参数。
/// 默认值对应无参重载的行为。</para>
/// </summary>
public sealed class RegexInferenceOptions
{
    /// <summary>
    /// Upper bound on DAG nodes visited during search, guarding the worst case of the generaliser.
    /// <para>搜索期间访问的 DAG 节点数上限，用于兜住泛化器的最坏情况。</para>
    /// </summary>
    /// <value>
    /// A positive node budget. When the budget is exhausted the search stops and returns whatever it found,
    /// so a tight budget trades coverage for a bounded latency. The default is 200000.
    /// <para>正的节点预算。预算耗尽时搜索停止并返回已找到的结果，因此收紧预算以有界延迟换取覆盖度。默认为 200000。</para>
    /// </value>
    public int MaxNodes { get; set; } = 200000;

    /// <summary>
    /// How many ranked candidates are returned.
    /// <para>返回多少个排序后的候选。</para>
    /// </summary>
    /// <value>
    /// At least 1; values below 1 are treated as 1. The default is 1.
    /// <para>至少为 1；小于 1 的值按 1 处理。默认为 1。</para>
    /// </value>
    public int TopResults { get; set; } = 1;

    /// <summary>
    /// Upper bound on emitted pattern length.
    /// <para>输出正则表达式长度的上限。</para>
    /// </summary>
    /// <value>
    /// A positive length cap applied to the rendered pattern; over-long candidates are discarded. The default is 512.
    /// <para>对渲染后正则生效的正长度上限；超长候选被丢弃。默认为 512。</para>
    /// </value>
    public int MaxOutputLength { get; set; } = 512;

    /// <summary>
    /// Upper bound on the number of derived traces analysed per positive example.
    /// <para>每个正例参与分析的推导轨迹数量上限。</para>
    /// </summary>
    /// <value>
    /// A positive cap that keeps ambiguous inputs bounded; extra traces are dropped in discovery order. The default is 200.
    /// <para>正数上限，用于约束歧义输入；超出的轨迹按发现顺序丢弃。默认为 200。</para>
    /// </value>
    public int MaxTraceLength { get; set; } = 200;

    /// <summary>
    /// Shortest constant text eligible for collapsing into a character class.
    /// <para>可折叠为字符类的最短常量文本长度。</para>
    /// </summary>
    /// <value>
    /// A non-negative threshold compared against the longest constant text shared by the traces at one
    /// position; text shorter than this is emitted verbatim. The default is 1.
    /// <para>非负阈值，与轨迹在同一位置上共享的最长常量文本长度比较；短于该值的文本原样输出。默认为 1。</para>
    /// </value>
    public int MinConstantLength { get; set; } = 1;

    /// <summary>
    /// Whether <c>*</c> repetitions are enabled.
    /// <para>是否启用 <c>*</c> 重复。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> allows zero-or-more repetitions. The default is <see langword="true"/>.
    /// <para><see langword="true"/> 允许零次或多次重复。默认为 <see langword="true"/>。</para>
    /// </value>
    public bool AllowStar { get; set; } = true;

    /// <summary>
    /// Whether <c>+</c> repetitions are enabled.
    /// <para>是否启用 <c>+</c> 重复。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> allows one-or-more repetitions. The default is <see langword="true"/>.
    /// <para><see langword="true"/> 允许一次或多次重复。默认为 <see langword="true"/>。</para>
    /// </value>
    public bool AllowPlus { get; set; } = true;

    /// <summary>
    /// Whether start and end anchors are preferred when they hold for every trace.
    /// <para>当所有轨迹都满足时是否优先输出首尾锚点。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> wraps the result in <c>^</c> and <c>$</c> when every trace consumes the whole
    /// input, which is the usual case for whole-line extraction. The default is <see langword="true"/>.
    /// <para><see langword="true"/> 时，若所有轨迹都消费完整输入，则给结果加上 <c>^</c> 与 <c>$</c>，这也是整行提取的常见情形。默认为 <see langword="true"/>。</para>
    /// </value>
    public bool PreferAnchors { get; set; } = true;
}
namespace UotanStudio.Scripting;

/// <summary>
/// Bounded resource profile for one untrusted script execution.
/// <para>一次不可信脚本执行所使用的有界资源画像。</para>
/// </summary>
public sealed record ScriptLimits
{
    /// <summary>
    /// Conservative default profile used when the host supplies no override.
    /// <para>宿主未覆盖时使用的保守默认画像。</para>
    /// </summary>
    public static ScriptLimits Default { get; } = new();

    /// <summary>
    /// Maximum wall-clock time for one script operation.
    /// <para>单次脚本操作允许的最长挂钟时间。</para>
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum number of statements executed in one engine entry.
    /// <para>单次引擎入口允许执行的最大语句数。</para>
    /// </summary>
    public int MaxStatements { get; init; } = 50_000;

    /// <summary>
    /// Maximum managed bytes a script may allocate during one operation.
    /// <para>一次操作期间脚本可分配的最大托管字节数。</para>
    /// </summary>
    public long MaxMemoryBytes { get; init; } = 16_000_000;

    /// <summary>
    /// Maximum JavaScript call depth.
    /// <para>允许的最大 JavaScript 调用深度。</para>
    /// </summary>
    public int MaxRecursionDepth { get; init; } = 64;

    /// <summary>
    /// Maximum accepted script source length in characters.
    /// <para>接受的脚本文本最大字符数。</para>
    /// </summary>
    public int MaxSourceLength { get; init; } = 100_000;

    /// <summary>
    /// Maximum accepted abstract syntax tree node count.
    /// <para>接受的抽象语法树最大节点数。</para>
    /// </summary>
    public int MaxNodeCount { get; init; } = 25_000;

    /// <summary>
    /// Maximum number of log entries retained for one execution.
    /// <para>一次执行保留的最大日志条数。</para>
    /// </summary>
    public int MaxLogEntries { get; init; } = 200;

    /// <summary>
    /// Maximum number of declarative UI elements a script may submit in one panel.
    /// <para>脚本一次提交声明式面板时允许的最大 UI 元素数。</para>
    /// </summary>
    public int MaxUiElements { get; init; } = 64;

    /// <summary>
    /// Maximum number of characters retained from a script result.
    /// <para>脚本结果保留的最大字符数。</para>
    /// </summary>
    public int MaxResultLength { get; init; } = 4_096;
}
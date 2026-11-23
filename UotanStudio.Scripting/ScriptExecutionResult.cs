namespace UotanStudio.Scripting;

/// <summary>
/// Outcome of one bounded script execution.
/// <para>一次有界脚本执行的结果。</para>
/// </summary>
public sealed record ScriptExecutionResult
{
    /// <summary>
    /// Whether the script completed without a reported failure.
    /// <para>脚本是否在无报错的情况下完成。</para>
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Bounded textual representation of the script return value, or <see langword="null"/> when there is none.
    /// <para>脚本返回值的有界文本表示；没有返回值时为 <see langword="null"/>。</para>
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// User-readable error text when <see cref="Success"/> is <see langword="false"/>.
    /// <para><see cref="Success"/> 为 <see langword="false"/> 时的用户可读错误文本。</para>
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Log entries emitted through the whitelisted log API.
    /// <para>通过白名单日志 API 产生的日志条目。</para>
    /// </summary>
    public IReadOnlyList<ScriptLogEntry> Logs { get; init; } = [];

    /// <summary>
    /// Creates a successful result.
    /// <para>创建成功结果。</para>
    /// </summary>
    /// <param name="output">Bounded return text.<para>有界返回文本。</para></param>
    /// <param name="logs">Log entries collected during execution.<para>执行期间收集的日志。</para></param>
    /// <returns>The successful result.<para>成功结果。</para></returns>
    public static ScriptExecutionResult Succeeded(string? output, IReadOnlyList<ScriptLogEntry> logs) =>
        new() { Success = true, Output = output, Logs = logs };

    /// <summary>
    /// Creates a failed result and preserves the collected logs.
    /// <para>创建失败结果并保留已收集的日志。</para>
    /// </summary>
    /// <param name="error">User-readable error text.<para>用户可读错误文本。</para></param>
    /// <param name="logs">Log entries collected before the failure.<para>失败前收集的日志。</para></param>
    /// <returns>The failed result.<para>失败结果。</para></returns>
    public static ScriptExecutionResult Failed(string error, IReadOnlyList<ScriptLogEntry> logs) =>
        new() { Success = false, Error = error, Logs = logs };
}

/// <summary>
/// Severity of one script log entry.
/// <para>脚本日志条目的级别。</para>
/// </summary>
public enum ScriptLogLevel
{
    /// <summary>Informational message.<para>提示信息。</para></summary>
    Info,

    /// <summary>Warning message.<para>警告信息。</para></summary>
    Warn,

    /// <summary>Error message.<para>错误信息。</para></summary>
    Error
}

/// <summary>
/// One log entry produced by a script.
/// <para>脚本产生的一条日志。</para>
/// </summary>
/// <param name="Level">Severity of the entry.<para>条目级别。</para></param>
/// <param name="Message">Bounded message text.<para>有界消息文本。</para></param>
public sealed record ScriptLogEntry(ScriptLogLevel Level, string Message);
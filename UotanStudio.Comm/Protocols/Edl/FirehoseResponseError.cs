using System.Globalization;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Classification of a rejected Firehose response.
/// <para>对被拒绝的 Firehose 响应所作的分类。</para>
/// No oracle assigns a meaning to a numeric Firehose <c>value</c> attribute, so numeric and
/// otherwise unknown values are reported as <see cref="Unrecognized"/> with the
/// raw text preserved instead of being mapped onto an invented table.
/// <para>没有任何本地权威实现为 Firehose 的数值 <c>value</c> 属性赋予含义，因此数值与未知取值一律
/// 报告为 <see cref="Unrecognized"/> 并原样保留文本，而不是套用臆造的对照表。</para>
/// </summary>
public enum FirehoseErrorKind
{
    /// <summary>
    /// The response was acknowledged; no error was observed. The acknowledgement flag is set exactly
    /// when the response <c>value</c> compares equal to <c>ack</c> ignoring case.
    /// <para>响应已被确认，未观测到错误。仅当响应 <c>value</c> 忽略大小写等于 <c>ack</c> 时置确认标志。</para>
    /// </summary>
    None = 0,

    /// <summary>
    /// The target answered <c>NAK</c> but the surrounding evidence does not identify a more
    /// specific cause; the raw value and attributes are preserved.
    /// <para>目标应答 <c>NAK</c>，但上下文证据不足以判定更具体的原因；原始取值与属性均被保留。</para>
    /// This is the generic rejection state: any value that is not
    /// <c>ack</c> leaves the acknowledgement flag cleared.
    /// <para>这是通用拒绝状态：任何非 <c>ack</c> 取值都不会置起确认标志。</para>
    /// </summary>
    Rejected,

    /// <summary>
    /// The target answered <c>NAK</c> while assigning its own attributes, which is treated as
    /// parameter negotiation rather than a storage failure.
    /// <para>目标在应答 <c>NAK</c> 的同时写入了自身的属性；这被视作参数协商而非存储故障。</para>
    /// </summary>
    AttributeNegotiation,

    /// <summary>
    /// The target rejected the command in a shape attributed to the storage layer.
    /// <para>目标以归因于存储层的形态拒绝了该命令。</para>
    /// Two evidenced shapes map here: a NAK whose response assigned no attributes at all (a storage
    /// call failed), and a NAK whose most recent log
    /// contains the literal token <c>ERROR</c>, meaning the named storage
    /// device did not come up.
    /// <para>两种有据可循的形态归入此处：完全未写入任何属性的 NAK（某次存储调用失败）；以及最近一条
    /// 日志含字面记号 <c>ERROR</c> 的 NAK（对应存储设备未能起来）。</para>
    /// </summary>
    Storage,

    /// <summary>
    /// The target reported a digest-validation failure, which is treated as
    /// unrecoverable without resetting the target.
    /// <para>目标报告摘要校验失败；视之为不重置目标就无法恢复的状态。</para>
    /// The only textual error identifier recognised in a target log is
    /// <c>PACKET_HASH_MISMATCH</c>, whose handling states "There is no recovery from this. You need
    /// to reset your target".
    /// <para>目标日志中唯一识别的文本错误标识是 <c>PACKET_HASH_MISMATCH</c>，其处理逻辑写明
    /// “There is no recovery from this. You need to reset your target”。</para>
    /// </summary>
    Validation,

    /// <summary>
    /// The response value is neither an acknowledgement nor a NAK, for example a bare number; the
    /// raw text is preserved and no meaning is claimed.
    /// <para>响应取值既不是确认也不是 NAK（例如一个裸数字）；保留原始文本且不宣称任何含义。</para>
    /// The acknowledgement flag is cleared for such a value but no error code is assigned, so anything
    /// else stays unclassified by design.
    /// <para>该类取值只清除确认标志而不赋予任何错误码，因此其余取值按设计保持未分类。</para>
    /// </summary>
    Unrecognized
}

/// <summary>
/// Classified description of one rejected Firehose response, preserving the raw evidence the
/// classification was made from.
/// <para>对单个被拒绝 Firehose 响应的分类描述，并保留分类所依据的原始证据。</para>
/// </summary>
public sealed class FirehoseResponseError
{
    /// <summary>
    /// Gets the classification kind.
    /// <para>获取分类种类。</para>
    /// </summary>
    /// <value>The kind derived from the response shape and the preceding target log.
    /// <para>由响应形态与其之前的目标日志推导出的种类。</para></value>
    public required FirehoseErrorKind Kind { get; init; }

    /// <summary>
    /// Gets the verbatim <c>value</c> attribute of the rejected response.
    /// <para>获取被拒绝响应的 <c>value</c> 属性原文。</para>
    /// </summary>
    /// <value>The raw text exactly as received, or <see langword="null"/> when the response carried
    /// no <c>value</c> attribute. <para>按接收原样保留的文本；响应不含 <c>value</c> 属性时为
    /// <see langword="null"/>。</para></value>
    public string? RawValue { get; init; }

    /// <summary>
    /// Gets the <see cref="RawValue"/> parsed as a number, without assigning it any meaning.
    /// <para>获取把 <see cref="RawValue"/> 按数字解析的结果，不赋予任何含义。</para>
    /// </summary>
    /// <value>The parsed value, or <see langword="null"/> when the raw text is not an integer.
    /// <para>解析出的数值；原始文本不是整数时为 <see langword="null"/>。</para></value>
    public long? NumericValue { get; init; }

    /// <summary>
    /// Gets the evidence token matched in the preceding target log.
    /// <para>获取在目标日志中匹配到的证据记号。</para>
    /// </summary>
    /// <value><c>PACKET_HASH_MISMATCH</c> or <c>ERROR</c> when matched, otherwise
    /// <see langword="null"/>. <para>匹配到时为 <c>PACKET_HASH_MISMATCH</c> 或 <c>ERROR</c>，否则为
    /// <see langword="null"/>。</para></value>
    public string? LogToken { get; init; }

    /// <summary>
    /// Gets whether the rejected response assigned target-controlled attributes besides
    /// <c>value</c> and <c>rawmode</c>.
    /// <para>获取被拒绝响应是否除 <c>value</c> 与 <c>rawmode</c> 之外还写入了目标控制的属性。</para>
    /// </summary>
    /// <value><see langword="true"/> when at least one negotiation attribute is present.
    /// <para>至少存在一个协商属性时为 <see langword="true"/>。</para></value>
    public bool AssignsTargetAttributes { get; init; }

    /// <summary>
    /// Gets whether the evidence marks this response as unrecoverable without a new device session.
    /// <para>获取证据是否把该响应标记为必须新建设备会话才能恢复。</para>
    /// </summary>
    /// <value><see langword="true"/> only for <see cref="FirehoseErrorKind.Validation"/>, the one
    /// shape declared as having "no recovery". Every other kind keeps the existing
    /// session behaviour, because no oracle marks a plain NAK as fatal; a session even continues
    /// after a NAK that only renegotiates attributes.
    /// <para>仅当种类为 <see cref="FirehoseErrorKind.Validation"/> 时为 <see langword="true"/>，这是
    /// 唯一写明“无法恢复”的形态。其他种类均保持既有会话行为：没有任何权威把普通 NAK 定为致命，
    /// 仅重新协商属性的 NAK 之后会话甚至直接继续运行。</para></value>
    public bool IsFatal { get; init; }

    /// <summary>
    /// Gets a short human-readable description of the classification.
    /// <para>获取分类的简短可读描述。</para>
    /// </summary>
    /// <value>An English sentence naming the observed shape; diagnostic text only, never a
    /// substitute for <see cref="Kind"/>. <para>描述所观测形态的英文句子；仅用于诊断，不能替代
    /// <see cref="Kind"/>。</para></value>
    public required string Description { get; init; }

    /// <summary>
    /// Returns the kind with the preserved raw evidence.
    /// <para>返回分类种类与保留的原始证据。</para>
    /// </summary>
    /// <returns>A single-line diagnostic string. <para>单行诊断字符串。</para></returns>
    public override string ToString()
    {
        var raw = RawValue is { Length: > 0 } value ? $", Value={value}" : string.Empty;
        var token = LogToken is { Length: > 0 } matched ? $", Log={matched}" : string.Empty;
        return $"{Kind}{raw}{token}";
    }
}

/// <summary>
/// Maps a parsed <see cref="FirehoseResponse"/> plus the target log that preceded it onto a
/// <see cref="FirehoseResponseError"/>.
/// <para>把已解析的 <see cref="FirehoseResponse"/> 与其之前的目标日志映射为
/// <see cref="FirehoseResponseError"/>。</para>
/// </summary>
public static class FirehoseErrorClassifier
{
    private const string PacketHashMismatchToken = "PACKET_HASH_MISMATCH";
    private const string StorageErrorToken = "ERROR";

    /// <summary>Classifies one response.<para>对单个响应进行分类。</para></summary>
    /// <param name="response">The parsed response.<para>已解析的响应。</para></param>
    /// <param name="lastLogValue">The <c>value</c> text of the last target log observed before this response within the same document, or <see langword="null"/> when none is available.
    /// <para>同一文档内该响应之前最后一条目标日志的 <c>value</c> 文本；无可用日志时为 <see langword="null"/>。</para></param>
    /// <returns>The classification, or <see langword="null"/> when the response carries no rejection to classify (an acknowledgement, a raw-mode indication, or a log-only block).
    /// <para>分类结果；响应不含需要分类的拒绝（确认应答、原始模式指示或仅含日志的块）时为 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/>is <see langword="null"/>.
    /// <paramref name="response"/>为 <see langword="null"/>。</exception>
    public static FirehoseResponseError? Classify(FirehoseResponse response, string? lastLogValue = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsAck || response.IsRawMode || response.IsLog)
        {
            return null;
        }

        var rawValue = response.RawValue;
        long? numeric = long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
        var assignsAttributes = HasAssignedAttributes(response);

        if (ContainsToken(lastLogValue, PacketHashMismatchToken))
        {
            return new FirehoseResponseError
            {
                Kind = FirehoseErrorKind.Validation,
                RawValue = rawValue,
                NumericValue = numeric,
                LogToken = PacketHashMismatchToken,
                AssignsTargetAttributes = assignsAttributes,
                IsFatal = true,
                Description = "The target reported a packet digest mismatch; the reference downloader " +
                    "requires a target reset before continuing."
            };
        }

        if (!response.IsNak)
        {
            return new FirehoseResponseError
            {
                Kind = FirehoseErrorKind.Unrecognized,
                RawValue = rawValue,
                NumericValue = numeric,
                LogToken = null,
                AssignsTargetAttributes = assignsAttributes,
                IsFatal = false,
                Description = "The response value is neither an acknowledgement nor a NAK; no reference " +
                    "downloader assigns a meaning to it, so the raw value is preserved."
            };
        }

        if (!assignsAttributes)
        {
            return new FirehoseResponseError
            {
                Kind = FirehoseErrorKind.Storage,
                RawValue = rawValue,
                NumericValue = numeric,
                LogToken = null,
                AssignsTargetAttributes = false,
                IsFatal = false,
                Description = "The target rejected the command without assigning any response attributes; " +
                    "the reference downloader reads that shape as a failed storage call."
            };
        }

        if (lastLogValue is null)
        {
            return new FirehoseResponseError
            {
                Kind = FirehoseErrorKind.Rejected,
                RawValue = rawValue,
                NumericValue = numeric,
                LogToken = null,
                AssignsTargetAttributes = true,
                IsFatal = false,
                Description = "The target rejected the command and no preceding log is available to " +
                    "distinguish negotiation from a storage failure."
            };
        }

        return ContainsToken(lastLogValue, StorageErrorToken)
            ? new FirehoseResponseError
            {
                Kind = FirehoseErrorKind.Storage,
                RawValue = rawValue,
                NumericValue = numeric,
                LogToken = StorageErrorToken,
                AssignsTargetAttributes = true,
                IsFatal = false,
                Description = "The target rejected the command and its most recent log carries the " +
                    "storage error token the reference downloader reports as the device not coming up."
            }
            : new FirehoseResponseError
            {
                Kind = FirehoseErrorKind.AttributeNegotiation,
                RawValue = rawValue,
                NumericValue = numeric,
                LogToken = null,
                AssignsTargetAttributes = true,
                IsFatal = false,
                Description = "The target rejected the current parameters but supplied its own; the " +
                    "reference downloader continues after this shape."
            };
    }

    /// <summary>
    /// Checks whether the response carries at least one attribute the target assigned, ignoring the
    /// framing attributes <c>value</c> and <c>rawmode</c>.
    /// <para>检查响应是否携带至少一个由目标写入的属性，忽略 framing 属性 <c>value</c> 与 <c>rawmode</c>。</para>
    /// </summary>
    /// <param name="response">The parsed response. <para>已解析的响应。</para></param>
    /// <returns><see langword="true"/> when a negotiation attribute is present.
    /// <para>存在协商属性时为 <see langword="true"/>。</para></returns>
    private static bool HasAssignedAttributes(FirehoseResponse response)
    {
        foreach (var key in response.Attributes.Keys)
        {
            if (!string.Equals(key, "value", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "rawmode", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches a literal token with a case-sensitive substring search.
    /// <para>以区分大小写的子串查找匹配字面记号。</para>
    /// </summary>
    /// <param name="logValue">The log text, possibly <see langword="null"/>. <para>日志文本，可为 <see langword="null"/>。</para></param>
    /// <param name="token">The token to look for. <para>要查找的记号。</para></param>
    /// <returns><see langword="true"/> when the token occurs verbatim.
    /// <para>记号原样出现时为 <see langword="true"/>。</para></returns>
    private static bool ContainsToken(string? logValue, string token) =>
        logValue is not null && logValue.Contains(token, StringComparison.Ordinal);
}
namespace UotanStudio.Comm.Protocols.Common.Evidence;

/// <summary>
/// A single immutable observation produced by a protocol exchange.
/// <para>协议交互产生的一条不可变观测记录。</para>
/// Evidence records what the host side observed on the wire, not what the device internally
/// achieved; promoting an observation to a stronger claim requires evidence at a later phase.
/// Serialised results must retain the key fields so consumers do not have to parse log text.
/// <para>证据记录宿主侧在线上观察到什么，而非设备内部达成了什么；将观测提升为更强的结论
/// 需要后续阶段的证据。序列化后必须保留关键字段，消费者不得依赖日志文本解析。</para>
/// </summary>
public sealed record ProtocolEvidence
{
    /// <summary>
    /// Gets the protocol identifier, e.g. <c>edl</c> or <c>mtk</c>.
    /// <para>获取协议标识，例如 <c>edl</c> 或 <c>mtk</c>。</para>
    /// </summary>
    public required string Protocol { get; init; }

    /// <summary>
    /// Gets the phase name this observation belongs to, e.g. <c>sahara-hello</c> or <c>da-crc</c>.
    /// <para>获取该观测所属的阶段名，例如 <c>sahara-hello</c> 或 <c>da-crc</c>。</para>
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    /// Gets the observed outcome for this phase.
    /// <para>获取该阶段的观测结果。</para>
    /// </summary>
    public required CommandOutcomeKind Outcome { get; init; }

    /// <summary>
    /// Gets an optional human-readable, localization-neutral detail string.
    /// <para>获取可选的、与本地化无关的人类可读详情文本。</para>
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Gets the hex-encoded raw response bytes when byte-level identity matters.
    /// <para>当需要保留字节级身份时，获取十六进制编码的原始响应字节。</para>
    /// </summary>
    public string? RawResponseHex { get; init; }

    /// <summary>
    /// Gets the SHA-256 hex digest of the resource bound to this observation, if any.
    /// <para>获取与该观测绑定的资源 SHA-256 十六进制摘要（若有）。</para>
    /// </summary>
    public string? ResourceSha256Hex { get; init; }

    /// <summary>
    /// Gets the transport generation this observation was taken on, if the transport layer supplied one.
    /// <para>获取该观测所在的传输代际（若传输层提供了代际）。</para>
    /// </summary>
    public ulong? TransportGeneration { get; init; }

    /// <summary>
    /// Gets the UTC instant at which the observation was recorded.
    /// <para>获取记录该观测的 UTC 时刻。</para>
    /// </summary>
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the elapsed milliseconds attributed to this phase, when measured.
    /// <para>获取该阶段实测的毫秒耗时（若已测量）。</para>
    /// </summary>
    public long? ElapsedMs { get; init; }
}
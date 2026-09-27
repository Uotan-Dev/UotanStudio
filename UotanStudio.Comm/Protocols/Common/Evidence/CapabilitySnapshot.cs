namespace UotanStudio.Comm.Protocols.Common.Evidence;

/// <summary>
/// Base type for an immutable snapshot of the capabilities a device or programmer reported.
/// <para>设备或 programmer 上报能力的不可变快照基础类型。</para>
/// A snapshot binds capability fields to the resource identity and the transport generation they
/// were negotiated on, so a later plan can prove which configuration it was built against.
/// <para>快照将能力字段与其协商时所依据的资源身份和传输代际绑定，使后续计划能够证明
/// 自己是基于哪一份配置构建的。</para>
/// </summary>
public abstract record CapabilitySnapshot
{
    /// <summary>
    /// Gets the UTC instant at which the snapshot was captured.
    /// <para>获取捕获该快照的 UTC 时刻。</para>
    /// </summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>
    /// Gets the ordered evidence that produced this snapshot.
    /// <para>获取生成本快照的按序证据。</para>
    /// </summary>
    public required IReadOnlyList<ProtocolEvidence> Evidence { get; init; }

    /// <summary>
    /// Gets the SHA-256 hex digest of the programmer, DA, or loader whose capabilities are described,
    /// when the snapshot is bound to a specific resource.
    /// <para>获取本快照所绑定的 programmer、DA 或 loader 的 SHA-256 十六进制摘要；
    /// 未绑定具体资源时为 null。</para>
    /// </summary>
    public string? ResourceSha256Hex { get; init; }

    /// <summary>
    /// Gets the transport generation this snapshot was negotiated on, when supplied by the transport layer.
    /// <para>获取本快照协商所在的传输代际（若传输层提供了代际）。</para>
    /// </summary>
    public ulong? TransportGeneration { get; init; }
}
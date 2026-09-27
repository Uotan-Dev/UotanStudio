using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// The kind of one flash-bundle storage item.
/// <para>刷写包单个存储条目的种类。</para>
/// </summary>
public enum FlashBundleItemKind
{
    /// <summary>
    /// A &lt;program&gt; payload transfer. <para>&lt;program&gt; 载荷传输。</para>
    /// </summary>
    Program,

    /// <summary>
    /// An &lt;erase&gt; request derived from a filename-less program entry.
    /// <para>由无文件名 program 条目派生的 &lt;erase&gt; 请求。</para>
    /// </summary>
    Erase,

    /// <summary>
    /// A &lt;patch&gt; write. <para>&lt;patch&gt; 写入。</para>
    /// </summary>
    Patch,

    /// <summary>
    /// A leading-sector wipe. <para>前导扇区擦写。</para>
    /// </summary>
    Wipe,

    /// <summary>
    /// The UFS provisioning command sequence. <para>UFS provisioning 命令序列。</para>
    /// </summary>
    UfsProvision,

    /// <summary>
    /// A device-range preflight validation over one physical partition.
    /// <para>针对单个物理分区的设备范围预检验证。</para>
    /// </summary>
    Preflight
}

/// <summary>
/// Structured evidence for one flash-bundle storage item: what was attempted, where, and how the
/// observation ended.
/// <para>单个刷写包存储条目的结构化证据：尝试了什么、位于何处、观测如何结束。</para>
/// The <see cref="Outcome"/> distinguishes a device rejection (storage unchanged) from a
/// cancellation after bytes were already streamed (<see cref="CommandOutcomeKind.Cancelled"/> plus
/// <see cref="RequiresReconnect"/>) and from a lost completion acknowledgement
/// (<see cref="CommandOutcomeKind.Unknown"/>). Consumers must branch on these values instead of
/// parsing error text.
/// <para><see cref="Outcome"/> 区分设备拒绝（存储未改变）、字节已流式传输后的取消
/// （<see cref="CommandOutcomeKind.Cancelled"/> 且 <see cref="RequiresReconnect"/>）与完成确认丢失
/// （<see cref="CommandOutcomeKind.Unknown"/>）。消费者必须依据这些值分支，而不是解析错误文本。</para>
/// </summary>
public sealed record FlashItemEvidence
{
    /// <summary>
    /// Gets the item kind.
    /// <para>获取条目种类。</para>
    /// </summary>
    public required FlashBundleItemKind Kind { get; init; }

    /// <summary>
    /// Gets the source file name, when the item carries one.
    /// <para>获取条目携带的源文件名（若有）。</para>
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Gets the manifest label, when present.
    /// <para>获取清单标签（若存在）。</para>
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Gets the physical range the item addressed, when the item is range-bound.
    /// <para>获取条目寻址的物理范围（若条目与范围绑定）。</para>
    /// </summary>
    public EdlPhysicalRange? Range { get; init; }

    /// <summary>
    /// Gets the observed outcome for this item.
    /// <para>获取本条目的观测结果。</para>
    /// </summary>
    public required CommandOutcomeKind Outcome { get; init; }

    /// <summary>
    /// Gets the typed failure classification, when the item failed.
    /// <para>获取条目失败时的类型化失败分类。</para>
    /// </summary>
    public EdlFailureKind? FailureKind { get; init; }

    /// <summary>
    /// Gets whether a device reconnect is required after this item's observation.
    /// <para>获取本条目的观测之后是否需要设备重连。</para>
    /// </summary>
    public bool RequiresReconnect { get; init; }

    /// <summary>
    /// Gets the payload bytes with a final device acknowledgement for this item.
    /// <para>获取本条目获得设备最终确认的载荷字节数。</para>
    /// </summary>
    public long AcknowledgedBytes { get; init; }

    /// <summary>
    /// Gets the elapsed milliseconds attributed to this item, when measured.
    /// <para>获取本条目实测的毫秒耗时（若已测量）。</para>
    /// </summary>
    public long? ElapsedMs { get; init; }

    /// <summary>
    /// Projects this item onto the shared evidence vocabulary.
    /// <para>把本条目投影到共享证据词汇。</para>
    /// </summary>
    /// <returns>The protocol evidence entry. <para>协议证据条目。</para></returns>
    internal ProtocolEvidence ToEvidence() => new()
    {
        Protocol = "edl",
        Phase = Kind.ToString().ToLowerInvariant(),
        Outcome = Outcome,
        Detail = $"{FileName ?? Label ?? Kind.ToString()} range={Range?.ToString() ?? "-"}",
        ObservedAtUtc = ObservedAtUtc,
        ElapsedMs = ElapsedMs
    };

    /// <summary>
    /// Gets the UTC instant at which the item observation ended.
    /// <para>获取条目观测结束的 UTC 时刻。</para>
    /// </summary>
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
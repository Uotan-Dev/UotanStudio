namespace UotanStudio.Services.Flash;

/// <summary>
/// Optional tuning values for an EDL bundle run; each one maps directly onto a
/// <c>FlashBundleOptions</c> field, and an absent value keeps the orchestrator's existing default.
/// <para>EDL 整包刷写的可选调优参数；三者都直接落在 <c>FlashBundleOptions</c> 上，
/// 未提供时保持编排器的既有默认行为。</para>
/// </summary>
public sealed record EdlBundleTuning
{
    /// <summary>
    /// Sector size in bytes that takes precedence over manifest and configured values; null means auto.
    /// <para>优先于清单与配置值的扇区大小（字节）；<see langword="null"/> 表示自动。</para>
    /// </summary>
    /// <value>Overrides inconsistent sector declarations; must be between 1 and 65536 and match the target-reported size. <para>覆盖不一致的扇区声明；必须在 1 到 65536 之间且与目标上报值一致。</para></value>
    public long? OverrideSectorSize { get; init; }

    /// <summary>
    /// A/B slot written to the Firehose configuration before programming; null keeps the current one.
    /// <para>编程前写入 Firehose 配置的 A/B 槽位；<see langword="null"/> 表示沿用当前配置。</para>
    /// </summary>
    /// <value>Zero means the slot attribute is not emitted; 1 and 2 select the A and B slots. <para>0 表示不输出槽位属性，1 与 2 对应 A/B 槽。</para></value>
    public int? Slot { get; init; }

    /// <summary>
    /// Leading sectors zeroed on every touched physical partition after programming or erasing; 0 disables it.
    /// <para>编程/擦除之后对涉及的每个物理分区以零擦写的前导扇区数；0 表示不擦写。</para>
    /// </summary>
    /// <value>Non-negative; overflow or negative values are rejected by the pre-check. <para>非负数；溢出或负值由预检拒绝。</para></value>
    public long WipeFirstSectors { get; init; }

    /// <summary>
    /// The write-chunk bound in bytes written to the session configuration; null keeps the default 1 MiB
    /// ceiling.
    /// <para>写入会话配置的写入块上限（字节）；<see langword="null"/> 表示沿用默认 1 MiB 硬上限。</para>
    /// </summary>
    /// <value>A multiple of 512 between 512 and 16777216 inclusive; above the programmer-reported support the protocol still refuses. <para>512 的倍数且介于 512 与 16777216（含）之间；超过 programmer 上报的支持值时协议仍会拒绝。</para></value>
    public long? MaxProgramChunkBytes { get; init; }

    /// <summary>
    /// The NAND <c>PAGES_PER_BLOCK</c> configure attribute; null or 0 omits the attribute.
    /// <para>NAND 的 <c>PAGES_PER_BLOCK</c> configure 属性；<see langword="null"/> 或 0 表示不输出该属性。</para>
    /// </summary>
    /// <value>0 to 65535; goes out with configure only when the storage is NAND. <para>0 至 65535；仅在存储为 NAND 时随 configure 上线。</para></value>
    public long? NandPagesPerBlock { get; init; }

    /// <summary>
    /// Whether the bundle's own <c>&lt;wipe&gt;</c> requests run; when false each one is refused
    /// before any wipe byte is sent.
    /// <para>清单自带的 <c>&lt;wipe&gt;</c> 请求是否需要执行；false 时编排器在发出任何擦写字节前逐条拒绝。</para>
    /// </summary>
    /// <value>True only when the wipe is explicitly confirmed together with the expert switch; wiping is irreversible. <para>仅当擦写被显式确认且 expert 开关同时开启时为 true；擦写不可逆。</para></value>
    public bool ConfirmManifestWipe { get; init; }

    /// <summary>
    /// Whether a wipe request spanning the whole storage, partition table included, may run; the second gate for
    /// requests whose range covers the whole device.
    /// <para>是否允许覆盖整片存储的擦写请求（含分区表）；范围覆盖多分区时的第二道门。</para>
    /// </summary>
    /// <value>Default false; when off, wipe requests covering the whole storage are refused and only that request is affected. <para>默认 false；关闭时整盘范围的擦写请求一律被拒绝，只影响该条请求。</para></value>
    public bool AllowEntireStorageWipe { get; init; }
}
namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>EDL 整包刷写的可选调优参数；三者都直接落在 <c>FlashBundleOptions</c> 上，
/// 未提供时保持编排器的既有默认行为。</para>
/// Optional tuning values for an EDL bundle run; each one maps directly onto a
/// <c>FlashBundleOptions</c> field, and an absent value keeps the orchestrator's existing default.
/// </summary>
public sealed record EdlBundleTuning
{
    /// <summary>
    /// <para>优先于清单与配置值的扇区大小（字节）；<see langword="null"/> 表示自动。</para>
    /// Sector size in bytes that takes precedence over manifest and configured values; null means auto.
    /// </summary>
    /// <value>用于覆盖清单里彼此不一致、或与配置不符的扇区声明；值必须在 1..65536 内，
    /// 且必须与目标上报的扇区大小一致，否则设备范围预检会拒绝该计划。
    /// <para>用于覆盖清单里彼此不一致、或与配置不符的扇区声明；值必须在 1..65536 内，
    /// 且必须与目标上报的扇区大小一致，否则设备范围预检会拒绝该计划。</para></value>
    public long? OverrideSectorSize { get; init; }

    /// <summary>
    /// <para>编程前写入 Firehose 配置的 A/B 槽位；<see langword="null"/> 表示沿用当前配置。</para>
    /// A/B slot written to the Firehose configuration before programming; null keeps the current one.
    /// </summary>
    /// <value>0 表示命令不输出槽位属性，1 与 2 对应 A/B 槽。 <para>0 表示命令不输出槽位属性，1 与 2 对应 A/B 槽。</para></value>
    public int? Slot { get; init; }

    /// <summary>
    /// <para>编程/擦除之后对涉及的每个物理分区以零擦写的前导扇区数；0 表示不擦写。</para>
    /// Leading sectors zeroed on every touched physical partition after programming or erasing; 0 disables it.
    /// </summary>
    /// <value>非负数；溢出或负值由预检拒绝。 <para>非负数；溢出或负值由预检拒绝。</para></value>
    public long WipeFirstSectors { get; init; }
}
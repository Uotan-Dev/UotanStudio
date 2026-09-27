using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk.Storage;

/// <summary>
/// One physical region inside a <see cref="MtkStorageCapabilitySnapshot"/> with its reported
/// identity, capacity and per-operation permissions.
/// <para><see cref="MtkStorageCapabilitySnapshot"/> 内的一个物理区域，携带其上报身份、容量与各操作权限。</para>
/// Every permission defaults to <see cref="FeatureSupport.Unknown"/> so an omitted or unverifiable
/// report fails closed; only <see cref="FeatureSupport.Supported"/> grants the operation, and an
/// <see cref="AuthenticatedOnly"/> region is never ordinary storage regardless of its permissions.
/// <para>所有权限默认 <see cref="FeatureSupport.Unknown"/>，缺失或无法核实的上报按失败关闭处理；
/// 只有 <see cref="FeatureSupport.Supported"/> 才授予操作，而 <see cref="AuthenticatedOnly"/> 区域无论权限如何都不是普通存储。</para>
/// </summary>
public sealed record MtkStorageRegionCapability
{
    /// <summary>
    /// Gets the unified role of the region.
    /// <para>获取区域的统一角色。</para>
    /// </summary>
    /// <value>
    /// The identity established by the reporting profile, or <see cref="MtkStorageRegionKind.Unknown"/>
    /// when the profile addresses the region without naming it.
    /// <para>上报配置确定的身份；配置可寻址但未命名该区域时为 <see cref="MtkStorageRegionKind.Unknown"/>。</para>
    /// </value>
    public required MtkStorageRegionKind Kind { get; init; }

    /// <summary>
    /// Gets the stable canonical name of the region.
    /// <para>获取区域的稳定规范名称。</para>
    /// </summary>
    /// <value>
    /// A profile-neutral token such as <c>EMMC-USER</c>, or an explicitly source-qualified name for
    /// regions whose role the profile never identified; unique across the snapshot.
    /// <para>与配置无关的标记（如 <c>EMMC-USER</c>），或上报配置从未确定角色的区域的显式来源限定名；在快照内唯一。</para>
    /// </value>
    public required string CanonicalName { get; init; }

    /// <summary>
    /// Gets the source-native physical partition identifier, when the profile used one.
    /// <para>获取来源原生的物理分区标识符（若配置使用）。</para>
    /// </summary>
    /// <value>
    /// The XFlash or Legacy partition identifier, or <see langword="null"/> for XML regions and
    /// whole-device NAND/NOR reports; unique among non-null values in the snapshot.
    /// <para>XFlash 或 Legacy 分区标识符；XML 区域以及整片 NAND/NOR 报告为 <see langword="null"/>；非空值在快照内唯一。</para>
    /// </value>
    public uint? PhysicalIdentifier { get; init; }

    /// <summary>
    /// Gets the reported byte capacity.
    /// <para>获取上报的字节容量。</para>
    /// </summary>
    /// <value>
    /// A nonnegative byte count, or <see langword="null"/> when the profile did not report a capacity
    /// bound for the region; a reported <c>0</c> means the region is present but empty.
    /// <para>非负字节数；配置未上报该区域容量界限时为 <see langword="null"/>；上报为 <c>0</c> 表示区域存在但为空。</para>
    /// </value>
    public long? CapacityBytes { get; init; }

    /// <summary>
    /// Gets whether ordinary reads of this region are reported as available.
    /// <para>获取该区域的普通读取是否被上报为可用。</para>
    /// </summary>
    public FeatureSupport ReadSupport { get; init; } = FeatureSupport.Unknown;

    /// <summary>
    /// Gets whether ordinary writes of this region are reported as available.
    /// <para>获取该区域的普通写入是否被上报为可用。</para>
    /// </summary>
    public FeatureSupport WriteSupport { get; init; } = FeatureSupport.Unknown;

    /// <summary>
    /// Gets whether device-side erase of this region is reported as available.
    /// <para>获取该区域的设备端擦除是否被上报为可用。</para>
    /// </summary>
    public FeatureSupport EraseSupport { get; init; } = FeatureSupport.Unknown;

    /// <summary>
    /// Gets whether the region is reserved for a separate authenticated protocol.
    /// <para>获取该区域是否保留给独立的已认证协议。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> only for replay-protected regions; ordinary block access must refuse them.
    /// <para>仅重放保护区域为 <see langword="true"/>；普通块访问必须拒绝这些区域。</para>
    /// </value>
    public bool AuthenticatedOnly { get; init; }

    /// <summary>
    /// Gets the reported byte alignment ordinary transfers must satisfy.
    /// <para>获取普通传输必须满足的上报字节对齐。</para>
    /// </summary>
    /// <value>
    /// A positive alignment in bytes, or <see langword="null"/> when the profile imposed none for this
    /// region; NAND main-area transfers align to the page size reported by <see cref="MtkNandCapability"/>.
    /// <para>以字节计的正数对齐值；配置对本区域未施加对齐时为 <see langword="null"/>；
    /// NAND 主区传输按 <see cref="MtkNandCapability"/> 上报的页大小对齐。</para>
    /// </value>
    public int? AlignmentBytes { get; init; }

    /// <summary>
    /// Gets whether ordinary writes may target this region under the current report.
    /// <para>获取按当前报告普通写入是否可指向本区域。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> only when writes are positively supported, the region is not
    /// authenticated-only, and a positive capacity was reported; unknown or absent reports fail closed.
    /// <para>仅当明确支持写入、区域不需认证且上报了正数容量时为 <see langword="true"/>；未知或缺失的报告按失败关闭处理。</para>
    /// </value>
    public bool IsOrdinaryWritable => !AuthenticatedOnly
        && Kind != MtkStorageRegionKind.Rpmb
        && WriteSupport == FeatureSupport.Supported
        && CapacityBytes > 0;
}
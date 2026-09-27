namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Selects an explicit Legacy erase wire profile.
/// <para>选择显式的 Legacy 擦除线协议配置。</para></summary>
public enum MtkLegacyEraseProfile
{
    /// <summary>64-bit SDK D4 without phone-info exchange, with one initial ACK pair and status-led progress.
    /// <para>不交换 phone-info 的 64 位 SDK D4，使用一对初始 ACK 和以状态字为准的进度。</para></summary>
    Sdk64
}

/// <summary>Defines the SDK erase response profile without automatic protocol fallback.
/// <para>定义 SDK 擦除响应配置，不自动回退协议。</para></summary>
public sealed record MtkLegacyEraseOptions
{
    /// <summary>Gets the caller-selected wire profile.
    /// <para>获取调用方选择的线协议配置。</para></summary>
    /// <value>The explicit SDK 64-bit profile; not the repeated-ACK compatibility layout.
    /// <para>显式的 SDK 64 位配置，而非重复 ACK 的兼容布局。</para></value>
    public required MtkLegacyEraseProfile Profile { get; init; }

    /// <summary>Gets whether to request device-side validation after erasing.
    /// <para>获取是否请求擦除后的设备端校验。</para></summary>
    /// <value>The Boolean validation byte sent to the agent; this is not host readback verification.
    /// <para>发送给代理的布尔校验字节；这不代表主机回读验证。</para></value>
    public bool ValidateAfterErase { get; init; }

    /// <summary>Gets the exact optional statistics marker supplied by the loader profile.
    /// <para>获取加载器配置提供的精确可选统计标记。</para></summary>
    /// <value>Null rejects statistics; otherwise the exact marker preceding a 28-byte report.
    /// ACK 0x5A is reserved for completion without statistics.
    /// <para>null 表示拒绝统计报告；否则为 28 字节报告前的精确标记。
    /// ACK 0x5A 保留用于无统计报告的完成响应。</para></value>
    public byte? StatisticsMarker { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(Profile) || StatisticsMarker == 0x5A)
        {
            throw new ArgumentOutOfRangeException(nameof(MtkLegacyEraseOptions));
        }
    }
}

/// <summary>Selects the explicitly requested Legacy NAND erase action.
/// <para>选择显式请求的 Legacy NAND 擦除动作。</para></summary>
public enum MtkLegacyNandEraseMode : byte
{
    /// <summary>Normal erase under the agent's bad-block policy. <para>按代理坏块策略进行普通擦除。</para></summary>
    Normal,
    /// <summary>Force erase, never selected by retry logic. <para>强制擦除，不由重试逻辑自动选择。</para></summary>
    Force,
    /// <summary>Explicitly mark blocks bad. <para>显式将块标记为坏块。</para></summary>
    MarkBadBlock
}

/// <summary>Defines explicit Legacy NAND erase addressing and operation choices.
/// <para>定义显式的 Legacy NAND 擦除寻址和操作选择。</para></summary>
public sealed record MtkLegacyNandEraseOptions
{
    /// <summary>Gets the selected wire profile and completion policy.
    /// <para>获取选定的线协议配置和完成策略。</para></summary>
    /// <value>The required immutable SDK erase options.
    /// <para>必需的不可变 SDK 擦除配置。</para></value>
    public required MtkLegacyEraseOptions Wire { get; init; }

    /// <summary>Gets the selected address interpretation.
    /// <para>获取选定的地址解释方式。</para></summary>
    /// <value>Logical or physical, never inferred from BMT presence.
    /// <para>逻辑或物理，不根据 BMT 是否存在推测。</para></value>
    public required MtkLegacyNandAddressType AddressType { get; init; }

    /// <summary>Gets the explicitly requested erase action.
    /// <para>获取显式请求的擦除动作。</para></summary>
    /// <value>Normal, force or mark-bad-block.
    /// <para>普通、强制或坏块标记。</para></value>
    public required MtkLegacyNandEraseMode Mode { get; init; }

    /// <summary>Gets the independently selected upper bound for logical main-area addressing.
    /// <para>获取独立选定的逻辑主区寻址上界。</para></summary>
    /// <value>A positive erase-block multiple for logical access; null for physical access.
    /// <para>逻辑访问时为正数擦除块大小倍数；物理访问时为 null。</para></value>
    public long? LogicalCapacityBytes { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Wire);
        Wire.Validate();
        if (!Enum.IsDefined(AddressType) || !Enum.IsDefined(Mode)
            || (AddressType == MtkLegacyNandAddressType.Logical
                ? LogicalCapacityBytes is null or <= 0 : LogicalCapacityBytes is not null))
        {
            throw new ArgumentOutOfRangeException(nameof(MtkLegacyNandEraseOptions));
        }
    }
}
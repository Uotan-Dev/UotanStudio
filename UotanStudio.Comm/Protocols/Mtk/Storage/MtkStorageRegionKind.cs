namespace UotanStudio.Comm.Protocols.Mtk.Storage;

/// <summary>
/// The role a physical storage region plays in the unified capability contract.
/// <para>物理存储区域在统一能力契约中承担的角色。</para>
/// <see cref="Unknown"/> marks regions the reporting wire profile addresses but never names; they
/// keep their reported geometry but must never be conflated with <see cref="EmmcUser"/> storage.
/// <para><see cref="Unknown"/> 标记上报配置可寻址但从未命名的区域；它们保留所上报的几何信息，
/// 但绝不可与 <see cref="EmmcUser"/> 用户存储混同。</para>
/// </summary>
public enum MtkStorageRegionKind
{
    /// <summary>Addressable but never identified by the reporting profile. <para>可寻址，但上报配置从未确定其身份。</para></summary>
    Unknown = 0,
    /// <summary>The ordinary eMMC user data region. <para>普通 eMMC 用户数据区域。</para></summary>
    EmmcUser,
    /// <summary>The eMMC hardware boot area 1; not ordinary user storage. <para>eMMC 硬件引导区 1；不是普通用户存储。</para></summary>
    EmmcBoot1,
    /// <summary>The eMMC hardware boot area 2; not ordinary user storage. <para>eMMC 硬件引导区 2；不是普通用户存储。</para></summary>
    EmmcBoot2,
    /// <summary>An eMMC general-purpose partition. <para>eMMC 通用分区。</para></summary>
    EmmcGeneralPurpose,
    /// <summary>A UFS logical unit exposed as ordinary block storage. <para>作为普通块存储暴露的 UFS 逻辑单元。</para></summary>
    UfsLogicalUnit,
    /// <summary>
    /// The replay-protected region of eMMC or the protected LU of UFS; never ordinary storage.
    /// <para>eMMC 重放保护区域或 UFS 受保护逻辑单元；绝不是普通存储。</para>
    /// </summary>
    Rpmb,
    /// <summary>The NAND main area addressed through dedicated page-stream methods.
    /// <para>通过专用页流方法寻址的 NAND 主区。</para></summary>
    NandMainArea,
    /// <summary>The NOR whole-device region. <para>NOR 整片设备区域。</para></summary>
    NorWholeDevice
}
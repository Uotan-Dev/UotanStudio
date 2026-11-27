namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Names the eight Legacy eMMC physical regions a MediaTek boot agent addresses, using the vendor's own
/// <c>EMMC_PART_*</c> numbering rather than a zero-based slot index.
/// <para>为联发科引导代理所寻址的八个 Legacy eMMC 物理区域命名，使用厂商自身的 <c>EMMC_PART_*</c> 编号，
/// 而不是从零开始的槽位索引。</para>
/// </summary>
/// <remarks>
/// The numbering is fixed by the boot agent: <c>1</c> and <c>2</c> are the two boot areas, <c>3</c> is the
/// replay-protected block, <c>4</c> through <c>7</c> are the general-purpose areas and <c>8</c> is the user area
/// that carries the partition table. Only the user area holds a GPT or MBR; the boot areas begin with a
/// <see cref="UotanStudio.Firmware.BlockImage.MediaTek.MtkBromLayoutHeader"/> prefix instead, so reading a
/// partition table from them reports a layout header rather than a table.
/// <para>该编号由引导代理固定：<c>1</c> 与 <c>2</c> 是两块引导区，<c>3</c> 是重放保护块，<c>4</c> 至 <c>7</c>
/// 是通用区，<c>8</c> 是承载分区表的用户区。只有用户区持有 GPT 或 MBR；引导区以
/// <see cref="UotanStudio.Firmware.BlockImage.MediaTek.MtkBromLayoutHeader"/> 前缀开始，因此从引导区读取分区表
/// 会得到布局头而不是分区表。</para>
/// </remarks>
public static class MtkLegacyEmmcRegion
{
    /// <summary>The first boot area; it holds a boot image, not a partition table.
    /// <para>第一块引导区，持有引导镜像而非分区表。</para>
    /// </summary>
    public const byte Boot1 = 1;

    /// <summary>The second boot area; it holds a boot image, not a partition table.
    /// <para>第二块引导区，持有引导镜像而非分区表。</para>
    /// </summary>
    public const byte Boot2 = 2;

    /// <summary>The replay-protected memory block, which requires a separate authenticated protocol.
    /// <para>重放保护存储块，需要单独的认证协议。</para>
    /// </summary>
    public const byte Rpmb = 3;

    /// <summary>The first general-purpose area.
    /// <para>第一个通用区。</para>
    /// </summary>
    public const byte GeneralPurpose1 = 4;

    /// <summary>The second general-purpose area.
    /// <para>第二个通用区。</para>
    /// </summary>
    public const byte GeneralPurpose2 = 5;

    /// <summary>The third general-purpose area.
    /// <para>第三个通用区。</para>
    /// </summary>
    public const byte GeneralPurpose3 = 6;

    /// <summary>The fourth general-purpose area.
    /// <para>第四个通用区。</para>
    /// </summary>
    public const byte GeneralPurpose4 = 7;

    /// <summary>The user area, which is the only region that normally carries a GPT or MBR.
    /// <para>用户区，通常也是唯一承载 GPT 或 MBR 的区域。</para>
    /// </summary>
    public const byte User = 8;

    /// <summary>Renders a region byte as the vendor's symbolic name.
    /// <para>将区域字节呈现为厂商的符号名。</para>
    /// </summary>
    /// <param name="partitionId">The Legacy partition byte, normally 1 through 8.
    /// <para>Legacy 分区字节，通常为 1 至 8。</para></param>
    /// <returns>The symbolic name, or a hexadecimal fallback for an out-of-range byte.
    /// <para>符号名；越界字节时返回十六进制回退形式。</para></returns>
    public static string Describe(byte partitionId) => partitionId switch
    {
        Boot1 => "BOOT1",
        Boot2 => "BOOT2",
        Rpmb => "RPMB",
        GeneralPurpose1 => "GP1",
        GeneralPurpose2 => "GP2",
        GeneralPurpose3 => "GP3",
        GeneralPurpose4 => "GP4",
        User => "USER",
        _ => "0x" + partitionId.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
    };
}

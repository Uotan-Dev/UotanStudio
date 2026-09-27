using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk.Profile;

/// <summary>
/// The container-generation hint read from the DA header marker; it is not a protocol capability
/// probe and never grants compatibility by itself.
/// <para>从 DA 头部标记读出的容器代系提示；它不是协议能力探测，也绝不单独构成兼容性结论。</para>
/// </summary>
public enum MtkDaKind
{
    /// <summary>
    /// The header carries no <c>MTK_DA_v6</c> marker.
    /// <para>头部不含 <c>MTK_DA_v6</c> 标记。</para>
    /// </summary>
    V5,

    /// <summary>
    /// The header carries the <c>MTK_DA_v6</c> marker.
    /// <para>头部含有 <c>MTK_DA_v6</c> 标记。</para>
    /// </summary>
    V6
}

/// <summary>
/// A validated download-agent directory entry promoted from the Firmware container reader, with
/// bounded region geometry. Entry names and model strings are deliberately absent: selection is
/// made from hardware identity and revision fields only, never from file naming.
/// <para>由 Firmware 容器读取器提升、带区域几何边界校验的下载代理目录条目。此处刻意不含条目名与机型字符串：
/// 选择仅依据硬件身份与版本字段，绝不依据文件命名。</para>
/// </summary>
/// <param name="EntryIndex">The zero-based directory index. <para>从零开始的目录索引。</para></param>
/// <param name="HardwareCode">The target hardware code; zero means the entry lacks identity.
/// <para>目标硬件代码；零表示该条目缺少身份信息。</para></param>
/// <param name="HardwareSubCode">The hardware subcode. <para>硬件子代码。</para></param>
/// <param name="HardwareVersion">The hardware revision the entry declares. <para>条目声明的硬件修订版本。</para></param>
/// <param name="SoftwareVersion">
/// The declared software revision, or <see langword="null"/> in the legacy directory layout.
/// <para>声明的软件修订版本；在 legacy 目录布局中为 <see langword="null"/>。</para></param>
/// <param name="PageSize">The unmodified page-size field. <para>未经改写的页大小字段。</para></param>
/// <param name="Kind">The header generation marker. <para>头部代系标记。</para></param>
/// <param name="Regions">The validated read-only region directory, with at most ten regions.
/// <para>经过校验的只读区域目录，最多十个区域。</para></param>
/// <param name="EmiLinked">Whether an EMI/DRAM companion resource was explicitly bound to this
/// entry; a DA container alone never implies one. <para>是否有显式绑定到该条目的 EMI/DRAM 伴随资源；仅凭 DA 容器本身绝不意味着存在。</para></param>
public sealed record MtkDaDescriptor(
    int EntryIndex,
    ushort HardwareCode,
    ushort HardwareSubCode,
    ushort HardwareVersion,
    ushort? SoftwareVersion,
    ushort PageSize,
    MtkDaKind Kind,
    IReadOnlyList<MtkDaRegion> Regions,
    bool EmiLinked = false)
{
    /// <summary>
    /// Gets the region indices that pass the bounded stage-one (DA1) and stage-two (DA2) geometry
    /// validation, or <see langword="null"/> pairs when the entry lacks a complete usable pair.
    /// <para>获取通过有界的 stage-one（DA1）与 stage-two（DA2）几何校验的区域索引；当条目缺少完整可用配对时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>
    /// The ordered usable pair, or <see langword="null"/>.
    /// <para>有序可用配对，或 <see langword="null"/>。</para>
    /// </value>
    public (int StageOne, int StageTwo)? UsableRegionPair
    {
        get
        {
            var stageOne = -1;
            for (var index = 0; index < Regions.Count; index++)
            {
                if (stageOne < 0 && IsStageOneUsable(Regions[index]))
                {
                    stageOne = index;
                    continue;
                }

                if (stageOne >= 0 && index != stageOne && IsStageTwoUsable(Regions[index]))
                {
                    return (stageOne, index);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Validates the DA1 shape: non-empty, at most 64 MiB, even length, word-aligned load address,
    /// no 32-bit range overflow, and a signature not longer than the region.
    /// <para>校验 DA1 形状：非空、至多 64 MiB、长度为偶数、加载地址按字对齐、不溢出 32 位范围，
    /// 且签名不长于区域。</para>
    /// </summary>
    /// <param name="region">
    /// The candidate region. <para>候选区域。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when every bound holds. <para>全部边界成立时为 <see langword="true"/>。</para>
    /// </returns>
    public static bool IsStageOneUsable(MtkDaRegion region) =>
        region.Length > 0 && region.Length <= 64u * 1024 * 1024 && (region.Length & 1u) == 0
        && (region.LoadAddress & 3u) == 0
        && (ulong)region.LoadAddress + region.Length <= (ulong)uint.MaxValue + 1
        && region.SignatureLength <= region.Length;

    /// <summary>
    /// Validates the DA2 shape: non-empty, at most 64 MiB, word-aligned load address, and a
    /// signature not longer than the region.
    /// <para>校验 DA2 形状：非空、至多 64 MiB、加载地址按字对齐，且签名不长于区域。</para>
    /// </summary>
    /// <param name="region">
    /// The candidate region. <para>候选区域。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when every bound holds. <para>全部边界成立时为 <see langword="true"/>。</para>
    /// </returns>
    public static bool IsStageTwoUsable(MtkDaRegion region) =>
        region.Length > 0 && region.Length <= 64u * 1024 * 1024
        && (region.LoadAddress & 3u) == 0
        && region.SignatureLength <= region.Length;
}
namespace UotanStudio.Firmware.BootSecurity.BootImage.Models;

/// <summary>
/// Partition role an Android boot image (<c>ANDROID!</c> magic) is intended to fulfill, per the AOSP Generic Kernel
/// Image rules for <c>boot</c>, <c>init_boot</c> and <c>recovery</c>.
/// <para>Android Boot 镜像（魔数 <c>ANDROID!</c>）按 AOSP GKI 规则承担的分区角色：<c>boot</c>、<c>init_boot</c> 或 <c>recovery</c>。</para>
/// </summary>
public enum BootImageRole
{
    /// <summary>Standard <c>boot</c> partition image carrying a kernel; any header version 0 through 4.
    /// <para>标准 <c>boot</c> 分区镜像，携带 kernel；头部版本 0 至 4 均可能。</para></summary>
    Boot,

    /// <summary><c>init_boot</c> partition image: header version 4 with a generic ramdisk and a mandatory empty
    /// kernel segment; the ramdisk is typically an lz4_legacy cpio archive.
    /// <para><c>init_boot</c> 分区镜像：头部版本 4，包含 generic ramdisk 且 kernel 区段必须为空；ramdisk 通常为 lz4_legacy cpio 归档。</para></summary>
    InitBoot,

    /// <summary><c>recovery</c> image: header versions 0 through 2 in the classic layout, or the vendor-boot variant
    /// where recovery resources ride in a vendor boot image instead.
    /// <para><c>recovery</c> 镜像：经典布局使用头部版本 0 至 2；厂商变体则把 recovery 资源放入 vendor boot 镜像。</para></summary>
    Recovery
}

/// <summary>
/// Resolves the <see cref="BootImageRole"/> of a boot image from its header version, the emptiness of the kernel
/// and ramdisk segments, and — when available — the partition-name hint carried by the source metadata.
/// <para>依据头部版本、kernel 与 ramdisk 区段的空性以及来源元数据提供的分区名提示（如有），解析镜像的 <see cref="BootImageRole"/>。</para>
/// </summary>
/// <remarks>
/// A/B slot suffixes (<c>_a</c>, <c>_b</c>) are stripped before the hint is compared, so <c>init_boot_b</c> matches
/// the <c>init_boot</c> rule. A partition hint takes precedence over the content heuristic; without a hint, only an
/// unambiguous content signature (version 4 with an empty kernel and a non-empty ramdisk) yields <see cref="BootImageRole.InitBoot"/>.
/// <para>比较提示前先剥离 A/B 槽位后缀（<c>_a</c>、<c>_b</c>），因此 <c>init_boot_b</c> 命中 <c>init_boot</c> 规则。分区名提示优先于内容启发式；
/// 无提示时，仅当内容特征明确（版本 4、kernel 为空且 ramdisk 非空）才返回 <see cref="BootImageRole.InitBoot"/>。</para>
/// </remarks>
internal static class BootImageRoleResolver
{
    /// <summary>
    /// Detects the role of a boot image.
    /// <para>检测 Boot 镜像的角色。</para>
    /// </summary>
    /// <param name="version">Header version of the image as parsed or to be packed.
    /// <para>已解析或即将打包镜像的头部版本。</para></param>
    /// <param name="kernelSize">Declared or actual kernel segment size in bytes.
    /// <para>声明或实际的 kernel 区段字节大小。</para></param>
    /// <param name="ramdiskSize">Declared or actual ramdisk segment size in bytes.
    /// <para>声明或实际的 ramdisk 区段字节大小。</para></param>
    /// <param name="partitionName">Partition-name hint (for example the unpacked source file name without its
    /// extension); <see langword="null"/> or an unrecognized name disables the hint path.
    /// <para>分区名提示（例如去扩展名的解包源文件名）；为 <see langword="null"/> 或无法识别时禁用提示路径。</para></param>
    /// <returns><see cref="BootImageRole.InitBoot"/> for an <c>init_boot</c> hint or for a version 4 image with an
    /// empty kernel and a non-empty ramdisk; <see cref="BootImageRole.Recovery"/> for a <c>recovery</c> hint;
    /// otherwise <see cref="BootImageRole.Boot"/>.
    /// <para><c>init_boot</c> 提示，或版本 4 且 kernel 为空、ramdisk 非空时返回 <see cref="BootImageRole.InitBoot"/>；
    /// <c>recovery</c> 提示时返回 <see cref="BootImageRole.Recovery"/>；其余返回 <see cref="BootImageRole.Boot"/>。</para></returns>
    public static BootImageRole Detect(BootImageVersion version, uint kernelSize, uint ramdiskSize, string? partitionName = null)
    {
        foreach (var candidate in Candidates(partitionName))
        {
            if (candidate.Equals("init_boot", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals("init-boot", StringComparison.OrdinalIgnoreCase)
                || candidate.Equals("initboot", StringComparison.OrdinalIgnoreCase))
            {
                return BootImageRole.InitBoot;
            }

            if (candidate.Equals("recovery", StringComparison.OrdinalIgnoreCase))
            {
                return BootImageRole.Recovery;
            }

            if (candidate.Equals("boot", StringComparison.OrdinalIgnoreCase))
            {
                return BootImageRole.Boot;
            }
        }

        if (version == BootImageVersion.V4 && kernelSize == 0 && ramdiskSize > 0)
        {
            return BootImageRole.InitBoot;
        }

        return BootImageRole.Boot;
    }

    private static IEnumerable<string> Candidates(string? partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            yield break;
        }

        var trimmed = partitionName.Trim();
        yield return trimmed;
        if (trimmed.Length > 2 && trimmed[^2] == '_'
            && (trimmed[^1] == 'a' || trimmed[^1] == 'b' || trimmed[^1] == 'A' || trimmed[^1] == 'B'))
        {
            yield return trimmed[..^2];
        }
    }
}
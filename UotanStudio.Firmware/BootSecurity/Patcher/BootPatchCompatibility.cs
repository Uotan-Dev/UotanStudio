namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>Why a detected package cannot patch the selected boot image.
/// <para>检测到的补丁包无法修补所选 boot 镜像的原因。</para>
/// </summary>
public enum BootPatchCompatibilityProblem
{
    /// <summary>No blocking problem was found. <para>没有发现阻塞问题。</para></summary>
    None,
    /// <summary>The patch mode needs a boot kernel, but the image has none.
    /// <para>该修补模式需要 boot 内核，但镜像没有内核。</para></summary>
    KernelRequired,
    /// <summary>The patch mode writes into the ramdisk, but the image has none.
    /// <para>该修补模式需要写入 ramdisk，但镜像没有 ramdisk。</para></summary>
    RamdiskRequired,
    /// <summary>The package publishes no ABI the boot kernel runs.
    /// <para>补丁包没有发布 boot 内核可用的 ABI。</para></summary>
    ArchitectureMismatch,
    /// <summary>The explicitly selected ABI differs from the boot kernel ABI.
    /// <para>显式选择的 ABI 与 boot 内核 ABI 不一致。</para></summary>
    SelectedArchitectureMismatch,
    /// <summary>Neither the boot kernel nor the caller supplies a KMI for a KernelSU LKM patch.
    /// <para>KernelSU LKM 修补既未从 boot 内核取得 KMI，调用方也未提供。</para></summary>
    KmiUnknown,
    /// <summary>The KernelSU package has no module for the selected or boot KMI.
    /// <para>KernelSU 包没有适用于所选或 boot KMI 的模块。</para></summary>
    KernelSuKmiMismatch,
    /// <summary>The GKI package kernel KMI differs from the boot kernel KMI.
    /// <para>GKI 包内核 KMI 与 boot 内核 KMI 不一致。</para></summary>
    GkiKmiMismatch,
}

/// <summary>
/// Evaluates whether a package's patch mode fits one boot image, so the window blocks a run that the patcher would
/// reject or that would produce an image for a different device.
/// <para>评估补丁包的修补模式是否适配一个 boot 镜像，使窗口在修补器会拒绝或产物面向不同设备时阻止运行。</para>
/// </summary>
public static class BootPatchCompatibility
{
    /// <summary>Checks component presence, ABI support and KMI consistency for one package and boot image.
    /// <para>检查一个补丁包与 boot 镜像的组件存在性、ABI 支持与 KMI 一致性。</para>
    /// </summary>
    /// <param name="detection">Package detection produced for the selected package.
    /// <para>为所选补丁包生成的检测结果。</para></param>
    /// <param name="mode">Resolved patch mode that would run.
    /// <para>将要运行的已解析修补模式。</para></param>
    /// <param name="boot">Boot image components read from the selected image.
    /// <para>从所选镜像读取到的 boot 组件。</para></param>
    /// <param name="selectedArchitecture">ABI the user picked explicitly, or <see langword="null"/> for automatic choice.
    /// <para>用户显式选择的 ABI；自动选择时为 <see langword="null"/>。</para></param>
    /// <param name="selectedKmi">KMI the user picked explicitly, or <see langword="null"/> for the boot kernel KMI.
    /// <para>用户显式选择的 KMI；使用 boot 内核 KMI 时为 <see langword="null"/>。</para></param>
    /// <returns>The first blocking problem, or <see cref="BootPatchCompatibilityProblem.None"/>.
    /// <para>第一个阻塞问题；没有问题时为 <see cref="BootPatchCompatibilityProblem.None"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="detection"/> or <paramref name="boot"/> is <see langword="null"/>.
    /// <para><paramref name="detection"/> 或 <paramref name="boot"/> 为 <see langword="null"/>。</para></exception>
    public static BootPatchCompatibilityProblem Check(BootPatchDetection detection, BootPatchMode mode,
        BootImageInspection boot, string? selectedArchitecture = null, string? selectedKmi = null)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(boot);
        if (RequiresKernel(detection.Kind, mode) && !boot.HasKernel)
            return BootPatchCompatibilityProblem.KernelRequired;
        if (RequiresRamdisk(detection.Kind, mode) && !boot.HasRamdisk)
            return BootPatchCompatibilityProblem.RamdiskRequired;

        if (boot.Architecture is { Length: > 0 } bootArchitecture)
        {
            if (detection.Architectures.Count > 0
                && !detection.Architectures.Contains(bootArchitecture, StringComparer.OrdinalIgnoreCase))
                return BootPatchCompatibilityProblem.ArchitectureMismatch;
            if (selectedArchitecture is { Length: > 0 }
                && !string.Equals(selectedArchitecture, bootArchitecture, StringComparison.OrdinalIgnoreCase))
                return BootPatchCompatibilityProblem.SelectedArchitectureMismatch;
        }

        if (detection.Kind == BootPatchKind.KernelSu && mode == BootPatchMode.Lkm)
        {
            var effectiveKmi = selectedKmi ?? boot.Kmi;
            if (string.IsNullOrWhiteSpace(effectiveKmi))
                return BootPatchCompatibilityProblem.KmiUnknown;
            if (detection.Kmis is not { Count: > 0 } modules
                || !modules.Contains(effectiveKmi, StringComparer.OrdinalIgnoreCase))
                return BootPatchCompatibilityProblem.KernelSuKmiMismatch;
            if (selectedKmi is { Length: > 0 } && boot.Kmi is { Length: > 0 } bootKmi
                && !string.Equals(selectedKmi, bootKmi, StringComparison.OrdinalIgnoreCase))
                return BootPatchCompatibilityProblem.KernelSuKmiMismatch;
        }

        if (detection.Kind == BootPatchKind.GkiKernel)
        {
            if (detection.Kmis is { Count: > 0 } packageKmis && boot.Kmi is { Length: > 0 } gkiBootKmi
                && !packageKmis.Contains(gkiBootKmi, StringComparer.OrdinalIgnoreCase))
                return BootPatchCompatibilityProblem.GkiKmiMismatch;
            if (selectedKmi is { Length: > 0 } gkiSelection && boot.Kmi is { Length: > 0 } gkiKernelKmi
                && !string.Equals(gkiSelection, gkiKernelKmi, StringComparison.OrdinalIgnoreCase))
                return BootPatchCompatibilityProblem.GkiKmiMismatch;
        }

        return BootPatchCompatibilityProblem.None;
    }

    /// <summary>
    /// Reports whether the supplied SuperKey satisfies the requirement of the resolved patch mode, mirroring the
    /// check the patcher applies before it starts.
    /// <para>报告提供的超级密钥是否满足已解析修补模式的要求，与修补器启动前的检查保持一致。</para>
    /// </summary>
    /// <param name="kind">Detected package family. <para>检测到的包类型族。</para></param>
    /// <param name="mode">Resolved patch mode. <para>已解析的修补模式。</para></param>
    /// <param name="superKey">Key typed by the user, or <see langword="null"/> when the field is empty.
    /// <para>用户输入的密钥；字段为空时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when no key is required or the key is acceptable.
    /// <para>不需要密钥或密钥可接受时为 <see langword="true"/>。</para></returns>
    public static bool IsSuperKeyAcceptable(BootPatchKind kind, BootPatchMode mode, string? superKey)
        => kind == BootPatchKind.APatch
            ? APatchSuperKey.IsValid(superKey)
            : kind != BootPatchKind.KernelPatch && mode != BootPatchMode.Kpm
              || !string.IsNullOrWhiteSpace(superKey);

    /// <summary>Reports whether a patch mode modifies the boot kernel.
    /// <para>报告某个修补模式是否会修改 boot 内核。</para>
    /// </summary>
    /// <param name="kind">Detected package family. <para>检测到的包类型族。</para></param>
    /// <param name="mode">Resolved patch mode. <para>已解析的修补模式。</para></param>
    /// <returns><see langword="true"/> for kernel-based modes and families.
    /// <para>基于内核的模式与类型族为 <see langword="true"/>。</para></returns>
    public static bool RequiresKernel(BootPatchKind kind, BootPatchMode mode)
        => kind is BootPatchKind.KernelSu or BootPatchKind.KernelSuLkm or BootPatchKind.APatch
            or BootPatchKind.KernelPatch or BootPatchKind.GkiKernel
            || mode is BootPatchMode.Kpm or BootPatchMode.Gki;

    /// <summary>Reports whether a patch mode writes into the boot ramdisk.
    /// <para>报告某个修补模式是否会写入 boot ramdisk。</para>
    /// </summary>
    /// <param name="kind">Detected package family. <para>检测到的包类型族。</para></param>
    /// <param name="mode">Resolved patch mode. <para>已解析的修补模式。</para></param>
    /// <returns><see langword="true"/> for Magisk and for KernelSU LKM patches.
    /// <para>Magisk 与 KernelSU LKM 修补为 <see langword="true"/>。</para></returns>
    public static bool RequiresRamdisk(BootPatchKind kind, BootPatchMode mode)
        => kind is BootPatchKind.Magisk or BootPatchKind.KernelSuLkm
            || (kind == BootPatchKind.KernelSu && mode == BootPatchMode.Lkm);
}
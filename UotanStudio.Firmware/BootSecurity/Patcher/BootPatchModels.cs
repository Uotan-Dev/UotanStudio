namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>Supported root patch package families. <para>支持的根补丁包类型族。</para></summary>
public enum BootPatchKind
{
    /// <summary>
    /// Family could not be identified from the inspected entry names.
    /// <para>无法从被检查的条目名称识别包类型族。</para>
    /// </summary>
    Unknown,
    /// <summary>
    /// Magisk package identified by its libmagisk assets and boot_patch.sh.
    /// <para>通过 libmagisk 资源与 boot_patch.sh 识别的 Magisk 包。</para>
    /// </summary>
    Magisk,
    /// <summary>
    /// KernelSU package carrying libksud or embedded .ko modules.
    /// <para>携带 libksud 或内置 .ko 模块的 KernelSU 包。</para>
    /// </summary>
    KernelSu,
    /// <summary>
    /// APatch package identified by kpatch/kpimg assets or script markers.
    /// <para>通过 kpatch/kpimg 资源或脚本标记识别的 APatch 包。</para>
    /// </summary>
    APatch,
    /// <summary>A standalone KernelSU loadable kernel module file. <para>独立的 KernelSU 可加载内核模块文件。</para></summary>
    KernelSuLkm,
    /// <summary>
    /// Package that patches the kernel with a bundled KernelPatch image and has no APatch install scripts.
    /// <para>仅携带 KernelPatch 镜像、不含 APatch 安装脚本的内核修补包。</para>
    /// </summary>
    /// <remarks>
    /// The image may sit at <c>assets/kpimg</c> or <c>assets/kpm/kpimg</c>; the superkey is a patch option,
    /// because the package does not publish one.
    /// <para>镜像可能位于 <c>assets/kpimg</c> 或 <c>assets/kpm/kpimg</c>；由于包内不公布超级密钥，
    /// 该密钥通过修补选项传入。</para>
    /// </remarks>
    KernelPatch,
    /// <summary>
    /// A standalone boot kernel image or AnyKernel3-style package used to replace the boot kernel in GKI mode.
    /// <para>用于 GKI 模式整体替换 boot 内核的独立内核镜像或 AnyKernel3 风格压缩包。</para>
    /// </summary>
    /// <remarks>
    /// The payload is a raw ARM64 <c>Image</c>, a gzip-compressed kernel, or a ZIP carrying one of those entries.
    /// <para>载荷为原始 ARM64 <c>Image</c>、gzip 压缩内核，或携带此类条目的 ZIP 压缩包。</para>
    /// </remarks>
    GkiKernel
}

/// <summary>Patch strategies a KernelSU-family boot patch can follow.
/// <para>KernelSU 系 boot 修补可采用的修补策略。</para></summary>
public enum BootPatchMode
{
    /// <summary>
    /// Let the patcher pick the strategy from the detected package family: KPM for KernelPatch
    /// packages and LKM otherwise.
    /// <para>由修补器按检测到的包类型族决定策略：KernelPatch 包为 KPM，其余为 LKM。</para>
    /// </summary>
    Auto,
    /// <summary>
    /// Install the package's KernelSU module into the ramdisk and leave the boot kernel untouched.
    /// <para>把包内的 KernelSU 模块装入 ramdisk，boot 内核保持不动。</para>
    /// </summary>
    Lkm,
    /// <summary>
    /// Replace the boot image kernel with a prebuilt GKI kernel supplied by the caller.
    /// <para>用调用方提供的预编译 GKI 内核替换 boot 镜像内核。</para>
    /// </summary>
    Gki,
    /// <summary>
    /// Patch the boot kernel with the package's KernelPatch image so KPM modules can load.
    /// <para>用包内 KernelPatch 镜像修补 boot 内核，使 KPM 模块可加载。</para>
    /// </summary>
    Kpm,
}

/// <summary>Optional controls exposed by a Magisk boot patch script. <para>Magisk 引导补丁脚本暴露的可选控制项。</para></summary>
public enum BootPatchOption
{
    /// <summary>
    /// KEEPVERITY: keep dm-verity enabled in the patched image.
    /// <para>KEEPVERITY：在修补后的镜像中保留 dm-verity。</para>
    /// </summary>
    KeepVerity,
    /// <summary>KEEPFORCEENCRYPT: keep forced encryption enabled. <para>KEEPFORCEENCRYPT：保留强制加密。</para></summary>
    KeepForceEncryption,
    /// <summary>
    /// PATCHVBMETAFLAG: request the vbmeta flag patch path.
    /// <para>PATCHVBMETAFLAG：请求 vbmeta 标志修补路径。</para>
    /// </summary>
    PatchVbmetaFlag,
    /// <summary>
    /// RECOVERYMODE: patch for a recovery-image installation.
    /// <para>RECOVERYMODE：面向 recovery 镜像安装进行修补。</para>
    /// </summary>
    RecoveryMode,
    /// <summary>
    /// LEGACYSAR: request the legacy system-as-root layout.
    /// <para>LEGACYSAR：请求传统 system-as-root 布局。</para>
    /// </summary>
    LegacySar,
    /// <summary>PREINITDEVICE: record an early-init device node. <para>PREINITDEVICE：记录早期 init 设备节点。</para></summary>
    PreInitDevice
}

/// <summary>
/// Result of inspecting a patch package: one asset found inside a KernelSU shared library.
/// <para>检查补丁包的结果：在 KernelSU 共享库内发现的一个资源。</para>
/// </summary>
/// <param name="Name">
/// Asset path name with backslashes normalized to forward slashes.
/// <para>反斜杠已规范为正斜杠的资源路径名。</para>
/// </param>
/// <param name="Kmi">
/// KMI string recovered from the asset name, or <c>null</c> when the name carries none.
/// <para>从资源名称还原的 KMI 字符串；名称不含 KMI 时为 <c>null</c>。</para>
/// </param>
/// <param name="Architecture">
/// Architecture inferred from the name or the ELF header, or <c>null</c> when unknown.
/// <para>从名称或 ELF 头推断的架构；无法判定时为 <c>null</c>。</para>
/// </param>
/// <param name="Method">
/// Extraction method reported by the embedded asset scanner.
/// <para>由内置资源扫描器报告的提取方法。</para>
/// </param>
/// <param name="HashVerified">Whether the asset passed its hash verification. <para>该资源是否通过哈希校验。</para></param>
/// <param name="Size">Length in bytes of the extracted payload. <para>提取后负载的字节长度。</para></param>
public sealed record KernelSuKmiAsset(string Name, string? Kmi, string? Architecture, string Method, bool HashVerified, long Size);

/// <summary>
/// Architectures, KMI strings and embedded assets discovered in a KernelSU APK or ZIP.
/// <para>在 KernelSU 的 APK 或 ZIP 中发现的架构、KMI 字符串与内置资源。</para>
/// </summary>
/// <param name="Architectures">Distinct ABI names detected in the package. <para>在包中检测到的去重 ABI 名称。</para></param>
/// <param name="Kmis">KMI strings recovered from embedded modules. <para>从内置模块还原的 KMI 字符串。</para></param>
/// <param name="Assets">
/// Verified assets extracted from libksud-style libraries.
/// <para>从 libksud 类库中提取的已校验资源。</para>
/// </param>
public sealed record KernelSuPackageInfo(IReadOnlyList<string> Architectures, IReadOnlyList<string> Kmis,
    IReadOnlyList<KernelSuKmiAsset> Assets);

/// <summary>
/// Architecture and KMI read from the kernel of a boot image in one pass.
/// <para>在一次遍历中从 boot 镜像内核读取到的架构与 KMI。</para>
/// </summary>
/// <param name="Architecture">ABI name such as <c>arm64</c>, or <c>null</c> when the kernel image is
/// unrecognized or cannot be decompressed. <para>形如 <c>arm64</c> 的 ABI 名称；内核镜像无法识别或无法解压时为 <c>null</c>。</para></param>
/// <param name="Kmi">KernelSU KMI string, or <c>null</c> when the kernel carries no extractable release.
/// <para>KernelSU KMI 字符串；内核不含可提取的版本号时为 <c>null</c>。</para></param>
public sealed record BootKernelInfo(string? Architecture, string? Kmi);

/// <summary>
/// Components and identity read from one Android boot image in a single parse.
/// <para>在单次解析中从 Android boot 镜像读到的组件与标识。</para>
/// </summary>
/// <param name="Architecture">Kernel ABI name such as <c>arm64</c>, or <c>null</c> when the kernel is absent or
/// unrecognized. <para>内核 ABI 名称，例如 <c>arm64</c>；内核缺失或无法识别时为 <c>null</c>。</para></param>
/// <param name="Kmi">KernelSU KMI string, or <c>null</c> when the kernel carries no extractable release.
/// <para>KernelSU KMI 字符串；内核不含可提取版本时为 <c>null</c>。</para></param>
/// <param name="HasKernel">Whether the image carries a non-empty kernel. <para>镜像是否携带非空内核。</para></param>
/// <param name="HasRamdisk">Whether the image carries a non-empty ramdisk. <para>镜像是否携带非空 ramdisk。</para></param>
public sealed record BootImageInspection(string? Architecture, string? Kmi, bool HasKernel, bool HasRamdisk);

/// <summary>
/// Detection outcome describing a package's family, ABIs, options and KMIs.
/// <para>描述包类型族、ABI、可选项与 KMI 的检测结果。</para>
/// </summary>
/// <param name="Kind">Identified package family. <para>识别出的包类型族。</para></param>
/// <param name="Architectures">ABI names the package supports. <para>包支持的 ABI 名称。</para></param>
/// <param name="Version">
/// MAGISK_VER string for Magisk packages; <c>null</c> for other families or when the marker is absent.
/// <para>Magisk 包的 MAGISK_VER 字符串；其他类型族或标记缺失时为 <c>null</c>。</para>
/// </param>
/// <param name="SupportedOptions">
/// Option tokens referenced by the package's boot_patch.sh; <c>null</c> for non-Magisk families.
/// <para>包内 boot_patch.sh 引用的选项标记；非 Magisk 类型族为 <c>null</c>。</para>
/// </param>
/// <param name="Kmis">
/// KMI strings whose module payload was extracted and hash-verified in the package; empty for other families.
/// <para>包内已提取并通过哈希校验的模块负载所对应的 KMI 字符串；其他类型族为空。</para>
/// </param>
/// <param name="HasKernelPatchImage">
/// Whether the package carries a KernelPatch image usable for KPM patching.
/// <para>包内是否携带可用于 KPM 修补的 KernelPatch 镜像。</para>
/// </param>
/// <param name="UnsupportedReason">
/// Why a recognized family or layout cannot patch a boot image, or <see langword="null"/> when it can.
/// <para>已识别的类型族或布局为何无法修补 boot 镜像；可以修补时为 <see langword="null"/>。</para>
/// </param>
public sealed record BootPatchDetection(BootPatchKind Kind, IReadOnlyList<string> Architectures, string? Version = null,
    IReadOnlySet<BootPatchOption>? SupportedOptions = null, IReadOnlyList<string>? Kmis = null,
    bool HasKernelPatchImage = false, string? UnsupportedReason = null)
{
    /// <summary>Whether a known family was detected together with at least one architecture.
    /// <para>是否同时检测到已知类型族与至少一个架构。</para></summary>
    /// <value>
    /// <see langword="true"/> when <c>Kind</c> is not <see cref="BootPatchKind.Unknown"/>, the ABI list is
    /// non-empty and <see cref="UnsupportedReason"/> is <see langword="null"/>.
    /// <para>当 <c>Kind</c> 不是 <see cref="BootPatchKind.Unknown"/>、ABI 列表非空且
    /// <see cref="UnsupportedReason"/> 为 <see langword="null"/> 时为 <see langword="true"/>。</para>
    /// </value>
    public bool IsSupported => Kind != BootPatchKind.Unknown && Architectures.Count > 0 && UnsupportedReason is null;
}

/// <summary>Options used by the native boot patcher. <para>原生引导修补器使用的选项。</para></summary>
public sealed record BootPatchOptions
{
    /// <summary>
    /// Patch strategy override; <see cref="BootPatchMode.Auto"/> picks the strategy from the package content:
    /// KernelSU packages with verified module payloads take the LKM path, KernelSU packages that only carry a
    /// KernelPatch image take the KPM path, and kernel images or AnyKernel3 packages take the GKI path.
    /// <para>修补策略覆盖；<see cref="BootPatchMode.Auto"/> 按包内容选择策略：带已验证模块负载的
    /// KernelSU 包走 LKM，仅携带 KernelPatch 镜像的 KernelSU 包走 KPM，内核镜像或 AnyKernel3 压缩包走 GKI。</para>
    /// </summary>
    /// <value>Defaults to <see cref="BootPatchMode.Auto"/>. <para>默认为 <see cref="BootPatchMode.Auto"/>。</para></value>
    /// <remarks>Explicit modes are rejected when the package family cannot perform them.
    /// <para>包类型族无法执行显式指定的模式时会被拒绝。</para></remarks>
    public BootPatchMode Mode { get; init; } = BootPatchMode.Auto;
    /// <summary>Keeps dm-verity active in the patched image. <para>在修补后的镜像中保留 dm-verity。</para></summary>
    /// <value>Defaults to <see langword="true"/>. <para>默认为 <see langword="true"/>。</para></value>
    public bool KeepVerity { get; init; } = true;
    /// <summary>Keeps forced encryption active in the patched image. <para>在修补后的镜像中保留强制加密。</para></summary>
    /// <value>Defaults to <see langword="true"/>. <para>默认为 <see langword="true"/>。</para></value>
    public bool KeepForceEncryption { get; init; } = true;
    /// <summary>Requests the Magisk PATCHVBMETAFLAG configuration.
    /// <para>请求 Magisk 的 PATCHVBMETAFLAG 配置。</para></summary>
    /// <value>Defaults to <see langword="false"/>. <para>默认为 <see langword="false"/>。</para></value>
    public bool PatchVbmetaFlag { get; init; }
    /// <summary>Patches for a recovery-image installation instead of a boot image.
    /// <para>面向 recovery 镜像安装而非 boot 镜像进行修补。</para></summary>
    /// <value>Defaults to <see langword="false"/>. <para>默认为 <see langword="false"/>。</para></value>
    public bool RecoveryMode { get; init; }
    /// <summary>Requests Magisk's legacy SAR rootfs kernel patch.
    /// <para>请求 Magisk 的传统 SAR rootfs 内核补丁。</para></summary>
    /// <remarks>Only legacy SAR images need this option; it stays disabled otherwise.
    /// <para>仅遗留 SAR 镜像需要该选项；其余情形保持禁用。</para></remarks>
    /// <value>Defaults to <see langword="false"/>. <para>默认为 <see langword="false"/>。</para></value>
    public bool ForceRootfs { get; init; }
    /// <summary>
    /// Device node passed as Magisk's PREINITDEVICE value.
    /// <para>作为 Magisk PREINITDEVICE 值传入的设备节点。</para>
    /// </summary>
    /// <value><c>null</c> when no pre-init device is requested. <para>未请求预初始化设备时为 <c>null</c>。</para></value>
    public string? PreInitDevice { get; init; }
    /// <summary>Target CPU architecture for the patch run. <para>本次修补运行的目标 CPU 架构。</para></summary>
    /// <value>An ABI name as produced by <see cref="BootPatchAnalyzer"/>, or <c>null</c> to let detection choose.
    /// <para>由 <see cref="BootPatchAnalyzer"/> 产生的 ABI 名称；为 <c>null</c> 时由检测流程决定。</para></value>
    public string? Architecture { get; init; }
    /// <summary>Explicit KernelSU KMI selection; null means use the boot kernel KMI.
    /// <para>显式指定的 KernelSU KMI；为 null 时使用 boot 内核的 KMI。</para></summary>
    /// <value>
    /// A KMI string, or <c>null</c> to derive it from the boot image.
    /// <para>KMI 字符串；为 <c>null</c> 时从 boot 镜像推导。</para>
    /// </value>
    public string? KernelSuKmi { get; init; }
    /// <summary>
    /// KernelPatch superkey used by APatch and KernelPatch packages.
    /// <para>APatch 与 KernelPatch 包使用的 KernelPatch 超级密钥。</para>
    /// </summary>
    /// <value><c>null</c> when no superkey is supplied; APatch requires a valid key and KernelPatch packages
    /// require a non-empty one. <para>未提供超级密钥时为 <c>null</c>；APatch 要求有效密钥，KernelPatch 包要求非空密钥。</para></value>
    public string? SuperPassword { get; init; }
    /// <summary>
    /// Optional sink that receives progress values from the patch pipeline.
    /// <para>接收修补流水线进度值的可选接收器。</para>
    /// </summary>
    /// <value><c>null</c> disables progress reporting; otherwise the pipeline reports <see cref="double"/> progress.
    /// <para>为 <c>null</c> 时不报告进度；否则流水线报告 <see cref="double"/> 进度值。</para></value>
    public IProgress<double>? Progress { get; init; }
}

/// <summary>
/// Result returned after a patch operation.
/// <para>修补操作完成后返回的结果。</para>
/// </summary>
/// <param name="OutputPath">Path of the patched image produced by the run. <para>本次运行生成的修补后镜像路径。</para></param>
/// <param name="Detection">Package detection metadata the patch was based on. <para>修补所依据的包检测元数据。</para></param>
/// <param name="Changes">Descriptions of the changes applied during the run. <para>本次运行中已应用变更的描述。</para></param>
public sealed record BootPatchResult(string OutputPath, BootPatchDetection Detection, IReadOnlyList<string> Changes);

/// <summary>
/// A parsed operation from Magisk/APatch shell patch scripts.
/// <para>从 Magisk/APatch shell 补丁脚本中解析出的一条操作。</para>
/// </summary>
/// <param name="Command">Recognized operation keyword as written in the script. <para>脚本中书写的可识别操作关键字。</para></param>
/// <param name="Arguments">
/// Quoting-stripped argument tokens following the command.
/// <para>已去除引号的命令后随参数标记。</para>
/// </param>
/// <param name="LineNumber">1-based source line the operation was found on. <para>该操作所在源码行的 1 基编号。</para></param>
public sealed record BootPatchScriptOperation(string Command, IReadOnlyList<string> Arguments, int LineNumber);
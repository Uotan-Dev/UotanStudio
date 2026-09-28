namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Stable content identities recognised by signature probing; no filename or extension contributes to these results.
/// <para>由签名探测识别的稳定内容标识；文件名与扩展名都不参与这些结果。</para>
/// </summary>
public enum PatchFormatKind
{
    /// <summary>No recognised signature or text dialect. <para>未识别到签名或文本方言。</para></summary>
    Unknown,
    /// <summary>JSON configuration text. <para>JSON 配置文本。</para></summary>
    Json,
    /// <summary>XML configuration text. <para>XML 配置文本。</para></summary>
    Xml,
    /// <summary>INI configuration text. <para>INI 配置文本。</para></summary>
    Ini,
    /// <summary>ELF executable. <para>ELF 可执行文件。</para></summary>
    Elf,
    /// <summary>Android sparse image. <para>Android 稀疏镜像。</para></summary>
    Sparse,
    /// <summary>ext2/ext3/ext4 filesystem image. <para>ext2/ext3/ext4 文件系统镜像。</para></summary>
    ExtFamily,
    /// <summary>EROFS filesystem image. <para>EROFS 文件系统镜像。</para></summary>
    Erofs,
    /// <summary>F2FS filesystem image. <para>F2FS 文件系统镜像。</para></summary>
    F2fs,
    /// <summary>Android boot image. <para>Android boot 镜像。</para></summary>
    Boot,
    /// <summary>Android vendor boot image. <para>Android vendor boot 镜像。</para></summary>
    VendorBoot,
    /// <summary>AVB vbmeta partition. <para>AVB vbmeta 分区。</para></summary>
    Vbmeta,
    /// <summary>OTA payload image. <para>OTA payload 镜像。</para></summary>
    Payload,
    /// <summary>Flattened device tree blob. <para>展平设备树 blob。</para></summary>
    Dtb,
    /// <summary>Device-tree overlay blob. <para>设备树覆盖 blob。</para></summary>
    Dtbo,
    /// <summary>DEX or compact DEX class file. <para>DEX 或紧凑 DEX 类文件。</para></summary>
    Dex,
    /// <summary>ZIP archive. <para>ZIP 归档。</para></summary>
    Zip,
    /// <summary>CPIO newc archive. <para>CPIO newc 归档。</para></summary>
    Cpio,
    /// <summary>gzip compressed stream. <para>gzip 压缩流。</para></summary>
    Gzip,
    /// <summary>xz compressed stream. <para>xz 压缩流。</para></summary>
    Xz,
    /// <summary>Zstd compressed frame. <para>Zstd 压缩帧。</para></summary>
    Zstd,
    /// <summary>LZ4 compressed frame. <para>LZ4 压缩帧。</para></summary>
    Lz4,
    /// <summary>bsdiff binary patch. <para>bsdiff 二进制补丁。</para></summary>
    Bsdiff,
    /// <summary>Rockchip firmware image. <para>Rockchip 固件镜像。</para></summary>
    Rockchip,
    /// <summary>Rockchip OPS package. <para>Rockchip OPS 打包。</para></summary>
    Ops,
    /// <summary>OPPO encrypted OTA package. <para>OPPO 加密 OTA 包。</para></summary>
    Ozip,
    /// <summary>NTPI update image. <para>NTPI 更新镜像。</para></summary>
    Ntpi,
    /// <summary>Amlogic image header. <para>Amlogic 镜像头。</para></summary>
    Amlogic
}

/// <summary>
/// Signature recognition is deliberately distinct from structural validation.
/// <para>签名识别与结构校验刻意分开。</para>
/// </summary>
public enum PatchFormatEvidence
{
    /// <summary>No format was recognised. <para>未识别到任何格式。</para></summary>
    Unknown,
    /// <summary>Only the leading signature matched. <para>仅匹配到起始签名。</para></summary>
    Signature,
    /// <summary>
    /// The signature matched and the available structural checks passed.
    /// <para>签名匹配且可用的结构校验已通过。</para>
    /// </summary>
    Structure
}

/// <summary>
/// Machine-readable inspection outcome; the UI translates the evidence code rather than persisting translated labels.
/// <para>机器可读的探测结果；界面翻译证据代码，而不是持久化已翻译的标签。</para>
/// </summary>
/// <param name="Format">
/// The recognised content identity, or <see cref="PatchFormatKind.Unknown"/> when nothing matched.
/// <para>识别到的内容标识；未匹配任何格式时为 <see cref="PatchFormatKind.Unknown"/>。</para>
/// </param>
/// <param name="Evidence">
/// How far recognition reached: no match, signature only, or full structural validation.
/// <para>识别达成的程度：未匹配、仅签名，或完成了结构校验。</para>
/// </param>
/// <param name="Detail">
/// Stable machine-readable evidence code describing the outcome; locale-independent.
/// <para>描述结果的稳定机器可读证据代码；与区域设置无关。</para>
/// </param>
/// <param name="Rejected">
/// Set when a detected signature's structural validation failed; defaults to <see langword="false"/>.
/// <para>检测到签名的结构校验失败时置位；默认为 <see langword="false"/>。</para>
/// </param>
public sealed record PatchFormatResult(PatchFormatKind Format, PatchFormatEvidence Evidence, string Detail, bool Rejected = false)
{
    /// <summary>
    /// Unknown and rejected inputs never satisfy a format precondition.
    /// <para>未知和被拒绝的输入永远不满足任何格式前提。</para>
    /// </summary>
    /// <param name="expected">
    /// The format identity the precondition requires; <see cref="PatchFormatKind.Unknown"/> never satisfies it.
    /// <para>前提所要求的格式标识；<see cref="PatchFormatKind.Unknown"/> 永不满足。</para>
    /// </param>
    /// <param name="minimum">
    /// The minimum evidence level required; <see cref="PatchFormatEvidence.Unknown"/> can never be satisfied.
    /// <para>要求的最低证据级别；<see cref="PatchFormatEvidence.Unknown"/> 不可能被满足。</para>
    /// </param>
    /// <returns><see langword="true"/> when the input is not rejected, <paramref name="expected"/> is a defined
    /// non-unknown format, <paramref name="minimum"/> is <see cref="PatchFormatEvidence.Signature"/> or
    /// <see cref="PatchFormatEvidence.Structure"/>, and the detected format and evidence meet or exceed them;
    /// otherwise <see langword="false"/>.
    /// <para>当输入未被拒绝、<paramref name="expected"/> 是已定义的非未知格式、<paramref name="minimum"/> 为
    /// <see cref="PatchFormatEvidence.Signature"/> 或 <see cref="PatchFormatEvidence.Structure"/>，且检测到的格式与
    /// 证据达到或超过它们时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</para></returns>
    public bool Matches(PatchFormatKind expected, PatchFormatEvidence minimum)
        => !Rejected && expected != PatchFormatKind.Unknown && Enum.IsDefined(expected)
            && minimum is PatchFormatEvidence.Signature or PatchFormatEvidence.Structure && Format == expected && Evidence >= minimum;
}
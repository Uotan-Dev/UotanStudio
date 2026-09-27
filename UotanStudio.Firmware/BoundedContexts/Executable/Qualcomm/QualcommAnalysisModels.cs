using UotanStudio.Firmware.BoundedContexts.Executable.Elf;

namespace UotanStudio.Firmware.BoundedContexts.Executable.Qualcomm;

/// <summary>Identifies the recognized on-disk image container.
/// <para>标识已识别的磁盘镜像容器。</para></summary>
public enum QualcommImageFormat
{
    /// <summary>ELF container. <para>ELF 容器。</para></summary>
    Elf,
    /// <summary>Standalone MBN container. <para>独立 MBN 容器。</para></summary>
    Mbn,
    /// <summary>SBL MBN container. <para>SBL MBN 容器。</para></summary>
    SblMbn
}

/// <summary>Separates missing evidence, unsupported variants and failed checks.
/// <para>区分缺失证据、不支持的变体与验证失败。</para></summary>
public enum QualcommVerificationStatus
{
    /// <summary>Evidence was not checked. <para>尚未检查证据。</para></summary>
    NotChecked,
    /// <summary>Evidence is absent. <para>证据不存在。</para></summary>
    NotPresent,
    /// <summary>The performed check succeeded. <para>已执行的检查通过。</para></summary>
    Valid,
    /// <summary>The performed check failed. <para>已执行的检查失败。</para></summary>
    Invalid,
    /// <summary>The variant exceeds supported verification. <para>变体超出受支持的验证范围。</para></summary>
    Unsupported
}

/// <summary>Limits analysis and supplies caller-owned root certificate fingerprints.
/// <para>限制分析资源，并提供由调用方管理的根证书指纹。</para></summary>
public sealed record QualcommAnalysisOptions
{
    /// <summary>Gets the maximum certificate-package size in bytes.
    /// <para>获取证书包最大字节数。</para></summary>
    /// <value>The maximum certificate-package size. <para>证书包最大字节数。</para></value>
    public int MaximumCertificateBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Gets the maximum number of certificates per authority.
    /// <para>获取每个签名方的最大证书数。</para></summary>
    /// <value>The maximum count. <para>最大数量。</para></value>
    public int MaximumCertificateCount { get; init; } = 16;

    /// <summary>Gets caller-supplied SHA-256 fingerprints of DER-encoded root certificates.
    /// Trust is not checked when both fingerprint collections are empty; no OEM roots are bundled.
    /// <para>获取调用方提供的 DER 根证书 SHA-256 指纹；两组指纹均为空时不检查信任，不内置 OEM 根证书。</para></summary>
    /// <value>Upper- or lowercase 64-character hexadecimal fingerprints.
    /// <para>大小写均可的 64 位十六进制指纹。</para></value>
    public IReadOnlyCollection<string> TrustedRootCertificateSha256 { get; init; } = [];

    /// <summary>Gets caller-supplied SHA-384 fingerprints of DER-encoded root certificates.
    /// A root matching either fingerprint collection is accepted as caller-trusted.
    /// <para>获取调用方提供的 DER 根证书 SHA-384 指纹；根证书匹配任一指纹集合即符合调用方信任要求。</para></summary>
    /// <value>Upper- or lowercase 96-character hexadecimal fingerprints.
    /// <para>大小写均可的 96 位十六进制指纹。</para></value>
    public IReadOnlyCollection<string> TrustedRootCertificateSha384 { get; init; } = [];
}

/// <summary>Describes a validated MBN layout within an ELF hash segment or a standalone file.
/// <para>描述 ELF 哈希段内或独立文件中已校验的 MBN 布局。</para></summary>
/// <param name="Version">MBN header version. <para>MBN 头版本。</para></param>
/// <param name="ImageId">Declared image identifier, when available. <para>可用时的镜像标识。</para></param>
/// <param name="HashOffset">Hash-table offset relative to the MBN header. <para>相对 MBN 头的摘要表偏移。</para></param>
/// <param name="HashLength">Hash-table length. <para>摘要表长度。</para></param>
/// <param name="HashAlgorithm">Declared or inferred digest algorithm; empty when ambiguous. <para>声明或推断的摘要算法；有歧义时为空。</para></param>
/// <param name="SoftwareId">Software identifier declared in signing metadata, when available. <para>可用时签名元数据中声明的软件标识。</para></param>
public sealed record QualcommMbnInfo(uint Version, uint? ImageId, long HashOffset, long HashLength, string HashAlgorithm, uint? SoftwareId);

/// <summary>Reports a digest comparison for one ELF segment or page.
/// <para>报告一个 ELF 段或页的摘要比较。</para></summary>
/// <param name="SegmentIndex">Program header index. <para>程序头索引。</para></param>
/// <param name="PageIndex">Page index, or null for a whole-segment digest. <para>页索引；整段摘要时为空。</para></param>
/// <param name="Status">Comparison status. <para>比较状态。</para></param>
public sealed record QualcommSegmentDigest(int SegmentIndex, int? PageIndex, QualcommVerificationStatus Status);

/// <summary>Describes one bounded ELF component discovered in a Qualcomm multi-component image.
/// <para>描述 Qualcomm 多组件镜像中发现的一个有界 ELF 组件。</para></summary>
public sealed record QualcommMelfComponent(long Offset, long Length, bool Is64Bit, ushort Machine);

/// <summary>Exposes bounded certificate metadata without exporting private keys.
/// <para>提供有界证书元数据，不导出私钥。</para></summary>
/// <param name="Authority">QTI or OEM signing authority. <para>QTI 或 OEM 签名方。</para></param>
/// <param name="Subject">Certificate subject. <para>证书主题。</para></param>
/// <param name="Issuer">Certificate issuer. <para>证书颁发者。</para></param>
/// <param name="Sha256">SHA-256 of the exact DER bytes. <para>精确 DER 字节的 SHA-256。</para></param>
public sealed record QualcommCertificateInfo(string Authority, string Subject, string Issuer, string Sha256)
{
    /// <summary>Gets the certificate's zero-based index within its authority package.
    /// <para>获取证书在对应签名方证书包中从零开始的索引。</para></summary>
    /// <value>The package index. <para>证书包索引。</para></value>
    public int Index { get; init; }
    /// <summary>Gets the X.509 serial number. <para>获取 X.509 序列号。</para></summary>
    /// <value>The hexadecimal serial number. <para>十六进制序列号。</para></value>
    public string SerialNumber { get; init; } = "";
    /// <summary>Gets the start of the certificate validity interval in UTC. <para>获取证书有效期的 UTC 起点。</para></summary>
    /// <value>The validity start. <para>有效期起点。</para></value>
    public DateTimeOffset NotBefore { get; init; }
    /// <summary>Gets the end of the certificate validity interval in UTC. <para>获取证书有效期的 UTC 终点。</para></summary>
    /// <value>The validity end. <para>有效期终点。</para></value>
    public DateTimeOffset NotAfter { get; init; }
    /// <summary>Gets the certificate signature algorithm OID. <para>获取证书签名算法 OID。</para></summary>
    /// <value>The OID, not an image verification result. <para>OID，不是镜像验证结果。</para></value>
    public string SignatureAlgorithmOid { get; init; } = "";
    /// <summary>Gets the public key algorithm OID. <para>获取公钥算法 OID。</para></summary>
    /// <value>The algorithm OID. <para>算法 OID。</para></value>
    public string PublicKeyAlgorithmOid { get; init; } = "";
    /// <summary>Gets the SHA-384 fingerprint of exact DER bytes. <para>获取精确 DER 字节的 SHA-384 指纹。</para></summary>
    /// <value>The hexadecimal fingerprint. <para>十六进制指纹。</para></value>
    public string Sha384 { get; init; } = "";
    /// <summary>Gets ordered Qualcomm subject OU declarations. <para>获取有序的 Qualcomm 主题 OU 声明。</para></summary>
    /// <value>Declarations without implicit authentication. <para>不隐含认证结果的声明。</para></value>
    public IReadOnlyList<QualcommCertificateAttribute> Attributes { get; init; } = [];
}

/// <summary>Records static programmer command evidence, not device support.
/// <para>记录 programmer 命令的静态证据，不代表设备支持。</para></summary>
/// <param name="Name">Observed command name. <para>观察到的命令名称。</para></param>
/// <param name="Source">Evidence kind, such as a mapped command table or a string.
/// <para>证据类别，例如已映射命令表或字符串。</para></param>
/// <param name="FileOffset">Offset of the evidence in the file. <para>证据在文件中的偏移。</para></param>
public sealed record QualcommCommandEvidence(string Name, string Source, long FileOffset);

/// <summary>Separates signature validity, embedded chain validity and externally supplied trust.
/// <para>分别报告签名有效性、内嵌证书链有效性和外部提供的信任。</para></summary>
public sealed record QualcommAuthorityResult
{
    /// <summary>Gets the signature status. <para>获取签名状态。</para></summary>
    /// <value>The cryptographic signature status. <para>密码学签名状态。</para></value>
    public QualcommVerificationStatus SignatureStatus { get; init; } = QualcommVerificationStatus.NotPresent;
    /// <summary>Gets the embedded certificate-chain status. <para>获取内嵌证书链状态。</para></summary>
    /// <value>The chain status without external trust. <para>不含外部信任的证书链状态。</para></value>
    public QualcommVerificationStatus CertificateChainStatus { get; init; } = QualcommVerificationStatus.NotPresent;
    /// <summary>Gets the caller-root trust status. <para>获取调用方根信任状态。</para></summary>
    /// <value>The external trust status. <para>外部信任状态。</para></value>
    public QualcommVerificationStatus TrustedRootStatus { get; init; } = QualcommVerificationStatus.NotChecked;
    /// <summary>Gets the verified signing algorithm. <para>获取已验证的签名算法。</para></summary>
    /// <value>The algorithm name, or null if unverified. <para>算法名称；未验证时为空。</para></value>
    public string? SignatureAlgorithm { get; init; }
    /// <summary>Gets the selected embedded root fingerprint. <para>获取选中的内嵌根证书指纹。</para></summary>
    /// <value>The DER SHA-256 fingerprint, or null if absent. <para>DER SHA-256 指纹；不存在时为空。</para></value>
    public string? RootCertificateSha256 { get; init; }
    /// <summary>Gets the selected embedded root's SHA-384 fingerprint.
    /// <para>获取选中的内嵌根证书的 SHA-384 指纹。</para></summary>
    /// <value>The DER SHA-384 fingerprint, or null if absent.
    /// <para>DER SHA-384 指纹；不存在时为空。</para></value>
    public string? RootCertificateSha384 { get; init; }
}

/// <summary>Read-only offline analysis with independent recognition, cryptography, trust and device states.
/// <para>只读离线分析，独立报告识别、密码学、信任及设备状态。</para></summary>
public sealed record QualcommAnalysisResult
{
    /// <summary>Gets ELF components discovered in a multi-component container; empty for a single ELF.
    /// <para>获取多组件容器中发现的 ELF 组件；单个 ELF 时为空。</para></summary>
    public IReadOnlyList<QualcommMelfComponent> MelfComponents { get; init; } = [];
    /// <summary>Gets the source stream size observed during analysis.
    /// <para>获取分析期间所观察到的源流长度。</para></summary>
    /// <value>The source length in bytes. <para>源数据的字节长度。</para></value>
    public long FileSize { get; init; }
    /// <summary>Gets the detected format. <para>获取检测到的格式。</para></summary>
    /// <value>The format, or null for unrecognized input. <para>格式；未识别时为空。</para></value>
    public QualcommImageFormat? Format { get; init; }
    /// <summary>Gets the validated ELF metadata, if applicable. <para>获取适用时经校验的 ELF 元数据。</para></summary>
    /// <value>ELF metadata or null. <para>ELF 元数据或空值。</para></value>
    public ElfInfo? Elf { get; init; }
    /// <summary>Gets the validated MBN layout, if applicable. <para>获取适用时经校验的 MBN 布局。</para></summary>
    /// <value>MBN metadata or null. <para>MBN 元数据或空值。</para></value>
    public QualcommMbnInfo? Mbn { get; init; }
    /// <summary>Gets validated MBN and SBL file regions. <para>获取经过校验的 MBN 和 SBL 文件区域。</para></summary>
    /// <value>Absolute file ranges. <para>绝对文件范围。</para></value>
    public IReadOnlyList<QualcommImageRegion> Regions { get; init; } = [];
    /// <summary>Gets signing declarations separated by authority and source.
    /// <para>获取按签名方与来源区分的签名声明。</para></summary>
    /// <value>Parsed declarations, independent of verification status. <para>解析的声明，与验证状态相互独立。</para></value>
    public IReadOnlyList<QualcommSigningMetadata> SigningMetadata { get; init; } = [];
    /// <summary>Gets bounded static version string evidence. <para>获取有界的静态版本字符串证据。</para></summary>
    /// <value>String evidence, not code-reference analysis. <para>字符串证据，不是代码引用分析。</para></value>
    public IReadOnlyList<QualcommVersionEvidence> VersionStrings { get; init; } = [];
    /// <summary>Gets segment digest comparisons. <para>获取段摘要比较。</para></summary>
    /// <value>Ordered digest results. <para>按顺序排列的摘要结果。</para></value>
    public IReadOnlyList<QualcommSegmentDigest> SegmentDigests { get; init; } = [];
    /// <summary>Gets embedded certificate summaries. <para>获取内嵌证书摘要。</para></summary>
    /// <value>Bounded certificate summaries. <para>有界证书摘要。</para></value>
    public IReadOnlyList<QualcommCertificateInfo> Certificates { get; init; } = [];
    /// <summary>Gets static command evidence. <para>获取静态命令证据。</para></summary>
    /// <value>Evidence, not a device capability claim. <para>证据，不代表设备能力。</para></value>
    public IReadOnlyList<QualcommCommandEvidence> SupportedCommands { get; init; } = [];
    /// <summary>Gets the ELF digest-table status. <para>获取 ELF 摘要表状态。</para></summary>
    /// <value>Digest status. <para>摘要状态。</para></value>
    public QualcommVerificationStatus HashStatus { get; init; } = QualcommVerificationStatus.NotPresent;
    /// <summary>Gets the QTI authority verification. <para>获取 QTI 签名方验证。</para></summary>
    /// <value>Independent QTI result. <para>独立 QTI 结果。</para></value>
    public QualcommAuthorityResult QualcommSignature { get; init; } = new();
    /// <summary>Gets the OEM authority verification. <para>获取 OEM 签名方验证。</para></summary>
    /// <value>Independent OEM result. <para>独立 OEM 结果。</para></value>
    public QualcommAuthorityResult OemSignature { get; init; } = new();
    /// <summary>Gets the combined signature status without suppressing failures.
    /// <para>获取不会掩盖失败的合并签名状态。</para></summary>
    /// <value>Combined status. <para>合并状态。</para></value>
    public QualcommVerificationStatus SignatureStatus => Combine(QualcommSignature.SignatureStatus, OemSignature.SignatureStatus);
    /// <summary>Gets the combined embedded-chain status. <para>获取合并的内嵌证书链状态。</para></summary>
    /// <value>Combined status. <para>合并状态。</para></value>
    public QualcommVerificationStatus CertificateChainStatus => Combine(QualcommSignature.CertificateChainStatus, OemSignature.CertificateChainStatus);
    /// <summary>Gets the signed OEM metadata root-digest comparison status.
    /// <para>获取已签名 OEM 元数据中根摘要的比较状态。</para></summary>
    /// <value>The metadata-root digest status. <para>元数据根摘要状态。</para></value>
    public QualcommVerificationStatus MetadataRootHashStatus { get; init; } = QualcommVerificationStatus.NotPresent;
    /// <summary>Gets the combined external-root trust status. <para>获取合并的外部根信任状态。</para></summary>
    /// <value>Combined status. <para>合并状态。</para></value>
    public QualcommVerificationStatus TrustedRootStatus => Combine(QualcommSignature.TrustedRootStatus, OemSignature.TrustedRootStatus);
    /// <summary>Gets offline device-usability status, which is never asserted by static analysis.
    /// <para>获取离线设备可用性状态，静态分析绝不会确认其有效。</para></summary>
    /// <value>Always not checked. <para>始终未检查。</para></value>
    public QualcommVerificationStatus DeviceUsabilityStatus => QualcommVerificationStatus.NotChecked;
    /// <summary>Gets format and verification diagnostics. <para>获取格式与验证诊断信息。</para></summary>
    /// <value>Bounded diagnostics. <para>有界诊断信息。</para></value>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    private static QualcommVerificationStatus Combine(QualcommVerificationStatus first, QualcommVerificationStatus second)
    {
        if (first == QualcommVerificationStatus.Invalid || second == QualcommVerificationStatus.Invalid) return QualcommVerificationStatus.Invalid;
        if (first == QualcommVerificationStatus.Unsupported || second == QualcommVerificationStatus.Unsupported) return QualcommVerificationStatus.Unsupported;
        if (first == QualcommVerificationStatus.Valid || second == QualcommVerificationStatus.Valid) return QualcommVerificationStatus.Valid;
        if (first == QualcommVerificationStatus.NotPresent || second == QualcommVerificationStatus.NotPresent) return QualcommVerificationStatus.NotPresent;
        return QualcommVerificationStatus.NotChecked;
    }
}

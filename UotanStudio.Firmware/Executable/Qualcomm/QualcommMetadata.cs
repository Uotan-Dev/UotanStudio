namespace UotanStudio.Firmware.Executable.Qualcomm;

/// <summary>Describes a declared signing identity without asserting authentication or device compatibility.
/// <para>描述声明的签名身份，不代表认证通过或设备兼容。</para></summary>
public sealed record QualcommSigningMetadata
{
    /// <summary>Gets the signing authority or common metadata owner. <para>获取签名方或公共元数据所有方。</para></summary>
    /// <value>QTI, OEM or Common. <para>QTI、OEM 或 Common。</para></value>
    public required string Authority { get; init; }
    /// <summary>Gets whether the fields originate in the leaf certificate subject.
    /// <para>获取字段是否来自叶证书主题。</para></summary>
    /// <value>True for certificate OU fields; false for MBN metadata. <para>证书 OU 字段为真，MBN 元数据为假。</para></value>
    public bool FromCertificate { get; init; }
    /// <summary>Gets the recognized metadata layout or the reason fields remain uninterpreted.
    /// <para>获取已识别的元数据布局或字段未被解释的原因。</para></summary>
    /// <value>A stable layout identifier. <para>稳定的布局标识。</para></value>
    public string Layout { get; init; } = "UnsupportedMetadata";
    /// <summary>Gets the metadata file offset when available. <para>获取可用的元数据文件偏移。</para></summary>
    /// <value>An absolute offset or null for certificate-derived fields. <para>绝对偏移；证书派生字段为空。</para></value>
    public long? FileOffset { get; init; }
    /// <summary>Gets the declared metadata major version. <para>获取声明的元数据主版本。</para></summary>
    /// <value>The version or null. <para>版本或空值。</para></value>
    public uint? MajorVersion { get; init; }
    /// <summary>Gets the declared metadata minor version. <para>获取声明的元数据次版本。</para></summary>
    /// <value>The version or null. <para>版本或空值。</para></value>
    public uint? MinorVersion { get; init; }
    /// <summary>Gets the software identifier, preserving all 64 certificate bits.
    /// <para>获取软件标识，保留证书字段的完整 64 位。</para></summary>
    /// <value>The declared identifier or null. <para>声明的标识或空值。</para></value>
    public ulong? SoftwareId { get; init; }
    /// <summary>Gets the secondary software identifier. <para>获取第二软件标识。</para></summary>
    /// <value>The declared identifier or null. <para>声明的标识或空值。</para></value>
    public uint? SecondarySoftwareId { get; init; }
    /// <summary>Gets the hardware identifier without translating it to a device model.
    /// <para>获取硬件标识，不将其转换为设备型号。</para></summary>
    /// <value>The declared identifier or null. <para>声明的标识或空值。</para></value>
    public ulong? HardwareId { get; init; }
    /// <summary>Gets the explicit OEM identifier. <para>获取显式 OEM 标识。</para></summary>
    /// <value>The identifier or null; zero remains a declared value. <para>标识或空值；零仍是声明值。</para></value>
    public uint? OemId { get; init; }
    /// <summary>Gets the model or OEM product identifier. <para>获取型号或 OEM 产品标识。</para></summary>
    /// <value>The declared identifier or null. <para>声明的标识或空值。</para></value>
    public uint? ModelId { get; init; }
    /// <summary>Gets the application identifier. <para>获取应用标识。</para></summary>
    /// <value>The declared identifier or null. <para>声明的标识或空值。</para></value>
    public uint? ApplicationId { get; init; }
    /// <summary>Gets the signed software size declaration. <para>获取签名的软件大小声明。</para></summary>
    /// <value>The declared byte count or null. <para>声明的字节数或空值。</para></value>
    public uint? SoftwareSize { get; init; }
    /// <summary>Gets the anti-rollback version declared in metadata or the upper certificate SW_ID bits.
    /// <para>获取元数据或证书 SW_ID 高位中声明的反回滚版本。</para></summary>
    /// <value>The image declaration, not the device fuse value. <para>镜像声明值，不是设备熔丝值。</para></value>
    public uint? AntiRollbackVersion { get; init; }
    /// <summary>Gets the declared multi-root certificate slot. <para>获取声明的多根证书槽位。</para></summary>
    /// <value>The zero-based slot or null. <para>从零开始的槽位或空值。</para></value>
    public uint? RootCertificateSlot { get; init; }
    /// <summary>Gets raw metadata policy flags. <para>获取原始元数据策略标志。</para></summary>
    /// <value>The flags or null. <para>标志或空值。</para></value>
    public uint? Flags { get; init; }
    /// <summary>Gets all declared SoC version slots including zeros.
    /// <para>获取所有声明的 SoC 版本槽位，包括零值。</para></summary>
    /// <value>Ordered version slots. <para>有序版本槽位。</para></value>
    public IReadOnlyList<uint> SocVersions { get; init; } = [];
    /// <summary>Gets all declared serial slots including zeros.
    /// <para>获取所有声明的序列号槽位，包括零值。</para></summary>
    /// <value>Ordered serial slots, not a connected device serial. <para>有序序列号槽位，不是已连接设备的序列号。</para></value>
    public IReadOnlyList<ulong> SerialNumbers { get; init; } = [];
    /// <summary>Gets the SoC feature identifier. <para>获取 SoC 特性标识。</para></summary>
    /// <value>The identifier or null. <para>标识或空值。</para></value>
    public uint? SocFeatureId { get; init; }
    /// <summary>Gets the declared JTAG identifier. <para>获取声明的 JTAG 标识。</para></summary>
    /// <value>The identifier or null. <para>标识或空值。</para></value>
    public uint? JtagId { get; init; }
    /// <summary>Gets the declared SoC lifecycle state. <para>获取声明的 SoC 生命周期状态。</para></summary>
    /// <value>The raw state or null. <para>原始状态或空值。</para></value>
    public uint? SocLifecycleState { get; init; }
    /// <summary>Gets the declared OEM lifecycle state. <para>获取声明的 OEM 生命周期状态。</para></summary>
    /// <value>The raw state or null. <para>原始状态或空值。</para></value>
    public uint? OemLifecycleState { get; init; }
    /// <summary>Gets the root hash algorithm identifier. <para>获取根摘要算法标识。</para></summary>
    /// <value>The raw identifier or null. <para>原始标识或空值。</para></value>
    public uint? RootHashAlgorithm { get; init; }
    /// <summary>Gets the raw 64-byte root hash field, including padding.
    /// <para>获取原始 64 字节根摘要字段，包括填充。</para></summary>
    /// <value>Hexadecimal bytes or null. <para>十六进制字节或空值。</para></value>
    public string? RootHash { get; init; }
}

/// <summary>Describes a validated file region. <para>描述经过边界校验的文件区域。</para></summary>
/// <param name="Name">Stable region identifier. <para>稳定的区域标识。</para></param>
/// <param name="Offset">Absolute file offset. <para>绝对文件偏移。</para></param>
/// <param name="Length">Region length in bytes. <para>区域字节长度。</para></param>
public sealed record QualcommImageRegion(string Name, long Offset, long Length);

/// <summary>Preserves a certificate OU declaration, including duplicates.
/// <para>保留证书 OU 声明，包括重复字段。</para></summary>
/// <param name="Name">Declared field name. <para>声明的字段名。</para></param>
/// <param name="Value">Declared text value. <para>声明的文本值。</para></param>
public sealed record QualcommCertificateAttribute(string Name, string Value);

/// <summary>Records a version string literal without claiming that executable code uses it.
/// <para>记录版本字符串字面量，不声称可执行代码使用该值。</para></summary>
/// <param name="Name">Version field identifier. <para>版本字段标识。</para></param>
/// <param name="Value">Bounded decoded value. <para>有界解码值。</para></param>
/// <param name="FileOffset">Absolute offset of the prefix. <para>前缀的绝对文件偏移。</para></param>
public sealed record QualcommVersionEvidence(string Name, string Value, long FileOffset);
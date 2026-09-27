using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// Declares the explicit identity, compatibility constraints and provenance of one Sahara programmer image.
/// <para>声明单个 Sahara programmer 镜像的显式身份、兼容性约束与资源来源。</para>
/// Matching is driven only by the declared fields; file names, model names and directory names are never
/// treated as compatibility evidence, and an unset constraint means "not declared", never "matches anything".
/// <para>匹配仅依据显式声明的字段进行；文件名、机型名与目录名从不作为兼容性证据，
/// 未设置的约束表示“未声明”，绝不表示“匹配任意”。</para>
/// </summary>
public sealed record EdlProgrammerDescriptor
{
    /// <summary>
    /// Gets the file name used to present this programmer to the user.
    /// <para>获取向用户展示该 programmer 时使用的文件名。</para>
    /// </summary>
    /// <value>A non-blank display name; never a matching key. <para>非空的展示名；从不作为匹配键。</para></value>
    public required string FileName { get; init; }

    /// <summary>
    /// Gets the lowercase SHA-256 hex digest of the programmer bytes.
    /// <para>获取 programmer 字节的小写 SHA-256 十六进制摘要。</para>
    /// </summary>
    /// <value>Exactly 64 lowercase hex characters. <para>恰好 64 个小写十六进制字符。</para></value>
    public required string Sha256Hex { get; init; }

    /// <summary>
    /// Gets the Sahara image ids this programmer is declared to satisfy, or <see langword="null"/> when undeclared.
    /// <para>获取该 programmer 声明满足的 Sahara 镜像标识；未声明时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The declared image id set, or <see langword="null"/>. <para>声明的镜像标识集合，或 <see langword="null"/>。</para></value>
    public IReadOnlySet<SaharaImageId>? SupportedSaharaImageIds { get; init; }

    /// <summary>
    /// Gets the exact legacy hardware identifier this programmer supports, or <see langword="null"/> when undeclared.
    /// <para>获取该 programmer 支持的精确传统硬件标识符；未声明时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The declared HWID value. <para>声明的 HWID 值。</para></value>
    public ulong? HardwareId { get; init; }

    /// <summary>
    /// Gets the bit mask applied to the device HWID before comparing with <see cref="HardwareId"/>.
    /// <para>获取在与 <see cref="HardwareId"/> 比较前应用于设备 HWID 的位掩码。</para>
    /// </summary>
    /// <value>The declared mask, or <see langword="null"/> for an exact HWID comparison.
    /// <para>声明的掩码；需要精确比较 HWID 时为 <see langword="null"/>。</para></value>
    public ulong? HardwareIdMask { get; init; }

    /// <summary>
    /// Gets the lowercase OEM public-key-hash replies this programmer is declared for.
    /// <para>获取该 programmer 声明适用的 OEM 公钥哈希回复（小写）。</para>
    /// </summary>
    /// <value>The declared hash set, or <see langword="null"/> when undeclared.
    /// <para>声明的哈希集合；未声明时为 <see langword="null"/>。</para></value>
    public IReadOnlySet<string>? OemPublicKeyHashes { get; init; }

    /// <summary>
    /// Gets the normalized storage types this programmer is declared to address.
    /// <para>获取该 programmer 声明寻址的规范化存储类型。</para>
    /// </summary>
    /// <value>Lowercase tokens such as <c>emmc</c> or <c>ufs</c>, or <see langword="null"/> when undeclared.
    /// <para>小写标记（例如 <c>emmc</c>、<c>ufs</c>）；未声明时为 <see langword="null"/>。</para></value>
    public IReadOnlySet<string>? SupportedStorageTypes { get; init; }

    /// <summary>
    /// Gets whether the declared flow requires vendor authentication before use, or <see langword="null"/> when undeclared.
    /// <para>获取声明的流程是否要求使用前完成厂商认证；未声明时为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The declared requirement tri-state. <para>声明的认证需求三态。</para></value>
    public bool? RequiresVendorAuthentication { get; init; }

    /// <summary>
    /// Gets the expected Firehose storage sector size in bytes, when the profile was verified with one.
    /// <para>获取预期的 Firehose 存储扇区大小（字节），仅在已按该大小验证配置时提供。</para>
    /// </summary>
    /// <value>A positive sector-size hint, or <see langword="null"/> when undeclared.
    /// <para>正的扇区大小提示；未声明时为 <see langword="null"/>。</para></value>
    public int? ExpectedFirehoseSectorSizeBytes { get; init; }

    /// <summary>
    /// Gets the devices on which this programmer was explicitly verified.
    /// <para>获取已显式验证过该 programmer 的设备列表。</para>
    /// </summary>
    /// <value>Declared verification labels, or <see langword="null"/> when none were recorded.
    /// <para>声明的验证标签；未记录时为 <see langword="null"/>。</para></value>
    public IReadOnlyList<string>? VerifiedDevices { get; init; }

    /// <summary>
    /// Gets the origin, license and hash provenance of the underlying resource.
    /// <para>获取底层资源的来源、许可与哈希溯源。</para>
    /// </summary>
    /// <value>The provenance record, or <see langword="null"/> when unknown.
    /// <para>溯源记录；未知时为 <see langword="null"/>。</para></value>
    public ResourceProvenance? Provenance { get; init; }

    /// <summary>
    /// Reports whether the descriptor declares at least one field that can positively match a Sahara device identity.
    /// <para>指示该描述符是否至少声明了一个可以与 Sahara 设备身份正匹配的子段。</para>
    /// </summary>
    /// <value><see langword="true"/> when an HWID or PKHash constraint is declared.
    /// <para>已声明 HWID 或 PKHash 约束时为 <see langword="true"/>。</para></value>
    public bool HasIdentityEvidence => HardwareId is not null || OemPublicKeyHashes is { Count: > 0 };

    /// <summary>
    /// Validates the declared field formats and internal consistency.
    /// <para>校验声明字段的格式与内部一致性。</para>
    /// </summary>
    /// <exception cref="ArgumentException">A required field is blank, a hash is not lowercase hex,
    /// a mask lacks its HWID, or the provenance hash contradicts <see cref="Sha256Hex"/>.
    /// <para>必填字段为空、哈希不是小写十六进制、掩码缺少 HWID，或溯源哈希与 <see cref="Sha256Hex"/> 矛盾。</para></exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FileName);
        if (!IsLowercaseHex(Sha256Hex, 64))
        {
            throw new ArgumentException("The programmer SHA-256 must be exactly 64 lowercase hex characters.", nameof(Sha256Hex));
        }
        if (HardwareIdMask is not null && HardwareId is null)
        {
            throw new ArgumentException("A hardware id mask requires a hardware id.", nameof(HardwareIdMask));
        }
        if (SupportedSaharaImageIds is { Count: 0 })
        {
            throw new ArgumentException("A declared Sahara image id set must not be empty.", nameof(SupportedSaharaImageIds));
        }
        if (OemPublicKeyHashes is { Count: 0 })
        {
            throw new ArgumentException("A declared OEM public-key-hash set must not be empty.", nameof(OemPublicKeyHashes));
        }
        if (OemPublicKeyHashes is { } hashes && !hashes.All(static hash => IsLowercaseHex(hash, 32, 256)))
        {
            throw new ArgumentException("OEM public-key hashes must be lowercase hex of plausible width.", nameof(OemPublicKeyHashes));
        }
        if (SupportedStorageTypes is { Count: 0 })
        {
            throw new ArgumentException("A declared storage type set must not be empty.", nameof(SupportedStorageTypes));
        }
        if (SupportedStorageTypes is { } storage && !storage.All(static value => value.Length > 0
            && value == value.ToLowerInvariant() && value.All(char.IsLetterOrDigit)))
        {
            throw new ArgumentException("Storage types must be nonempty lowercase alphanumeric tokens.", nameof(SupportedStorageTypes));
        }
        if (ExpectedFirehoseSectorSizeBytes is { } sector && sector is <= 0 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpectedFirehoseSectorSizeBytes));
        }
        if (VerifiedDevices is { Count: 0 })
        {
            throw new ArgumentException("A declared verified-device list must not be empty.", nameof(VerifiedDevices));
        }
        if (Provenance is { } provenance && !string.Equals(provenance.Sha256Hex, Sha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The provenance hash must equal the descriptor SHA-256.", nameof(Provenance));
        }
    }

    private static bool IsLowercaseHex(string? value, int exactLength) =>
        value is not null && IsLowercaseHexCore(value, exactLength, exactLength);

    private static bool IsLowercaseHex(string? value, int minLength, int maxLength) =>
        value is not null && IsLowercaseHexCore(value, minLength, maxLength);

    private static bool IsLowercaseHexCore(string value, int minLength, int maxLength) =>
        value.Length >= minLength && value.Length <= maxLength
        && value.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
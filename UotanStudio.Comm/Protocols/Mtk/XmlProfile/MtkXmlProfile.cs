namespace UotanStudio.Comm.Protocols.Mtk.XmlProfile;

/// <summary>
/// A versioned whitelist of the XML DA envelope, commands, results, fields, file-request rules,
/// storage region tokens and reboot modes this build can speak.
/// <para>版本化的 XML DA 白名单：本构建可说的信封、命令、结果、字段、文件请求规则、存储区域标记与重启模式。</para>
/// A profile records what has been reviewed; it is evidence for grading unknown fields, not proof
/// that any device conforms to it. An unlisted profile version is never silently treated as a
/// known profile: <see cref="Recognize"/> returns <see langword="null"/> and the parser rejects
/// the message while reporting the observed version. Every profile here has scripted-transcript
/// coverage only; no real-device XML DA matrix has been executed against it.
/// <para>配置记录经过审阅的内容；它是未知字段分级的证据，而非任何设备符合该配置的证据。未列出的配置版本
/// 绝不会被静默当作已知配置：<see cref="Recognize"/> 返回 <see langword="null"/>，解析器会拒绝消息并报告
/// 观测到的版本号。此处每个配置都只有脚本化 transcript 覆盖；未针对真实设备执行任何 XML DA 矩阵。</para>
/// </summary>
public sealed record MtkXmlProfile
{
    /// <summary>
    /// Gets the profile version token as it appears in the wire <c>version</c> field.
    /// <para>获取线协议 <c>version</c> 字段中出现的配置版本标记。</para>
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the allowed root envelope names for device-authored messages.
    /// <para>获取设备侧消息允许的信封根名称。</para>
    /// </summary>
    public required IReadOnlySet<string> EnvelopeNames { get; init; }

    /// <summary>
    /// Gets the top-level field names that carry protocol meaning.
    /// <para>获取承载协议语义的顶层字段名。</para>
    /// </summary>
    public required IReadOnlySet<string> FieldNames { get; init; }

    /// <summary>
    /// Gets the additional field names that <see cref="MtkXmlFieldPolicy.TolerantUnknown"/> may
    /// downgrade to warning evidence; they never shadow a known field.
    /// <para>获取 <see cref="MtkXmlFieldPolicy.TolerantUnknown"/> 可降级为 warning 证据的附加字段名；
    /// 它们绝不遮蔽已知字段。</para>
    /// </summary>
    public required IReadOnlySet<string> IgnorableFieldNames { get; init; }

    /// <summary>
    /// Gets the reviewed command tokens, each required to keep the <c>CMD:</c> prefix.
    /// <para>获取经过审阅的命令标记，每个都必须保留 <c>CMD:</c> 前缀。</para>
    /// </summary>
    public required IReadOnlySet<string> Commands { get; init; }

    /// <summary>
    /// Gets the reviewed result tokens for terminal <c>CMD:END</c> messages.
    /// <para>获取终态 <c>CMD:END</c> 消息经过审阅的结果标记。</para>
    /// </summary>
    public required IReadOnlySet<string> ResultTokens { get; init; }

    /// <summary>
    /// Gets the reviewed storage region name prefixes; concrete regions must additionally have
    /// been reported by the device geometry and must not be authenticated-only.
    /// <para>获取经过审阅的存储区域名前缀；具体区域还必须由设备几何信息上报，且不得为需认证区域。</para>
    /// </summary>
    public required IReadOnlySet<string> StorageRegionPrefixes { get; init; }

    /// <summary>
    /// Gets the reviewed <c>CMD:BOOT-TO</c> mode tokens.
    /// <para>获取经过审阅的 <c>CMD:BOOT-TO</c> 模式标记。</para>
    /// </summary>
    public required IReadOnlySet<string> RebootModes { get; init; }

    /// <summary>
    /// Gets the file-request rules that every upload or download callback must satisfy: the exact
    /// checksum token, the bounded packet length and the negotiated direction.
    /// <para>获取每个上传或下载回调都必须满足的文件请求规则：精确的校验和标记、有界的包长以及协商好的方向。</para>
    /// </summary>
    public required MtkXmlFileRequestRules FileRequests { get; init; }

    /// <summary>
    /// Gets the reviewed version 1.0 profile.
    /// <para>获取经过审阅的 1.0 版本配置。</para>
    /// </summary>
    /// <value>The baseline XML DA profile accepted by <c>MtkXmlMessage.Parse</c>.
    /// <para><c>MtkXmlMessage.Parse</c> 接受的基础 XML DA 配置。</para></value>
    public static MtkXmlProfile Version10 { get; } = Create("1.0");

    /// <summary>
    /// Gets the reviewed version 1.1 profile.
    /// <para>获取经过审阅的 1.1 版本配置。</para>
    /// </summary>
    /// <value>The extended profile with the same wire whitelist as version 1.0.
    /// <para>线协议白名单与 1.0 相同的扩展配置。</para></value>
    public static MtkXmlProfile Version11 { get; } = Create("1.1");

    /// <summary>
    /// Gets every profile this build recognizes.
    /// <para>获取本构建识别的全部配置。</para>
    /// </summary>
    /// <value>The known profiles in version order. <para>按版本排序的已知配置。</para></value>
    public static IReadOnlyList<MtkXmlProfile> Known { get; } = [Version10, Version11];

    /// <summary>
    /// Resolves a wire-reported version token to its reviewed profile.
    /// <para>将线协议上报的版本标记解析为其经过审阅的配置。</para>
    /// </summary>
    /// <param name="version">The reported version text, possibly null or unknown.
    /// <para>上报的版本文本，可能为 null 或未知。</para></param>
    /// <returns>
    /// The matching profile, or <see langword="null"/> when the version is not reviewed; a null
    /// result must fail closed and report the version instead of guessing a compatible profile.
    /// <para>匹配的配置；版本未经审阅时返回 <see langword="null"/>；此时必须失败关闭并报告版本号，
    /// 而不是猜测兼容配置。</para>
    /// </returns>
    public static MtkXmlProfile? Recognize(string? version) =>
        Known.FirstOrDefault(profile => string.Equals(profile.Version, version, StringComparison.Ordinal));

    /// <summary>
    /// Reports whether the named top-level field has protocol meaning in this profile.
    /// <para>报告指定名称的顶层字段在本配置中是否具有协议语义。</para>
    /// </summary>
    /// <param name="fieldName">The unqualified local name. <para>不带命名空间的局部名。</para></param>
    /// <returns><see langword="true"/> for a known field. <para>已知字段返回 <see langword="true"/>。</para></returns>
    public bool IsKnownField(string fieldName) => FieldNames.Contains(fieldName);

    /// <summary>
    /// Reports whether the named unknown field may be downgraded under
    /// <see cref="MtkXmlFieldPolicy.TolerantUnknown"/> instead of being rejected.
    /// <para>报告未知名未知字段是否可在 <see cref="MtkXmlFieldPolicy.TolerantUnknown"/> 下降级而非拒绝。</para>
    /// </summary>
    /// <param name="fieldName">The unqualified local name; never a known field name.
    /// <para>不带命名空间的局部名；绝不能是已知字段名。</para></param>
    /// <returns><see langword="true"/> when the field is ignorable. <para>字段可忽略时返回 <see langword="true"/>。</para></returns>
    public bool IsIgnorableField(string fieldName) =>
        !IsKnownField(fieldName) && IgnorableFieldNames.Contains(fieldName);

    private static MtkXmlProfile Create(string version) => new()
    {
        Version = version,
        EnvelopeNames = new HashSet<string>(StringComparer.Ordinal) { "host", "da", "sla", "da_hw_info" },
        FieldNames = new HashSet<string>(StringComparer.Ordinal) { "version", "command", "result", "arg" },
        IgnorableFieldNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "status", "info", "log", "debug", "reason", "task_id", "progress"
        },
        Commands = new HashSet<string>(StringComparer.Ordinal)
        {
            "CMD:START", "CMD:END", "CMD:HOST-SUPPORTED-COMMANDS", "CMD:SET-RUNTIME-PARAMETER",
            "CMD:NOTIFY-INIT-HW", "CMD:SET-HOST-INFO", "CMD:GET-SYS-PROPERTY", "CMD:GET-HW-INFO",
            "CMD:UPLOAD-FILE", "CMD:DOWNLOAD-FILE", "CMD:PROGRESS-REPORT", "CMD:READ-FLASH",
            "CMD:WRITE-FLASH", "CMD:ERASE-FLASH", "CMD:FILE-SYS-OPERATION",
            "CMD:SECURITY-GET-DEV-FW-INFO", "CMD:SECURITY-SET-FLASH-POLICY",
            "CMD:SECURITY-SET-ALLINONE-SIGNATURE", "CMD:SET-BOOT-MODE", "CMD:BOOT-TO", "CMD:REBOOT"
        },
        ResultTokens = new HashSet<string>(StringComparer.Ordinal) { "OK", "ERR!" },
        StorageRegionPrefixes = new HashSet<string>(StringComparer.Ordinal) { "EMMC-", "UFS-LUA" },
        RebootModes = new HashSet<string>(StringComparer.Ordinal) { "FASTBOOT", "META", "ANDROID-TEST-MODE" },
        FileRequests = new MtkXmlFileRequestRules
        {
            ChecksumToken = "CHK_NO",
            MaximumPacketBytes = 1024 * 1024,
            AllowedDirections = new HashSet<MtkXmlFileRequestDirection>
            {
                MtkXmlFileRequestDirection.UploadToDevice, MtkXmlFileRequestDirection.DownloadFromDevice
            }
        }
    };
}

/// <summary>
/// The bounded, explicit rules every device-initiated XML DA file request must satisfy.
/// <para>每个设备发起的 XML DA 文件请求都必须满足的显式有界规则。</para>
/// The token check pins the unauthenticated <c>CHK_NO</c> checksum declaration so a device can
/// never claim a stronger integrity mode the host did not negotiate; the length bound rejects
/// zero and over-large packets before any byte moves; the direction check ensures an upload
/// callback only arrives while the host declared an uploadable destination and a download callback
/// only while the host declared a downloadable source. These rules are enforced by the session
/// validation paths, and this record states them as profile data.
/// <para>标记检查钉住未经认证的 <c>CHK_NO</c> 校验和声明，设备绝不能声称宿主未协商的更强完整性模式；
/// 长度限制在任何字节移动之前拒绝零值和过大包；方向检查确保上传回调仅在宿主声明可上传目标时到达，
/// 下载回调仅在宿主声明可下载源时到达。这些规则由会话校验路径执行，本记录将其表述为配置数据。</para>
/// </summary>
public sealed record MtkXmlFileRequestRules
{
    /// <summary>
    /// Gets the only accepted checksum token, <c>CHK_NO</c>: file transfers carry no cryptographic
    /// integrity claim.
    /// <para>获取唯一接受的校验和标记 <c>CHK_NO</c>：文件传输不携带任何密码学完整性声明。</para>
    /// </summary>
    public required string ChecksumToken { get; init; }

    /// <summary>
    /// Gets the inclusive upper bound for one negotiated packet length, in bytes.
    /// <para>获取单个协商包长的包含式上限（字节）。</para>
    /// </summary>
    public required int MaximumPacketBytes { get; init; }

    /// <summary>
    /// Gets the directions this profile allows for device-initiated file requests.
    /// <para>获取本配置允许的设备发起文件请求方向。</para>
    /// </summary>
    public required IReadOnlySet<MtkXmlFileRequestDirection> AllowedDirections { get; init; }
}

/// <summary>
/// The two directions a device-initiated XML DA file transfer can take.
/// <para>设备发起的 XML DA 文件传输可能的两个方向。</para>
/// </summary>
public enum MtkXmlFileRequestDirection
{
    /// <summary>The device pulls bytes from the host destination it was given (host-to-device).
    /// <para>设备从其获定的宿主目标拉取字节（宿主到设备）。</para></summary>
    UploadToDevice,

    /// <summary>The device pushes bytes toward the host source it was given (device-to-host).
    /// <para>设备向其获定的宿主源推送字节（设备到宿主）。</para></summary>
    DownloadFromDevice
}
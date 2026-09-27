using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Commands guarded by the negotiated Firehose capability snapshot.
/// <para>受协商得到的 Firehose 能力快照保护的命令。</para>
/// </summary>
public enum EdlFirehoseCommand
{
    /// <summary>
    /// The &lt;read&gt; streaming command. <para>&lt;read&gt; 流式命令。</para>
    /// </summary>
    Read,

    /// <summary>
    /// The &lt;program&gt; write command. <para>&lt;program&gt; 写命令。</para>
    /// </summary>
    Program,

    /// <summary>
    /// The &lt;erase&gt; command. <para>&lt;erase&gt; 命令。</para>
    /// </summary>
    Erase,

    /// <summary>
    /// The &lt;patch&gt; command. <para>&lt;patch&gt; 命令。</para>
    /// </summary>
    Patch,

    /// <summary>
    /// The wipe (program-zeros) sequence. <para>擦写（零字节编程）序列。</para>
    /// </summary>
    Wipe,

    /// <summary>
    /// Sparse image program sequences. <para>Sparse 镜像编程序列。</para>
    /// </summary>
    Sparse,

    /// <summary>
    /// GPT discovery reads. <para>GPT 发现读取。</para>
    /// </summary>
    Gpt,

    /// <summary>
    /// UFS provisioning command sequences. <para>UFS provisioning 命令序列。</para>
    /// </summary>
    UfsProvisioning,

    /// <summary>
    /// Raw binary mode transfers. <para>原始二进制模式传输。</para>
    /// </summary>
    RawMode,

    /// <summary>
    /// Vendor authentication command sequences. <para>厂商认证命令序列。</para>
    /// </summary>
    VendorAuthentication,

    /// <summary>
    /// 64-bit sector addressing. <para>64 位扇区寻址。</para>
    /// </summary>
    SixtyFourBitAddressing,

    /// <summary>
    /// Programmer-side reconnect continuation support. <para>programmer 侧重连续传支持。</para>
    /// </summary>
    Reconnect
}

/// <summary>
/// Immutable capability snapshot negotiated once from the Firehose configure acknowledgement.
/// <para>由 Firehose configure 确认应答一次性协商得到的不可变能力快照。</para>
/// Each capability is tri-state: <see cref="FeatureSupport.Unknown"/> means the programmer did not
/// report (or contradicted itself about) the capability and is never treated as support. High-risk
/// storage commands (<see cref="EdlFirehoseCommand.Erase"/>, <see cref="EdlFirehoseCommand.Patch"/>,
/// <see cref="EdlFirehoseCommand.Wipe"/> and <see cref="EdlFirehoseCommand.UfsProvisioning"/>)
/// require the matching configure-response attribute
/// (<c>Erase</c>, <c>Patch</c>, <c>Wipe</c>, <c>UfsProvisioning</c>, values <c>true</c>/<c>1</c>/
/// <c>supported</c> versus <c>false</c>/<c>0</c>/<c>unsupported</c>) and are refused before any
/// byte reaches the wire otherwise. Non-destructive read paths are evidenced by the configure ACK
/// itself; the write path additionally requires the negotiated <c>MaxPayloadSizeToTargetInBytes</c>.
/// Vendor-extension attributes unknown to this contract are ignored.
/// <para>每项能力均为三态：<see cref="FeatureSupport.Unknown"/> 表示 programmer 未上报（或自相矛盾），
/// 绝不被当作支持。高风险存储命令（<see cref="EdlFirehoseCommand.Erase"/>、
/// <see cref="EdlFirehoseCommand.Patch"/>、<see cref="EdlFirehoseCommand.Wipe"/> 与
/// <see cref="EdlFirehoseCommand.UfsProvisioning"/>）要求 configure 应答携带对应属性
/// （<c>Erase</c>、<c>Patch</c>、<c>Wipe</c>、<c>UfsProvisioning</c>，取值 <c>true</c>/<c>1</c>/
/// <c>supported</c> 或 <c>false</c>/<c>0</c>/<c>unsupported</c>），否则在任何字节上线前被拒绝。
/// 非破坏性读取路径由 configure ACK 本身作证；写路径还要求协商得到
/// <c>MaxPayloadSizeToTargetInBytes</c>。本合同不认识的厂商扩展属性会被忽略。</para>
/// </summary>
public sealed record FirehoseCapabilitySnapshot : CapabilitySnapshot
{
    /// <summary>
    /// Gets the storage type negotiated at configure time.
    /// <para>获取 configure 时协商得到的存储类型。</para>
    /// </summary>
    public required FirehoseStorageType StorageType { get; init; }

    /// <summary>
    /// Gets the maximum program payload the programmer negotiated, in bytes.
    /// <para>获取协商得到的最大编程载荷（字节）。</para>
    /// </summary>
    public long? MaxPayloadSizeToTargetInBytes { get; init; }

    /// <summary>
    /// Gets the maximum supported program payload the programmer advertised, in bytes.
    /// <para>获取 programmer 通告的最大受支持编程载荷（字节）。</para>
    /// </summary>
    public long? MaxPayloadSizeToTargetSupportedInBytes { get; init; }

    /// <summary>
    /// Gets the maximum read payload from the target, in bytes.
    /// <para>获取来自目标的最大读取载荷（字节）。</para>
    /// </summary>
    public long? MaxPayloadSizeFromTargetInBytes { get; init; }

    /// <summary>
    /// Gets the effective sector size in bytes after configure and storage detection.
    /// <para>获取 configure 与存储检测后的生效扇区大小（字节）。</para>
    /// </summary>
    public long? SectorSizeInBytes { get; init; }

    /// <summary>
    /// Gets the A/B slot negotiated for command emission.
    /// <para>获取为命令输出协商的 A/B 槽位。</para>
    /// </summary>
    public int? StorageSlot { get; init; }

    /// <summary>
    /// Gets the validated storage geometry observed during the configuration window.
    /// <para>获取在配置窗口内观测到的经校验存储几何信息。</para>
    /// </summary>
    public FirehoseStorageGeometry? Geometry { get; init; }

    /// <summary>
    /// Gets the Firehose version string the programmer reported, when present.
    /// <para>获取 programmer 上报的 Firehose 版本字符串（若存在）。</para>
    /// </summary>
    public string? FirehoseVersion { get; init; }

    /// <summary>
    /// Gets the file name of the uploaded programmer, when the session recorded one.
    /// <para>获取会话记录的已上传 programmer 文件名（若有）。</para>
    /// </summary>
    public string? ProgrammerFileName { get; init; }

    /// <summary>
    /// Gets whether the session that produced this snapshot already requires a device reconnect.
    /// <para>获取产生本快照的会话是否已需要设备重连。</para>
    /// </summary>
    public bool RequiresReconnect { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for the &lt;read&gt; command.
    /// <para>获取 &lt;read&gt; 命令的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport Read { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for the &lt;program&gt; command.
    /// <para>获取 &lt;program&gt; 命令的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport Program { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for the &lt;erase&gt; command.
    /// <para>获取 &lt;erase&gt; 命令的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport Erase { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for the &lt;patch&gt; command.
    /// <para>获取 &lt;patch&gt; 命令的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport Patch { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for the wipe sequence.
    /// <para>获取擦写序列的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport Wipe { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for sparse program sequences.
    /// <para>获取 sparse 编程序列的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport Sparse { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for GPT discovery reads.
    /// <para>获取 GPT 发现读取的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport Gpt { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for UFS provisioning.
    /// <para>获取 UFS provisioning 的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport UfsProvisioning { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for raw binary mode transfers.
    /// <para>获取原始二进制模式传输的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport RawMode { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for vendor authentication command sequences.
    /// <para>获取厂商认证命令序列的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport VendorAuthentication { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for 64-bit sector addressing.
    /// <para>获取 64 位扇区寻址的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport SixtyFourBitAddressing { get; init; }

    /// <summary>
    /// Gets the tri-state support marker for programmer-side reconnect continuation.
    /// <para>获取 programmer 侧重连续传的三态支持标记。</para>
    /// </summary>
    public required FeatureSupport Reconnect { get; init; }

    /// <summary>
    /// Gets the tri-state marker for one guarded command.
    /// <para>获取受保护命令的三态标记。</para>
    /// </summary>
    /// <param name="command">The command to look up. <para>要查询的命令。</para></param>
    /// <returns>The recorded support state; <see cref="FeatureSupport.Unknown"/> for an unmapped
    /// command so new commands fail closed. <para>记录的支持状态；未映射的命令返回
    /// <see cref="FeatureSupport.Unknown"/>，使新命令失败关闭。</para></returns>
    public FeatureSupport For(EdlFirehoseCommand command) => command switch
    {
        EdlFirehoseCommand.Read => Read,
        EdlFirehoseCommand.Program => Program,
        EdlFirehoseCommand.Erase => Erase,
        EdlFirehoseCommand.Patch => Patch,
        EdlFirehoseCommand.Wipe => Wipe,
        EdlFirehoseCommand.Sparse => Sparse,
        EdlFirehoseCommand.Gpt => Gpt,
        EdlFirehoseCommand.UfsProvisioning => UfsProvisioning,
        EdlFirehoseCommand.RawMode => RawMode,
        EdlFirehoseCommand.VendorAuthentication => VendorAuthentication,
        EdlFirehoseCommand.SixtyFourBitAddressing => SixtyFourBitAddressing,
        EdlFirehoseCommand.Reconnect => Reconnect,
        _ => FeatureSupport.Unknown
    };

    /// <summary>
    /// Builds the snapshot from one configure acknowledgement, the negotiated configuration, and the
    /// geometry observed during the same configuration window.
    /// <para>由单个 configure 确认应答、协商后的配置，以及同一配置窗口内观测到的几何信息构建快照。</para>
    /// </summary>
    /// <param name="configureResponse">The final normal-mode configure ACK. <para>最终的普通模式 configure ACK。</para></param>
    /// <param name="configuration">The negotiated mutable configuration; copied, never referenced.
    /// <para>协商后的可变配置；复制而非引用。</para></param>
    /// <param name="geometry">The validated geometry observed so far, when any. <para>迄今观测到的经校验几何信息（若有）。</para></param>
    /// <param name="requiresReconnect">Whether the session already requires a reconnect. <para>会话是否已需要重连。</para></param>
    /// <param name="capturedAtUtc">The capture instant. <para>捕获时刻。</para></param>
    /// <returns>The immutable snapshot. <para>不可变快照。</para></returns>
    /// <exception cref="ArgumentNullException">The response or configuration is <see langword="null"/>.
    /// <para>应答或配置为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The response is not an acknowledgement. <para>应答不是确认。</para></exception>
    public static FirehoseCapabilitySnapshot FromConfigureAck(FirehoseResponse configureResponse,
        FirehoseConfiguration configuration, FirehoseStorageGeometry? geometry, bool requiresReconnect,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(configureResponse);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configureResponse.IsAck || configureResponse.IsRawMode)
        {
            throw new ArgumentException("Only a normal-mode configure acknowledgement negotiates capabilities.", nameof(configureResponse));
        }

        var ackPayload = configureResponse.GetInt64("MaxPayloadSizeToTargetInBytes");
        var programSupport = ackPayload is > 0 ? FeatureSupport.Supported : FeatureSupport.Unknown;
        var wireEvidence = new ProtocolEvidence
        {
            Protocol = "edl",
            Phase = "configure",
            Outcome = CommandOutcomeKind.Completed,
            Detail = $"attributeCount={configureResponse.Attributes.Count}",
            RawResponseHex = configureResponse.RawXml is { Length: > 0 and <= 65536 } xml
                ? Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(xml))
                : null,
            ObservedAtUtc = capturedAtUtc
        };
        return new FirehoseCapabilitySnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            Evidence = [wireEvidence],
            ResourceSha256Hex = null,
            TransportGeneration = null,
            StorageType = configuration.StorageType,
            MaxPayloadSizeToTargetInBytes = ackPayload ?? configuration.MaxPayloadSizeToTarget,
            MaxPayloadSizeToTargetSupportedInBytes = configureResponse.GetInt64("MaxPayloadSizeToTargetInBytesSupported")
                ?? configuration.MaxPayloadSizeToTargetSupported,
            MaxPayloadSizeFromTargetInBytes = configureResponse.GetInt64("MaxPayloadSizeFromTargetInBytes")
                ?? configuration.MaxPayloadSizeFromTarget,
            SectorSizeInBytes = geometry?.SectorSize ?? (configuration.SectorSize > 0 ? configuration.SectorSize : null),
            StorageSlot = configuration.StorageSlot != 0 ? configuration.StorageSlot : configuration.Slot != 0 ? configuration.Slot : null,
            Geometry = geometry,
            FirehoseVersion = configureResponse["Version"] ?? configureResponse["MinVersion"],
            RequiresReconnect = requiresReconnect,
            Read = FeatureSupport.Supported,
            Program = programSupport,
            Erase = ParseTriState(configureResponse["Erase"]),
            Patch = ParseTriState(configureResponse["Patch"]),
            Wipe = ParseTriState(configureResponse["Wipe"]),
            Sparse = programSupport,
            Gpt = FeatureSupport.Supported,
            UfsProvisioning = ParseTriState(configureResponse["UfsProvisioning"]),
            RawMode = ParseTriState(configureResponse["rawmode"]),
            VendorAuthentication = ParseTriState(configureResponse["VendorAuth"]),
            SixtyFourBitAddressing = ParseTriState(configureResponse["SixtyFourBitAddressing"]),
            Reconnect = ParseTriState(configureResponse["Reconnect"])
        };
    }

    /// <summary>
    /// Parses one tri-state capability attribute value.
    /// <para>解析单个三态能力属性值。</para>
    /// </summary>
    /// <param name="value">The raw attribute value, or <see langword="null"/> when the attribute was
    /// absent. <para>原始属性值；属性缺失时为 <see langword="null"/>。</para></param>
    /// <returns>
    /// <see cref="FeatureSupport.Supported"/> for <c>true</c>/<c>1</c>/<c>supported</c>,
    /// <see cref="FeatureSupport.Unsupported"/> for <c>false</c>/<c>0</c>/<c>unsupported</c>, and
    /// <see cref="FeatureSupport.Unknown"/> for anything else including the absence of a report.
    /// <para><c>true</c>/<c>1</c>/<c>supported</c> 返回 <see cref="FeatureSupport.Supported"/>；
    /// <c>false</c>/<c>0</c>/<c>unsupported</c> 返回 <see cref="FeatureSupport.Unsupported"/>；
    /// 其余（包括未上报）返回 <see cref="FeatureSupport.Unknown"/>。</para>
    /// </returns>
    internal static FeatureSupport ParseTriState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => FeatureSupport.Unknown,
        "true" or "1" or "supported" => FeatureSupport.Supported,
        "false" or "0" or "unsupported" => FeatureSupport.Unsupported,
        _ => FeatureSupport.Unknown
    };
}
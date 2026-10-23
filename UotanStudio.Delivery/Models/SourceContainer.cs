namespace UotanStudio.Delivery;

/// <summary>Original Super or Payload metadata used to rebuild delivery layouts. <para>用于重建交付布局的原始 Super 或 Payload 元数据。</para></summary>
public sealed record DeliverySourceContainerSettings
{
    /// <summary>
    /// Gets or sets the SourceName value.
    /// <para>获取或设置 SourceName 值。</para>
    /// </summary>
    public string SourceName { get; init; } = "";
    /// <summary>
    /// Gets or sets the Kind value.
    /// <para>获取或设置 Kind 值。</para>
    /// </summary>
    public string Kind { get; init; } = "";
    /// <summary>LP block-device partition name read from a Super source; empty when unavailable.
    /// <para>从 Super 来源读取的 LP 块设备分区名；不可用时为空。</para></summary>
    public string DeviceName { get; init; } = "";
    /// <summary>
    /// Gets or sets the Storage value.
    /// <para>获取或设置 Storage 值。</para>
    /// </summary>
    public string Storage { get; init; } = "RAW";
    /// <summary>
    /// Gets or sets the DeviceType value.
    /// <para>获取或设置 DeviceType 值。</para>
    /// </summary>
    public string DeviceType { get; init; } = "Unknown";
    /// <summary>
    /// Gets or sets the Size value.
    /// <para>获取或设置 Size 值。</para>
    /// </summary>
    public ulong Size { get; init; }
    /// <summary>
    /// Gets or sets the BlockSize value.
    /// <para>获取或设置 BlockSize 值。</para>
    /// </summary>
    public uint BlockSize { get; init; }
    /// <summary>
    /// Gets or sets the MetadataSlots value.
    /// <para>获取或设置 MetadataSlots 值。</para>
    /// </summary>
    public uint MetadataSlots { get; init; }
    /// <summary>
    /// Gets or sets the MetadataMaxSize value.
    /// <para>获取或设置 MetadataMaxSize 值。</para>
    /// </summary>
    public uint MetadataMaxSize { get; init; }
    /// <summary>
    /// Gets or sets the MinorVersion value.
    /// <para>获取或设置 MinorVersion 值。</para>
    /// </summary>
    public uint MinorVersion { get; init; }
    /// <summary>
    /// Gets or sets the MaxTimestamp value.
    /// <para>获取或设置 MaxTimestamp 值。</para>
    /// </summary>
    public long MaxTimestamp { get; init; }
    /// <summary>
    /// Gets or sets the SecurityPatchLevel value.
    /// <para>获取或设置 SecurityPatchLevel 值。</para>
    /// </summary>
    public string SecurityPatchLevel { get; init; } = "";
    /// <summary>OTA metadata <c>post-build</c> fingerprint read from a ZIP-wrapped source; empty when unavailable.
    /// <para>从 ZIP 包裹来源读取的 OTA 元数据 <c>post-build</c> 指纹；不可用时为空。</para></summary>
    public string BuildFingerprint { get; init; } = "";
    /// <summary>OTA metadata <c>post-build-incremental</c> value; empty when unavailable.
    /// <para>OTA 元数据 <c>post-build-incremental</c> 值；不可用时为空。</para></summary>
    public string Incremental { get; init; } = "";
    /// <summary>OTA metadata <c>post-sdk-level</c> value; zero when unavailable.
    /// <para>OTA 元数据 <c>post-sdk-level</c> 值；不可用时为 0。</para></summary>
    public uint SdkLevel { get; init; }
    /// <summary>
    /// Gets or sets the PartitionSizes value.
    /// <para>获取或设置 PartitionSizes 值。</para>
    /// </summary>
    public ulong[] PartitionSizes { get; init; } = [];
    /// <summary>
    /// Gets or sets the GroupNames value.
    /// <para>获取或设置 GroupNames 值。</para>
    /// </summary>
    public string[] GroupNames { get; init; } = [];
    /// <summary>
    /// Gets or sets the GroupSizes value.
    /// <para>获取或设置 GroupSizes 值。</para>
    /// </summary>
    public ulong[] GroupSizes { get; init; } = [];
    /// <summary>
    /// Gets or sets the PartitionNames value.
    /// <para>获取或设置 PartitionNames 值。</para>
    /// </summary>
    public string[] PartitionNames { get; init; } = [];
    /// <summary>
    /// Gets or sets the OriginalStructures value.
    /// <para>获取或设置 OriginalStructures 值。</para>
    /// </summary>
    public byte[][] OriginalStructures { get; init; } = [];
}
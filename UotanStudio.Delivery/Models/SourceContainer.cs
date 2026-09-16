namespace UotanStudio.Services.Delivery;

/// <summary>Original Super or Payload metadata used to rebuild delivery layouts.</summary>
internal sealed record DeliverySourceContainerSettings
{
    public string SourceName { get; init; } = "";
    public string Kind { get; init; } = "";
    /// <summary>LP block-device partition name read from a Super source; empty when unavailable.
    /// <para>从 Super 来源读取的 LP 块设备分区名；不可用时为空。</para></summary>
    public string DeviceName { get; init; } = "";
    public string Storage { get; init; } = "RAW";
    public string DeviceType { get; init; } = "Unknown";
    public ulong Size { get; init; }
    public uint BlockSize { get; init; }
    public uint MetadataSlots { get; init; }
    public uint MetadataMaxSize { get; init; }
    public uint MinorVersion { get; init; }
    public long MaxTimestamp { get; init; }
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
    public ulong[] PartitionSizes { get; init; } = [];
    public string[] GroupNames { get; init; } = [];
    public ulong[] GroupSizes { get; init; } = [];
    public string[] PartitionNames { get; init; } = [];
    public byte[][] OriginalStructures { get; init; } = [];
}
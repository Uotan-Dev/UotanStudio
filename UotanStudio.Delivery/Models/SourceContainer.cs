namespace UotanStudio.Services.Delivery;

/// <summary>Original Super or Payload metadata used to rebuild delivery layouts.</summary>
internal sealed record DeliverySourceContainerSettings
{
    public string SourceName { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Storage { get; init; } = "RAW";
    public string DeviceType { get; init; } = "Unknown";
    public ulong Size { get; init; }
    public uint BlockSize { get; init; }
    public uint MetadataSlots { get; init; }
    public uint MetadataMaxSize { get; init; }
    public uint MinorVersion { get; init; }
    public long MaxTimestamp { get; init; }
    public string SecurityPatchLevel { get; init; } = "";
    public ulong[] PartitionSizes { get; init; } = [];
    public string[] GroupNames { get; init; } = [];
    public ulong[] GroupSizes { get; init; } = [];
    public string[] PartitionNames { get; init; } = [];
    public byte[][] OriginalStructures { get; init; } = [];
}